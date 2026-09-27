using System.Collections.Generic;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Operations;
using TheNetwork.History;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;
using Verse;

namespace TheNetwork.UI
{
    /// <summary>
    /// Phase 2 narrative: contracts, contractors and their people, in words. Descriptors only; nothing
    /// here shows a hidden number (doctrine values, strength, odds, the resolver's band before the
    /// contractor has reported back).
    /// </summary>
    public static class ContractNarrative
    {
        private static string T(string key) => key.Translate().Resolve();

        // ------------------------------------------------------------------ history records

        public static string RecordLine(HistoryRecord r)
        {
            string actor = r.Note("actor") ?? "?";
            string person = r.Note("person") ?? "?";
            string item = r.subjectDef?.LabelSnapshot ?? "?";
            string quantity = r.Note("quantity") ?? "?";
            switch (r.typeKey)
            {
                case EventKeys.ContractorCasualties:
                    return "TheNetwork_History_Casualties".Translate(actor, r.magnitudes.casualties, r.magnitudes.count).Resolve();
                case EventKeys.CharacterKilled:
                    return "TheNetwork_History_PersonKilled".Translate(person, actor).Resolve();
                case EventKeys.LeaderKilled:
                    return "TheNetwork_History_LeaderKilled".Translate(person, actor).Resolve();
                case EventKeys.LeaderSucceeded:
                    return "TheNetwork_History_Succeeded".Translate(r.Note("successor") ?? "?", actor, person).Resolve();
                case EventKeys.CharacterPromoted:
                    return "TheNetwork_History_Promoted".Translate(person, actor).Resolve();
                case EventKeys.ContractorCaptured:
                    return "TheNetwork_History_Captured".Translate(actor).Resolve();
                case EventKeys.ContractorMissing:
                    return "TheNetwork_History_Missing".Translate(actor).Resolve();
                case EventKeys.ContractorStranded:
                    return "TheNetwork_History_Stranded".Translate(actor).Resolve();
                case EventKeys.ContractorEnded:
                    return "TheNetwork_History_Ended".Translate(actor, EndReason(r.outcomeKey)).Resolve();
                case EventKeys.ContractorOriginLost:
                    return "TheNetwork_History_OriginLost".Translate(actor).Resolve();
                case EventKeys.ContractCompleted:
                    return "TheNetwork_History_ContractDone".Translate(actor, r.magnitudes.count, item).Resolve();
                case EventKeys.ContractPartiallyCompleted:
                    return "TheNetwork_History_ContractPartial".Translate(actor, r.magnitudes.count, quantity, item).Resolve();
                case EventKeys.ContractFailed:
                    return "TheNetwork_History_ContractFailed".Translate(quantity, item, actor, Cause(r.outcomeKey)).Resolve();
                case EventKeys.PaymentDefaulted:
                    return "TheNetwork_History_Defaulted".Translate(actor, quantity, item).Resolve();
                case EventKeys.OpportunityFollowUpCreated:
                    return "TheNetwork_History_LastKnown".Translate(actor, item).Resolve();
                default:
                    return null;
            }
        }

        public static string JournalLine(NetworkEvent e)
        {
            ContractEvent c = e as ContractEvent;
            if (c != null)
            {
                string item = c.quantity + "x " + (c.itemLabel ?? "?");
                string who = c.contractorName ?? "?";
                switch (e.typeKey)
                {
                    case EventKeys.ContractPosted: return "TheNetwork_Journal_Posted".Translate(item, c.brokerName ?? "?").Resolve();
                    case EventKeys.ContractOfferReceived: return "TheNetwork_Journal_Offer".Translate(who, item).Resolve();
                    case EventKeys.ContractRefused: return "TheNetwork_Journal_Refused".Translate(who, item, Reasons(c.reasonKeys)).Resolve();
                    case EventKeys.ContractAwarded: return "TheNetwork_Journal_Awarded".Translate(who, item).Resolve();
                    case EventKeys.ContractUnfilled: return "TheNetwork_Journal_Unfilled".Translate(item).Resolve();
                    case EventKeys.ContractDelayed: return "TheNetwork_Journal_Delayed".Translate(who, item, Cause(c.causeKey)).Resolve();
                    case EventKeys.ContractRenegotiationRequested: return "TheNetwork_Journal_Renegotiate".Translate(who, item).Resolve();
                    case EventKeys.ContractCancelled: return "TheNetwork_Journal_ContractCancelled".Translate(item).Resolve();
                    case EventKeys.ContractVoided: return "TheNetwork_Journal_Voided".Translate(item, Cause(c.causeKey)).Resolve();
                    case EventKeys.ContractExpired: return "TheNetwork_Journal_Expired".Translate(item).Resolve();
                    case EventKeys.PaymentReceived: return "TheNetwork_Journal_Paid".Translate(c.silver, who).Resolve();
                }
                return null;
            }
            ContractorEvent k = e as ContractorEvent;
            if (k != null)
            {
                switch (e.typeKey)
                {
                    case EventKeys.ContractorCreated: return "TheNetwork_Journal_NewOutfit".Translate(k.actorName ?? "?").Resolve();
                    case EventKeys.MoraleShifted: return "TheNetwork_Journal_Mood".Translate(k.actorName ?? "?", Morale(k.descriptorKey)).Resolve();
                }
            }
            OperationEvent o = e as OperationEvent;
            if (o != null && e.typeKey == EventKeys.OperationStarted) return "TheNetwork_Journal_OpStarted".Translate(o.contractorName ?? "?", o.itemLabel ?? "?").Resolve();
            // Operation.Resolved is not shown: the client learns the result when the contractor reports back.
            return null;
        }

        // ------------------------------------------------------------------ descriptors

        public static string Cause(string key) => ("TheNetwork_Cause_" + (key ?? "Unknown")).Translate().Resolve();
        public static string Refusal(string key) => ("TheNetwork_Refusal_" + (key ?? "Unknown")).Translate().Resolve();
        public static string Morale(string key) => ("TheNetwork_Morale_" + (key ?? "Steady")).Translate().Resolve();
        public static string Experience(ExperienceBand b) => ("TheNetwork_Experience_" + b).Translate().Resolve();
        public static string Fame(FameBand b) => ("TheNetwork_Fame_" + b).Translate().Resolve();
        public static string Doctrine(string key) => ("TheNetwork_Doctrine_" + (key ?? "Professional")).Translate().Resolve();
        public static string EndReason(string key) => ("TheNetwork_EndedBecause_" + (key ?? "Unknown")).Translate().Resolve();
        public static string Readiness(Availability a) => ("TheNetwork_Readiness_" + a).Translate().Resolve();

        public static string Reasons(List<string> keys)
        {
            if (keys == null || keys.Count == 0) return Refusal(null);
            List<string> parts = new List<string>();
            for (int i = 0; i < keys.Count; i++) parts.Add(Refusal(keys[i]));
            return string.Join("; ", parts.ToArray());
        }

        public static string Conditions(List<string> keys)
        {
            if (keys == null || keys.Count == 0) return null;
            List<string> parts = new List<string>();
            for (int i = 0; i < keys.Count; i++) parts.Add(("TheNetwork_Condition_" + keys[i]).Translate().Resolve());
            return string.Join(", ", parts.ToArray());
        }

        /// <summary>Where the work stands, as the client knows it (never the committed outcome before the report).</summary>
        public static string StatusLine(Contract c, Operation op)
        {
            string status = ("TheNetwork_ContractStatus_" + c.status).Translate().Resolve();
            if (c.subStatus != null) status += " — " + ("TheNetwork_SubStatus_" + c.subStatus).Translate().Resolve();
            else if (c.IsUnderway && op != null && !op.IsFinished) status += " — " + ("TheNetwork_OpPhase_" + op.phase).Translate().Resolve();
            if (c.quarantinedReason != null) status += " [" + T("TheNetwork_Quarantined") + "]";
            return status;
        }

        /// <summary>A vague ETA from the frozen quote and the start, never a committed due tick.</summary>
        public static string Eta(Contract c, Operation op, int now)
        {
            if (c.terms == null || op == null || op.IsFinished) return null;
            int left = op.startedTick + c.terms.etaTicks - now;
            if (c.status == ContractStatus.Delayed) return T("TheNetwork_Eta_Late");
            if (left <= 0) return T("TheNetwork_Eta_Any");
            float days = left / (float)Ticks.PerDay;
            return days < 1.5f ? T("TheNetwork_Eta_Soon") : "TheNetwork_Eta_Days".Translate(((int)System.Math.Round(days)).ToString()).Resolve();
        }

        public static string Money(Contract c)
        {
            if (c.terms == null) return null;
            int paid = c.Paid(), refunded = c.Refunded();
            string s = "TheNetwork_ContractMoney".Translate(c.terms.price, paid).Resolve();
            if (refunded > 0) s += " · " + "TheNetwork_MoneyRefunded".Translate(refunded).Resolve();
            if (c.HasPendingRefund()) s += " · " + T("TheNetwork_RefundPending");
            if (c.subStatus == SubStatus.AwaitingPayment && c.Deliver != null) s += " · " + "TheNetwork_BalanceOwed".Translate(c.Deliver.balanceDue).Resolve();
            else if (!c.IsTerminal && c.terms.balance > 0) s += " · " + "TheNetwork_BalanceOnDelivery".Translate(c.terms.balance).Resolve();
            return s;
        }

        public static string InsuranceLine(Insurance ins)
        {
            if (ins == null) return null;
            return "TheNetwork_InsuranceLine".Translate(Coverage(ins.coverage), ins.premium).Resolve();
        }

        /// <summary>Coverage as words (roughly a third, about half, about two thirds), never a percentage table.</summary>
        public static string Coverage(float coverage)
        {
            if (coverage >= 0.6f) return T("TheNetwork_Coverage_TwoThirds");
            if (coverage >= 0.45f) return T("TheNetwork_Coverage_Half");
            return T("TheNetwork_Coverage_Some");
        }

        /// <summary>Headcount in words: exact for a handful, bands beyond.</summary>
        public static string Headcount(int people)
        {
            if (people <= 1) return T("TheNetwork_Headcount_One");
            if (people <= 5) return "TheNetwork_Headcount_Few".Translate(people).Resolve();
            if (people <= 12) return T("TheNetwork_Headcount_Dozen");
            if (people <= 25) return T("TheNetwork_Headcount_Score");
            return T("TheNetwork_Headcount_Many");
        }

        public static string Lineage(Contract c)
        {
            if (!c.lineage.parent.IsValid) return c.lineage.children.Count > 0 ? "TheNetwork_Lineage_Children".Translate(c.lineage.children.Count).Resolve() : null;
            string key = c.lineage.relationKey == ContractLineage.Replacement ? "TheNetwork_Lineage_Replacement" : "TheNetwork_Lineage_Continuation";
            return key.Translate(c.lineage.parent.ToString()).Resolve();
        }
    }
}
