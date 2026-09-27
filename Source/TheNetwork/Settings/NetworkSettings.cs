using System.Collections.Generic;
using TheNetwork.Kernel;
using TheNetwork.Settings;
using Verse;

namespace TheNetwork
{
    public enum ItemOverride : byte
    {
        Auto = 0,
        Allowed = 1,
        Blocked = 2
    }

    /// <summary>A per-item catalog override, kept by defName so unknown (removed) defs stay dormant.</summary>
    public sealed class ItemOverrideEntry : IExposable
    {
        public string defName;
        public ItemOverride value;

        public void ExposeData()
        {
            Scribe_Values.Look(ref defName, "def");
            NetScribe.LookEnum(ref value, "value", ItemOverride.Auto);
        }
    }

    /// <summary>
    /// Player preferences and the global Network cast (DATA_MODEL § 18, ARCHITECTURE § 9). Versioned by
    /// its own <see cref="CurrentVersion"/> (NetworkSettingsVersion), which is read first. Runtime code
    /// never writes world outcomes here: the only writers are the settings UI, first-time cast
    /// generation, and settings migrations.
    /// </summary>
    public sealed class NetworkSettings : ModSettings
    {
        /// <summary>NetworkSettingsVersion. 0 = a file written before versioning (or hand-made).</summary>
        public const int CurrentVersion = 1;

        public int settingsVersion;
        public GlobalNetworkRoster roster = new GlobalNetworkRoster();
        public int targetContractorCount = 100;
        public int targetFixerCount = 8;
        public List<ItemOverrideEntry> itemOverrides = new List<ItemOverrideEntry>();
        public bool showUnusualItems;
        public bool verboseLogging;
        public bool profiling;

        // Runtime only.
        public int LoadedVersion { get; private set; } = -1;
        public bool LoadedFromNewerVersion => LoadedVersion > CurrentVersion;
        public List<string> LoadReport { get; } = new List<string>();
        public bool NeedsWrite { get; set; }

        private Dictionary<string, ItemOverride> overrideIndex;

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                settingsVersion = LoadedFromNewerVersion ? LoadedVersion : CurrentVersion;
            }
            Scribe_Values.Look(ref settingsVersion, "settingsVersion", 0, true);
            if (Scribe.mode == LoadSaveMode.LoadingVars) LoadedVersion = settingsVersion;

            Scribe_Deep.Look(ref roster, "roster");
            Scribe_Values.Look(ref targetContractorCount, "targetContractorCount", 100);
            Scribe_Values.Look(ref targetFixerCount, "targetFixerCount", 8);
            NetScribe.LookListTolerant(ref itemOverrides, "itemOverrides", "settings.overrides", null, null);
            Scribe_Values.Look(ref showUnusualItems, "showUnusualItems", false);
            Scribe_Values.Look(ref verboseLogging, "verboseLogging", false);
            Scribe_Values.Look(ref profiling, "profiling", false);

            if (Scribe.mode == LoadSaveMode.PostLoadInit || Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (roster == null) roster = new GlobalNetworkRoster();
                if (itemOverrides == null) itemOverrides = new List<ItemOverrideEntry>();
            }
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                AfterLoad();
            }
            overrideIndex = null;
        }

        /// <summary>Runs settings migrations and validation once the file is read (SAVE_AND_MIGRATION § 11).</summary>
        public void AfterLoad()
        {
            LoadReport.Clear();
            if (LoadedFromNewerVersion)
            {
                LoadReport.Add("Settings file is from a newer version of The Network (" + LoadedVersion + " > " + CurrentVersion + "); loaded best-effort and not rewritten.");
            }
            else if (SettingsMigrations.Run(this, LoadReport))
            {
                NeedsWrite = true;
            }
            int q = roster.Validate(LoadReport);
            if (q > 0 && !LoadedFromNewerVersion) NeedsWrite = true;
            int unreadable = roster.unreadable.Count;
            if (unreadable > 0) LoadReport.Add(unreadable + " cast entries could not be read and are preserved as raw data.");
            ApplyRuntimeToggles();
        }

        public void ApplyRuntimeToggles()
        {
            NetLog.VerboseEnabled = verboseLogging;
            NetProfiler.Enabled = profiling;
        }

        public ItemOverride GetOverride(string defName)
        {
            if (defName == null) return ItemOverride.Auto;
            if (overrideIndex == null)
            {
                overrideIndex = new Dictionary<string, ItemOverride>();
                for (int i = 0; i < itemOverrides.Count; i++)
                {
                    ItemOverrideEntry e = itemOverrides[i];
                    if (e != null && e.defName != null) overrideIndex[e.defName] = e.value;
                }
            }
            ItemOverride v;
            return overrideIndex.TryGetValue(defName, out v) ? v : ItemOverride.Auto;
        }

        public void SetOverride(string defName, ItemOverride value)
        {
            if (defName == null) return;
            itemOverrides.RemoveAll(e => e == null || e.defName == defName);
            if (value != ItemOverride.Auto) itemOverrides.Add(new ItemOverrideEntry { defName = defName, value = value });
            overrideIndex = null;
            StateVersion.Bump();
        }
    }
}
