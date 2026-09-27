using System;
using System.Collections.Generic;
using RimWorld;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Catalog;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;
using UnityEngine;
using Verse;

namespace TheNetwork.UI
{
    /// <summary>
    /// Phase 2 tabs: Procurement (ask for an exact item and quantity through a Fixer, compare quotes,
    /// answer the contractor), Contracts (everything you have asked for) and Contractors (who is out
    /// there, in words). Descriptors only: no hidden stat, probability or unreported outcome is shown.
    /// </summary>
    public partial class MainTabWindow_Network
    {
        private string procDef;
        private int procQty = 1;
        private string procQtyBuffer = "1";
        private ActorId procFixer;
        private ProcurementMode procMode = ProcurementMode.Open;
        private ActorId procDirect;
        private int procPremium;
        private string procPremiumBuffer = "0";
        private bool procInsurance;
        private bool contractorsOnlyKnown;
        private Vector2 procCatalogScroll, procListScroll, contractsScroll, contractorsScroll;

        private static string Tr(string key) => key.Translate().Resolve();

        // Command checks behind buttons can count beacon silver; they are cached briefly instead of
        // re-run every frame (cleared whenever Network state changes, and every second).
        private readonly Dictionary<string, CommandResult> canCache = new Dictionary<string, CommandResult>();
        private int canCacheVersion = -1;
        private float canCacheTime;

        private CommandResult Can(string key, Func<CommandResult> check)
        {
            if (canCacheVersion != StateVersion.Current || Time.realtimeSinceStartup - canCacheTime > 1f)
            {
                canCache.Clear();
                canCacheVersion = StateVersion.Current;
                canCacheTime = Time.realtimeSinceStartup;
            }
            CommandResult r;
            if (!canCache.TryGetValue(key, out r))
            {
                r = check();
                canCache[key] = r;
            }
            return r;
        }

        // ================================================================== Procurement tab

        private void DrawProcurement(Rect r, NetworkRuntime rt)
        {
            string commsReason;
            bool comms = rt.Read.CommsUsable(out commsReason);
            GUI.color = comms ? new Color(0.6f, 0.9f, 0.6f) : new Color(1f, 0.75f, 0.4f);
            Widgets.Label(new Rect(r.x, r.y, r.width, 24f), comms ? Tr("TheNetwork_CommsOk") : "TheNetwork_CommsMissing".Translate(("TheNetwork_Reason_" + commsReason).Translate()).Resolve());
            GUI.color = Color.white;
            float top = r.y + 28f;
            float leftW = r.width * 0.36f;
            DrawProcurementCatalog(new Rect(r.x, top, leftW, r.yMax - top), rt);
            Rect right = new Rect(r.x + leftW + 12f, top, r.width - leftW - 12f, r.yMax - top);
            Rect form = new Rect(right.x, right.y, right.width, 250f);
            DrawProcurementForm(form, rt);
            Rect list = new Rect(right.x, form.yMax + 8f, right.width, right.yMax - form.yMax - 8f);
            List<ContractRowView> rows = new List<ContractRowView>();
            foreach (ContractRowView row in rt.ContractsRead.Contracts()) if (!row.terminal && (row.seeking || row.needsDecision)) rows.Add(row);
            Widgets.Label(new Rect(list.x, list.y, list.width, 24f), Tr("TheNetwork_Proc_Waiting"));
            DrawContractList(new Rect(list.x, list.y + 26f, list.width, list.height - 26f), rt, rows, ref procListScroll, Tr("TheNetwork_Proc_NothingWaiting"));
        }

        private void DrawProcurementCatalog(Rect r, NetworkRuntime rt)
        {
            Widgets.Label(new Rect(r.x, r.y, 70f, 24f), Tr("TheNetwork_Search"));
            search = Widgets.TextField(new Rect(r.x + 70f, r.y, r.width - 70f, 24f), search ?? "");
            NetworkSettings s = NetworkMod.Settings;
            List<CatalogRowView> rows = Filtered(rt, s != null && s.showUnusualItems);
            Rect outRect = new Rect(r.x, r.y + 28f, r.width, r.height - 28f);
            float rowH = 24f;
            Rect view = new Rect(0f, 0f, outRect.width - 16f, rows.Count * rowH);
            Widgets.BeginScrollView(outRect, ref procCatalogScroll, view);
            int first = Math.Max(0, (int)(procCatalogScroll.y / rowH) - 1);
            int last = Math.Min(rows.Count, first + (int)(outRect.height / rowH) + 3);
            for (int i = first; i < last; i++)
            {
                CatalogRowView row = rows[i];
                Rect rr = new Rect(0f, i * rowH, view.width, rowH);
                if (row.defName == procDef) Widgets.DrawHighlightSelected(rr);
                else Widgets.DrawHighlightIfMouseover(rr);
                if (!row.requestable) GUI.color = Color.gray;
                Widgets.Label(new Rect(rr.x + 4f, rr.y + 2f, rr.width * 0.62f, rowH), row.label);
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(rr.x + rr.width * 0.64f, rr.y + 4f, rr.width * 0.36f, rowH), row.marketValue.ToStringMoney() + " · " + row.modName);
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                TooltipHandler.TipRegion(rr, CatalogTip(row));
                if (row.requestable && Widgets.ButtonInvisible(rr))
                {
                    procDef = row.defName;
                    procQty = Math.Max(1, procQty);
                }
            }
            Widgets.EndScrollView();
        }

        private void DrawProcurementForm(Rect r, NetworkRuntime rt)
        {
            Widgets.DrawMenuSection(r);
            Rect inner = r.ContractedBy(8f);
            float y = inner.y;
            ItemFacts facts = procDef == null ? null : rt.Ctx.catalog.Facts(procDef);
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inner.x, y, inner.width, 30f), facts == null ? Tr("TheNetwork_Proc_ChooseItem") : "TheNetwork_Proc_Title".Translate(facts.label).Resolve());
            Text.Font = GameFont.Small;
            y += 32f;

            // Exact quantity: procurement asks for exactly this many.
            Widgets.Label(new Rect(inner.x, y, 110f, 24f), Tr("TheNetwork_Proc_Quantity"));
            int max = facts == null ? 1 : Valuation.MaxQuantity(facts, ContractKindRegistry.Get(Domain.Contractors.ContractKinds.Procurement));
            Widgets.TextFieldNumeric(new Rect(inner.x + 110f, y, 90f, 24f), ref procQty, ref procQtyBuffer, 1, max);
            Widgets.Label(new Rect(inner.x + 210f, y, 200f, 24f), Tr("TheNetwork_Proc_Premium"));
            Widgets.TextFieldNumeric(new Rect(inner.x + 410f, y, 90f, 24f), ref procPremium, ref procPremiumBuffer, 0, ProcurementService.MaxPremium);
            TooltipHandler.TipRegion(new Rect(inner.x + 210f, y, 290f, 24f), Tr("TheNetwork_Proc_PremiumTip"));
            y += 28f;

            // Fixer.
            List<FixerChoiceView> fixers = rt.ContractsRead.Fixers();
            FixerChoiceView fixer = null;
            foreach (FixerChoiceView f in fixers) if (f.id == procFixer) fixer = f;
            Widgets.Label(new Rect(inner.x, y, 110f, 24f), Tr("TheNetwork_Proc_Fixer"));
            if (Widgets.ButtonText(new Rect(inner.x + 110f, y, 220f, 24f), fixer?.name ?? Tr("TheNetwork_Proc_ChooseFixer")))
            {
                List<FloatMenuOption> opts = new List<FloatMenuOption>();
                foreach (FixerChoiceView f in fixers)
                {
                    ActorId id = f.id;
                    opts.Add(new FloatMenuOption(f.name + " — " + f.line, () => procFixer = id));
                }
                if (opts.Count == 0) opts.Add(new FloatMenuOption(Tr("TheNetwork_Proc_NoFixers"), null));
                Find.WindowStack.Add(new FloatMenu(opts));
            }
            if (fixer != null)
            {
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(inner.x + 336f, y + 4f, inner.width - 336f, 24f), fixer.line);
                Text.Font = GameFont.Small;
            }
            y += 28f;

            // Open or Direct.
            Widgets.Label(new Rect(inner.x, y, 110f, 24f), Tr("TheNetwork_Proc_Mode"));
            if (Widgets.ButtonText(new Rect(inner.x + 110f, y, 100f, 24f), Tr("TheNetwork_Mode_Open"), true, true, procMode != ProcurementMode.Open)) procMode = ProcurementMode.Open;
            if (Widgets.ButtonText(new Rect(inner.x + 214f, y, 100f, 24f), Tr("TheNetwork_Mode_Direct"), true, true, procMode != ProcurementMode.Direct)) procMode = ProcurementMode.Direct;
            if (procMode == ProcurementMode.Direct)
            {
                string name = procDirect.IsValid ? rt.Ctx.actors.NameOf(procDirect) : Tr("TheNetwork_Proc_ChooseContractor");
                if (Widgets.ButtonText(new Rect(inner.x + 320f, y, inner.width - 320f, 24f), name))
                {
                    List<FloatMenuOption> opts = new List<FloatMenuOption>();
                    foreach (ContractorChoiceView c in rt.ContractsRead.DirectChoices())
                    {
                        ActorId id = c.id;
                        opts.Add(new FloatMenuOption(c.name + " — " + c.line, () => procDirect = id));
                    }
                    Find.WindowStack.Add(new FloatMenu(opts));
                }
            }
            else
            {
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(inner.x + 320f, y + 4f, inner.width - 320f, 24f), Tr("TheNetwork_Proc_OpenHint"));
                Text.Font = GameFont.Small;
            }
            y += 28f;
            Widgets.CheckboxLabeled(new Rect(inner.x, y, 320f, 24f), Tr("TheNetwork_Proc_WantInsurance"), ref procInsurance);
            y += 28f;

            // Preview: public market prices only; the real price comes from the quotes.
            Text.Font = GameFont.Tiny;
            string preview = facts == null ? Tr("TheNetwork_Proc_PreviewNone")
                : "TheNetwork_Proc_Preview".Translate(procQty, facts.label, ((int)Math.Ceiling(Math.Max(0f, facts.marketValue) * procQty)).ToString()).Resolve();
            Widgets.Label(new Rect(inner.x, y, inner.width, 36f), preview);
            Text.Font = GameFont.Small;

            ProcurementRequest req = new ProcurementRequest
            {
                defName = procDef,
                count = procQty,
                broker = procFixer,
                mode = procMode,
                invited = procMode == ProcurementMode.Direct ? procDirect : ActorId.None,
                premiumContribution = procPremium,
                wantsInsurance = procInsurance
            };
            CommandResult can = procDef == null ? CommandResult.Fail("NoItem") : (!procFixer.IsValid ? CommandResult.Fail("NoBroker")
                : Can("post|" + procDef + "|" + procQty + "|" + procFixer.Value + "|" + procMode + "|" + procDirect.Value + "|" + procPremium, () => rt.Commands.CanPostProcurement(req)));
            if (can.ok && procMode == ProcurementMode.Direct && !procDirect.IsValid) can = CommandResult.Fail("ChooseContractor");
            Rect post = new Rect(inner.xMax - 220f, inner.yMax - 30f, 220f, 30f);
            if (Widgets.ButtonText(post, Tr("TheNetwork_Proc_Post"), true, true, can.ok))
            {
                CommandResult res = rt.Commands.PostProcurement(req);
                if (res.ok) Messages.Message("TheNetwork_Message_Posted".Translate(procQty, facts?.label ?? "?", fixer?.name ?? "?"), MessageTypeDefOf.TaskCompletion, false);
                else Report(res);
            }
            if (!can.ok)
            {
                TooltipHandler.TipRegion(post, ("TheNetwork_Reason_" + can.reasonKey).Translate());
                GUI.color = new Color(1f, 0.7f, 0.4f);
                Widgets.Label(new Rect(inner.x, inner.yMax - 26f, inner.width - 230f, 24f), ("TheNetwork_Reason_" + can.reasonKey).Translate());
                GUI.color = Color.white;
            }
        }

        // ================================================================== contract cards (shared)

        private void DrawContractList(Rect outRect, NetworkRuntime rt, List<ContractRowView> rows, ref Vector2 scroll, string empty)
        {
            float width = outRect.width - 16f;
            float total = 0f;
            for (int i = 0; i < rows.Count; i++) total += CardHeight(rows[i], width) + 6f;
            Rect view = new Rect(0f, 0f, width, Math.Max(total, 10f));
            Widgets.BeginScrollView(outRect, ref scroll, view);
            if (rows.Count == 0) Widgets.Label(new Rect(4f, 4f, width, 40f), empty);
            float y = 0f;
            for (int i = 0; i < rows.Count; i++)
            {
                float h = CardHeight(rows[i], width);
                DrawContractCard(new Rect(0f, y, width, h), rt, rows[i]);
                y += h + 6f;
            }
            Widgets.EndScrollView();
        }

        private static List<string> CardLines(ContractRowView row)
        {
            List<string> lines = new List<string>();
            string who = row.contractor != null ? "TheNetwork_Card_With".Translate(row.contractor, row.broker ?? "?").Resolve() : "TheNetwork_Card_Via".Translate(row.broker ?? "?", row.mode).Resolve();
            lines.Add(who);
            if (row.eta != null) lines.Add(row.eta);
            if (row.money != null) lines.Add(row.money);
            if (row.insurance != null) lines.Add(row.insurance);
            if (row.result != null) lines.Add(row.result);
            if (row.lineage != null) lines.Add(row.lineage);
            return lines;
        }

        private static float CardHeight(ContractRowView row, float width)
        {
            float h = 30f + CardLines(row).Count * 18f;
            for (int i = 0; i < row.offers.Count; i++) h += row.offers[i].open ? 96f : 22f;
            h += row.refusals.Count * 18f;
            if (HasActions(row)) h += 34f;
            if (row.status == ContractStatus.Renegotiating) h += 20f;
            return h + 8f;
        }

        private static bool HasActions(ContractRowView row)
        {
            return !row.terminal;
        }

        private void DrawContractCard(Rect r, NetworkRuntime rt, ContractRowView row)
        {
            Widgets.DrawMenuSection(r);
            Rect inner = r.ContractedBy(6f);
            float y = inner.y;
            if (row.terminal) GUI.color = new Color(0.8f, 0.8f, 0.8f);
            Widgets.Label(new Rect(inner.x, y, inner.width, 24f), row.title + "  —  " + row.statusLine);
            GUI.color = Color.white;
            y += 24f;
            Text.Font = GameFont.Tiny;
            foreach (string line in CardLines(row))
            {
                Widgets.Label(new Rect(inner.x, y, inner.width, 18f), line);
                y += 18f;
            }
            Text.Font = GameFont.Small;
            for (int i = 0; i < row.offers.Count; i++) y = DrawOffer(new Rect(inner.x + 6f, y + 2f, inner.width - 6f, 0f), rt, row.offers[i]);
            Text.Font = GameFont.Tiny;
            for (int i = 0; i < row.refusals.Count; i++)
            {
                GUI.color = new Color(0.85f, 0.75f, 0.65f);
                Widgets.Label(new Rect(inner.x + 6f, y, inner.width - 6f, 18f), row.refusals[i]);
                GUI.color = Color.white;
                y += 18f;
            }
            Text.Font = GameFont.Small;
            if (HasActions(row)) DrawContractActions(new Rect(inner.x, y + 4f, inner.width, 28f), rt, row);
        }

        private float DrawOffer(Rect r, NetworkRuntime rt, OfferView o)
        {
            if (!o.open)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = Color.gray;
                Widgets.Label(new Rect(r.x, r.y, r.width, 20f), o.bidder + " — " + o.stateLabel);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                return r.y + 22f;
            }
            Rect box = new Rect(r.x, r.y, r.width, 92f);
            Widgets.DrawBoxSolid(box, new Color(1f, 1f, 1f, 0.05f));
            Widgets.Label(new Rect(box.x + 4f, box.y + 2f, box.width - 8f, 22f), o.bidder + (o.newcomer ? "  (" + Tr("TheNetwork_Offer_Newcomer") + ")" : "") + " — " + o.bidderLine);
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(box.x + 4f, box.y + 24f, box.width - 8f, 18f), "TheNetwork_OfferTerms".Translate(o.price, o.deposit, o.balance, o.eta).Resolve());
            string extra = (o.insurance ?? Tr("TheNetwork_Offer_NoInsurance")) + (o.conditions != null ? " · " + o.conditions : "") + " · " + o.validity;
            Widgets.Label(new Rect(box.x + 4f, box.y + 42f, box.width - 8f, 18f), extra);
            Text.Font = GameFont.Small;
            float bx = box.x + 4f, by = box.y + 62f;
            OfferId id = o.id;
            CommandResult plain = Can("accept|" + id.Value, () => rt.Commands.CanAcceptOffer(id, false));
            Rect b1 = new Rect(bx, by, 200f, 26f);
            if (Widgets.ButtonText(b1, "TheNetwork_Offer_Accept".Translate(o.dueWithout), true, true, plain.ok)) Report(rt.Commands.AcceptOffer(id, false));
            if (!plain.ok) TooltipHandler.TipRegion(b1, ("TheNetwork_Reason_" + plain.reasonKey).Translate());
            bx += 204f;
            if (o.insurable)
            {
                CommandResult ins = Can("acceptIns|" + id.Value, () => rt.Commands.CanAcceptOffer(id, true));
                Rect b2 = new Rect(bx, by, 220f, 26f);
                if (Widgets.ButtonText(b2, "TheNetwork_Offer_AcceptInsured".Translate(o.dueWith), true, true, ins.ok)) Report(rt.Commands.AcceptOffer(id, true));
                if (!ins.ok) TooltipHandler.TipRegion(b2, ("TheNetwork_Reason_" + ins.reasonKey).Translate());
                bx += 224f;
            }
            if (Widgets.ButtonText(new Rect(bx, by, 110f, 26f), Tr("TheNetwork_Offer_Decline"))) Report(rt.Commands.DeclineOffer(id));
            return r.y + 96f;
        }

        private void DrawContractActions(Rect r, NetworkRuntime rt, ContractRowView row)
        {
            ContractId id = row.id;
            float x = r.x;
            if (row.status == ContractStatus.Renegotiating && row.subStatus == SubStatus.WorseThanExpected)
            {
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(r.x, r.y - 2f, r.width, 20f), "TheNetwork_Card_WorseAsk".Translate(row.askExtra, row.askReduced, row.quantity).Resolve());
                Text.Font = GameFont.Small;
                r.y += 20f;
                x = Action(r, x, 190f, "TheNetwork_Act_PayMore".Translate(row.askExtra).Resolve(), Can("payMore|" + id.Value, () => rt.Commands.CanRespondWorse(id, WorseChoice.PayMore)), () => rt.Commands.RespondWorse(id, WorseChoice.PayMore));
                x = Action(r, x, 190f, "TheNetwork_Act_Reduce".Translate(row.askReduced).Resolve(), rt.Commands.CanRespondWorse(id, WorseChoice.AcceptReduced), () => rt.Commands.RespondWorse(id, WorseChoice.AcceptReduced));
                x = Action(r, x, 120f, Tr("TheNetwork_Act_Refuse"), rt.Commands.CanRespondWorse(id, WorseChoice.Refuse), () => rt.Commands.RespondWorse(id, WorseChoice.Refuse));
                Confirmed(r, x, 120f, Tr("TheNetwork_Act_Cancel"), rt.Commands.CanCancelContract(id), () => rt.Commands.CancelContract(id), Tr("TheNetwork_Act_CancelConfirmLate"));
                return;
            }
            if (row.status == ContractStatus.Renegotiating && row.subStatus == SubStatus.PartialResult)
            {
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(r.x, r.y - 2f, r.width, 20f), "TheNetwork_Card_PartialAsk".Translate(row.secured, row.quantity).Resolve());
                Text.Font = GameFont.Small;
                r.y += 20f;
                x = Action(r, x, 150f, Tr("TheNetwork_Act_AcceptPartial"), rt.Commands.CanRespondPartial(id, PartialChoice.AcceptPartial), () => rt.Commands.RespondPartial(id, PartialChoice.AcceptPartial));
                x = Action(r, x, 190f, Tr("TheNetwork_Act_AcceptContinue"), rt.Commands.CanRespondPartial(id, PartialChoice.AcceptAndContinue), () => rt.Commands.RespondPartial(id, PartialChoice.AcceptAndContinue));
                x = Action(r, x, 190f, Tr("TheNetwork_Act_AskSame"), rt.Commands.CanRespondPartial(id, PartialChoice.AskSameContractor), () => rt.Commands.RespondPartial(id, PartialChoice.AskSameContractor));
                Confirmed(r, x, 120f, Tr("TheNetwork_Act_Refuse"), rt.Commands.CanRespondPartial(id, PartialChoice.Refuse), () => rt.Commands.RespondPartial(id, PartialChoice.Refuse), Tr("TheNetwork_Act_RefusePartialConfirm"));
                return;
            }
            if (row.subStatus == SubStatus.AwaitingPayment)
            {
                x = Action(r, x, 200f, "TheNetwork_Act_PayBalance".Translate(row.balanceDue).Resolve(), Can("balance|" + id.Value, () => rt.Commands.CanPayBalance(id)), () => rt.Commands.PayBalance(id));
                return;
            }
            if (row.status == ContractStatus.Unfilled)
            {
                x = Action(r, x, 150f, Tr("TheNetwork_Act_Repost"), rt.Commands.CanRepost(id), () => rt.Commands.Repost(id, false));
                x = Action(r, x, 190f, Tr("TheNetwork_Act_RepostOpen"), rt.Commands.CanRepost(id), () => rt.Commands.Repost(id, true));
            }
            CommandResult cancel = rt.Commands.CanCancelContract(id);
            if (cancel.ok) Confirmed(r, x, 120f, Tr("TheNetwork_Act_Cancel"), cancel, () => rt.Commands.CancelContract(id), row.seeking ? Tr("TheNetwork_Act_CancelConfirmFree") : Tr("TheNetwork_Act_CancelConfirmAwarded"));
        }

        private static float Action(Rect r, float x, float w, string label, CommandResult can, Func<CommandResult> act)
        {
            Rect b = new Rect(x, r.y, w, 26f);
            if (Widgets.ButtonText(b, label, true, true, can.ok)) Report(act());
            if (!can.ok) TooltipHandler.TipRegion(b, ("TheNetwork_Reason_" + can.reasonKey).Translate());
            return x + w + 4f;
        }

        private static float Confirmed(Rect r, float x, float w, string label, CommandResult can, Func<CommandResult> act, string question)
        {
            Rect b = new Rect(x, r.y, w, 26f);
            if (Widgets.ButtonText(b, label, true, true, can.ok))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(question, () => Report(act()), true));
            }
            if (!can.ok) TooltipHandler.TipRegion(b, ("TheNetwork_Reason_" + can.reasonKey).Translate());
            return x + w + 4f;
        }

        // ================================================================== Contracts tab

        private void DrawContracts(Rect r, NetworkRuntime rt)
        {
            List<ContractRowView> rows = rt.ContractsRead.Contracts();
            int live = 0;
            for (int i = 0; i < rows.Count; i++) if (!rows[i].terminal) live++;
            Widgets.Label(new Rect(r.x, r.y, r.width, 24f), "TheNetwork_Contracts_Header".Translate(live, rows.Count - live).Resolve());
            DrawContractList(new Rect(r.x, r.y + 28f, r.width, r.height - 28f), rt, rows, ref contractsScroll, Tr("TheNetwork_Contracts_None"));
        }

        // ================================================================== Contractors tab

        private void DrawContractors(Rect r, NetworkRuntime rt)
        {
            List<ContractorCardView> all = rt.ContractsRead.Cards();
            List<ContractorCardView> cards = new List<ContractorCardView>();
            for (int i = 0; i < all.Count; i++) if (!contractorsOnlyKnown || all[i].dealtWith) cards.Add(all[i]);
            Widgets.Label(new Rect(r.x, r.y, 400f, 24f), "TheNetwork_Contractors_Header".Translate(all.Count).Resolve());
            Widgets.CheckboxLabeled(new Rect(r.xMax - 300f, r.y, 300f, 24f), Tr("TheNetwork_Contractors_OnlyKnown"), ref contractorsOnlyKnown);
            Rect outRect = new Rect(r.x, r.y + 28f, r.width, r.height - 28f);
            float rowH = 82f;
            Rect view = new Rect(0f, 0f, outRect.width - 16f, Math.Max(cards.Count * rowH, 10f));
            Widgets.BeginScrollView(outRect, ref contractorsScroll, view);
            int first = Math.Max(0, (int)(contractorsScroll.y / rowH) - 1);
            int last = Math.Min(cards.Count, first + (int)(outRect.height / rowH) + 3);
            for (int i = first; i < last; i++)
            {
                ContractorCardView c = cards[i];
                Rect card = new Rect(0f, i * rowH, view.width, rowH - 4f);
                Widgets.DrawMenuSection(card);
                Rect inner = card.ContractedBy(5f);
                if (c.ended) GUI.color = Color.gray;
                Widgets.Label(new Rect(inner.x, inner.y, inner.width * 0.6f, 22f), c.name + "  (" + c.kind + ")");
                Widgets.Label(new Rect(inner.x + inner.width * 0.6f, inner.y, inner.width * 0.4f, 22f), c.ended ? c.endReason : c.readiness + (c.morale != null ? " · " + c.morale : ""));
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(inner.x, inner.y + 22f, inner.width, 16f), c.experience + " · " + c.fame + " · " + c.doctrine + " · " + c.people + (c.leader != null ? " · " + c.leader : ""));
                List<string> facts = new List<string>();
                if (c.specialties != null) facts.Add(c.specialties);
                if (c.origin != null) facts.Add(c.origin);
                if (c.intelContact) facts.Add(Tr("TheNetwork_Contractor_IntelContact"));
                Widgets.Label(new Rect(inner.x, inner.y + 38f, inner.width, 16f), string.Join(" · ", facts.ToArray()));
                List<string> known = new List<string>();
                if (c.relation != null) known.Add(c.relation);
                if (c.record != null) known.Add(c.record);
                Widgets.Label(new Rect(inner.x, inner.y + 54f, inner.width, 16f), known.Count > 0 ? string.Join(" · ", known.ToArray()) : Tr("TheNetwork_Contractor_NoDealings"));
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }
            Widgets.EndScrollView();
        }
    }
}
