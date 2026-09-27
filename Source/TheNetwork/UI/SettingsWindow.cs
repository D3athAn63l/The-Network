using System;
using System.Collections.Generic;
using TheNetwork.Core;
using TheNetwork.Integration;
using TheNetwork.Settings;
using UnityEngine;
using Verse;

namespace TheNetwork.UI
{
    /// <summary>
    /// Mod Settings (ARCHITECTURE § 9): general toggles, the minimal global-cast path (regenerate
    /// generated entries, enable/disable, rename, inspect; no profile editor), and "Prepare save for
    /// removal". Only this UI, first-time generation and settings migrations write ModSettings.
    /// </summary>
    public static class SettingsWindow
    {
        private static Vector2 scroll;
        private static bool showContractors = true;
        private static string filter = "";

        public static void Draw(Rect inRect, NetworkSettings s)
        {
            if (s == null) return;
            NetworkStartup.EnsureRoster(s, "settings");
            Listing_Standard top = new Listing_Standard();
            Rect topRect = new Rect(inRect.x, inRect.y, inRect.width, 240f);
            top.Begin(topRect);
            bool unusual = s.showUnusualItems, verbose = s.verboseLogging, prof = s.profiling, intel = s.intelEnabled;
            top.CheckboxLabeled("TheNetwork_Settings_ShowUnusual".Translate(), ref s.showUnusualItems, "TheNetwork_Settings_ShowUnusualTip".Translate());
            top.CheckboxLabeled("TheNetwork_Settings_Verbose".Translate(), ref s.verboseLogging);
            top.CheckboxLabeled("TheNetwork_Settings_Profiling".Translate(), ref s.profiling);
            top.CheckboxLabeled("TheNetwork_Settings_IntelEnabled".Translate(), ref s.intelEnabled, "TheNetwork_Settings_IntelEnabledTip".Translate());
            bool changed = unusual != s.showUnusualItems || verbose != s.verboseLogging || prof != s.profiling || intel != s.intelEnabled;
            if (changed)
            {
                s.ApplyRuntimeToggles();
                s.NeedsWrite = true;
                Kernel.StateVersion.Bump();
            }

            GlobalNetworkRoster roster = s.roster;
            top.GapLine();
            top.Label("TheNetwork_Settings_CastSummary".Translate(roster.contractorTemplates.Count, roster.CountActive(true), roster.fixerTemplates.Count, roster.CountActive(false), roster.CountQuarantined()));
            if (s.LoadedFromNewerVersion) top.Label("TheNetwork_Settings_NewerFile".Translate());
            Rect buttons = top.GetRect(30f);
            if (Widgets.ButtonText(new Rect(buttons.x, buttons.y, 260f, 28f), "TheNetwork_Settings_Regenerate".Translate()))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("TheNetwork_Settings_RegenerateConfirm".Translate(), () => NetworkStartup.Regenerate(s), true));
            }
            if (Current.ProgramState == ProgramState.Playing && Widgets.ButtonText(new Rect(buttons.x + 830f, buttons.y, 200f, 28f), "TheNetwork_Settings_RebuildCatalog".Translate()))
            {
                CatalogCache.Reset();
                Kernel.StateVersion.Bump();
            }
            NetworkRuntime rt = NetworkRuntime.Current;
            if (Current.ProgramState == ProgramState.Playing && rt != null)
            {
                if (!rt.Inert)
                {
                    if (Widgets.ButtonText(new Rect(buttons.x + 270f, buttons.y, 260f, 28f), "TheNetwork_Settings_PrepareRemoval".Translate()))
                    {
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("TheNetwork_Settings_PrepareRemovalConfirm".Translate(), () =>
                        {
                            string summary = RemovalPreparer.Prepare(rt);
                            Find.WindowStack.Add(new Dialog_MessageBox(summary));
                        }, true));
                    }
                }
                else
                {
                    GUI.color = Color.yellow;
                    Widgets.Label(new Rect(buttons.x + 270f, buttons.y + 4f, 380f, 24f), "TheNetwork_Settings_Prepared".Translate());
                    GUI.color = Color.white;
                    if (Widgets.ButtonText(new Rect(buttons.x + 660f, buttons.y, 160f, 28f), "TheNetwork_Settings_Resume".Translate())) RemovalPreparer.Resume(rt);
                }
            }
            top.End();

            // ---- roster list
            Rect listArea = new Rect(inRect.x, inRect.y + 245f, inRect.width, inRect.height - 245f);
            Rect bar = new Rect(listArea.x, listArea.y, listArea.width, 28f);
            if (Widgets.ButtonText(new Rect(bar.x, bar.y, 160f, 26f), (showContractors ? "TheNetwork_Settings_Contractors" : "TheNetwork_Settings_Fixers").Translate()))
            {
                showContractors = !showContractors;
                scroll = Vector2.zero;
            }
            Widgets.Label(new Rect(bar.x + 170f, bar.y + 3f, 60f, 24f), "TheNetwork_Search".Translate());
            filter = Widgets.TextField(new Rect(bar.x + 230f, bar.y, 220f, 26f), filter ?? "");

            List<CastTemplate> rows = new List<CastTemplate>();
            if (showContractors) foreach (ContractorTemplate t in roster.contractorTemplates) rows.Add(t);
            else foreach (FixerTemplate t in roster.fixerTemplates) rows.Add(t);
            if (!string.IsNullOrEmpty(filter)) rows.RemoveAll(t => t.DisplayLabel.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0);

            Rect outRect = new Rect(listArea.x, listArea.y + 32f, listArea.width, listArea.height - 64f);
            float rowH = 26f;
            Rect view = new Rect(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, rows.Count * rowH + roster.unreadable.Count * rowH + 4f));
            Widgets.BeginScrollView(outRect, ref scroll, view);
            float y = 0f;
            for (int i = 0; i < rows.Count; i++)
            {
                DrawRow(new Rect(0f, y, view.width, rowH), rows[i], s);
                y += rowH;
            }
            for (int i = 0; i < roster.unreadable.Count; i++)
            {
                GUI.color = new Color(1f, 0.6f, 0.4f);
                Widgets.Label(new Rect(4f, y + 3f, view.width, rowH), "TheNetwork_Settings_Unreadable".Translate(roster.unreadable[i].reasonKey ?? "?"));
                GUI.color = Color.white;
                y += rowH;
            }
            Widgets.EndScrollView();
        }

        private static void DrawRow(Rect r, CastTemplate t, NetworkSettings s)
        {
            Widgets.DrawHighlightIfMouseover(r);
            bool enabled = t.enabled;
            if (t.IsQuarantined)
            {
                GUI.color = new Color(1f, 0.6f, 0.4f);
                Widgets.Label(new Rect(r.x + 4f, r.y + 3f, r.width - 8f, r.height), t.DisplayLabel + " — " + "TheNetwork_Settings_QuarantinedRow".Translate(t.quarantined.reasonKey ?? "?", t.quarantined.message ?? ""));
                GUI.color = Color.white;
                return;
            }
            Widgets.Checkbox(new Vector2(r.x + 4f, r.y + 1f), ref enabled, 24f);
            if (enabled != t.enabled)
            {
                t.enabled = enabled;
                s.NeedsWrite = true;
            }
            Widgets.Label(new Rect(r.x + 34f, r.y + 3f, 300f, r.height), t.DisplayLabel);
            string info = ("TheNetwork_Provenance_" + t.provenance).Translate().Resolve();
            ContractorTemplate c = t as ContractorTemplate;
            if (c != null)
            {
                info += " · " + c.form + " · " + ("TheNetwork_Experience_" + c.startingExperience).Translate().Resolve() + " · " + ("TheNetwork_Fame_" + c.startingFame).Translate().Resolve()
                    + (c.canIssueWork ? " · " + "TheNetwork_Settings_CanIssue".Translate().Resolve() : "");
            }
            FixerTemplate f = t as FixerTemplate;
            if (f != null)
            {
                info += " · " + ("TheNetwork_Fee_" + f.feeBand).Translate().Resolve() + " · " + ("TheNetwork_Speed_" + f.speedBand).Translate().Resolve() + " · " + ("TheNetwork_Fame_" + f.startingFame).Translate().Resolve();
            }
            Widgets.Label(new Rect(r.x + 340f, r.y + 3f, r.width - 450f, r.height), info);
            if (Widgets.ButtonText(new Rect(r.xMax - 100f, r.y + 1f, 96f, r.height - 2f), "TheNetwork_Settings_Rename".Translate()))
            {
                Find.WindowStack.Add(new Dialog_RenameTemplate(t, s));
            }
        }
    }

    /// <summary>Renames a template. The template id never changes; existing saves keep their own names.</summary>
    public sealed class Dialog_RenameTemplate : Window
    {
        private readonly CastTemplate template;
        private readonly NetworkSettings settings;
        private string name;
        private string nick;

        public Dialog_RenameTemplate(CastTemplate template, NetworkSettings settings)
        {
            this.template = template;
            this.settings = settings;
            name = template.displayName ?? "";
            nick = template.nickname ?? "";
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
            doCloseX = true;
        }

        public override Vector2 InitialSize => new Vector2(420f, 220f);

        public override void DoWindowContents(Rect inRect)
        {
            Widgets.Label(new Rect(0f, 0f, inRect.width, 28f), "TheNetwork_Settings_RenameTitle".Translate());
            Widgets.Label(new Rect(0f, 40f, 90f, 28f), "TheNetwork_Settings_Name".Translate());
            name = Widgets.TextField(new Rect(95f, 38f, inRect.width - 95f, 28f), name);
            Widgets.Label(new Rect(0f, 76f, 90f, 28f), "TheNetwork_Settings_Nickname".Translate());
            nick = Widgets.TextField(new Rect(95f, 74f, inRect.width - 95f, 28f), nick);
            bool dup = settings.roster.ActiveNames().Contains(name) && name != template.displayName;
            if (dup) Widgets.Label(new Rect(0f, 110f, inRect.width, 28f), "TheNetwork_Settings_DuplicateName".Translate());
            if (Widgets.ButtonText(new Rect(inRect.width - 120f, inRect.height - 32f, 120f, 30f), "OK".Translate(), true, true, !string.IsNullOrEmpty(name?.Trim())))
            {
                template.displayName = name.Trim();
                template.nickname = string.IsNullOrEmpty(nick?.Trim()) ? null : nick.Trim();
                settings.NeedsWrite = true;
                NetworkMod.Instance?.WriteSettings();
                Close();
            }
        }
    }
}
