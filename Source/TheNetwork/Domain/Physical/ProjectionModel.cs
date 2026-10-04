using System;
using System.Collections.Generic;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Kernel;
using TheNetwork.Persist;

namespace TheNetwork.Domain.Physical
{
    // Role-constrained first projection (PHYSICAL_LIFECYCLE § 6.5–6.8, ADR-050). Everything in this file is PURE: it reads plain values
    // and never a RimWorld object, so the role verdict, the smallest correction, the role derivation, the name pins, the kind ranking and
    // the request builder are proven headlessly (RT-PHYS-020…022, 030). The real adapter (Integration/Physical) only snapshots a candidate
    // pawn into a RoleCandidate, asks these functions, and applies the one correction they allow.

    /// <summary>Which existing PawnKindDefs suit a role (kind selection only; the role verdict is the authority, § 6.8).</summary>
    public enum RoleClass : byte
    {
        Generalist = 0,
        Ranged = 1,
        Melee = 2,
        Heavy = 3,
        Medical = 4,
        Technical = 5,
        Social = 6,
        Light = 7
    }

    /// <summary>
    /// What a role REQUIRES of a first projection (§ 6.6.3): capabilities expressed in vanilla vocabulary (work-tag names, skill def
    /// names, trait def names), never mod content. Only the role-defining skills carry a floor; everything else stays vanilla-random.
    /// </summary>
    public sealed class RoleSpec
    {
        public OperationalRole role;

        /// <summary>WorkTags names that must not be disabled (for example "Violent", "Caring", "Social").</summary>
        public List<string> requiredWorkTags = new List<string>();

        /// <summary>Skill def names that must not be totally disabled.</summary>
        public List<string> requiredSkills = new List<string>();

        /// <summary>Trait def names a candidate may not have (a Marksman is never a Brawler: vanilla refuses Brawlers ranged weapons).</summary>
        public List<string> prohibitedTraits = new List<string>();

        /// <summary>The role-defining skills: the best of them must reach <see cref="floor"/> (effective level, aptitudes included).</summary>
        public List<string> floorSkills = new List<string>();

        public int floor;
        public RoleClass kindClass;

        public bool NeedsViolence => requiredWorkTags.Contains("Violent");

        public string Describe()
        {
            return role + " (floor " + floor + (floorSkills.Count > 0 ? " in " + string.Join("/", floorSkills.ToArray()) : "") + (requiredWorkTags.Count > 0 ? "; needs " + string.Join(",", requiredWorkTags.ToArray()) : "")
                + (prohibitedTraits.Count > 0 ? "; never " + string.Join(",", prohibitedTraits.ToArray()) : "") + ")";
        }
    }

    /// <summary>One skill of a candidate, as plain data. The effective level is base + aptitude clamped to 0–20 (vanilla's GetLevel).</summary>
    public sealed class SkillFacts
    {
        public int levelBase;
        public int aptitude;
        public bool totallyDisabled;

        /// <summary>The passion, as its vanilla enum value. Read for the invariance proof only: NEVER changed (§ 6.6.3).</summary>
        public int passion;

        public int Effective => totallyDisabled ? 0 : Math.Max(0, Math.Min(20, levelBase + aptitude));

        public SkillFacts Copy()
        {
            return new SkillFacts { levelBase = levelBase, aptitude = aptitude, totallyDisabled = totallyDisabled, passion = passion };
        }
    }

    /// <summary>
    /// A read-only snapshot of an unbound, unspawned candidate pawn: what the role verdict reads, plus the identity facts the invariance
    /// proof compares (a correction may change ONE skill's base level, upward, and nothing else).
    /// </summary>
    public sealed class RoleCandidate
    {
        public HashSet<string> disabledWorkTags = new HashSet<string>();
        public List<string> traits = new List<string>();
        public Dictionary<string, SkillFacts> skills = new Dictionary<string, SkillFacts>();

        // identity facts: never touched by a correction
        public string name;
        public string gender;
        public string childhood;
        public string adulthood;
        public string xenotype;
        public long bioAgeTicks;
        public int genes;
        public int hediffs;

        public SkillFacts Skill(string def)
        {
            SkillFacts s;
            return def != null && skills.TryGetValue(def, out s) ? s : null;
        }

        public RoleCandidate Copy()
        {
            RoleCandidate c = new RoleCandidate
            {
                name = name, gender = gender, childhood = childhood, adulthood = adulthood, xenotype = xenotype, bioAgeTicks = bioAgeTicks, genes = genes, hediffs = hediffs
            };
            c.disabledWorkTags.UnionWith(disabledWorkTags);
            c.traits.AddRange(traits);
            foreach (KeyValuePair<string, SkillFacts> kv in skills) c.skills[kv.Key] = kv.Value.Copy();
            return c;
        }

        /// <summary>A canonical description of everything but the base skill levels (the invariance proof's fingerprint).</summary>
        public string IdentityKey()
        {
            List<string> tags = new List<string>(disabledWorkTags);
            tags.Sort(StringComparer.Ordinal);
            List<string> tr = new List<string>(traits);
            tr.Sort(StringComparer.Ordinal);
            List<string> sk = new List<string>();
            foreach (KeyValuePair<string, SkillFacts> kv in skills) sk.Add(kv.Key + ":apt" + kv.Value.aptitude + ":dis" + kv.Value.totallyDisabled + ":pas" + kv.Value.passion);
            sk.Sort(StringComparer.Ordinal);
            return name + "|" + gender + "|" + childhood + "|" + adulthood + "|" + xenotype + "|" + bioAgeTicks + "|" + genes + "|" + hediffs + "|"
                + string.Join(",", tags.ToArray()) + "|" + string.Join(",", tr.ToArray()) + "|" + string.Join(",", sk.ToArray());
        }
    }

    /// <summary>The authoritative verdict on one candidate (§ 6.8): it holds, it is rejected (with the clause), or one skill may be raised.</summary>
    public sealed class RoleVerdict
    {
        public bool holds;
        public string failedClause;

        /// <summary>When only a role-defining skill is below its floor: the skill to raise and the base level to raise it TO.</summary>
        public string correctSkill;
        public int correctBaseTo = -1;

        public bool Correctable => !holds && correctSkill != null;

        public static readonly RoleVerdict Holds = new RoleVerdict { holds = true };

        public static RoleVerdict Reject(string clause)
        {
            return new RoleVerdict { failedClause = clause };
        }

        public override string ToString()
        {
            return holds ? "holds" : Correctable ? "correctable: raise " + correctSkill + " base to " + correctBaseTo : "rejected: " + failedClause;
        }
    }

    /// <summary>
    /// The role rules (§ 6.6.3, § 6.8). Illustrative tuning (OPEN O-8) with a frozen shape: hard capabilities are rejected, never "fixed";
    /// the ONLY allowed correction raises the base level of ONE role-defining skill just enough that its effective level (base plus
    /// aptitudes) reaches the floor. Passion, traits, backstory, genes, incapabilities, hediffs, age, gender, name and relations are never
    /// changed; no skill is ever lowered.
    /// </summary>
    public static class RoleRules
    {
        /// <summary>The capability band → role-skill floor (O-8, example values): Green none · Experienced 4 · Seasoned 7 · Veteran 10 · Elite 13 · Legendary 16.</summary>
        public static int FloorFor(ExperienceBand band)
        {
            switch (band)
            {
                case ExperienceBand.Experienced: return 4;
                case ExperienceBand.Seasoned: return 7;
                case ExperienceBand.Veteran: return 10;
                case ExperienceBand.Elite: return 13;
                case ExperienceBand.Legendary: return 16;
                default: return 0;
            }
        }

        public static RoleSpec SpecFor(OperationalRole role, ExperienceBand capability)
        {
            int floor = FloorFor(capability);
            RoleSpec s = new RoleSpec { role = role, floor = floor, kindClass = RoleClass.Generalist };
            switch (role)
            {
                case OperationalRole.Leader:
                    s.requiredWorkTags.Add("Social");
                    s.floorSkills.Add("Social");
                    s.kindClass = RoleClass.Social;
                    break;
                case OperationalRole.Marksman:
                    s.requiredWorkTags.Add("Violent");
                    s.requiredSkills.Add("Shooting");
                    s.prohibitedTraits.Add("Brawler");
                    s.floorSkills.Add("Shooting");
                    s.kindClass = RoleClass.Ranged;
                    break;
                case OperationalRole.Rifleman:
                    s.requiredWorkTags.Add("Violent");
                    s.requiredSkills.Add("Shooting");
                    s.floorSkills.Add("Shooting");
                    s.floor = Math.Max(0, floor - 3);
                    s.kindClass = RoleClass.Ranged;
                    break;
                case OperationalRole.Heavy:
                    s.requiredWorkTags.Add("Violent");
                    s.floorSkills.Add("Shooting");
                    s.floorSkills.Add("Melee");
                    s.kindClass = RoleClass.Heavy;
                    break;
                case OperationalRole.Breacher:
                    s.requiredWorkTags.Add("Violent");
                    s.floorSkills.Add("Melee");
                    s.floorSkills.Add("Shooting");
                    s.kindClass = RoleClass.Melee;
                    break;
                case OperationalRole.Medic:
                    s.requiredWorkTags.Add("Caring");
                    s.requiredSkills.Add("Medicine");
                    s.floorSkills.Add("Medicine");
                    s.kindClass = RoleClass.Medical;
                    break;
                case OperationalRole.Scout:
                    s.floor = 0;
                    s.kindClass = RoleClass.Light;
                    break;
                case OperationalRole.Engineer:
                    s.requiredWorkTags.Add("Constructing");
                    s.requiredSkills.Add("Construction");
                    s.floorSkills.Add("Construction");
                    s.kindClass = RoleClass.Technical;
                    break;
                case OperationalRole.Technician:
                    s.requiredWorkTags.Add("Crafting");
                    s.requiredSkills.Add("Crafting");
                    s.floorSkills.Add("Crafting");
                    s.kindClass = RoleClass.Technical;
                    break;
                case OperationalRole.Logistician:
                    s.requiredWorkTags.Add("Hauling");
                    s.floor = 0;
                    s.kindClass = RoleClass.Light;
                    break;
                case OperationalRole.Negotiator:
                    s.requiredWorkTags.Add("Social");
                    s.requiredSkills.Add("Social");
                    s.floorSkills.Add("Social");
                    s.kindClass = RoleClass.Social;
                    break;
                default:
                    // Specialist / Unset: nothing to contradict yet.
                    s.floor = 0;
                    break;
            }
            if (s.floorSkills.Count == 0) s.floor = 0;
            return s;
        }

        /// <summary>The AUTHORITATIVE verdict over a candidate snapshot. Pure: reads only, decides once.</summary>
        public static RoleVerdict Verify(RoleSpec spec, RoleCandidate c)
        {
            if (spec == null) return RoleVerdict.Reject("no role spec");
            if (c == null) return RoleVerdict.Reject("no candidate");
            for (int i = 0; i < spec.requiredWorkTags.Count; i++)
            {
                if (c.disabledWorkTags.Contains(spec.requiredWorkTags[i])) return RoleVerdict.Reject("work tag " + spec.requiredWorkTags[i] + " is disabled (an incapability is identity: rejected, never corrected)");
            }
            for (int i = 0; i < spec.requiredSkills.Count; i++)
            {
                SkillFacts s = c.Skill(spec.requiredSkills[i]);
                if (s == null) return RoleVerdict.Reject("skill " + spec.requiredSkills[i] + " is missing");
                if (s.totallyDisabled) return RoleVerdict.Reject("skill " + spec.requiredSkills[i] + " is totally disabled");
            }
            for (int i = 0; i < spec.prohibitedTraits.Count; i++)
            {
                if (c.traits.Contains(spec.prohibitedTraits[i])) return RoleVerdict.Reject("trait " + spec.prohibitedTraits[i] + " contradicts the role");
            }
            if (spec.floor <= 0 || spec.floorSkills.Count == 0) return RoleVerdict.Holds;
            // The best usable role-defining skill decides; the smallest raise is preferred, then the order of floorSkills.
            string best = null;
            int bestEffective = -1;
            for (int i = 0; i < spec.floorSkills.Count; i++)
            {
                SkillFacts s = c.Skill(spec.floorSkills[i]);
                if (s == null || s.totallyDisabled) continue;
                if (s.Effective > bestEffective)
                {
                    best = spec.floorSkills[i];
                    bestEffective = s.Effective;
                }
            }
            if (best == null) return RoleVerdict.Reject("no usable role-defining skill (" + string.Join("/", spec.floorSkills.ToArray()) + ")");
            if (bestEffective >= spec.floor) return RoleVerdict.Holds;
            SkillFacts f = c.Skill(best);
            int baseTo = spec.floor - f.aptitude;
            if (baseTo > 20) return RoleVerdict.Reject(best + " cannot reach " + spec.floor + " (aptitude " + f.aptitude + ")");
            if (baseTo <= f.levelBase) return RoleVerdict.Reject(best + " is capped below the floor by vanilla's 0–20 clamp");
            return new RoleVerdict { correctSkill = best, correctBaseTo = baseTo, failedClause = best + " " + bestEffective + " < floor " + spec.floor };
        }

        /// <summary>The ONE allowed correction, over the snapshot (the adapter applies the same raise to the real SkillRecord). Raise only.</summary>
        public static bool ApplyCorrection(RoleCandidate c, RoleVerdict v)
        {
            if (c == null || v == null || !v.Correctable) return false;
            SkillFacts s = c.Skill(v.correctSkill);
            if (s == null || s.totallyDisabled || v.correctBaseTo <= s.levelBase || v.correctBaseTo > 20) return false;
            s.levelBase = v.correctBaseTo;
            return true;
        }
    }

    /// <summary>
    /// A Solo's operational role from IMMUTABLE ORIGIN FACTS only (§ 6.6.5, P3-INV-030): the actor's seed (assigned once at Instantiate)
    /// and its original specialties (written once at Instantiate). Pure and versioned: version 1 never changes its output for the same
    /// inputs; a better function would be a new version for actors created after it. It reads no experience, doctrine, fame, funds,
    /// morale, career or status, so the answer is identical whether it is first needed in year 1 or year 10.
    /// </summary>
    public static class RoleDerivation
    {
        public const int Version = 1;

        private static readonly Dictionary<string, OperationalRole[]> BySpecialty = new Dictionary<string, OperationalRole[]>(StringComparer.Ordinal)
        {
            { "combat acquisition", new[] { OperationalRole.Rifleman, OperationalRole.Marksman, OperationalRole.Heavy } },
            { "escort", new[] { OperationalRole.Rifleman, OperationalRole.Heavy } },
            { "medical", new[] { OperationalRole.Medic } },
            { "logistics", new[] { OperationalRole.Logistician } },
            { "demolition", new[] { OperationalRole.Breacher } },
            { "scouting", new[] { OperationalRole.Scout } },
            { "vacuum work", new[] { OperationalRole.Engineer } },
            { "salvage", new[] { OperationalRole.Technician } },
            { "recovery", new[] { OperationalRole.Scout } },
            { "tech retrieval", new[] { OperationalRole.Technician } },
            { "animal handling", new[] { OperationalRole.Specialist } },
            { "infiltration", new[] { OperationalRole.Scout } },
            { "extraction", new[] { OperationalRole.Breacher } }
        };

        /// <summary>The role candidates of a specialty list, in stored order, distinct. Unknown specialties (custom templates) add Specialist.</summary>
        public static List<OperationalRole> Candidates(IList<string> specialties)
        {
            List<OperationalRole> r = new List<OperationalRole>();
            if (specialties != null)
            {
                for (int i = 0; i < specialties.Count; i++)
                {
                    string key = (specialties[i] ?? "").Trim().ToLowerInvariant();
                    OperationalRole[] roles;
                    if (!BySpecialty.TryGetValue(key, out roles)) roles = new[] { OperationalRole.Specialist };
                    for (int k = 0; k < roles.Length; k++) if (!r.Contains(roles[k])) r.Add(roles[k]);
                }
            }
            if (r.Count == 0) r.Add(OperationalRole.Specialist);
            return r;
        }

        /// <summary>Version 1: one deterministic pick among the candidates by the actor's own seed.</summary>
        public static OperationalRole SoloRole(int actorSeed, IList<string> specialties)
        {
            List<OperationalRole> c = Candidates(specialties);
            uint h = unchecked((uint)NetHash.Combine(actorSeed, "oprole.v" + Version));
            return c[(int)(h % (uint)c.Count)];
        }

        /// <summary>
        /// The role of an individual's embodied person from the actor's stored ORIGIN facts (its seed and the specialties it was created with).
        /// Total for every individual that is not an organization: a contractor reads its <see cref="ContractorProfile"/>, a Fixer (also an
        /// individual with an embodied person, and what <c>ContractorService.IsSolo</c> counts as one) its <see cref="FixerProfile"/>, and an
        /// individual with no specialty on record the documented fallback (Specialist). It is never Unset for an embodied individual, so a
        /// materialization can never reach a pawn with the role unknown. Null-safe; Unset only for an actor that is not an individual.
        /// </summary>
        public static OperationalRole ForSolo(NetworkActor a)
        {
            if (a == null || a.kind != ActorKind.Individual || a.Has<OrganizationProfile>()) return OperationalRole.Unset;
            ContractorProfile p = a.Get<ContractorProfile>();
            if (p != null) return SoloRole(a.seed, p.specialties);
            FixerProfile f = a.Get<FixerProfile>();
            return SoloRole(a.seed, f?.specialties);
        }
    }

    /// <summary>The established name facts of a person, mapped onto a three-part pawn name (§ 6.3). Null parts stay vanilla-random.</summary>
    public struct NamePins
    {
        public string first;
        public string nick;
        public string last;

        public bool Any => first != null || last != null || nick != null;

        /// <summary>
        /// first/nick/last when the record stores them; otherwise the display is split at its first space (a generated Solo's display is
        /// "First Last"). A one-word display pins the first name only. The nickname is pinned only when the record has one.
        /// </summary>
        public static NamePins From(NameSnapshot n)
        {
            NamePins p = new NamePins();
            if (n == null) return p;
            p.nick = Clean(n.nick);
            p.first = Clean(n.first);
            p.last = Clean(n.last);
            if (p.first == null && p.last == null)
            {
                string d = Clean(n.display);
                if (d != null)
                {
                    int space = d.IndexOf(' ');
                    if (space > 0)
                    {
                        p.first = Clean(d.Substring(0, space));
                        p.last = Clean(d.Substring(space + 1));
                    }
                    else
                    {
                        p.first = d;
                    }
                }
            }
            return p;
        }

        private static string Clean(string s)
        {
            if (s == null) return null;
            s = s.Trim();
            return s.Length == 0 ? null : s;
        }

        public override string ToString()
        {
            return (first ?? "?") + (nick != null ? " '" + nick + "'" : "") + (last != null ? " " + last : "");
        }
    }

    /// <summary>
    /// One candidate member kind OFFERED BY THE ENCOUNTER FACTION, as plain facts (gathered by the adapter from the faction's own def; mods
    /// included, none named). Every flag is read from a vanilla PawnKindDef field: nothing here knows a def name.
    /// </summary>
    public sealed class MemberKindFacts
    {
        public string defName;

        /// <summary>Where the faction offers it: "basic member kind", or "<group kind> group".</summary>
        public string source;

        /// <summary>It IS the faction's basic member kind (the faction's own statement of its generic member).</summary>
        public bool basic;

        public bool humanlike;
        public bool toolUser;

        /// <summary>The kind belongs to no OTHER faction: its default faction is this faction or none.</summary>
        public bool native;

        // Special-purpose content: a kind carrying any of these is somebody's quest, boss, title, cult, cryptosleeper or vampire, not a person.
        public bool playerKind;
        public bool factionLeader;
        public bool boss;
        public bool mutant;
        public bool titled;
        public bool trader;
        public bool hostileToAll;
        public bool fixedBackstory;
        public bool builtInConditions;
        public bool builtInAbilities;
        public bool forcedTraits;
        public bool forcedXenotype;

        public bool fighter;
        public bool ranged;
        public bool melee;
        public float combatPower;
    }

    /// <summary>
    /// First-projection PawnKind eligibility (Phase 3.1 runtime-QA correction, § 6.2 "kind", O-3). The kind of a person's FIRST pawn is drawn
    /// ONLY from the temporary encounter faction's own GENERIC member pool: its basic member kind first (the faction's own statement of
    /// "an ordinary member"), then the kinds its Combat and Peaceful groups list, each of which must pass <see cref="Reject"/>. There is no
    /// global scan of loaded kinds and no fallback to one: when the faction offers no generic member the projection fails cleanly, before
    /// anything is bound. Role strength is the ROLE's job (the work-tag, skill and trait clauses of <see cref="RoleRules"/> and its one allowed
    /// skill raise), never a reason to borrow a boss, royal, cultist or ancient kind. The rule is structural (a kind carrying special-purpose
    /// content is not generic), never a list of def names, so a modded race or faction is judged by what its kinds ARE.
    ///
    /// This governs FIRST realization only. A pawn that already exists keeps its kind for life (and whatever the game later does to it).
    /// </summary>
    public static class GenericMemberPolicy
    {
        public const int MaxChain = 3;

        public static float TargetPower(int equipmentTier)
        {
            return 40f + 25f * Math.Max(1, Math.Min(5, equipmentTier));
        }

        /// <summary>Null when the kind is a generic member; otherwise the plain reason it is not.</summary>
        public static string Reject(MemberKindFacts k)
        {
            if (k == null || string.IsNullOrEmpty(k.defName)) return "no kind";
            if (!k.humanlike || !k.toolUser) return "not a humanlike tool user";
            if (!k.native) return "belongs to another faction";
            if (k.playerKind) return "a player-faction kind";
            if (k.factionLeader) return "a faction leader kind";
            if (k.boss) return "a boss kind";
            if (k.mutant) return "a mutant kind";
            if (k.titled) return "requires a royal title";
            if (k.trader) return "a trader kind";
            if (k.hostileToAll) return "hostile to everything";
            if (k.fixedBackstory) return "carries fixed backstories";
            if (k.builtInConditions) return "carries built-in conditions (hediffs, missing parts or addictions)";
            if (k.builtInAbilities) return "carries built-in abilities";
            if (k.forcedTraits) return "carries forced traits";
            if (k.forcedXenotype) return "forces a non-baseline xenotype";
            return null;
        }

        /// <summary>How well a kind's combat bias suits a role class and equipment tier. Only orders members of the SAME generic pool.</summary>
        public static float Score(MemberKindFacts k, RoleClass cls, int equipmentTier)
        {
            float s = 0f;
            switch (cls)
            {
                case RoleClass.Ranged: s += k.fighter && k.ranged ? 3f : k.fighter ? 1f : 0f; break;
                case RoleClass.Melee: s += k.fighter && k.melee ? 3f : k.fighter ? 1f : 0f; break;
                case RoleClass.Heavy: s += k.fighter ? 2f + (k.ranged ? 0.5f : 0f) : 0f; break;
                default: s += 1f; break;
            }
            s -= Math.Abs(k.combatPower - TargetPower(equipmentTier)) / 25f;
            return s;
        }

        /// <summary>The generic members among the candidates (deduplicated by def name), in the order the projection tries them. Empty when the faction offers none.</summary>
        public static List<string> Chain(IList<MemberKindFacts> candidates, RoleClass cls, int equipmentTier)
        {
            List<MemberKindFacts> ok = Eligible(candidates);
            List<string> chain = new List<string>();
            for (int i = 0; i < ok.Count; i++) if (ok[i].basic) chain.Add(ok[i].defName);
            List<MemberKindFacts> rest = new List<MemberKindFacts>();
            for (int i = 0; i < ok.Count; i++) if (!ok[i].basic) rest.Add(ok[i]);
            rest.Sort((a, b) =>
            {
                int c = Score(b, cls, equipmentTier).CompareTo(Score(a, cls, equipmentTier));
                return c != 0 ? c : string.CompareOrdinal(a.defName, b.defName);
            });
            for (int i = 0; i < rest.Count && chain.Count < MaxChain; i++) chain.Add(rest[i].defName);
            return chain;
        }

        /// <summary>The kinds the policy admits, deduplicated (a kind offered twice is judged once, as the basic member when it is one).</summary>
        public static List<MemberKindFacts> Eligible(IList<MemberKindFacts> candidates)
        {
            List<MemberKindFacts> ok = new List<MemberKindFacts>();
            HashSet<string> seen = new HashSet<string>();
            if (candidates == null) return ok;
            for (int i = 0; i < candidates.Count; i++)
            {
                MemberKindFacts k = candidates[i];
                if (Reject(k) != null || !seen.Add(k.defName)) continue;
                ok.Add(k);
            }
            return ok;
        }

        /// <summary>"kind: reason" for every candidate the policy refused (diagnostics: why the pool is what it is).</summary>
        public static List<string> Rejections(IList<MemberKindFacts> candidates)
        {
            List<string> r = new List<string>();
            if (candidates == null) return r;
            for (int i = 0; i < candidates.Count; i++)
            {
                string why = Reject(candidates[i]);
                if (why != null && !r.Contains(candidates[i].defName + ": " + why)) r.Add(candidates[i].defName + ": " + why);
            }
            return r;
        }
    }

    /// <summary>
    /// The person-level choices a person's FIRST pawn creation honours (§ 6.3): the intended gender and where in the race's adult range the first
    /// age falls. It is derived from PERSON-level immutable facts only (<see cref="PersonIdentity"/>) and is a pure value: nothing about it is
    /// persisted (the pawn is the truth once it exists), and a later materialization never reads it (it never rewrites a pawn).
    /// </summary>
    public struct FirstIdentity
    {
        /// <summary>Resolution of <see cref="ageUnit"/>: the age's position in the adult range, in 1/65536ths (an integer, so the mapping is exact).</summary>
        public const int Resolution = 65536;

        /// <summary>False for an anonymous slot: the Network states nothing about it.</summary>
        public bool set;

        /// <summary>The intended gender, honoured only where the loaded race and kind leave the gender open.</summary>
        public bool female;

        /// <summary>0 … <see cref="Resolution"/> − 1: the position of the first age within the race's adult window.</summary>
        public int ageUnit;

        /// <summary>The whole year this identity picks in the inclusive range [lo, hi]. Integer arithmetic: identical on every machine.</summary>
        public int PickYear(int lo, int hi)
        {
            if (hi < lo) throw new ArgumentException("an empty age window");
            return lo + (int)(((long)ageUnit * ((long)hi - lo + 1)) / Resolution);
        }

        public override string ToString()
        {
            return set ? (female ? "female" : "male") + ", age position " + ageUnit + "/" + Resolution : "unset";
        }
    }

    /// <summary>
    /// First age and gender from PERSON-level immutable facts only (§ 6.3, P3-INV-018): <c>Hash(networkSeed, CharacterId, "physical.identity.v1")</c>
    /// with a separate salt for each choice. The same person of the same world resolves to the same intended first gender and age whichever
    /// episode happens to materialize them first, on whichever map, in whichever slot, at whatever time, and whatever their fame, reputation,
    /// experience, doctrine or funds: none of those is an input. A better function would be a new version for people created after it; version 1
    /// never changes its output for the same inputs.
    /// </summary>
    public static class PersonIdentity
    {
        public const int Version = 1;
        public const string Salt = "physical.identity.v1";

        public static FirstIdentity For(int networkSeed, CharacterId person)
        {
            if (!person.IsValid) return new FirstIdentity();
            int h = NetHash.Combine(NetHash.Combine(networkSeed, person.Value), Salt);
            uint gender = unchecked((uint)NetHash.Combine(h, "gender"));
            uint age = unchecked((uint)NetHash.Combine(h, "age"));
            return new FirstIdentity { set = true, female = (gender >> 31) == 1u, ageUnit = (int)(age >> 16) };
        }
    }

    /// <summary>One life stage of a race as plain data: its first age and whether vanilla counts it as the Adult developmental stage.</summary>
    public struct LifeStageFact
    {
        public float minAge;
        public bool adult;
    }

    /// <summary>
    /// The sensible adult age window of a loaded race and kind, computed from the DEFS and never from human numbers: from the race's own Adult
    /// life stage (vanilla's <c>AdultMinAge</c>) up to half-way through the rest of its life expectancy, inside the kind's own generation range and
    /// inside the contiguous Adult stages. For vanilla humans that is 18 – 49, the flat part of their own age curve; a modded race scales by its own
    /// numbers. No safe window (no Adult stage, a life expectancy at or below adulthood, an empty intersection) means NO age is pinned: the
    /// narrowest supported behaviour is to leave the age to vanilla, never to guess.
    /// </summary>
    public static class AdultAgeWindow
    {
        public static bool TryFor(IList<LifeStageFact> stages, float lifeExpectancy, int kindMinAge, int kindMaxAge, out int lo, out int hi)
        {
            lo = 0;
            hi = -1;
            if (stages == null || stages.Count == 0) return false;
            List<LifeStageFact> sorted = new List<LifeStageFact>(stages);
            sorted.Sort((a, b) => a.minAge.CompareTo(b.minAge));
            int first = -1;
            for (int i = 0; i < sorted.Count; i++)
            {
                if (sorted[i].adult)
                {
                    first = i;
                    break;
                }
            }
            if (first < 0) return false;
            float adultStart = sorted[first].minAge;
            float adultEnd = float.PositiveInfinity;
            for (int i = first + 1; i < sorted.Count; i++)
            {
                if (!sorted[i].adult)
                {
                    adultEnd = sorted[i].minAge;
                    break;
                }
            }
            if (!(lifeExpectancy > adultStart)) return false;
            double ceiling = adultStart + 0.5 * (lifeExpectancy - adultStart);
            double high = Math.Min(Math.Min(ceiling, kindMaxAge), float.IsPositiveInfinity(adultEnd) ? double.MaxValue : adultEnd - 1e-3);
            double low = Math.Max(adultStart, kindMinAge);
            int l = (int)Math.Ceiling(low - 1e-6);
            int h = (int)Math.Floor(high + 1e-6);
            if (h < l) return false;
            lo = l;
            hi = h;
            return true;
        }
    }

    /// <summary>
    /// Builds a first-materialization request from DURABLE Network truth only (§ 6.5, P3-INV-018/019): the name snapshot, the operational
    /// role, the capability band (current experience: competence within the role), the equipment tier and the person's first identity
    /// (gender and age position, from <see cref="PersonIdentity"/>). Fame, the reputation score and visibility are never read (RT-PHYS-022).
    /// Anything else the Network never established (traits, backstory, appearance) is not part of the request and stays vanilla-random.
    /// </summary>
    public static class ProjectionPolicy
    {
        public static ProjectionRequest ForPerson(PhysicalEpisode e, EpisodeMember m, KnownCharacter c, NetworkActor actor, int networkSeed)
        {
            ContractorSimulation sim = actor?.Get<ContractorSimulation>();
            return new ProjectionRequest
            {
                episode = e.id,
                actor = e.actor,
                character = c.id,
                slot = m.slot,
                tier = m.tier,
                name = c.name,
                role = c.opRole,
                capability = ContractorService.Experience(actor),
                equipmentTier = sim?.equipment?.tier ?? 2,
                seed = NetHash.Combine(e.seed, m.slot),
                faction = e.faction,
                identity = PersonIdentity.For(networkSeed, c.id)
            };
        }

        /// <summary>A canonical key of everything a request asks for (the fame-invariance property test compares it).</summary>
        public static string Key(ProjectionRequest r)
        {
            if (r == null) return "null";
            NamePins p = NamePins.From(r.name);
            return r.episode + "|" + r.actor + "|" + r.character + "|" + r.slot + "|" + r.tier + "|" + p + "|" + r.role + "|" + r.capability + "|" + r.equipmentTier + "|" + r.seed
                + "|" + (r.faction?.loadId ?? -1) + "|" + r.identity;
        }
    }
}
