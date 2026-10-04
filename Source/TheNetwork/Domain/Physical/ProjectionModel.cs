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

        /// <summary>The role of a Solo's embodied person from the actor's stored origin facts (null-safe; Unset if not a contractor).</summary>
        public static OperationalRole ForSolo(NetworkActor a)
        {
            if (a == null) return OperationalRole.Unset;
            ContractorProfile p = a.Get<ContractorProfile>();
            return p == null ? OperationalRole.Unset : SoloRole(a.seed, p.specialties);
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

    /// <summary>What a PawnKindDef can do, as plain data (gathered once by the adapter from the loaded defs; mods included, never named).</summary>
    public sealed class KindFacts
    {
        public string defName;
        public bool humanlike;
        public bool toolUser;
        public bool fighter;
        public bool factionLeader;
        public bool playerKind;
        public bool humanlikeFaction;
        public bool ranged;
        public bool melee;
        public float combatPower;
    }

    /// <summary>
    /// Kind selection by capability (§ 6.2 "kind", OPEN O-3): only EXISTING kinds (a runtime kind would not survive a save), humanlike
    /// tool users of a humanlike non-player faction, never a faction leader kind. Scored by the role's kind class and the closeness of the
    /// kind's combat power to the equipment tier. Pure and deterministic (ties by def name). The role verdict, not the kind, is the authority.
    /// </summary>
    public static class KindPolicy
    {
        public static float TargetPower(int equipmentTier)
        {
            return 40f + 25f * Math.Max(1, Math.Min(5, equipmentTier));
        }

        public static bool Eligible(KindFacts k)
        {
            return k != null && k.humanlike && k.toolUser && !k.factionLeader && !k.playerKind && k.humanlikeFaction && !string.IsNullOrEmpty(k.defName);
        }

        public static float Score(KindFacts k, RoleClass cls, int equipmentTier)
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

        /// <summary>The eligible kinds, best first. Empty when nothing qualifies (the adapter then falls back to a faction or vanilla default).</summary>
        public static List<string> Rank(IList<KindFacts> kinds, RoleClass cls, int equipmentTier)
        {
            List<KindFacts> ok = new List<KindFacts>();
            if (kinds != null) for (int i = 0; i < kinds.Count; i++) if (Eligible(kinds[i])) ok.Add(kinds[i]);
            ok.Sort((a, b) =>
            {
                int c = Score(b, cls, equipmentTier).CompareTo(Score(a, cls, equipmentTier));
                return c != 0 ? c : string.CompareOrdinal(a.defName, b.defName);
            });
            List<string> r = new List<string>();
            for (int i = 0; i < ok.Count; i++) r.Add(ok[i].defName);
            return r;
        }
    }

    /// <summary>
    /// Builds a first-materialization request from DURABLE Network truth only (§ 6.5, P3-INV-018/019): the name snapshot, the operational
    /// role, the capability band (current experience: competence within the role) and the equipment tier. Fame, the reputation score and
    /// visibility are never read (RT-PHYS-022). Anything the Network never established (gender, age, traits, backstory, appearance) is not
    /// part of the request and stays vanilla-random.
    /// </summary>
    public static class ProjectionPolicy
    {
        public static ProjectionRequest ForPerson(PhysicalEpisode e, EpisodeMember m, KnownCharacter c, NetworkActor actor)
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
                faction = e.faction
            };
        }

        /// <summary>A canonical key of everything a request asks for (the fame-invariance property test compares it).</summary>
        public static string Key(ProjectionRequest r)
        {
            if (r == null) return "null";
            NamePins p = NamePins.From(r.name);
            return r.episode + "|" + r.actor + "|" + r.character + "|" + r.slot + "|" + r.tier + "|" + p + "|" + r.role + "|" + r.capability + "|" + r.equipmentTier + "|" + r.seed
                + "|" + (r.faction?.loadId ?? -1);
        }
    }
}
