using System;
using System.Collections.Generic;
using System.Diagnostics;
using RimWorld;
using TheNetwork.Domain;
using TheNetwork.Domain.Physical;
using TheNetwork.Kernel;
using UnityEngine;
using Verse;

namespace TheNetwork.Integration.Physical
{
    /// <summary>
    /// Reads an unbound, unspawned candidate pawn into the pure <see cref="RoleCandidate"/> the role verdict decides on, and applies the ONE
    /// correction the verdict allows (raise one role-defining skill's base level). Nothing else is ever written: no passion, trait,
    /// backstory, gene, incapability, hediff, age, gender, name (except the established name pins, applied separately) or relation.
    /// </summary>
    public static class PawnRoleReader
    {
        /// <summary>The work tags a role may require (vanilla WorkTags names).</summary>
        public static readonly string[] WorkTagNames = { "Violent", "Caring", "Social", "Hauling", "Crafting", "Constructing", "Intellectual" };

        public static RoleCandidate Snapshot(Pawn p)
        {
            RoleCandidate c = new RoleCandidate();
            if (p == null) return c;
            for (int i = 0; i < WorkTagNames.Length; i++)
            {
                WorkTags tag;
                if (Enum.TryParse(WorkTagNames[i], out tag) && p.WorkTagIsDisabled(tag)) c.disabledWorkTags.Add(WorkTagNames[i]);
            }
            if (p.story?.traits?.allTraits != null) foreach (Trait t in p.story.traits.allTraits) if (t?.def != null) c.traits.Add(t.def.defName);
            if (p.skills?.skills != null)
            {
                foreach (SkillRecord s in p.skills.skills)
                {
                    if (s?.def == null) continue;
                    c.skills[s.def.defName] = new SkillFacts { levelBase = s.levelInt, aptitude = s.Aptitude, totallyDisabled = s.TotallyDisabled, passion = (int)s.passion };
                }
            }
            c.name = p.Name?.ToStringFull;
            c.gender = p.gender.ToString();
            c.childhood = p.story?.Childhood?.defName;
            c.adulthood = p.story?.Adulthood?.defName;
            c.xenotype = p.genes?.Xenotype?.defName;
            c.bioAgeTicks = p.ageTracker?.AgeBiologicalTicks ?? -1;
            c.genes = p.genes?.GenesListForReading?.Count ?? 0;
            c.hediffs = p.health?.hediffSet?.hediffs?.Count ?? 0;
            return c;
        }

        /// <summary>The allowed correction on the real skill record: raise the base level to the verdict's target, never lower, never passion.</summary>
        public static bool ApplyCorrection(Pawn p, RoleVerdict v)
        {
            if (p?.skills == null || v == null || !v.Correctable) return false;
            SkillDef def = DefDatabase<SkillDef>.GetNamedSilentFail(v.correctSkill);
            SkillRecord s = def == null ? null : p.skills.GetSkill(def);
            if (s == null || s.TotallyDisabled || v.correctBaseTo <= s.levelInt) return false;
            s.levelInt = Mathf.Clamp(v.correctBaseTo, 0, 20);
            return true;
        }
    }

    /// <summary>
    /// The request-time identity pins of ONE candidate kind (PHYSICAL_LIFECYCLE § 6.3): a person's first gender and age, from the pure
    /// <see cref="FirstIdentity"/>, expressed through vanilla's own <c>PawnGenerationRequest</c> inputs (<c>FixedGender</c>,
    /// <c>FixedBiologicalAge</c>, <c>FixedChronologicalAge</c>, audited in the 1.6 assembly). Nothing is applied to a pawn afterwards.
    ///
    /// Why these inputs and these limits (all read from the 1.6 generator, not recalled):
    /// <list type="bullet">
    /// <item>The generator assigns <c>FixedGender</c> before anything else and its validator rejects any other, so a pin that contradicts the
    /// kind's own <c>fixedGender</c> or the race's <c>forceGender</c> would reject every candidate: it is simply not set there, and the kind or race
    /// decides (deterministically).</item>
    /// <item><c>FixedBiologicalAge</c> skips the generator's own age checks, so the window is computed here from the race's life stages and the
    /// kind's generation range (<see cref="AdultAgeWindow"/>), never from human numbers. The generator's validator compares the float EXACTLY, so
    /// only WHOLE years are pinned (exactly representable). No safe window means no pin and vanilla's age stands.</item>
    /// <item><c>FixedChronologicalAge</c> removes the cryptosleep roll (a random extra chronological age drawn from the episode's random stream)
    /// so the first age is not episode-dependent; a kind with its own <c>chronologicalAgeRange</c> would be rejected by the validator, so there
    /// the range stands.</item>
    /// </list>
    /// </summary>
    public sealed class IdentityPins
    {
        public Gender? gender;
        public float? biologicalAge;
        public float? chronologicalAge;
        public int windowLo = -1, windowHi = -1;

        public string Describe()
        {
            return "gender " + (gender.HasValue ? gender.Value.ToString() : "left to the kind or race")
                + ", biological age " + (biologicalAge.HasValue ? biologicalAge.Value.ToString("0") + " (adult window " + windowLo + "-" + windowHi + ")" : "left to vanilla (no safe adult window)")
                + (chronologicalAge.HasValue ? ", chronological age pinned equal" : "");
        }
    }

    public static class PawnIdentityPins
    {
        public static IdentityPins For(PawnKindDef kind, FirstIdentity id)
        {
            IdentityPins pins = new IdentityPins();
            RaceProperties race = kind?.race?.race;
            if (!id.set || race == null) return pins;
            if (race.hasGenders && race.forceGender == Gender.None && !kind.fixedGender.HasValue) pins.gender = id.female ? Gender.Female : Gender.Male;
            List<LifeStageFact> stages = new List<LifeStageFact>();
            if (race.lifeStageAges != null)
            {
                for (int i = 0; i < race.lifeStageAges.Count; i++)
                {
                    LifeStageAge a = race.lifeStageAges[i];
                    if (a == null) continue;
                    stages.Add(new LifeStageFact { minAge = a.minAge, adult = a.def != null && a.def.developmentalStage.Adult() });
                }
            }
            int lo, hi;
            if (AdultAgeWindow.TryFor(stages, race.lifeExpectancy, kind.minGenerationAge, kind.maxGenerationAge, out lo, out hi))
            {
                int years = id.PickYear(lo, hi);
                pins.windowLo = lo;
                pins.windowHi = hi;
                pins.biologicalAge = years;
                if (!kind.chronologicalAgeRange.HasValue) pins.chronologicalAge = years;
            }
            return pins;
        }
    }

    /// <summary>The outcome of one first projection (measured: attempts, corrections, generation time).</summary>
    public sealed class ProjectionResult
    {
        public Pawn pawn;
        public int attempts;
        public int corrections;
        public int rejected;
        public double ms;
        public string kind;
        public string failure;
        public RoleSpec spec;
        public RoleVerdict verdict;

        /// <summary>The identity pins the returned pawn (or the last attempt) was asked for (diagnostics; RT-PHYX-011 compares them with the real pawn).</summary>
        public IdentityPins pins;

        /// <summary>
        /// The GENERIC member pool the kind was drawn from (the encounter faction's own, § 6.2 "kind"): the only kinds a first projection may use,
        /// with their provenance and every refused candidate. RT-PHYX-011 checks the real pawn's kind against it.
        /// </summary>
        public FactionMemberPool pool;

        public override string ToString()
        {
            return (pawn != null ? "pawn #" + pawn.thingIDNumber + " (" + kind + (pool != null ? " from the " + pool.Describe() : "") + ")" : "FAILED (" + failure + ")") + ", " + attempts + " attempt(s), " + rejected + " rejected, "
                + corrections + " correction(s), " + ms.ToString("0.0") + " ms" + (spec != null ? ", role " + spec.Describe() : "") + (pins != null ? ", identity " + pins.Describe() : "");
        }
    }

    /// <summary>
    /// Role-constrained first projection (PHYSICAL_LIFECYCLE § 6.8, ADR-050, S25): request → candidate → AUTHORITATIVE verification →
    /// at most the smallest correction → re-verification → returned (the lifecycle binds it before it is spawned). Bounded: K outer attempts,
    /// each at most vanilla's own 120 internal tries. The request forces a NEW pawn (never a redressed world pawn), generates no relations,
    /// uses only kinds from the encounter faction's GENERIC member pool (<see cref="FactionMemberKinds"/>: never a scan of every loaded kind, never
    /// a fallback to one; no generic member means a clean failure before anything is generated), and never reads fame. A rejected candidate is
    /// unbound, unspawned and unreferenced: it is dropped (RT-PHYX-011 measures residue). No contradicting candidate is ever returned.
    /// </summary>
    public static class PawnProjection
    {
        public const int MaxAttempts = 4;

        public static ProjectionResult Project(ProjectionRequest r, Faction f)
        {
            ProjectionResult res = new ProjectionResult();
            Stopwatch sw = Stopwatch.StartNew();
            RoleSpec spec = RoleRules.SpecFor(r.role, r.capability);
            res.spec = spec;
            FactionMemberPool pool = FactionMemberKinds.For(f?.def, spec.kindClass, r.equipmentTier);
            res.pool = pool;
            List<PawnKindDef> chain = pool.chain;
            if (chain.Count == 0)
            {
                res.failure = "the encounter faction offers no safe generic member kind (" + pool.Describe() + "); no special-purpose kind is borrowed";
                return Done(res, sw);
            }
            List<TraitDef> prohibited = new List<TraitDef>();
            for (int i = 0; i < spec.prohibitedTraits.Count; i++)
            {
                TraitDef t = DefDatabase<TraitDef>.GetNamedSilentFail(spec.prohibitedTraits[i]);
                if (t != null) prohibited.Add(t);
            }
            // An optimization only (vanilla drops validators after 100 tries): the hard clauses, so vanilla re-rolls instead of us.
            Predicate<Pawn> hard = c =>
            {
                RoleVerdict v = RoleRules.Verify(spec, PawnRoleReader.Snapshot(c));
                return v.holds || v.Correctable;
            };
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                PawnKindDef kind = chain[Math.Min(attempt / 2, chain.Count - 1)];
                res.attempts++;
                Pawn c = null;
                // The person's FIRST gender and age (§ 6.3): from PERSON-level facts only, through vanilla's own request inputs. The episode seed
                // below still drives everything the Network never established (traits, backstory, appearance), never these.
                IdentityPins pins = PawnIdentityPins.For(kind, r.identity);
                res.pins = pins;
                Rand.PushState(NetHash.Combine(r.seed, attempt));
                try
                {
                    PawnGenerationRequest req = new PawnGenerationRequest(kind, f, PawnGenerationContext.NonPlayer, null,
                        forceGenerateNewPawn: true, allowDead: false, allowDowned: false, canGeneratePawnRelations: false,
                        mustBeCapableOfViolence: spec.NeedsViolence, colonistRelationChanceFactor: 0f, allowPregnant: false,
                        validatorPreGear: hard, prohibitedTraits: prohibited.Count > 0 ? prohibited : null,
                        fixedBiologicalAge: pins.biologicalAge, fixedChronologicalAge: pins.chronologicalAge, fixedGender: pins.gender,
                        developmentalStages: DevelopmentalStage.Adult);
                    c = PawnGenerator.GeneratePawn(req);
                }
                catch (Exception ex)
                {
                    res.failure = "generation threw (" + kind.defName + "): " + ex.Message;
                    c = null;
                }
                finally
                {
                    Rand.PopState();
                }
                if (c == null)
                {
                    if (res.failure == null) res.failure = "vanilla returned no pawn (" + kind.defName + ")";
                    continue;
                }
                RoleVerdict verdict = RoleRules.Verify(spec, PawnRoleReader.Snapshot(c));
                if (!verdict.holds && verdict.Correctable && PawnRoleReader.ApplyCorrection(c, verdict))
                {
                    res.corrections++;
                    verdict = RoleRules.Verify(spec, PawnRoleReader.Snapshot(c));
                }
                res.verdict = verdict;
                if (verdict.holds)
                {
                    res.pawn = c;
                    res.kind = kind.defName;
                    res.failure = null;
                    return Done(res, sw);
                }
                // Rejected: unbound, unspawned, unreferenced. Dropped; never "fixed" beyond the one allowed raise.
                res.rejected++;
                res.failure = "role " + spec.role + " not satisfied by " + kind.defName + ": " + verdict.failedClause;
            }
            return Done(res, sw);
        }

        private static ProjectionResult Done(ProjectionResult r, Stopwatch sw)
        {
            sw.Stop();
            r.ms = sw.Elapsed.TotalMilliseconds;
            return r;
        }

        /// <summary>
        /// The established name facts (§ 6.3): pinned parts replace the generated ones; anything the Network never stated stays vanilla's.
        /// A race whose pawns are not three-part named keeps a single composed name (logged once, OPEN O-7).
        /// </summary>
        public static void ApplyNamePins(Pawn p, NamePins pins)
        {
            if (p == null || !pins.Any) return;
            NameTriple old = p.Name as NameTriple;
            if (old != null)
            {
                string first = pins.first ?? old.First;
                string nick = pins.nick ?? pins.first ?? old.Nick;
                string last = pins.last ?? old.Last;
                p.Name = new NameTriple(first, nick, last);
                return;
            }
            if (pins.first != null)
            {
                p.Name = new NameSingle(NameSnapshot.Compose(pins.first, pins.nick, pins.last));
                NetLog.WarnOnce(LogCategory.Physical, "name.single." + p.def?.defName, "Race " + p.def?.defName + " is not three-part named: a single name was used (OPEN O-7).");
            }
        }
    }
}
