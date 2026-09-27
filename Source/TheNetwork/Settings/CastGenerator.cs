using System;
using System.Collections.Generic;
using TheNetwork.Domain;
using TheNetwork.Kernel;

namespace TheNetwork.Settings
{
    /// <summary>
    /// Generates the default global cast (DATA_MODEL § 18.1, SIMULATION § 7): about 100 contractor
    /// identities (Solos, duos, crews, teams, companies, specialists; many obscure, a small famous
    /// upper tier) and a small set of Fixers. Bands and style keys only. Deterministic for a given
    /// seed and id factory; ratios are tuning, not contract.
    /// </summary>
    public sealed class CastGenerator
    {
        public const int GeneratorVersion = 1;

        private static readonly string[] Specialties =
        {
            "combat acquisition", "salvage", "recovery", "escort", "medical", "logistics", "demolition",
            "scouting", "vacuum work", "tech retrieval", "animal handling", "infiltration", "extraction"
        };

        private static readonly string[] DoctrineStyles = { "Aggressive", "Cautious", "Professional", "Opportunistic", "Scavenger", "Explorer" };

        private static readonly string[] FixerSpecialties =
        {
            "rare goods", "weapons", "salvage leads", "faction gossip", "archotech rumors", "medical supplies",
            "old ruins", "trader routes", "mechanoid sites", "pirate hideouts"
        };

        /// <summary>A Fixer archetype: fee, speed, reliability, reach, intel style, fame.</summary>
        private struct FixerArchetype
        {
            public Band fee, speed, reliability;
            public ReachBand contractorReach, geographicReach;
            public string intelStyle;
            public FameBand fame;
        }

        private static readonly FixerArchetype[] FixerArchetypes =
        {
            new FixerArchetype { fee = Band.VeryLow, speed = Band.Medium, reliability = Band.Low, contractorReach = ReachBand.Local, geographicReach = ReachBand.Local, intelStyle = "Brisk", fame = FameBand.Local },
            new FixerArchetype { fee = Band.VeryHigh, speed = Band.High, reliability = Band.High, contractorReach = ReachBand.Wide, geographicReach = ReachBand.Wide, intelStyle = "PayPerRound", fame = FameBand.Established },
            new FixerArchetype { fee = Band.Medium, speed = Band.Low, reliability = Band.High, contractorReach = ReachBand.Regional, geographicReach = ReachBand.Regional, intelStyle = "Thorough", fame = FameBand.Unknown },
            new FixerArchetype { fee = Band.Low, speed = Band.VeryHigh, reliability = Band.Low, contractorReach = ReachBand.Local, geographicReach = ReachBand.Regional, intelStyle = "Brisk", fame = FameBand.Local },
            new FixerArchetype { fee = Band.Medium, speed = Band.Medium, reliability = Band.Medium, contractorReach = ReachBand.Regional, geographicReach = ReachBand.Regional, intelStyle = "Discount", fame = FameBand.Local },
            new FixerArchetype { fee = Band.High, speed = Band.Medium, reliability = Band.VeryHigh, contractorReach = ReachBand.Vast, geographicReach = ReachBand.Vast, intelStyle = "Patient", fame = FameBand.Famous },
            new FixerArchetype { fee = Band.Low, speed = Band.Low, reliability = Band.Medium, contractorReach = ReachBand.Minimal, geographicReach = ReachBand.Local, intelStyle = "Patient", fame = FameBand.Unknown },
            new FixerArchetype { fee = Band.Medium, speed = Band.High, reliability = Band.VeryLow, contractorReach = ReachBand.Regional, geographicReach = ReachBand.Wide, intelStyle = "Discount", fame = FameBand.Unknown },
        };

        private readonly NamePools pools;
        private readonly int castSeed;
        private readonly Func<string> newId;
        private readonly HashSet<string> usedNames;

        public CastGenerator(NamePools pools, int castSeed, Func<string> newId, IEnumerable<string> namesToAvoid)
        {
            this.pools = pools != null && pools.IsUsable ? pools : NamePools.Fallback();
            this.castSeed = castSeed;
            this.newId = newId ?? GlobalNetworkRoster.NewTemplateId;
            usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (namesToAvoid != null)
            {
                foreach (string n in namesToAvoid)
                {
                    if (!string.IsNullOrEmpty(n)) usedNames.Add(n);
                }
            }
        }

        public List<ContractorTemplate> GenerateContractors(int count)
        {
            List<ContractorTemplate> list = new List<ContractorTemplate>(count);
            for (int i = 0; i < count; i++) list.Add(GenerateContractor(i));
            return list;
        }

        public List<FixerTemplate> GenerateFixers(int count)
        {
            List<FixerTemplate> list = new List<FixerTemplate>(count);
            for (int i = 0; i < count; i++) list.Add(GenerateFixer(i));
            return list;
        }

        public ContractorTemplate GenerateContractor(int index)
        {
            int entrySeed = NetHash.Combine(NetHash.Combine(castSeed, "cast.contractor"), index);
            NetRng rng = new NetRng(entrySeed, "cast.contractor");
            ContractorTemplate t = new ContractorTemplate
            {
                templateId = newId(),
                provenance = TemplateProvenance.Generated,
                enabled = true
            };
            t.form = (ContractorForm)rng.WeightedIndex(new[] { 30f, 10f, 20f, 15f, 15f, 10f });
            t.startingExperience = (ExperienceBand)rng.WeightedIndex(new[] { 25f, 30f, 22f, 13f, 7f, 3f });

            // Fame is a separate axis: a mild pull toward experience, never a copy of it.
            float[] fameWeights = { 42f, 30f, 17f, 8f, 3f };
            int expTier = (int)t.startingExperience;
            if (expTier >= 3) { fameWeights[0] -= 12f; fameWeights[2] += 6f; fameWeights[3] += 4f; fameWeights[4] += 2f; }
            else if (expTier == 0) { fameWeights[0] += 10f; fameWeights[3] -= 4f; fameWeights[4] -= 2f; }
            t.startingFame = (FameBand)rng.WeightedIndex(fameWeights);

            int specialtyCount = rng.RangeInclusive(1, t.form == ContractorForm.Specialist ? 1 : 3);
            t.specialties = PickDistinct(rng, Specialties, specialtyCount);
            t.doctrineStyle = rng.Pick(DoctrineStyles);

            // Mobility is capability, independent of fame, experience and size.
            t.mobility = new List<string> { "Ground" };
            if (rng.Chance(0.30f)) t.mobility.Add("LongRange");
            if (rng.Chance(0.15f)) t.mobility.Add("RapidTransport");
            if (rng.Chance(t.form == ContractorForm.Company ? 0.3f : 0.08f)) t.mobility.Add("HeavyLift");
            if (rng.Chance(0.03f)) t.mobility.Add("Orbital");

            // Chosen once from archetype odds and stored; never re-derived later.
            float issueOdds;
            switch (t.form)
            {
                case ContractorForm.Solo: issueOdds = 0.10f; break;
                case ContractorForm.Duo: issueOdds = 0.20f; break;
                case ContractorForm.Crew: issueOdds = 0.45f; break;
                case ContractorForm.Team: issueOdds = 0.60f; break;
                case ContractorForm.Company: issueOdds = 0.85f; break;
                default: issueOdds = 0.30f; break;
            }
            t.canIssueWork = rng.Chance(issueOdds);

            List<string> parts;
            bool person = t.form == ContractorForm.Solo || t.form == ContractorForm.Specialist;
            string name = person ? PersonName(rng, out parts, out t.nickname) : OrgName(rng, t.form, out parts);
            t.displayName = name;
            t.generation = new TemplateGeneration { generatorVersion = GeneratorVersion, seed = entrySeed, nameParts = parts };
            return t;
        }

        public FixerTemplate GenerateFixer(int index)
        {
            int entrySeed = NetHash.Combine(NetHash.Combine(castSeed, "cast.fixer"), index);
            NetRng rng = new NetRng(entrySeed, "cast.fixer");
            FixerArchetype a = FixerArchetypes[index % FixerArchetypes.Length];
            FixerTemplate t = new FixerTemplate
            {
                templateId = newId(),
                provenance = TemplateProvenance.Generated,
                enabled = true,
                feeBand = a.fee,
                speedBand = a.speed,
                reliabilityBand = a.reliability,
                contractorReach = a.contractorReach,
                geographicReach = a.geographicReach,
                intelStyle = a.intelStyle,
                startingFame = a.fame,
                brokerageStyle = rng.Pick(new[] { "Lean", "Standard", "Premium" }),
                insuranceStyle = rng.Pick(new[] { "None", "Basic", "Generous" })
            };
            t.specialties = PickDistinct(rng, FixerSpecialties, rng.RangeInclusive(1, 2));
            List<string> parts;
            t.displayName = PersonName(rng, out parts, out t.nickname, forceNick: true);
            t.generation = new TemplateGeneration { generatorVersion = GeneratorVersion, seed = entrySeed, nameParts = parts };
            return t;
        }

        /// <summary>A person's name from the pools (used for world-generated contractors' Known Characters).</summary>
        public string GeneratePersonName(NetRng rng, out string nickname)
        {
            List<string> parts;
            return PersonName(rng, out parts, out nickname);
        }

        private string OrgName(NetRng rng, ContractorForm form, out List<string> parts)
        {
            parts = new List<string>();
            for (int attempt = 0; attempt < 30; attempt++)
            {
                string a = rng.Pick(pools.orgWordsA);
                string b = rng.Pick(pools.orgWordsB);
                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) continue;
                string name = a + " " + b;
                string suffix = null;
                bool wantsSuffix = (form == ContractorForm.Company || form == ContractorForm.Team) ? rng.Chance(0.45f) : rng.Chance(0.15f);
                if (wantsSuffix && pools.orgSuffixes.Count > 0)
                {
                    suffix = rng.Pick(pools.orgSuffixes);
                    if (!string.Equals(suffix, b, StringComparison.OrdinalIgnoreCase)) name += " " + suffix;
                    else suffix = null;
                }
                if (usedNames.Add(name))
                {
                    parts.Add(a);
                    parts.Add(b);
                    if (suffix != null) parts.Add(suffix);
                    return name;
                }
            }
            return Uniquify(rng.Pick(pools.orgWordsA) + " " + rng.Pick(pools.orgWordsB), parts);
        }

        private string PersonName(NetRng rng, out List<string> parts, out string nick, bool forceNick = false)
        {
            parts = new List<string>();
            nick = null;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                string first = rng.Pick(pools.firstNames);
                string last = rng.Pick(pools.lastNames);
                string n = null;
                if (pools.nicknames.Count > 0 && (forceNick || rng.Chance(0.4f))) n = rng.Pick(pools.nicknames);
                string display = first + " " + last;
                if (usedNames.Add(display))
                {
                    parts.Add(first);
                    parts.Add(last);
                    if (n != null) parts.Add(n);
                    nick = n;
                    return display;
                }
            }
            return Uniquify(rng.Pick(pools.firstNames) + " " + rng.Pick(pools.lastNames), parts);
        }

        private string Uniquify(string baseName, List<string> parts)
        {
            for (int i = 2; i < 1000; i++)
            {
                string candidate = baseName + " " + Roman(i);
                if (usedNames.Add(candidate))
                {
                    parts.Add(candidate);
                    return candidate;
                }
            }
            parts.Add(baseName);
            return baseName;
        }

        private static string Roman(int n)
        {
            string[] r = { "", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X" };
            return n < r.Length ? r[n] : n.ToString();
        }

        private static List<string> PickDistinct(NetRng rng, string[] pool, int count)
        {
            List<string> copy = new List<string>(pool);
            List<string> result = new List<string>();
            for (int i = 0; i < count && copy.Count > 0; i++)
            {
                int k = rng.Range(0, copy.Count);
                result.Add(copy[k]);
                copy.RemoveAt(k);
            }
            return result;
        }

        /// <summary>
        /// Regenerates the Generated part of a roster (DATA_MODEL § 18.3): new entries with new ids,
        /// Custom entries and quarantined data kept, custom names avoided where practical.
        /// </summary>
        public static void RegenerateGenerated(GlobalNetworkRoster roster, NamePools pools, int castSeed, int contractorCount, int fixerCount, Func<string> newId, string timestamp)
        {
            List<string> keep = new List<string>();
            for (int i = 0; i < roster.contractorTemplates.Count; i++)
            {
                ContractorTemplate t = roster.contractorTemplates[i];
                if (t.provenance == TemplateProvenance.Custom || t.quarantined != null) keep.Add(t.displayName);
            }
            for (int i = 0; i < roster.fixerTemplates.Count; i++)
            {
                FixerTemplate t = roster.fixerTemplates[i];
                if (t.provenance == TemplateProvenance.Custom || t.quarantined != null) keep.Add(t.displayName);
            }
            CastGenerator gen = new CastGenerator(pools, castSeed, newId, keep);
            roster.ReplaceGenerated(gen.GenerateContractors(contractorCount), gen.GenerateFixers(fixerCount),
                new RosterGeneration { generatorVersion = GeneratorVersion, castSeed = castSeed, lastGeneratedAt = timestamp });
        }
    }
}
