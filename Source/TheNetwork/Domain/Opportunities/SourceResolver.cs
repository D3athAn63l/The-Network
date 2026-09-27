using System.Collections.Generic;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;

namespace TheNetwork.Domain.Opportunities
{
    public sealed class SourceCandidate
    {
        public SourceKind kind;
        public FactionFacts holder;
        public float weight;
        public string threatProfileKey;
        public List<string> evidence = new List<string>();

        public override string ToString()
        {
            return kind + (holder != null ? " [" + holder.name + "]" : "") + " w=" + weight.ToString("0.00") + " " + threatProfileKey + " {" + string.Join(",", evidence.ToArray()) + "}";
        }
    }

    public sealed class SourceDecision
    {
        public SourceKind kind = SourceKind.NoCredibleSource;
        public FactionFacts holder;
        public Stance holderStance = Stance.None;
        public string threatProfileKey = ThreatProfiles.None;
        public List<string> evidence = new List<string>();

        /// <summary>Every candidate considered, with its weight (for the dev "explain" action).</summary>
        public List<SourceCandidate> candidates = new List<SourceCandidate>();

        /// <summary>Filter notes: factions skipped and why (for the dev "explain" action).</summary>
        public List<string> skipped = new List<string>();

        public bool IsNoCredibleSource => kind == SourceKind.NoCredibleSource;
    }

    /// <summary>
    /// The simplified Phase 1 source resolver (ARCHITECTURE § 6.14.1). Pure: plain facts in, a
    /// decision out. The rules it keeps:
    /// <list type="bullet">
    /// <item>The item's source package is contextual evidence, never ownership. Nothing names a
    /// specific item, faction or mod.</item>
    /// <item>The master § 15 hierarchy is a prior (weights), not a rule; the choice is a seeded draw.</item>
    /// <item>A non-hostile faction is never made a hostile guard. Phase 1 can express only guarded
    /// or unguarded caches, so a friendly or neutral same-package faction (a plausible owner or
    /// trader) is skipped with evidence, and the resolver falls down the hierarchy.</item>
    /// <item>Defeated factions are not candidates; hidden factions only when the package fits.</item>
    /// <item>"No credible source" is a real result.</item>
    /// </list>
    /// </summary>
    public static class SourceResolver
    {
        // The hierarchy prior. Tuning, replaceable.
        public const float WSameSource = 5.0f;
        public const float WSimilarTech = 3.0f;
        public const float WHostileByTech = 2.0f;
        public const float WHostileLowTech = 0.8f;
        public const float WAncientSite = 1.2f;
        public const float WMechanoids = 1.0f;
        public const float WPirates = 0.9f;
        public const float WAbandoned = 0.6f;
        public const float WNoCredible = 0.4f;

        public static SourceDecision Resolve(ItemFacts item, List<FactionFacts> factions, LeadDivergence divergence, NetRng rng, SourceKind? forced = null)
        {
            SourceDecision d = new SourceDecision();
            List<SourceCandidate> cands = BuildCandidates(item, factions, d.skipped);
            d.candidates = cands;

            SourceCandidate chosen = null;
            if (forced.HasValue)
            {
                for (int i = 0; i < cands.Count; i++)
                {
                    if (cands[i].kind == forced.Value) { chosen = cands[i]; break; }
                }
            }
            if (chosen == null)
            {
                float[] weights = new float[cands.Count];
                for (int i = 0; i < cands.Count; i++) weights[i] = cands[i].weight;
                chosen = cands[rng.WeightedIndex(weights)];
            }

            d.kind = chosen.kind;
            d.holder = chosen.holder;
            d.holderStance = chosen.holder == null ? Stance.None : (chosen.holder.hostileToPlayer ? Stance.Hostile : Stance.Neutral);
            d.evidence.AddRange(chosen.evidence);
            d.threatProfileKey = chosen.threatProfileKey;

            // A trap makes the cache look softer than it is; it never changes who holds it.
            if (divergence == LeadDivergence.Trap && d.kind != SourceKind.NoCredibleSource && d.kind != SourceKind.Mechanoids)
            {
                d.threatProfileKey = ThreatProfiles.AmbushHidden;
                d.evidence.Add("trap");
            }
            return d;
        }

        public static List<SourceCandidate> BuildCandidates(ItemFacts item, List<FactionFacts> factions, List<string> skipped)
        {
            List<SourceCandidate> cands = new List<SourceCandidate>();
            int itemTech = item?.techLevel ?? 0;
            string itemPackage = item?.packageId;
            bool packageIsEvidence = item != null && !item.isLudeon && !string.IsNullOrEmpty(itemPackage);
            bool anyFactionCandidate = false;
            FactionFacts mechanoids = null;

            List<FactionFacts> sorted = factions == null ? new List<FactionFacts>() : new List<FactionFacts>(factions);
            sorted.Sort((a, b) => a.loadId.CompareTo(b.loadId));

            for (int i = 0; i < sorted.Count; i++)
            {
                FactionFacts f = sorted[i];
                if (f == null || f.isPlayer || f.temporary) continue;
                bool sameSource = packageIsEvidence && string.Equals(f.defPackageId, itemPackage, System.StringComparison.OrdinalIgnoreCase);
                if (f.defeated)
                {
                    skipped?.Add(f.name + ": defeated" + (sameSource ? " (same source package)" : ""));
                    continue;
                }
                if (f.isMechanoid)
                {
                    mechanoids = f;
                    continue;
                }
                if (!f.hostileToPlayer)
                {
                    // Stance is kept, never forced: a friendly or neutral faction is a possible owner or
                    // trader, which Phase 1 has no archetype for. It is never turned into a guard.
                    if (sameSource) skipped?.Add(f.name + ": same source package but not hostile (possible owner/trader; not expressible in Phase 1)");
                    continue;
                }
                if (f.hidden && !sameSource)
                {
                    skipped?.Add(f.name + ": hidden");
                    continue;
                }
                if (!f.humanlike || !f.canGuardSite)
                {
                    skipped?.Add(f.name + ": cannot guard a site");
                    continue;
                }

                SourceCandidate c = new SourceCandidate { holder = f, threatProfileKey = ThreatProfiles.Outpost };
                c.evidence.Add("hostileToPlayer");
                if (sameSource)
                {
                    c.kind = SourceKind.SameSourceFaction;
                    c.weight = WSameSource;
                    c.evidence.Add("sameSourcePackage");
                }
                else if (itemTech > 0 && f.techLevel == itemTech)
                {
                    c.kind = SourceKind.SimilarTechFaction;
                    c.weight = WSimilarTech;
                    c.evidence.Add("techMatch");
                }
                else if (itemTech <= 0 || f.techLevel >= itemTech - 1)
                {
                    c.kind = f.permanentEnemy ? SourceKind.Pirates : SourceKind.HostileFaction;
                    c.weight = f.permanentEnemy ? WPirates : WHostileByTech;
                    c.evidence.Add(f.permanentEnemy ? "raiders" : "techPlausible");
                    if (f.permanentEnemy) c.threatProfileKey = ThreatProfiles.BanditCamp;
                }
                else
                {
                    c.kind = f.permanentEnemy ? SourceKind.Pirates : SourceKind.HostileFaction;
                    c.weight = WHostileLowTech;
                    c.evidence.Add("techBelowItem");
                    if (f.permanentEnemy) c.threatProfileKey = ThreatProfiles.BanditCamp;
                }
                if (f.nearestSettlementTiles >= 0 && f.nearestSettlementTiles <= 15)
                {
                    c.weight *= 1.25f;
                    c.evidence.Add("nearby");
                }
                cands.Add(c);
                anyFactionCandidate = true;
            }

            if (mechanoids != null && itemTech >= 4)
            {
                SourceCandidate m = new SourceCandidate
                {
                    kind = SourceKind.Mechanoids,
                    holder = mechanoids,
                    weight = itemTech >= 5 ? WMechanoids * 1.5f : WMechanoids,
                    threatProfileKey = ThreatProfiles.SleepingMechanoids
                };
                m.evidence.Add("mechanoidsPresent");
                cands.Add(m);
            }

            SourceCandidate ancient = new SourceCandidate { kind = SourceKind.AncientSite, weight = WAncientSite, threatProfileKey = ThreatProfiles.Manhunters };
            ancient.evidence.Add("ancientSite");
            cands.Add(ancient);

            SourceCandidate abandoned = new SourceCandidate { kind = SourceKind.AbandonedCache, weight = WAbandoned, threatProfileKey = ThreatProfiles.None };
            abandoned.evidence.Add("abandoned");
            cands.Add(abandoned);

            float noCredible = WNoCredible;
            SourceCandidate none = new SourceCandidate { kind = SourceKind.NoCredibleSource, threatProfileKey = ThreatProfiles.None };
            if (item != null && item.unique)
            {
                noCredible += 0.8f;
                none.evidence.Add("uniqueItem");
            }
            if (!anyFactionCandidate && packageIsEvidence)
            {
                noCredible += 0.3f;
                none.evidence.Add("noFittingFaction");
            }
            none.weight = noCredible;
            cands.Add(none);
            return cands;
        }
    }
}
