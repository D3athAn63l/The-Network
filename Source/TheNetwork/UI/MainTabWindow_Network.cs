using System;
using System.Collections.Generic;
using RimWorld;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Catalog;
using TheNetwork.Domain.Intel;
using TheNetwork.Kernel;
using UnityEngine;
using Verse;

namespace TheNetwork.UI
{
    /// <summary>
    /// The Network main tab (MainButtonDef TheNetwork_MainButton). Phase 2 shows Intel, Procurement,
    /// Contracts, Contractors and History; later tabs appear with their phases. Reading never needs a
    /// Comms Console; every outgoing command is gated and its button says why when disabled.
    /// </summary>
    public partial class MainTabWindow_Network : MainTabWindow
    {
        private enum Tab { Intel, Procurement, Contracts, Contractors, History }

        private enum CatalogSort { Name, Value, Mod }

        private Tab tab = Tab.Intel;
        private Vector2 contactsScroll, catalogScroll, requestsScroll, historyScroll;
        private SourceKey? selectedSource;
        private string selectedDef;
        private string search = "";
        private CatalogSort sort = CatalogSort.Name;
        private List<CatalogRowView> filtered;
        private int filteredVersion = -1;
        private string filteredKey;

        public override Vector2 RequestedTabSize => new Vector2(1180f, 760f);

        public override void DoWindowContents(Rect inRect)
        {
            NetworkRuntime rt = NetworkRuntime.Current;
            if (rt == null)
            {
                Widgets.Label(inRect, "TheNetwork_NotActive".Translate());
                return;
            }
            List<TabRecord> tabs = new List<TabRecord>
            {
                new TabRecord("TheNetwork_Tab_Intel".Translate(), () => tab = Tab.Intel, tab == Tab.Intel),
                new TabRecord("TheNetwork_Tab_Procurement".Translate(), () => tab = Tab.Procurement, tab == Tab.Procurement),
                new TabRecord("TheNetwork_Tab_Contracts".Translate(), () => tab = Tab.Contracts, tab == Tab.Contracts),
                new TabRecord("TheNetwork_Tab_Contractors".Translate(), () => tab = Tab.Contractors, tab == Tab.Contractors),
                new TabRecord("TheNetwork_Tab_History".Translate(), () => tab = Tab.History, tab == Tab.History)
            };
            Rect body = new Rect(inRect.x, inRect.y + 36f, inRect.width, inRect.height - 36f);
            Widgets.DrawMenuSection(body);
            TabDrawer.DrawTabs(body, tabs, 1, 200f);
            Rect content = body.ContractedBy(10f);
            if (!rt.EnsureStarted() && rt.Session.IsFailed)
            {
                // Readable, but nothing can be changed this session.
                GUI.color = new Color(1f, 0.55f, 0.45f);
                Widgets.Label(new Rect(content.x, content.y, content.width, 24f), "TheNetwork_StartupFailed".Translate(rt.Session.FailedStage ?? "?"));
                GUI.color = Color.white;
                content.yMin += 26f;
            }
            else if (rt.Inert)
            {
                GUI.color = Color.yellow;
                Widgets.Label(new Rect(content.x, content.y, content.width, 24f), "TheNetwork_Settings_Prepared".Translate());
                GUI.color = Color.white;
                content.yMin += 26f;
            }
            switch (tab)
            {
                case Tab.Intel: DrawIntel(content, rt); break;
                case Tab.Procurement: DrawProcurement(content, rt); break;
                case Tab.Contracts: DrawContracts(content, rt); break;
                case Tab.Contractors: DrawContractors(content, rt); break;
                default: DrawHistory(content, rt); break;
            }
        }

        // ================================================================== Intel tab

        private void DrawIntel(Rect r, NetworkRuntime rt)
        {
            string commsReason;
            bool comms = rt.Read.CommsUsable(out commsReason);
            Rect banner = new Rect(r.x, r.y, r.width, 24f);
            GUI.color = comms ? new Color(0.6f, 0.9f, 0.6f) : new Color(1f, 0.75f, 0.4f);
            Widgets.Label(banner, comms ? "TheNetwork_CommsOk".Translate().Resolve() : "TheNetwork_CommsMissing".Translate(("TheNetwork_Reason_" + commsReason).Translate()).Resolve());
            GUI.color = Color.white;

            float top = r.y + 28f;
            float leftW = r.width * 0.5f - 6f;
            Rect left = new Rect(r.x, top, leftW, r.yMax - top);
            Rect right = new Rect(r.x + leftW + 12f, top, r.width - leftW - 12f, r.yMax - top);

            Rect contactsRect = new Rect(left.x, left.y, left.width, 200f);
            DrawContacts(contactsRect, rt);
            Rect catalogRect = new Rect(left.x, contactsRect.yMax + 8f, left.width, left.yMax - contactsRect.yMax - 8f);
            DrawCatalog(catalogRect, rt);
            DrawRequests(right, rt);
        }

        private void DrawContacts(Rect r, NetworkRuntime rt)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(r.x, r.y, r.width, 24f), "TheNetwork_Contacts".Translate());
            List<ContactView> contacts = rt.Read.Contacts();
            Rect outRect = new Rect(r.x, r.y + 24f, r.width, r.height - 24f);
            float rowH = 40f;
            Rect view = new Rect(0f, 0f, outRect.width - 16f, contacts.Count * rowH);
            Widgets.BeginScrollView(outRect, ref contactsScroll, view);
            for (int i = 0; i < contacts.Count; i++)
            {
                ContactView c = contacts[i];
                Rect row = new Rect(0f, i * rowH, view.width, rowH - 2f);
                bool selected = selectedSource.HasValue && selectedSource.Value.ToString() == c.key.ToString();
                if (selected) Widgets.DrawHighlightSelected(row);
                else Widgets.DrawHighlightIfMouseover(row);
                if (!c.usable) GUI.color = Color.gray;
                Widgets.Label(new Rect(row.x + 4f, row.y, row.width - 8f, 20f), c.name + "  (" + c.kindLabel + ")");
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(row.x + 4f, row.y + 19f, row.width - 8f, 18f), c.usable ? c.descriptors : ("TheNetwork_Reason_" + c.reasonKey).Translate().Resolve());
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                if (c.usable && Widgets.ButtonInvisible(row)) selectedSource = c.key;
            }
            Widgets.EndScrollView();
        }

        private void DrawCatalog(Rect r, NetworkRuntime rt)
        {
            NetworkSettings s = NetworkMod.Settings;
            Widgets.Label(new Rect(r.x, r.y, 160f, 24f), "TheNetwork_Catalog".Translate());
            Widgets.Label(new Rect(r.x + 160f, r.y + 2f, 60f, 24f), "TheNetwork_Search".Translate());
            search = Widgets.TextField(new Rect(r.x + 220f, r.y, 180f, 24f), search ?? "");
            if (Widgets.ButtonText(new Rect(r.x + 405f, r.y, 110f, 24f), ("TheNetwork_Sort_" + sort).Translate()))
            {
                sort = (CatalogSort)(((int)sort + 1) % 3);
                filteredVersion = -1;
            }
            bool unusual = s != null && s.showUnusualItems;
            bool before = unusual;
            Widgets.CheckboxLabeled(new Rect(r.x, r.y + 26f, 260f, 24f), "TheNetwork_Settings_ShowUnusual".Translate(), ref unusual);
            if (unusual != before) rt.Commands.SetShowUnusual(unusual);

            List<CatalogRowView> rows = Filtered(rt, unusual);
            Rect outRect = new Rect(r.x, r.y + 52f, r.width, r.height - 52f - 34f);
            float rowH = 24f;
            Rect view = new Rect(0f, 0f, outRect.width - 16f, rows.Count * rowH);
            Widgets.BeginScrollView(outRect, ref catalogScroll, view);
            int first = Math.Max(0, (int)(catalogScroll.y / rowH) - 1);
            int last = Math.Min(rows.Count, first + (int)(outRect.height / rowH) + 3);
            for (int i = first; i < last; i++)
            {
                CatalogRowView row = rows[i];
                Rect rr = new Rect(0f, i * rowH, view.width, rowH);
                if (row.defName == selectedDef) Widgets.DrawHighlightSelected(rr);
                else Widgets.DrawHighlightIfMouseover(rr);
                if (!row.requestable) GUI.color = Color.gray;
                Widgets.Label(new Rect(rr.x + 4f, rr.y + 2f, rr.width * 0.45f, rowH), row.label);
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(rr.x + rr.width * 0.45f, rr.y + 4f, rr.width * 0.25f, rowH), row.modName);
                Widgets.Label(new Rect(rr.x + rr.width * 0.70f, rr.y + 4f, 70f, rowH), row.marketValue.ToStringMoney());
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                string tip = CatalogTip(row);
                TooltipHandler.TipRegion(rr, tip);
                Rect overBtn = new Rect(rr.xMax - 90f, rr.y + 1f, 88f, rowH - 2f);
                if (Widgets.ButtonText(overBtn, ("TheNetwork_Override_" + row.over).Translate(), true, true, !row.technical))
                {
                    string def = row.defName;
                    List<FloatMenuOption> opts = new List<FloatMenuOption>();
                    foreach (ItemOverride o in new[] { ItemOverride.Auto, ItemOverride.Allowed, ItemOverride.Blocked })
                    {
                        ItemOverride captured = o;
                        opts.Add(new FloatMenuOption(("TheNetwork_Override_" + o).Translate(), () => { rt.Commands.SetItemOverride(def, captured); filteredVersion = -1; }));
                    }
                    Find.WindowStack.Add(new FloatMenu(opts));
                }
                else if (Widgets.ButtonInvisible(new Rect(rr.x, rr.y, rr.width - 92f, rr.height)))
                {
                    selectedDef = row.defName;
                }
            }
            Widgets.EndScrollView();

            // Request button: topic + contact, never a quantity.
            Rect req = new Rect(r.x, r.yMax - 30f, r.width, 30f);
            CommandResult can = selectedSource.HasValue && selectedDef != null ? rt.Commands.CanSubmitIntel(selectedSource.Value, selectedDef) : CommandResult.Fail("SelectContactAndItem");
            if (Widgets.ButtonText(req, "TheNetwork_RequestIntel".Translate(), true, true, can.ok))
            {
                Find.WindowStack.Add(new Dialog_RequestIntel(rt, selectedSource.Value, selectedDef));
            }
            if (!can.ok) TooltipHandler.TipRegion(req, ("TheNetwork_Reason_" + can.reasonKey).Translate());
        }

        private static string CatalogTip(CatalogRowView row)
        {
            string tip = row.label + " (" + row.defName + ")\n" + "TheNetwork_Catalog_Verdict".Translate(("TheNetwork_Verdict_" + row.verdict).Translate()).Resolve();
            if (!string.IsNullOrEmpty(row.reasons)) tip += "\n" + "TheNetwork_Catalog_Reasons".Translate(row.reasons).Resolve();
            if (row.technical) tip += "\n" + "TheNetwork_Catalog_TechnicalNote".Translate().Resolve();
            if (!row.requestable && row.blockReason != null) tip += "\n" + ("TheNetwork_Reason_" + row.blockReason).Translate().Resolve();
            return tip;
        }

        private List<CatalogRowView> Filtered(NetworkRuntime rt, bool showUnusual)
        {
            string key = (search ?? "").ToLowerInvariant() + "|" + showUnusual + "|" + sort;
            if (filtered != null && filteredVersion == Kernel.StateVersion.Current && filteredKey == key) return filtered;
            List<CatalogRowView> all = rt.Read.CatalogRows();
            string needle = (search ?? "").Trim().ToLowerInvariant();
            List<CatalogRowView> list = new List<CatalogRowView>();
            for (int i = 0; i < all.Count; i++)
            {
                CatalogRowView row = all[i];
                if (row.over == ItemOverride.Blocked && !showUnusual) continue;
                if (!showUnusual && row.verdict != CatalogVerdict.Eligible && row.over != ItemOverride.Allowed) continue;
                if (needle.Length > 0 && row.labelLower.IndexOf(needle, StringComparison.Ordinal) < 0 && row.modName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                list.Add(row);
            }
            switch (sort)
            {
                case CatalogSort.Value: list.Sort((a, b) => b.marketValue.CompareTo(a.marketValue)); break;
                case CatalogSort.Mod: list.Sort((a, b) => { int c = string.Compare(a.modName, b.modName, StringComparison.OrdinalIgnoreCase); return c != 0 ? c : string.Compare(a.label, b.label, StringComparison.OrdinalIgnoreCase); }); break;
                default: break; // catalog order is already by label
            }
            filtered = list;
            filteredVersion = Kernel.StateVersion.Current;
            filteredKey = key;
            return list;
        }

        private void DrawRequests(Rect r, NetworkRuntime rt)
        {
            Widgets.Label(new Rect(r.x, r.y, r.width, 24f), "TheNetwork_Searches".Translate());
            List<IntelRowView> rows = rt.Read.IntelRows();
            Rect outRect = new Rect(r.x, r.y + 26f, r.width, r.height - 26f);
            float viewH = 0f;
            for (int i = 0; i < rows.Count; i++) viewH += RowHeight(rows[i]);
            Rect view = new Rect(0f, 0f, outRect.width - 16f, Math.Max(viewH, 10f));
            Widgets.BeginScrollView(outRect, ref requestsScroll, view);
            float y = 0f;
            if (rows.Count == 0) Widgets.Label(new Rect(4f, 4f, view.width, 48f), "TheNetwork_NoSearches".Translate());
            for (int i = 0; i < rows.Count; i++)
            {
                float h = RowHeight(rows[i]);
                DrawRequest(new Rect(0f, y, view.width, h - 6f), rows[i], rt);
                y += h;
            }
            Widgets.EndScrollView();
        }

        private static float RowHeight(IntelRowView row)
        {
            float h = 96f;
            for (int i = 0; i < row.leads.Count; i++) h += LeadHeight(row.leads[i]);
            return h;
        }

        private static float LeadHeight(LeadView l)
        {
            int lines = 2;
            for (int i = 0; i < l.summary.Length; i++) if (l.summary[i] == '\n') lines++;
            return 16f * lines + 30f;
        }

        private void DrawRequest(Rect r, IntelRowView row, NetworkRuntime rt)
        {
            Widgets.DrawMenuSection(r);
            Rect inner = r.ContractedBy(6f);
            Widgets.Label(new Rect(inner.x, inner.y, inner.width, 22f), "TheNetwork_RequestTitle".Translate(row.itemLabel, row.sourceName, row.stateLabel));
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(inner.x, inner.y + 22f, inner.width, 18f), row.timing);
            Widgets.Label(new Rect(inner.x, inner.y + 38f, inner.width, 18f), row.terms + " · " + row.money);
            Text.Font = GameFont.Small;

            float bx = inner.x;
            float by = inner.y + 58f;
            if (row.state == IntelState.AwaitingDecision)
            {
                CommandResult cc = rt.Commands.CanContinueIntel(row.id);
                Rect b1 = new Rect(bx, by, 150f, 26f);
                if (Widgets.ButtonText(b1, "TheNetwork_ContinueSearch".Translate(), true, true, cc.ok)) Report(rt.Commands.ContinueIntel(row.id));
                if (!cc.ok) TooltipHandler.TipRegion(b1, ("TheNetwork_Reason_" + cc.reasonKey).Translate());
                CommandResult ce = rt.Commands.CanEndIntel(row.id);
                Rect b2 = new Rect(bx + 156f, by, 150f, 26f);
                if (Widgets.ButtonText(b2, "TheNetwork_EndSearch".Translate(), true, true, ce.ok)) Report(rt.Commands.EndIntel(row.id));
                if (!ce.ok) TooltipHandler.TipRegion(b2, ("TheNetwork_Reason_" + ce.reasonKey).Translate());
            }
            else if (row.state == IntelState.Searching)
            {
                CommandResult cx = rt.Commands.CanCancelIntel(row.id);
                Rect b3 = new Rect(bx, by, 150f, 26f);
                if (Widgets.ButtonText(b3, "TheNetwork_CancelSearch".Translate(), true, true, cx.ok))
                {
                    IntelRequestId id = row.id;
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("TheNetwork_CancelConfirm".Translate(), () => Report(rt.Commands.CancelIntel(id)), true));
                }
                if (!cx.ok) TooltipHandler.TipRegion(b3, ("TheNetwork_Reason_" + cx.reasonKey).Translate());
            }

            float ly = by + 32f;
            for (int i = 0; i < row.leads.Count; i++)
            {
                LeadView l = row.leads[i];
                float h = LeadHeight(l);
                Rect lr = new Rect(inner.x + 8f, ly, inner.width - 8f, h - 4f);
                Widgets.DrawBoxSolid(lr, new Color(1f, 1f, 1f, 0.04f));
                Widgets.Label(new Rect(lr.x + 4f, lr.y + 2f, lr.width - 130f, 22f), l.title + " — " + l.stateLabel);
                if (l.canLookAt && Widgets.ButtonText(new Rect(lr.xMax - 124f, lr.y + 2f, 120f, 24f), "TheNetwork_LookAtSite".Translate()))
                {
                    rt.Read.LookAt(l.opportunity);
                }
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(lr.x + 4f, lr.y + 26f, lr.width - 8f, h - 30f), l.summary);
                Text.Font = GameFont.Small;
                ly += h;
            }
        }

        private static void Report(CommandResult result)
        {
            if (!result.ok) Messages.Message(("TheNetwork_Reason_" + result.reasonKey).Translate(), MessageTypeDefOf.RejectInput, false);
        }

        // ================================================================== History tab

        private void DrawHistory(Rect r, NetworkRuntime rt)
        {
            Widgets.Label(new Rect(r.x, r.y, r.width, 24f), rt.Read.PlayerSummaryLine());
            List<HistoryEntryView> rows = rt.Read.HistoryRows();
            Rect outRect = new Rect(r.x, r.y + 30f, r.width, r.height - 30f);
            float rowH = 26f;
            Rect view = new Rect(0f, 0f, outRect.width - 16f, Math.Max(rows.Count * rowH, 10f));
            Widgets.BeginScrollView(outRect, ref historyScroll, view);
            if (rows.Count == 0) Widgets.Label(new Rect(4f, 4f, view.width, 40f), "TheNetwork_History_Empty".Translate());
            int first = Math.Max(0, (int)(historyScroll.y / rowH) - 1);
            int last = Math.Min(rows.Count, first + (int)(outRect.height / rowH) + 3);
            for (int i = first; i < last; i++)
            {
                HistoryEntryView e = rows[i];
                if (e.date == null) e.date = Narrative.Date(e.tick); // only rows actually drawn
                Rect rr = new Rect(0f, i * rowH, view.width, rowH);
                Widgets.DrawHighlightIfMouseover(rr);
                if (e.fromJournal) GUI.color = new Color(0.8f, 0.8f, 0.8f);
                else if (e.importance >= Kernel.Importance.Major) GUI.color = new Color(1f, 0.85f, 0.5f);
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(rr.x + 4f, rr.y + 5f, 150f, rowH), e.date);
                Text.Font = GameFont.Small;
                Widgets.Label(new Rect(rr.x + 160f, rr.y + 2f, rr.width - 164f, rowH), e.line);
                GUI.color = Color.white;
            }
            Widgets.EndScrollView();
        }
    }

    /// <summary>
    /// The request dialog: the chosen contact, the item, the fee that contact charges and how it
    /// continues. There is deliberately no quantity field (DATA_MODEL § 8).
    /// </summary>
    public sealed class Dialog_RequestIntel : Window
    {
        private readonly NetworkRuntime rt;
        private readonly SourceKey source;
        private readonly string defName;

        public Dialog_RequestIntel(NetworkRuntime rt, SourceKey source, string defName)
        {
            this.rt = rt;
            this.source = source;
            this.defName = defName;
            forcePause = true;
            absorbInputAroundWindow = true;
            doCloseX = true;
            closeOnClickedOutside = true;
        }

        public override Vector2 InitialSize => new Vector2(520f, 320f);

        public override void DoWindowContents(Rect inRect)
        {
            SearchTerms t = rt.Ctx.Intel.PreviewTerms(source, defName);
            string item = rt.Ctx.catalog.Facts(defName)?.label ?? defName;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 32f), "TheNetwork_RequestDialogTitle".Translate(item));
            Text.Font = GameFont.Small;
            if (t == null)
            {
                Widgets.Label(new Rect(0f, 40f, inRect.width, 60f), "TheNetwork_Reason_SourceMissing".Translate());
                return;
            }
            string body = "TheNetwork_RequestDialogBody".Translate(t.sourceName, item, t.initialFee, Narrative.Estimate(t.speedBand), Narrative.ContinuationPolicy(t)).Resolve();
            Widgets.Label(new Rect(0f, 40f, inRect.width, inRect.height - 90f), body);
            CommandResult can = rt.Commands.CanSubmitIntel(source, defName);
            Rect ok = new Rect(inRect.width - 180f, inRect.height - 34f, 180f, 32f);
            if (Widgets.ButtonText(ok, "TheNetwork_RequestConfirm".Translate(t.initialFee), true, true, can.ok))
            {
                CommandResult res = rt.Commands.SubmitIntel(source, defName);
                if (res.ok)
                {
                    Messages.Message("TheNetwork_Message_Submitted".Translate(t.sourceName, item), MessageTypeDefOf.TaskCompletion, false);
                    Close();
                }
                else
                {
                    Messages.Message(("TheNetwork_Reason_" + res.reasonKey).Translate(), MessageTypeDefOf.RejectInput, false);
                }
            }
            if (!can.ok)
            {
                GUI.color = new Color(1f, 0.7f, 0.4f);
                Widgets.Label(new Rect(0f, inRect.height - 30f, inRect.width - 190f, 28f), ("TheNetwork_Reason_" + can.reasonKey).Translate());
                GUI.color = Color.white;
            }
        }
    }
}
