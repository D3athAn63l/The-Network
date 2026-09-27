using System;
using System.Collections.Generic;
using TheNetwork.Domain.Catalog;

namespace TheNetwork.Tests
{
    public static class CatalogTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("Catalog.HardExclusions", HardExclusions));
            t.Add(new KeyValuePair<string, Action>("Catalog.Heuristics", Heuristics));
            t.Add(new KeyValuePair<string, Action>("Catalog.OverridesAndRuntimeFailure", Overrides));
            t.Add(new KeyValuePair<string, Action>("Catalog.ExtraCargoPoolIsGeneric", ExtraPool));
        }

        /// <summary>A plain, valid modded item: Eligible under every rule.</summary>
        public static CatalogFacts Item(string defName = "ModX_Weirdium")
        {
            return new CatalogFacts
            {
                defName = defName, label = "weirdium", packageId = "someone.weirdmod", modName = "Weird Mod",
                category = "Item", topCategoryLabel = "resources", thingClassName = "Verse.ThingWithComps", thingClassUsual = true,
                everHaulable = true, hasGraphic = true, marketValue = 40f, stackLimit = 50, techLevel = 5, craftable = false, tradeTagCount = 1
            };
        }

        private static CatalogVerdict V(CatalogFacts f, out List<string> reasons)
        {
            Classification c = CatalogClassifier.Classify(f, 10f);
            reasons = c.reasons;
            return c.verdict;
        }

        private static void Expect(CatalogFacts f, CatalogVerdict verdict, string reason, string what)
        {
            List<string> reasons;
            CatalogVerdict v = V(f, out reasons);
            T.Eq(verdict, v, what + " verdict");
            if (reason != null) T.Check(reasons.Contains(reason), what + " has reason " + reason + " (got " + string.Join(",", reasons.ToArray()) + ")");
        }

        private static void HardExclusions()
        {
            Expect(Item(), CatalogVerdict.Eligible, null, "plain modded item");
            foreach (string cat in new[] { "Pawn", "Plant", "Projectile", "Filth", "Gas", "Attachment", "Mote", "Ethereal", "PsychicEmitter", "None" })
            {
                CatalogFacts f = Item();
                f.category = cat;
                Expect(f, CatalogVerdict.Ineligible, CatalogReasons.NotAThingCategory, "category " + cat);
            }
            CatalogFacts b = Item(); b.category = "Building"; b.minifiable = false;
            Expect(b, CatalogVerdict.Ineligible, CatalogReasons.NonMinifiableBuilding, "installed building");
            CatalogFacts nc = Item(); nc.thingClassInvalid = true;
            Expect(nc, CatalogVerdict.Ineligible, CatalogReasons.NoThingClass, "no thing class");
            CatalogFacts corpse = Item(); corpse.isCorpse = true;
            Expect(corpse, CatalogVerdict.Ineligible, CatalogReasons.Corpse, "corpse");
            CatalogFacts unf = Item(); unf.isUnfinished = true;
            Expect(unf, CatalogVerdict.Ineligible, CatalogReasons.Unfinished, "unfinished thing");
            CatalogFacts mini = Item(); mini.thingClassIsMinifiedWrapper = true;
            Expect(mini, CatalogVerdict.Ineligible, CatalogReasons.MinifiedWrapper, "minified wrapper");
            CatalogFacts drop = Item(); drop.destroyOnDrop = true;
            Expect(drop, CatalogVerdict.Ineligible, CatalogReasons.DestroyOnDrop, "destroy on drop");
            CatalogFacts internalPod = Item(); internalPod.thingClassIsEngineInternal = true;
            Expect(internalPod, CatalogVerdict.Ineligible, CatalogReasons.EngineInternal, "engine internal carrier");
            foreach (string r in new[] { CatalogReasons.NotAThingCategory, CatalogReasons.NonMinifiableBuilding, CatalogReasons.NoThingClass, CatalogReasons.Corpse, CatalogReasons.Unfinished, CatalogReasons.MinifiedWrapper, CatalogReasons.DestroyOnDrop, CatalogReasons.EngineInternal })
            {
                T.Check(CatalogReasons.IsTechnical(r), r + " is a technical (non-overridable) reason");
            }
        }

        private static void Heuristics()
        {
            CatalogFacts f;
            f = Item(); f.everHaulable = false; Expect(f, CatalogVerdict.Unusual, CatalogReasons.NotHaulable, "not haulable");
            f = Item(); f.label = null; Expect(f, CatalogVerdict.Unusual, CatalogReasons.NoLabel, "no label");
            f = Item(); f.hasGraphic = false; Expect(f, CatalogVerdict.Unusual, CatalogReasons.NoGraphic, "no graphic");
            f = Item(); f.thingClassUsual = false; Expect(f, CatalogVerdict.Unusual, CatalogReasons.UnusualThingClass, "unusual class");
            f = Item(); f.tradeabilityNone = true; f.tradeTagCount = 0; Expect(f, CatalogVerdict.Unusual, CatalogReasons.NotTradeable, "not tradeable");
            f = Item(); f.marketValue = 0f; Expect(f, CatalogVerdict.Unusual, CatalogReasons.NoMarketValue, "no market value");
            f = Item(); f.questRewardTag = true; f.tradeabilityNone = true; f.tradeTagCount = 0; f.craftable = false; Expect(f, CatalogVerdict.Unusual, CatalogReasons.QuestOrStoryItem, "quest/story item");
            f = Item(); f.category = "Building"; f.minifiable = true; Expect(f, CatalogVerdict.Unusual, CatalogReasons.Building, "minifiable building");
            f = Item(); f.isChunk = true; Expect(f, CatalogVerdict.Unusual, CatalogReasons.Chunk, "chunk");
            f = Item(); f.marketValue = 50000f;
            List<string> reasons;
            T.Eq(CatalogVerdict.Unusual, CatalogClassifier.Classify(f, 40f).verdict, "implausible outlier");
            T.Check(CatalogClassifier.Classify(f, 40f).reasons.Contains(CatalogReasons.Implausible), "implausible reason");
            T.Eq(CatalogVerdict.Eligible, CatalogClassifier.Classify(Item(), 40f).verdict, "normal value not implausible");
            f = Item(); f.tradeabilityNone = true; f.tradeTagCount = 2;
            T.Eq(CatalogVerdict.Eligible, V(f, out reasons), "trade tags alone make it tradeable");
        }

        private static void Overrides()
        {
            List<CatalogFacts> facts = new List<CatalogFacts>();
            CatalogFacts plain = Item("Plain");
            CatalogFacts odd = Item("Odd"); odd.everHaulable = false;
            CatalogFacts impossible = Item("Impossible"); impossible.isCorpse = true;
            CatalogFacts blocked = Item("BlockedOne");
            facts.Add(plain); facts.Add(odd); facts.Add(impossible); facts.Add(blocked);
            Dictionary<string, ItemOverride> over = new Dictionary<string, ItemOverride>
            {
                { "Odd", ItemOverride.Allowed }, { "Impossible", ItemOverride.Allowed }, { "BlockedOne", ItemOverride.Blocked }
            };
            ItemCatalog cat = new ItemCatalog(facts, d => { ItemOverride v; return over.TryGetValue(d, out v) ? v : ItemOverride.Auto; });
            string reason;
            T.Check(cat.IsRequestable("Plain", out reason), "eligible + Auto is requestable");
            T.Check(cat.IsRequestable("Odd", out reason), "Allowed rescues an Unusual heuristic");
            T.Check(!cat.IsRequestable("Impossible", out reason) && reason == "Ineligible", "Allowed cannot rescue a technical exclusion");
            T.Check(!cat.IsRequestable("BlockedOne", out reason) && reason == "Blocked", "Blocked excludes an eligible item");
            over.Remove("Odd");
            T.Check(!cat.IsRequestable("Odd", out reason) && reason == "Unusual", "Unusual without Allowed is not requestable");
            T.Check(!cat.IsRequestable("NotADef", out reason) && reason == "NotInCatalog", "unknown def");
            cat.MarkUnusable("Plain", "FailedToGenerate:test");
            T.Check(!cat.IsRequestable("Plain", out reason) && reason == CatalogReasons.FailedToGenerate, "runtime failure makes a def unusable for the session");
            over["Plain"] = ItemOverride.Allowed;
            T.Check(!cat.IsRequestable("Plain", out reason), "even Allowed cannot bring back a runtime failure this session");
            T.Check(T.netLog.Exists(l => l.Contains("could not be produced")), "one diagnostic line for the runtime failure");
        }

        private static void ExtraPool()
        {
            List<CatalogFacts> facts = new List<CatalogFacts>();
            CatalogFacts cheap = Item("CheapStack"); cheap.marketValue = 3f; cheap.techLevel = 3;
            CatalogFacts drug = Item("SomeDrug"); drug.marketValue = 10f; drug.isDrug = true;
            CatalogFacts single = Item("Single"); single.stackLimit = 1; single.marketValue = 10f;
            CatalogFacts spacer = Item("SpacerStack"); spacer.marketValue = 20f; spacer.techLevel = 5;
            facts.Add(cheap); facts.Add(drug); facts.Add(single); facts.Add(spacer);
            ItemCatalog cat = new ItemCatalog(facts, null);
            IList<TheNetwork.Domain.Ports.ItemFacts> low = cat.ExtraCargoPool(4);
            T.Eq(1, low.Count, "extra cargo: stackable, tradeable, not a drug, within tech");
            T.Eq("CheapStack", low[0].defName, "pool content");
            T.Eq(2, cat.ExtraCargoPool(5).Count, "higher tech widens the pool");
        }
    }
}
