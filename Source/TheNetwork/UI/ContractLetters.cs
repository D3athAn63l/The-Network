using System;
using RimWorld;
using RimWorld.Planet;
using TheNetwork.Domain;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Kernel;
using TheNetwork.Persist.Events;
using Verse;

namespace TheNetwork.UI
{
    /// <summary>
    /// Phase 2 letters and messages (presentation, last in the global order). Vanilla letter classes
    /// only. Letters read like world events told by the people involved, never like system notices;
    /// choices stay in the Network tab.
    /// </summary>
    public sealed class ContractLetterConsumer : IEventConsumer
    {
        public static readonly string[] ConsumedKeys =
        {
            EventKeys.ContractOfferReceived, EventKeys.ContractUnfilled, EventKeys.ContractAwarded, EventKeys.ContractDelayed,
            EventKeys.ContractRenegotiationRequested, EventKeys.ContractCompleted, EventKeys.ContractPartiallyCompleted,
            EventKeys.ContractFailed, EventKeys.ContractCancelled, EventKeys.ContractVoided, EventKeys.ContractExpired,
            EventKeys.ContractorMissing, EventKeys.ContractorCaptured, EventKeys.ContractorStranded, EventKeys.LeaderKilled,
            EventKeys.ContractorCreated, EventKeys.OpportunityFollowUpCreated
        };

        private readonly DomainContext ctx;

        public ContractLetterConsumer(DomainContext ctx)
        {
            this.ctx = ctx;
        }

        public string Name => "ContractLetters";

        public void Handle(NetworkEvent evt)
        {
            if (Find.LetterStack == null) return;
            ContractEvent c = evt as ContractEvent;
            if (c != null)
            {
                HandleContract(c);
                return;
            }
            ContractorEvent k = evt as ContractorEvent;
            if (k != null)
            {
                HandleContractor(k);
                return;
            }
            OpportunityEvent o = evt as OpportunityEvent;
            if (o != null && o.typeKey == EventKeys.OpportunityFollowUpCreated) LastKnownLocation(o);
        }

        private void HandleContract(ContractEvent e)
        {
            string item = e.quantity + "x " + (e.itemLabel ?? "?");
            string who = e.contractorName ?? "?";
            string broker = e.brokerName ?? "?";
            switch (e.typeKey)
            {
                case EventKeys.ContractOfferReceived:
                    Message("TheNetwork_Message_Offer".Translate(broker, who, item), MessageTypeDefOf.NeutralEvent);
                    break;
                case EventKeys.ContractAwarded:
                    Message("TheNetwork_Message_Awarded".Translate(who, item), MessageTypeDefOf.TaskCompletion);
                    break;
                case EventKeys.ContractUnfilled:
                    Send("TheNetwork_Letter_UnfilledLabel".Translate(e.itemLabel ?? "?"), "TheNetwork_Letter_UnfilledText".Translate(broker, item, ContractNarrative.Reasons(e.reasonKeys)), LetterDefOf.NeutralEvent);
                    break;
                case EventKeys.ContractDelayed:
                    if (e.causeKey == "DeliveryHold")
                    {
                        Send("TheNetwork_Letter_HoldLabel".Translate(e.itemLabel ?? "?"), "TheNetwork_Letter_HoldText".Translate(who, item, ContractNarrative.Reasons(e.reasonKeys)), LetterDefOf.NegativeEvent);
                    }
                    else if (e.causeKey == SubStatus.AwaitingPayment)
                    {
                        Send("TheNetwork_Letter_AwaitingLabel".Translate(e.itemLabel ?? "?"), "TheNetwork_Letter_AwaitingText".Translate(who, item, e.silver), LetterDefOf.NegativeEvent);
                    }
                    else
                    {
                        Message("TheNetwork_Message_Delayed".Translate(who, item), MessageTypeDefOf.NeutralEvent);
                    }
                    break;
                case EventKeys.ContractRenegotiationRequested:
                    if (e.causeKey == SubStatus.PartialResult)
                    {
                        Send("TheNetwork_Letter_PartialLabel".Translate(e.itemLabel ?? "?"), "TheNetwork_Letter_PartialText".Translate(who, e.delivered, item), LetterDefOf.NeutralEvent);
                    }
                    else
                    {
                        Send("TheNetwork_Letter_WorseLabel".Translate(e.itemLabel ?? "?"), "TheNetwork_Letter_WorseText".Translate(who, item, e.silver, e.delivered), LetterDefOf.NeutralEvent);
                    }
                    break;
                case EventKeys.ContractCompleted:
                    Send("TheNetwork_Letter_DeliveredLabel".Translate(e.itemLabel ?? "?"), "TheNetwork_Letter_DeliveredText".Translate(who, item), LetterDefOf.PositiveEvent);
                    break;
                case EventKeys.ContractPartiallyCompleted:
                    Send("TheNetwork_Letter_PartDeliveredLabel".Translate(e.itemLabel ?? "?"), "TheNetwork_Letter_PartDeliveredText".Translate(who, e.delivered, item, ContractNarrative.Cause(e.causeKey)), LetterDefOf.NeutralEvent);
                    break;
                case EventKeys.ContractFailed:
                {
                    string text = ("TheNetwork_Letter_Failed_" + FailureVoice(e.causeKey)).Translate(who, item, ContractNarrative.Cause(e.causeKey)).Resolve();
                    if (e.insuranceSilver > 0) text += "\n\n" + "TheNetwork_Letter_InsurancePaid".Translate(e.insuranceSilver).Resolve();
                    else if (e.silver > 0) text += "\n\n" + "TheNetwork_Letter_Refund".Translate(e.silver).Resolve();
                    else text += "\n\n" + "TheNetwork_Letter_DepositLost".Translate().Resolve();
                    Contract c = ctx.contracts.Get(e.contract);
                    if (c != null && c.lineage.children.Count > 0) text += "\n\n" + "TheNetwork_Letter_Replaced".Translate(broker).Resolve();
                    Send("TheNetwork_Letter_FailedLabel".Translate(e.itemLabel ?? "?"), text, LetterDefOf.NegativeEvent);
                    break;
                }
                case EventKeys.ContractVoided:
                {
                    string text = "TheNetwork_Letter_VoidedText".Translate(item, ContractNarrative.Cause(e.causeKey)).Resolve();
                    if (e.silver > 0) text += "\n\n" + "TheNetwork_Letter_Refund".Translate(e.silver).Resolve();
                    Send("TheNetwork_Letter_VoidedLabel".Translate(e.itemLabel ?? "?"), text, LetterDefOf.NeutralEvent);
                    break;
                }
                case EventKeys.ContractCancelled:
                    Message("TheNetwork_Message_ContractCancelled".Translate(item, e.silver), MessageTypeDefOf.NeutralEvent);
                    break;
                case EventKeys.ContractExpired:
                    Message("TheNetwork_Message_ContractExpired".Translate(item), MessageTypeDefOf.NeutralEvent);
                    break;
            }
        }

        private static string FailureVoice(string cause)
        {
            switch (cause)
            {
                case Causes.CatastrophicLoss:
                case Causes.ContractorLost:
                case Causes.PreWorkLoss:
                case Causes.ContractorWalked:
                    return cause;
                default:
                    return "Default";
            }
        }

        private void HandleContractor(ContractorEvent e)
        {
            string who = e.actorName ?? "?";
            switch (e.typeKey)
            {
                case EventKeys.ContractorMissing:
                case EventKeys.ContractorCaptured:
                case EventKeys.ContractorStranded:
                {
                    if (!e.contract.IsValid) return;
                    Contract c = ctx.contracts.Get(e.contract);
                    string item = c == null ? "?" : c.Quantity + "x " + c.ItemLabel;
                    string key = e.typeKey == EventKeys.ContractorCaptured ? "Captured" : (e.typeKey == EventKeys.ContractorStranded ? "Stranded" : "Missing");
                    string text = ("TheNetwork_Letter_Troubled_" + key).Translate(who, item).Resolve();
                    text += "\n\n" + (e.descriptorKey == "CargoSecured" ? "TheNetwork_Letter_HadCargo" : "TheNetwork_Letter_NoCargo").Translate(who).Resolve();
                    text += "\n\n" + "TheNetwork_Letter_TroubledWait".Translate().Resolve();
                    Send(("TheNetwork_Letter_TroubledLabel_" + key).Translate(who), text, LetterDefOf.NegativeEvent);
                    break;
                }
                case EventKeys.LeaderKilled:
                    if (!e.contract.IsValid) return;
                    Send("TheNetwork_Letter_LeaderKilledLabel".Translate(who), "TheNetwork_Letter_LeaderKilledText".Translate(e.characterName ?? "?", who), LetterDefOf.NegativeEvent);
                    break;
                case EventKeys.ContractorCreated:
                    if (e.reasonKey == "NewcomerBidder") Message("TheNetwork_Message_NewOutfit".Translate(who), MessageTypeDefOf.NeutralEvent);
                    break;
            }
        }

        /// <summary>Consequence Engine v0: a world event, not a system notice. The deposit is not mentioned as refunded: it is not.</summary>
        private void LastKnownLocation(OpportunityEvent e)
        {
            string who = ctx.actors.NameOf(e.source) ?? "?";
            string text = "TheNetwork_Letter_LastKnownText".Translate(who).Resolve();
            text += "\n\n" + (e.targetCount > 0 ? "TheNetwork_Letter_LastKnownCargo".Translate(e.targetLabel ?? "?") : "TheNetwork_Letter_LastKnownNoCargo".Translate()).Resolve();
            text += "\n\n" + "TheNetwork_Letter_LastKnownThreat".Translate().Resolve();
            Opportunity opp = ctx.opportunities.Get(e.opportunity);
            WorldObject wo = opp?.site == null ? null : (ctx.sites as Integration.SiteAdapter)?.Resolve(opp.site);
            Send("TheNetwork_Letter_LastKnownLabel".Translate(who), text, LetterDefOf.NeutralEvent, wo != null ? new LookTargets(wo) : LookTargets.Invalid);
        }

        private static void Message(TaggedString text, MessageTypeDef type)
        {
            try
            {
                Messages.Message(text, type, false);
            }
            catch (Exception ex)
            {
                NetLog.ErrorOnce(LogCategory.UI, "message:" + text.RawText, "Could not show a message: " + ex.Message);
            }
        }

        private static void Send(TaggedString label, TaggedString text, LetterDef def, LookTargets targets = null)
        {
            try
            {
                Find.LetterStack.ReceiveLetter(label, text, def, targets ?? LookTargets.Invalid);
            }
            catch (Exception ex)
            {
                NetLog.ErrorOnce(LogCategory.UI, "letter:" + label.RawText, "Could not send letter '" + label + "': " + ex);
            }
        }
    }
}
