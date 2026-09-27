using System;
using System.Collections.Generic;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;

namespace TheNetwork.Domain.Opportunities
{
    public sealed class GenerationInput
    {
        public ItemFacts item;
        public LeadDivergence divergence;
        public float baseThreatPoints;
        public List<FactionFacts> factions;
        public IList<ItemFacts> extraCargoPool;
        public int seed;
        public SourceKind? forcedKind;
    }

    public sealed class DraftCargo
    {
        public string defName;
        public string label;
        public int count;
        public int qualityBand = -1;
        public bool target;
    }

    /// <summary>The truth of one opportunity before it is given an id, a tile and a site.</summary>
    public sealed class OpportunityDraft
    {
        public SourceDecision decision;
        public int targetCount;
        /// <summary>What the cache held before the divergence shaped it (what an outdated or bad report still describes).</summary>
        public int nominalCount;
        public List<DraftCargo> extras = new List<DraftCargo>();
        public int targetQualityBand = -1;
        public float threatPoints;
        public int windowDays;
        public bool NoCredibleLead => decision == null || decision.IsNoCredibleSource;
    }

    /// <summary>
    /// Decides what exists (ARCHITECTURE § 6.14): source context, quantity, extra cargo, threat and the
    /// operational window. Quantity comes from market value, stack limit, category, threat and the
    /// archetype, then is shaped by the hidden divergence. It never comes from the request.
    /// Pure and deterministic for its inputs; every draw comes from named NetRng streams of the seed.
    /// </summary>
    public static class OpportunityGenerator
    {
        public static OpportunityDraft Generate(GenerationInput input)
        {
            OpportunityDraft draft = new OpportunityDraft();
            NetRng sourceRng = new NetRng(input.seed, "opp.source");
            draft.decision = SourceResolver.Resolve(input.item, input.factions, input.divergence, sourceRng, input.forcedKind);
            if (draft.NoCredibleLead) return draft;

            NetRng qty = new NetRng(input.seed, "opp.quantity");
            draft.threatPoints = ThreatPoints(input, draft.decision, new NetRng(input.seed, "opp.threat"));
            draft.nominalCount = NominalCount(input.item, draft.threatPoints, input.baseThreatPoints, qty);
            draft.targetCount = ShapeByDivergence(draft.nominalCount, input.divergence, input.item, qty);
            if (input.item != null && input.item.hasQuality) draft.targetQualityBand = QualityBand(new NetRng(input.seed, "opp.quality"), draft.decision.kind);
            draft.extras = ExtraCargo(input, draft, new NetRng(input.seed, "opp.extra"));
            NetRng window = new NetRng(input.seed, "opp.window");
            draft.windowDays = window.RangeInclusive(12, 22);
            return draft;
        }

        public static float ThreatPoints(GenerationInput input, SourceDecision d, NetRng rng)
        {
            if (d.threatProfileKey == ThreatProfiles.None) return 0f;
            float basePoints = Math.Max(150f, input.baseThreatPoints);
            float holder;
            switch (d.kind)
            {
                case SourceKind.SameSourceFaction:
                case SourceKind.SimilarTechFaction: holder = 1.0f; break;
                case SourceKind.Mechanoids: holder = 1.1f; break;
                case SourceKind.AncientSite: holder = 0.75f; break;
                case SourceKind.Pirates: holder = 0.95f; break;
                default: holder = 0.9f; break;
            }
            float div = 1f;
            if (input.divergence == LeadDivergence.Complication) div = rng.Range(1.6f, 2.2f);
            else if (input.divergence == LeadDivergence.Jackpot) div = 1.2f;
            float points = basePoints * holder * div * rng.Range(0.85f, 1.15f);
            return (float)Math.Round(points);
        }

        /// <summary>How much of the item the cache holds before divergence (master § 10). Replaceable.</summary>
        public static int NominalCount(ItemFacts item, float threatPoints, float baseThreatPoints, NetRng rng)
        {
            if (item == null) return 1;
            if (item.unique) return 1;
            float unit = Math.Max(0.5f, item.marketValue);
            float budget = (350f + Math.Max(threatPoints, baseThreatPoints * 0.5f) * 1.6f) * rng.Range(0.7f, 1.35f);
            int raw = (int)Math.Round(budget / unit);
            int stack = Math.Max(1, item.stackLimit);
            if (stack <= 1)
            {
                int cap = item.isBuilding ? 2 : 4;
                return BandUtility.Clamp(raw, 1, cap);
            }
            return BandUtility.Clamp(raw, 1, stack * 4);
        }

        public static int ShapeByDivergence(int nominal, LeadDivergence divergence, ItemFacts item, NetRng rng)
        {
            switch (divergence)
            {
                case LeadDivergence.Outdated:
                    if (rng.Chance(0.2f)) return 0; // already looted
                    return Math.Max(1, (int)Math.Round(nominal * rng.Range(0.1f, 0.4f)));
                case LeadDivergence.Bad:
                    return 0;
                case LeadDivergence.Jackpot:
                    if (item != null && item.unique) return 1;
                    int stack = item == null ? 1 : Math.Max(1, item.stackLimit);
                    int cap = stack <= 1 ? 6 : stack * 8;
                    return BandUtility.Clamp((int)Math.Round(nominal * rng.Range(2f, 3f)), nominal + 1, Math.Max(nominal + 1, cap));
                default:
                    return nominal;
            }
        }

        /// <summary>QualityCategory int (0 Awful … 6 Legendary), committed at generation.</summary>
        public static int QualityBand(NetRng rng, SourceKind kind)
        {
            float[] w = { 4f, 14f, 34f, 26f, 14f, 6f, 2f };
            if (kind == SourceKind.SameSourceFaction || kind == SourceKind.SimilarTechFaction)
            {
                w[4] += 4f;
                w[5] += 2f;
            }
            return rng.WeightedIndex(w);
        }

        public static List<DraftCargo> ExtraCargo(GenerationInput input, OpportunityDraft draft, NetRng rng)
        {
            List<DraftCargo> list = new List<DraftCargo>();
            IList<ItemFacts> pool = input.extraCargoPool;
            if (pool == null || pool.Count == 0) return list;
            int n = rng.WeightedIndex(new[] { 3f, 5f, 2f }); // 0, 1 or 2 extra stacks
            if (input.divergence == LeadDivergence.Bad && n == 0) n = 1; // a bad lead still leads somewhere with something
            float targetValue = Math.Max(100f, (input.item?.marketValue ?? 10f) * Math.Max(1, draft.nominalCount));
            HashSet<string> used = new HashSet<string>();
            if (input.item != null) used.Add(input.item.defName);
            for (int i = 0; i < n; i++)
            {
                ItemFacts pick = null;
                for (int attempt = 0; attempt < 6 && pick == null; attempt++)
                {
                    ItemFacts c = rng.Pick(pool);
                    if (c != null && !used.Contains(c.defName)) pick = c;
                }
                if (pick == null) break;
                used.Add(pick.defName);
                float share = targetValue * rng.Range(0.1f, 0.3f);
                int count = BandUtility.Clamp((int)Math.Round(share / Math.Max(0.5f, pick.marketValue)), 1, Math.Max(1, pick.stackLimit) * 2);
                list.Add(new DraftCargo { defName = pick.defName, label = pick.label, count = count });
            }
            return list;
        }
    }
}
