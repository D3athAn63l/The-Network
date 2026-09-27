using System;
using System.Collections.Generic;
using System.Diagnostics;
using RimWorld;
using TheNetwork.Domain.Catalog;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Integration
{
    /// <summary>
    /// Builds the session item catalog (COMPATIBILITY § 2.1): one pass over every ThingDef plus one
    /// pass over recipes and buildings, lazily on first use, never inside a Scribe pass, never saved.
    /// It never instantiates Things (ThingMaker allocates IDs and runs side effects). Defs cannot
    /// change within a session, so the result is valid until the game restarts.
    /// </summary>
    public static class CatalogCache
    {
        private static ItemCatalog catalog;

        public static ItemCatalog Get()
        {
            if (catalog == null) catalog = Build();
            return catalog;
        }

        public static bool IsBuilt => catalog != null;

        /// <summary>Dev "Rebuild item catalog": forgets the session catalog (runtime failures included).</summary>
        public static void Reset()
        {
            catalog = null;
            DefRefCaches.ClearAll();
        }

        private static ItemCatalog Build()
        {
            long t0 = Stopwatch.GetTimestamp();
            HashSet<ThingDef> products = new HashSet<ThingDef>();
            List<RecipeDef> recipes = DefDatabase<RecipeDef>.AllDefsListForReading;
            for (int i = 0; i < recipes.Count; i++)
            {
                List<ThingDefCountClass> p = recipes[i].products;
                if (p == null) continue;
                for (int k = 0; k < p.Count; k++) if (p[k]?.thingDef != null) products.Add(p[k].thingDef);
            }
            HashSet<ThingDef> mineables = new HashSet<ThingDef>();
            List<ThingDef> defs = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < defs.Count; i++)
            {
                ThingDef m = defs[i].building?.mineableThing;
                if (m != null) mineables.Add(m);
            }

            List<CatalogFacts> facts = new List<CatalogFacts>(defs.Count);
            int failed = 0;
            for (int i = 0; i < defs.Count; i++)
            {
                ThingDef d = defs[i];
                // Only plausible item or building candidates are catalogued; the rest are classified
                // Ineligible cheaply (and kept so "explain item" can say why).
                try
                {
                    facts.Add(FactsOf(d, products, mineables));
                }
                catch (Exception ex)
                {
                    failed++;
                    NetLog.WarnOnce(LogCategory.Catalog, "facts:" + d.defName, "Could not read ThingDef '" + d.defName + "' for the catalog: " + ex.Message);
                }
            }
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            NetworkSettings settings = NetworkMod.Settings;
            ItemCatalog c = new ItemCatalog(facts, settings == null ? (Func<string, ItemOverride>)null : settings.GetOverride, ms);
            NetLog.Info(LogCategory.Catalog, "Item catalog built: " + c.Count + " defs (" + c.CountVerdict(CatalogVerdict.Eligible) + " eligible, "
                + c.CountVerdict(CatalogVerdict.Unusual) + " unusual, " + c.CountVerdict(CatalogVerdict.Ineligible) + " ineligible"
                + (failed > 0 ? ", " + failed + " unreadable" : "") + ") in " + ms.ToString("0.0") + " ms.");
            return c;
        }

        public static CatalogFacts FactsOf(ThingDef d, HashSet<ThingDef> products, HashSet<ThingDef> mineables)
        {
            ModContentPack pack = d.modContentPack;
            Type cls = d.thingClass;
            CatalogFacts f = new CatalogFacts
            {
                defName = d.defName,
                label = d.label,
                packageId = ModClue.PackageOf(d),
                modName = pack?.Name,
                isCore = pack != null && pack.IsCoreMod,
                isOfficialDlc = pack != null && pack.IsOfficialMod && !pack.IsCoreMod,
                category = d.category.ToString(),
                topCategoryLabel = TopCategoryLabel(d),
                thingClassName = cls?.FullName,
                thingClassInvalid = cls == null || cls.IsAbstract || !typeof(Thing).IsAssignableFrom(cls),
                thingClassIsMinifiedWrapper = cls != null && typeof(MinifiedThing).IsAssignableFrom(cls),
                thingClassIsEngineInternal = cls != null && (typeof(ActiveTransporter).IsAssignableFrom(cls) || typeof(Skyfaller).IsAssignableFrom(cls)),
                minifiable = d.Minifiable,
                isCorpse = cls != null && d.IsCorpse,
                isUnfinished = d.isUnfinishedThing,
                destroyOnDrop = d.destroyOnDrop,
                everHaulable = d.EverHaulable,
                hasGraphic = d.graphicData != null,
                tradeabilityNone = d.tradeability == Tradeability.None,
                tradeTagCount = d.tradeTags?.Count ?? 0,
                stackLimit = d.stackLimit,
                techLevel = (int)d.techLevel,
                isStuff = d.IsStuff,
                madeFromStuff = d.MadeFromStuff,
                hasQuality = d.HasComp(typeof(CompQuality)),
                craftable = products.Contains(d) || (d.category == ThingCategory.Building && d.BuildableByPlayer),
                mineable = mineables.Contains(d) || d.deepCommonality > 0f,
                questRewardTag = HasRewardTag(d)
            };
            f.thingClassUsual = UsualClass(cls, d.category);
            if (d.category == ThingCategory.Item || d.category == ThingCategory.Building)
            {
                f.isWeapon = SafeBool(() => d.IsWeapon);
                f.isApparel = SafeBool(() => d.IsApparel);
                f.isMedicine = SafeBool(() => d.IsMedicine);
                f.isDrug = SafeBool(() => d.IsDrug);
                f.isFood = SafeBool(() => d.IsNutritionGivingIngestible);
                f.isChunk = d.thingCategories != null && ThingCategoryDefOf.Chunks != null && IsUnder(d, ThingCategoryDefOf.Chunks);
                f.marketValue = MarketValue(d);
            }
            return f;
        }

        private static bool SafeBool(Func<bool> read)
        {
            try
            {
                return read();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static float MarketValue(ThingDef d)
        {
            try
            {
                if (d.MadeFromStuff)
                {
                    ThingDef stuff = GenStuff.DefaultStuffFor(d);
                    return d.GetStatValueAbstract(StatDefOf.MarketValue, stuff);
                }
                return d.BaseMarketValue;
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        private static bool UsualClass(Type cls, ThingCategory category)
        {
            if (cls == null) return false;
            if (cls == typeof(Thing)) return category != ThingCategory.Building;
            if (!typeof(ThingWithComps).IsAssignableFrom(cls)) return false;
            if (typeof(Pawn).IsAssignableFrom(cls) || typeof(Plant).IsAssignableFrom(cls)) return false;
            bool isBuildingClass = typeof(Building).IsAssignableFrom(cls);
            return category == ThingCategory.Building ? isBuildingClass : !isBuildingClass;
        }

        private static bool HasRewardTag(ThingDef d)
        {
            List<string> tags = d.thingSetMakerTags;
            if (tags == null) return false;
            for (int i = 0; i < tags.Count; i++)
            {
                if (tags[i] != null && tags[i].IndexOf("Reward", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static bool IsUnder(ThingDef d, ThingCategoryDef ancestor)
        {
            for (int i = 0; i < d.thingCategories.Count; i++)
            {
                ThingCategoryDef c = d.thingCategories[i];
                int guard = 0;
                while (c != null && guard++ < 16)
                {
                    if (c == ancestor) return true;
                    c = c.parent;
                }
            }
            return false;
        }

        private static string TopCategoryLabel(ThingDef d)
        {
            ThingCategoryDef c = d.FirstThingCategory;
            if (c == null) return d.category.ToString();
            int guard = 0;
            while (c.parent != null && c.parent != ThingCategoryDefOf.Root && guard++ < 16) c = c.parent;
            return c.label ?? c.defName;
        }
    }
}
