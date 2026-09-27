using System;
using System.Collections.Generic;
using TheNetwork.Kernel;
using TheNetwork.Settings;
using UnityEngine;
using Verse;

namespace TheNetwork
{
    /// <summary>The mod entry point: owns the settings. No Harmony, no patches.</summary>
    public class NetworkMod : Mod
    {
        public static NetworkMod Instance { get; private set; }
        public static NetworkSettings Settings { get; private set; }

        public NetworkMod(ModContentPack content) : base(content)
        {
            Instance = this;
            try
            {
                Settings = GetSettings<NetworkSettings>();
            }
            catch (Exception ex)
            {
                NetLog.Error(LogCategory.Settings, "Settings could not be read; using defaults this session (the file is not overwritten unless you change a setting): " + ex);
                Settings = new NetworkSettings();
            }
        }

        public override string SettingsCategory()
        {
            return "The Network";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            UI.SettingsWindow.Draw(inRect, Settings);
        }

        public override void WriteSettings()
        {
            if (Settings != null && Settings.LoadedFromNewerVersion && !Settings.NeedsWrite)
            {
                // A file from a newer version is not rewritten until the player changes something.
                return;
            }
            base.WriteSettings();
        }
    }

    /// <summary>Runs once after all Defs are loaded: build stamp, settings report, cast generation when needed.</summary>
    [StaticConstructorOnStartup]
    public static class NetworkStartup
    {
        static NetworkStartup()
        {
            NetLog.Info(LogCategory.Kernel, "The Network " + NetworkWorldComponent.ModVersion + " loaded (" + NetworkBuildStamp.Stamp + "). No Harmony.");
            NetworkSettings s = NetworkMod.Settings;
            if (s == null) return;
            s.ApplyRuntimeToggles();
            for (int i = 0; i < s.LoadReport.Count; i++) NetLog.Info(LogCategory.Settings, s.LoadReport[i]);
            EnsureRoster(s, "startup");
            if (s.NeedsWrite && !s.LoadedFromNewerVersion) NetworkMod.Instance?.WriteSettings();
        }

        /// <summary>
        /// Generates the global cast only when none was ever generated (DATA_MODEL § 18.1). Never runs
        /// against a settings file from a newer version.
        /// </summary>
        public static bool EnsureRoster(NetworkSettings s, string when)
        {
            if (s == null || s.LoadedFromNewerVersion || !s.roster.NeverGenerated) return false;
            int castSeed = Guid.NewGuid().GetHashCode();
            CastGenerator.RegenerateGenerated(s.roster, NamePools.FromDefs(), castSeed, s.targetContractorCount, s.targetFixerCount, null, DateTime.UtcNow.ToString("u"));
            s.roster.Validate(null);
            s.NeedsWrite = true;
            NetLog.Info(LogCategory.Settings, "Generated the global Network cast (" + when + "): " + s.roster.contractorTemplates.Count + " contractor and " + s.roster.fixerTemplates.Count + " Fixer templates.");
            NetworkMod.Instance?.WriteSettings();
            s.NeedsWrite = false;
            return true;
        }

        /// <summary>Settings UI "Regenerate generated entries": Custom entries are always kept.</summary>
        public static void Regenerate(NetworkSettings s)
        {
            if (s == null) return;
            int custom = 0;
            for (int i = 0; i < s.roster.contractorTemplates.Count; i++) if (s.roster.contractorTemplates[i].provenance == TemplateProvenance.Custom) custom++;
            for (int i = 0; i < s.roster.fixerTemplates.Count; i++) if (s.roster.fixerTemplates[i].provenance == TemplateProvenance.Custom) custom++;
            CastGenerator.RegenerateGenerated(s.roster, NamePools.FromDefs(), Guid.NewGuid().GetHashCode(), s.targetContractorCount, s.targetFixerCount, null, DateTime.UtcNow.ToString("u"));
            s.roster.Validate(null);
            s.NeedsWrite = true;
            NetworkMod.Instance?.WriteSettings();
            NetLog.Info(LogCategory.Settings, "Regenerated the global cast; " + custom + " custom entries kept. Existing saves keep their own snapshots.");
        }
    }
}
