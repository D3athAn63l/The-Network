using System;
using RimWorld;
using RimWorld.Planet;
using TheNetwork.Domain;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Kernel;
using TheNetwork.Persist.Events;
using Verse;

namespace TheNetwork.UI
{
    /// <summary>
    /// Presentation consumer (last in the global order, so text reflects final state). Vanilla letter
    /// classes only (RIMWORLD_INTEGRATION § 2.12): no custom Letter subclass, so nothing breaks in the
    /// letter stack or archive if the mod is removed. Choices stay in the Network tab.
    /// </summary>
    public sealed class LetterConsumer : IEventConsumer
    {
        public static readonly string[] ConsumedKeys =
        {
            EventKeys.IntelLeadDelivered, EventKeys.IntelConcluded, EventKeys.IntelInvalidated, EventKeys.IntelCancelled,
            EventKeys.OpportunityClaimed, EventKeys.OpportunityAbandoned, EventKeys.OpportunityExpiringSoon,
            EventKeys.OpportunityInvalidated, EventKeys.OpportunityExpired
        };

        private readonly DomainContext ctx;

        public LetterConsumer(DomainContext ctx)
        {
            this.ctx = ctx;
        }

        public string Name => "Letters";

        public void Handle(NetworkEvent evt)
        {
            if (Find.LetterStack == null) return;
            IntelEvent ie = evt as IntelEvent;
            if (ie != null)
            {
                HandleIntel(ie);
                return;
            }
            OpportunityEvent oe = evt as OpportunityEvent;
            if (oe != null) HandleOpportunity(oe);
        }

        private void HandleIntel(IntelEvent e)
        {
            string source = e.sourceName ?? "?";
            string item = e.topicLabel ?? "?";
            switch (e.typeKey)
            {
                case EventKeys.IntelLeadDelivered:
                {
                    Lead lead = ctx.intel.Get(e.lead);
                    IntelRequest r = ctx.intel.Get(e.request);
                    string text = LeadOpening(lead, source, item);
                    if (lead != null) text += "\n\n" + Narrative.LeadSummary(lead, ctx);
                    if (r != null && r.state == IntelState.AwaitingDecision) text += "\n\n" + "TheNetwork_Letter_LeadAsk".Translate(source).Resolve();
                    else if (r != null && r.state == IntelState.Searching) text += "\n\n" + "TheNetwork_Letter_LeadKeepsLooking".Translate(source).Resolve();
                    Send("TheNetwork_Letter_LeadLabel".Translate(item).Resolve(), text, LetterDefOf.PositiveEvent, SiteTarget(e.opportunity));
                    break;
                }
                case EventKeys.IntelConcluded:
                    if (e.leadsSoFar == 0)
                    {
                        Send("TheNetwork_Letter_NothingLabel".Translate(item).Resolve(), "TheNetwork_Letter_NothingText".Translate(source, item).Resolve(), LetterDefOf.NeutralEvent, LookTargets.Invalid);
                    }
                    else if (e.reasonKey != "PlayerEnded")
                    {
                        Messages.Message("TheNetwork_Message_SearchEnded".Translate(source, item, e.leadsSoFar), MessageTypeDefOf.NeutralEvent, false);
                    }
                    break;
                case EventKeys.IntelInvalidated:
                {
                    string reason = ("TheNetwork_Reason_" + (e.reasonKey ?? "Unknown")).Translate().Resolve();
                    string text = "TheNetwork_Letter_InvalidatedText".Translate(source, item, reason).Resolve();
                    if (e.silver > 0) text += "\n\n" + "TheNetwork_Letter_Refund".Translate(e.silver).Resolve();
                    Send("TheNetwork_Letter_InvalidatedLabel".Translate(item).Resolve(), text, LetterDefOf.NegativeEvent, LookTargets.Invalid);
                    break;
                }
                case EventKeys.IntelCancelled:
                    Messages.Message("TheNetwork_Message_Cancelled".Translate(item, e.silver), MessageTypeDefOf.NeutralEvent, false);
                    break;
            }
        }

        private void HandleOpportunity(OpportunityEvent e)
        {
            string item = e.targetLabel ?? "?";
            switch (e.typeKey)
            {
                case EventKeys.OpportunityClaimed:
                {
                    string band = ("TheNetwork_Recovered_" + e.recoveredBand).Translate().Resolve();
                    Send("TheNetwork_Letter_ClaimedLabel".Translate(item).Resolve(), "TheNetwork_Letter_ClaimedText".Translate(item, e.holderName ?? "?", band).Resolve(), LetterDefOf.PositiveEvent, LookTargets.Invalid);
                    break;
                }
                case EventKeys.OpportunityAbandoned:
                    Send("TheNetwork_Letter_AbandonedLabel".Translate(item).Resolve(),
                        (e.reasonKey == "NothingThere" ? "TheNetwork_Letter_AbandonedNothing" : "TheNetwork_Letter_AbandonedText").Translate(item, e.holderName ?? "?").Resolve(),
                        LetterDefOf.NeutralEvent, LookTargets.Invalid);
                    break;
                case EventKeys.OpportunityExpiringSoon:
                    Send("TheNetwork_Letter_ExpiringLabel".Translate(item).Resolve(), "TheNetwork_Letter_ExpiringText".Translate(item).Resolve(), LetterDefOf.NeutralEvent, SiteTarget(e.opportunity));
                    break;
                case EventKeys.OpportunityExpired:
                    Messages.Message("TheNetwork_Message_Expired".Translate(item), MessageTypeDefOf.NeutralEvent, false);
                    break;
                case EventKeys.OpportunityInvalidated:
                {
                    string reason = ("TheNetwork_Reason_" + ReasonHead(e.reasonKey)).Translate().Resolve();
                    Send("TheNetwork_Letter_OppInvalidatedLabel".Translate(item).Resolve(), "TheNetwork_Letter_OppInvalidatedText".Translate(item, reason).Resolve(), LetterDefOf.NegativeEvent, LookTargets.Invalid);
                    break;
                }
            }
        }

        /// <summary>
        /// The report in the source's words (master § 81: read like a RimWorld event, not a system notice).
        /// Built from what the lead REPORTS, never from the hidden truth.
        /// </summary>
        private static string LeadOpening(Lead lead, string source, string item)
        {
            if (lead == null) return "TheNetwork_Letter_LeadText".Translate(source, item).Resolve();
            string holder = lead.reported.holderText ?? "?";
            switch (lead.reported.sourceKindKey)
            {
                case "Mechanoids": return "TheNetwork_Letter_LeadMechs".Translate(source, item).Resolve();
                case "AncientSite": return "TheNetwork_Letter_LeadRuin".Translate(source, item).Resolve();
                case "AbandonedCache": return "TheNetwork_Letter_LeadAbandoned".Translate(source, item).Resolve();
                default: return "TheNetwork_Letter_LeadHeld".Translate(source, item, holder).Resolve();
            }
        }

        private static string ReasonHead(string reasonKey)
        {
            if (string.IsNullOrEmpty(reasonKey)) return "Unknown";
            int colon = reasonKey.IndexOf(':');
            return colon > 0 ? reasonKey.Substring(0, colon) : reasonKey;
        }

        private LookTargets SiteTarget(OpportunityId id)
        {
            Opportunity opp = ctx.opportunities.Get(id);
            WorldObject wo = opp?.site == null ? null : (ctx.sites as Integration.SiteAdapter)?.Resolve(opp.site);
            return wo != null ? new LookTargets(wo) : LookTargets.Invalid;
        }

        private static void Send(string label, string text, LetterDef def, LookTargets targets)
        {
            try
            {
                Find.LetterStack.ReceiveLetter(label, text, def, targets);
            }
            catch (Exception ex)
            {
                NetLog.ErrorOnce(LogCategory.UI, "letter:" + label, "Could not send letter '" + label + "': " + ex);
            }
        }
    }
}
