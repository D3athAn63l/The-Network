using System;
using System.Collections.Generic;
using TheNetwork.Kernel;

namespace TheNetwork.Settings
{
    /// <summary>A forward-only settings migration (SAVE_AND_MIGRATION § 11).</summary>
    public interface ISettingsMigration
    {
        int From { get; }
        int To { get; }
        string Name { get; }

        /// <summary>Migrates one template. Throwing quarantines that template only.</summary>
        void ApplyToTemplate(CastTemplate template, List<string> report);
    }

    /// <summary>
    /// Version 0 is any file written before settings were versioned, or written by hand. Moving it to 1
    /// makes every default explicit: <c>canIssueWork</c> is false when absent (never inferred from form,
    /// fame, experience, size, wealth or mobility), a missing provenance is Custom (so regeneration can
    /// never delete it), and a missing enabled flag is true. Scribe already applied those defaults when
    /// reading; this migration records them and marks the file for rewriting so the values become
    /// explicit.
    /// </summary>
    public sealed class SettingsMigration_0_1_ExplicitDefaults : ISettingsMigration
    {
        public int From => 0;
        public int To => 1;
        public string Name => "ExplicitDefaults";

        public void ApplyToTemplate(CastTemplate template, List<string> report)
        {
            ContractorTemplate c = template as ContractorTemplate;
            if (c != null && c.canIssueWorkMissing)
            {
                c.canIssueWork = false;
                c.canIssueWorkMissing = false;
            }
            if (template.specialties == null) template.specialties = new List<string>();
        }
    }

    public static class SettingsMigrations
    {
        public static readonly List<ISettingsMigration> Registry = new List<ISettingsMigration>
        {
            new SettingsMigration_0_1_ExplicitDefaults()
        };

        /// <summary>
        /// Runs every migration above the loaded version. Returns true if anything ran (the file should
        /// be written). A template that throws is quarantined; the rest continue.
        /// </summary>
        public static bool Run(NetworkSettings settings, List<string> report)
        {
            int v = settings.LoadedVersion < 0 ? NetworkSettings.CurrentVersion : settings.LoadedVersion;
            if (v >= NetworkSettings.CurrentVersion) return false;
            bool ran = false;
            int missingIssue = 0;
            for (int i = 0; i < settings.roster.contractorTemplates.Count; i++)
            {
                if (settings.roster.contractorTemplates[i].canIssueWorkMissing) missingIssue++;
            }
            for (int m = 0; m < Registry.Count; m++)
            {
                ISettingsMigration mig = Registry[m];
                if (mig.From < v) continue;
                int failed = 0;
                failed += ApplyAll(mig, settings.roster.contractorTemplates, report);
                failed += ApplyAll(mig, settings.roster.fixerTemplates, report);
                report?.Add("Settings migration " + mig.Name + " (" + mig.From + "→" + mig.To + ") applied" + (failed > 0 ? ", " + failed + " template(s) quarantined" : "") + ".");
                v = mig.To;
                ran = true;
            }
            if (missingIssue > 0) report?.Add(missingIssue + " contractor template(s) had no canIssueWork; set to false.");
            return ran;
        }

        private static int ApplyAll<T>(ISettingsMigration mig, List<T> list, List<string> report) where T : CastTemplate
        {
            int failed = 0;
            for (int i = 0; i < list.Count; i++)
            {
                T t = list[i];
                if (t.quarantined != null) continue;
                try
                {
                    mig.ApplyToTemplate(t, report);
                }
                catch (Exception ex)
                {
                    failed++;
                    t.quarantined = new TemplateQuarantine
                    {
                        reasonKey = "MigrationFailed",
                        message = mig.Name + ": " + NetScribe.Truncate(ex.GetType().Name + ": " + ex.Message, 300)
                    };
                    report?.Add(t.KindKey + " '" + t.DisplayLabel + "' quarantined by migration " + mig.Name + ".");
                }
            }
            return failed;
        }
    }
}
