using System;
using System.Collections.Generic;

namespace TheNetwork.Domain.Catalog
{
    public enum CatalogVerdict : byte
    {
        Eligible = 0,
        Unusual = 1,
        Ineligible = 2
    }

    /// <summary>Reason codes (COMPATIBILITY § 2.3). Strings are stable: they appear in reports and the UI.</summary>
    public static class CatalogReasons
    {
        // A. Technical impossibility (Ineligible, not overridable).
        public const string NotAThingCategory = "NotAThingCategory";
        public const string NonMinifiableBuilding = "NonMinifiableBuilding";
        public const string NoThingClass = "NoThingClass";
        public const string Corpse = "Corpse";
        public const string Unfinished = "Unfinished";
        public const string MinifiedWrapper = "MinifiedWrapper";
        public const string DestroyOnDrop = "DestroyOnDrop";
        public const string EngineInternal = "EngineInternal";

        // B. Heuristics (Unusual, overridable by Allowed).
        public const string NotHaulable = "NotHaulable";
        public const string NoLabel = "NoLabel";
        public const string NoGraphic = "NoGraphic";
        public const string UnusualThingClass = "UnusualThingClass";
        public const string NotTradeable = "NotTradeable";
        public const string NoMarketValue = "NoMarketValue";
        public const string QuestOrStoryItem = "QuestOrStoryItem";
        public const string Building = "Building";
        public const string Chunk = "Chunk";
        public const string Implausible = "Implausible";

        // Runtime safety net.
        public const string FailedToGenerate = "FailedToGenerate";

        public static bool IsTechnical(string reason)
        {
            switch (reason)
            {
                case NotAThingCategory:
                case NonMinifiableBuilding:
                case NoThingClass:
                case Corpse:
                case Unfinished:
                case MinifiedWrapper:
                case DestroyOnDrop:
                case EngineInternal:
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Everything the classifier reads about a ThingDef, as plain data (built once per session by the
    /// Integration catalog builder). Keeping it plain makes every rule testable headlessly.
    /// </summary>
    public sealed class CatalogFacts
    {
        public string defName;
        public string label;
        public string packageId;
        public string modName;
        public bool isCore;
        public bool isOfficialDlc;

        /// <summary>ThingCategory name: Item, Building, Pawn, Plant, Projectile, Filth, Gas, Attachment, Mote, Ethereal, PsychicEmitter, None.</summary>
        public string category;
        public string topCategoryLabel;
        public string thingClassName;
        public bool thingClassInvalid;
        public bool thingClassIsMinifiedWrapper;
        public bool thingClassIsEngineInternal;
        public bool thingClassUsual = true;

        public bool minifiable;
        public bool isCorpse;
        public bool isUnfinished;
        public bool destroyOnDrop;
        public bool everHaulable;
        public bool hasGraphic;
        public bool tradeabilityNone;
        public int tradeTagCount;
        public float marketValue;
        public float mass;
        public int stackLimit = 1;
        public int techLevel;
        public bool isStuff;
        public bool madeFromStuff;
        public bool hasQuality;
        public bool isWeapon;
        public bool isApparel;
        public bool isMedicine;
        public bool isDrug;
        public bool isFood;
        public bool isChunk;
        public bool craftable;
        public bool mineable;
        public bool questRewardTag;

        /// <summary>Cheapest recipe input value per unit produced; -1 when no recipe was readable (valuation signal).</summary>
        public float recipeInputValue = -1f;

        public bool HasLabel => !string.IsNullOrEmpty(label);
        public bool Tradeable => !tradeabilityNone || tradeTagCount > 0;
        public bool IsLudeon => isCore || isOfficialDlc;
    }

    public sealed class Classification
    {
        public CatalogVerdict verdict;
        public List<string> reasons = new List<string>();

        public bool HasTechnicalExclusion
        {
            get
            {
                for (int i = 0; i < reasons.Count; i++) if (CatalogReasons.IsTechnical(reasons[i])) return true;
                return false;
            }
        }
    }

    /// <summary>
    /// The conservative eligibility rules (COMPATIBILITY § 2.3). Technical impossibilities are
    /// Ineligible and can never be allowed; everything merely unusual is a heuristic the player's
    /// Allowed override rescues. Pure: no ThingMaker, no Def lookups.
    /// </summary>
    public static class CatalogClassifier
    {
        private static readonly HashSet<string> NonThingCategories = new HashSet<string>
        {
            "Pawn", "Plant", "Projectile", "Filth", "Gas", "Attachment", "Mote", "Ethereal", "PsychicEmitter", "None"
        };

        public const float ImplausibleFactor = 100f;
        public const float ImplausibleFloor = 5000f;

        public static Classification Classify(CatalogFacts f, float categoryMedianValue)
        {
            Classification c = new Classification();

            // ---- A. technical impossibility
            if (f.category == null || NonThingCategories.Contains(f.category)) c.reasons.Add(CatalogReasons.NotAThingCategory);
            if (f.category == "Building" && !f.minifiable) c.reasons.Add(CatalogReasons.NonMinifiableBuilding);
            if (f.thingClassInvalid) c.reasons.Add(CatalogReasons.NoThingClass);
            if (f.isCorpse) c.reasons.Add(CatalogReasons.Corpse);
            if (f.isUnfinished) c.reasons.Add(CatalogReasons.Unfinished);
            if (f.thingClassIsMinifiedWrapper) c.reasons.Add(CatalogReasons.MinifiedWrapper);
            if (f.destroyOnDrop) c.reasons.Add(CatalogReasons.DestroyOnDrop);
            if (f.thingClassIsEngineInternal) c.reasons.Add(CatalogReasons.EngineInternal);
            if (c.reasons.Count > 0)
            {
                c.verdict = CatalogVerdict.Ineligible;
                return c;
            }

            // ---- B. heuristics
            if (!f.everHaulable && f.category != "Building") c.reasons.Add(CatalogReasons.NotHaulable);
            if (!f.HasLabel) c.reasons.Add(CatalogReasons.NoLabel);
            if (!f.hasGraphic) c.reasons.Add(CatalogReasons.NoGraphic);
            if (!f.thingClassUsual) c.reasons.Add(CatalogReasons.UnusualThingClass);
            if (!f.Tradeable) c.reasons.Add(CatalogReasons.NotTradeable);
            if (f.marketValue <= 0f) c.reasons.Add(CatalogReasons.NoMarketValue);
            if (f.questRewardTag && !f.craftable && !f.Tradeable) c.reasons.Add(CatalogReasons.QuestOrStoryItem);
            if (f.category == "Building") c.reasons.Add(CatalogReasons.Building);
            if (f.isChunk) c.reasons.Add(CatalogReasons.Chunk);
            if (categoryMedianValue > 0f && f.marketValue > Math.Max(ImplausibleFloor, categoryMedianValue * ImplausibleFactor)) c.reasons.Add(CatalogReasons.Implausible);

            c.verdict = c.reasons.Count > 0 ? CatalogVerdict.Unusual : CatalogVerdict.Eligible;
            return c;
        }

        /// <summary>Median market value per top category (for the Implausible heuristic).</summary>
        public static Dictionary<string, float> CategoryMedians(List<CatalogFacts> all)
        {
            Dictionary<string, List<float>> values = new Dictionary<string, List<float>>();
            for (int i = 0; i < all.Count; i++)
            {
                CatalogFacts f = all[i];
                if (f.marketValue <= 0f) continue;
                string key = f.topCategoryLabel ?? "";
                List<float> list;
                if (!values.TryGetValue(key, out list))
                {
                    list = new List<float>();
                    values[key] = list;
                }
                list.Add(f.marketValue);
            }
            Dictionary<string, float> medians = new Dictionary<string, float>();
            foreach (KeyValuePair<string, List<float>> kv in values)
            {
                kv.Value.Sort();
                medians[kv.Key] = kv.Value[kv.Value.Count / 2];
            }
            return medians;
        }

        /// <summary>
        /// Final requestability: verdict + override + runtime failure. Allowed rescues Unusual only;
        /// an Ineligible technical exclusion stays Ineligible whatever the override.
        /// </summary>
        public static bool Effective(CatalogVerdict verdict, ItemOverride over, bool runtimeFailed, out string reasonKey)
        {
            reasonKey = null;
            if (runtimeFailed) reasonKey = CatalogReasons.FailedToGenerate;
            else if (verdict == CatalogVerdict.Ineligible) reasonKey = "Ineligible";
            else if (over == ItemOverride.Blocked) reasonKey = "Blocked";
            else if (verdict == CatalogVerdict.Unusual && over != ItemOverride.Allowed) reasonKey = "Unusual";
            return reasonKey == null;
        }
    }
}
