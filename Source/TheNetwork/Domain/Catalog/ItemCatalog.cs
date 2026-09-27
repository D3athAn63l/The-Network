using System;
using System.Collections.Generic;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;

namespace TheNetwork.Domain.Catalog
{
    public sealed class CatalogEntry
    {
        public CatalogFacts facts;
        public Classification classification;
        public ItemFacts item;

        public string DefName => facts.defName;
        public string Label => facts.HasLabel ? facts.label : facts.defName;
        public CatalogVerdict Verdict => classification.verdict;
    }

    /// <summary>
    /// The session item catalog (ARCHITECTURE § 6.18, COMPATIBILITY § 2). Built once from plain facts;
    /// never serialized. Overrides are read through a delegate (they live in ModSettings). Runtime
    /// failures mark a def unusable for the rest of the session, whatever its override.
    /// </summary>
    public sealed class ItemCatalog : ICatalog
    {
        private readonly List<CatalogEntry> entries = new List<CatalogEntry>();
        private readonly Dictionary<string, CatalogEntry> byDefName = new Dictionary<string, CatalogEntry>();
        private readonly Dictionary<string, string> runtimeFailures = new Dictionary<string, string>();
        private readonly Dictionary<int, List<ItemFacts>> extraPools = new Dictionary<int, List<ItemFacts>>();
        private readonly Func<string, ItemOverride> overrides;

        public double BuildMs { get; private set; }

        public ItemCatalog(List<CatalogFacts> facts, Func<string, ItemOverride> overrides, double buildMs = 0)
        {
            this.overrides = overrides ?? (d => ItemOverride.Auto);
            BuildMs = buildMs;
            Dictionary<string, float> medians = CatalogClassifier.CategoryMedians(facts);
            for (int i = 0; i < facts.Count; i++)
            {
                CatalogFacts f = facts[i];
                if (f == null || string.IsNullOrEmpty(f.defName) || byDefName.ContainsKey(f.defName)) continue;
                float median;
                medians.TryGetValue(f.topCategoryLabel ?? "", out median);
                CatalogEntry e = new CatalogEntry { facts = f, classification = CatalogClassifier.Classify(f, median), item = ToItemFacts(f) };
                entries.Add(e);
                byDefName[f.defName] = e;
            }
            entries.Sort((a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        }

        public static ItemFacts ToItemFacts(CatalogFacts f)
        {
            return new ItemFacts
            {
                defName = f.defName,
                label = f.HasLabel ? f.label : f.defName,
                packageId = f.packageId,
                modName = f.modName,
                isLudeon = f.IsLudeon,
                techLevel = f.techLevel,
                marketValue = f.marketValue,
                stackLimit = Math.Max(1, f.stackLimit),
                hasQuality = f.hasQuality,
                madeFromStuff = f.madeFromStuff,
                isBuilding = f.category == "Building",
                isWeapon = f.isWeapon,
                isApparel = f.isApparel,
                isResource = f.isStuff || f.stackLimit > 1,
                craftable = f.craftable,
                tradeable = f.Tradeable,
                unique = f.questRewardTag && !f.craftable && !f.Tradeable,
                mineable = f.mineable
            };
        }

        public IReadOnlyList<CatalogEntry> Entries => entries;
        public int Count => entries.Count;

        public CatalogEntry Get(string defName)
        {
            CatalogEntry e;
            return defName != null && byDefName.TryGetValue(defName, out e) ? e : null;
        }

        public ItemOverride OverrideOf(string defName)
        {
            return overrides(defName);
        }

        public string RuntimeFailure(string defName)
        {
            string r;
            return defName != null && runtimeFailures.TryGetValue(defName, out r) ? r : null;
        }

        // ------------------------------------------------------------------ ICatalog

        public bool IsRequestable(string defName, out string reasonKey)
        {
            CatalogEntry e = Get(defName);
            if (e == null)
            {
                reasonKey = "NotInCatalog";
                return false;
            }
            return CatalogClassifier.Effective(e.Verdict, overrides(defName), runtimeFailures.ContainsKey(defName), out reasonKey);
        }

        public ItemFacts Facts(string defName)
        {
            return Get(defName)?.item;
        }

        public IList<ItemFacts> ExtraCargoPool(int maxTechLevel)
        {
            List<ItemFacts> pool;
            if (extraPools.TryGetValue(maxTechLevel, out pool)) return pool;
            pool = new List<ItemFacts>();
            for (int i = 0; i < entries.Count; i++)
            {
                CatalogEntry e = entries[i];
                CatalogFacts f = e.facts;
                if (e.Verdict != CatalogVerdict.Eligible) continue;
                if (f.category != "Item" || f.stackLimit <= 1 || !f.Tradeable || f.isDrug) continue;
                if (f.marketValue < 0.5f || f.marketValue > 60f) continue;
                if (f.techLevel > maxTechLevel) continue;
                if (overrides(f.defName) == ItemOverride.Blocked) continue;
                pool.Add(e.item);
            }
            pool.Sort((a, b) => string.CompareOrdinal(a.defName, b.defName));
            extraPools[maxTechLevel] = pool;
            return pool;
        }

        public void MarkUnusable(string defName, string reason)
        {
            if (defName == null || runtimeFailures.ContainsKey(defName)) return;
            runtimeFailures[defName] = reason ?? "unknown";
            foreach (List<ItemFacts> pool in extraPools.Values) pool.RemoveAll(x => x.defName == defName);
            NetLog.Warn(LogCategory.Catalog, "'" + defName + "' could not be produced in this game and is unusable until the next session: " + (reason ?? "unknown") + ".");
            StateVersion.Bump();
        }

        public int CountVerdict(CatalogVerdict v)
        {
            int n = 0;
            for (int i = 0; i < entries.Count; i++) if (entries[i].Verdict == v) n++;
            return n;
        }
    }
}
