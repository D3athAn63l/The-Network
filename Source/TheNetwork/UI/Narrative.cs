using System.Collections.Generic;
using System.Text;
using RimWorld;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Intel;
using TheNetwork.History;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;
using Verse;

namespace TheNetwork.UI
{
    /// <summary>
    /// The NarrativeFormatter (ARCHITECTURE § 9): narrative, not numbers. Text is built from records,
    /// events and snapshots through keyed strings and never stored. Nothing here exposes hidden truth,
    /// due ticks or probabilities.
    /// </summary>
    public static class Narrative
    {
        // ------------------------------------------------------------------ durations

        public static string Elapsed(int ticks)
        {
            return "TheNetwork_ElapsedDays".Translate((ticks / (float)Ticks.PerDay).ToString("0.0")).Resolve();
        }

        /// <summary>A vague estimate from the source's speed band, never from the committed due tick.</summary>
        public static string Estimate(Band speed)
        {
            switch (speed)
            {
                case Band.VeryHigh: return "TheNetwork_Estimate_VeryFast".Translate().Resolve();
                case Band.High: return "TheNetwork_Estimate_Fast".Translate().Resolve();
                case Band.Low: return "TheNetwork_Estimate_Slow".Translate().Resolve();
                case Band.VeryLow: return "TheNetwork_Estimate_VerySlow".Translate().Resolve();
                default: return "TheNetwork_Estimate_Medium".Translate().Resolve();
            }
        }

        // ------------------------------------------------------------------ sources

        /// <summary>
        /// Descriptors the player can know: price (visible), pace (by reputation), and reliability as
        /// learned from how this source's leads turned out once the player saw the sites.
        /// </summary>
        public static string SourceDescriptors(NetworkActor a, IntelSourceProfile p, ActorRecordSummary record)
        {
            List<string> parts = new List<string>();
            parts.Add(("TheNetwork_Fee_" + p.feeBand).Translate().Resolve());
            if (a.reputation.fame >= FameBand.Established || p.derived) parts.Add(("TheNetwork_Speed_" + p.speedBand).Translate().Resolve());
            else parts.Add("TheNetwork_Speed_Unknown".Translate().Resolve());
            parts.Add(Reliability(record));
            if (a.reputation.fame >= FameBand.Famous) parts.Add(("TheNetwork_Fame_" + a.reputation.fame).Translate().Resolve());
            return string.Join(", ", parts.ToArray());
        }

        public static string Reliability(ActorRecordSummary record)
        {
            int held = record?.Lifetime("intel.report.held") ?? 0;
            int off = record?.Lifetime("intel.report.off") ?? 0;
            int seen = held + off;
            if (seen < 2) return "TheNetwork_Reliability_Untested".Translate().Resolve();
            float ratio = held / (float)seen;
            if (ratio >= 0.75f) return "TheNetwork_Reliability_Good".Translate(held, seen).Resolve();
            if (ratio >= 0.45f) return "TheNetwork_Reliability_Mixed".Translate(held, seen).Resolve();
            return "TheNetwork_Reliability_Poor".Translate(held, seen).Resolve();
        }

        public static string ContinuationPolicy(SearchTerms t)
        {
            switch (t.continuationPolicyKey)
            {
                case SourcePolicies.ContAuto: return "TheNetwork_Cont_Auto".Translate().Resolve();
                case SourcePolicies.ContAskFee: return "TheNetwork_Cont_AskFee".Translate(t.continuationFee).Resolve();
                case SourcePolicies.ContAskReduced: return "TheNetwork_Cont_AskReduced".Translate(t.continuationFee).Resolve();
                case SourcePolicies.ContSingle: return "TheNetwork_Cont_Single".Translate().Resolve();
                default: return "TheNetwork_Cont_AskFree".Translate().Resolve();
            }
        }

        // ------------------------------------------------------------------ leads

        public static string LeadSummary(Lead lead, DomainContext ctx)
        {
            LeadReport r = lead.reported;
            StringBuilder sb = new StringBuilder();
            sb.Append("TheNetwork_Lead_Amount".Translate(r.estimateLow, r.estimateHigh).Resolve());
            if (!string.IsNullOrEmpty(r.holderText)) sb.Append("\n").Append("TheNetwork_Lead_Holder".Translate(r.holderText).Resolve());
            sb.Append("\n").Append("TheNetwork_Lead_Threat".Translate(("TheNetwork_Threat_" + r.threatBand).Translate()).Resolve());
            string cargo = OtherCargo(r);
            if (cargo != null) sb.Append("\n").Append(cargo);
            int tiles = ctx.world.TilesFromPlayerHome(r.location);
            if (tiles >= 0) sb.Append("\n").Append("TheNetwork_Lead_Distance".Translate(tiles).Resolve());
            if (r.expiresAroundTick > 0)
            {
                int left = r.expiresAroundTick - ctx.Now;
                sb.Append("\n").Append(left > 0
                    ? "TheNetwork_Lead_Window".Translate(left.ToStringTicksToPeriod(false, true)).Resolve()
                    : "TheNetwork_Lead_WindowPast".Translate().Resolve());
            }
            sb.Append("\n").Append("TheNetwork_Lead_Confidence".Translate(("TheNetwork_Confidence_" + r.confidence).Translate()).Resolve());
            return sb.ToString();
        }

        public static string OtherCargo(LeadReport r)
        {
            if (r.otherCargo.Count == 0 && !r.unknownExtra) return null;
            List<string> items = new List<string>();
            for (int i = 0; i < r.otherCargo.Count; i++)
            {
                ReportedCargo c = r.otherCargo[i];
                items.Add(c.thing?.LabelSnapshot + " (" + c.low + (c.high > c.low ? "–" + c.high : "") + ")");
            }
            if (r.unknownExtra) items.Add("TheNetwork_Lead_MaybeMore".Translate().Resolve());
            return "TheNetwork_Lead_OtherCargo".Translate(string.Join(", ", items.ToArray())).Resolve();
        }

        // ------------------------------------------------------------------ history

        public static string RecordLine(HistoryRecord r)
        {
            string item = r.subjectDef?.LabelSnapshot ?? "?";
            string source = r.Note("source") ?? "?";
            string holder = r.Note("holder") ?? "?";
            switch (r.typeKey)
            {
                case EventKeys.IntelLeadDelivered:
                    return "TheNetwork_History_Lead".Translate(source, item).Resolve();
                case EventKeys.IntelConcluded:
                    return r.magnitudes.count == 0
                        ? "TheNetwork_History_Nothing".Translate(source, item, r.magnitudes.duration).Resolve()
                        : "TheNetwork_History_Concluded".Translate(source, item, r.magnitudes.count).Resolve();
                case EventKeys.IntelInvalidated:
                    return "TheNetwork_History_IntelInvalidated".Translate(item, ("TheNetwork_Reason_" + (r.outcomeKey ?? "Unknown")).Translate()).Resolve();
                case EventKeys.OpportunityEngaged:
                    return "TheNetwork_History_Engaged".Translate(item, holder).Resolve();
                case EventKeys.OpportunityClaimed:
                    return (r.outcomeKey == "FullRecovery" || r.outcomeKey == "Settled" ? "TheNetwork_History_ClaimedFull" : "TheNetwork_History_ClaimedPartial")
                        .Translate(item, holder, ("TheNetwork_Recovered_" + (r.Note("band") ?? "Some")).Translate()).Resolve();
                case EventKeys.OpportunityAbandoned:
                    return (r.outcomeKey == "NothingThere" ? "TheNetwork_History_AbandonedNothing" : "TheNetwork_History_Abandoned").Translate(item, holder).Resolve();
                case EventKeys.OpportunityExpired:
                    return "TheNetwork_History_Expired".Translate(item).Resolve();
                case EventKeys.OpportunityDestroyed:
                    return "TheNetwork_History_Destroyed".Translate(item).Resolve();
                case EventKeys.OpportunityInvalidated:
                    return "TheNetwork_History_OppInvalidated".Translate(item).Resolve();
                default:
                    return r.typeKey + " (" + item + ")";
            }
        }

        public static string JournalLine(NetworkEvent e)
        {
            IntelEvent ie = e as IntelEvent;
            if (ie != null)
            {
                switch (e.typeKey)
                {
                    case EventKeys.IntelRequested: return "TheNetwork_Journal_Requested".Translate(ie.sourceName, ie.topicLabel, ie.silver).Resolve();
                    case EventKeys.IntelNoLead: return "TheNetwork_Journal_NoLead".Translate(ie.sourceName, ie.round).Resolve();
                    case EventKeys.IntelSearchContinued: return "TheNetwork_Journal_Continued".Translate(ie.sourceName, ie.topicLabel).Resolve();
                    case EventKeys.IntelCancelled: return "TheNetwork_Journal_Cancelled".Translate(ie.topicLabel).Resolve();
                }
                return null;
            }
            return null;
        }

        public static string Date(int tick)
        {
            if (Find.WorldGrid == null) return tick.ToStringTicksToDays();
            float longitude = 0f;
            return GenDate.DateFullStringAt(GenDate.TickGameToAbs(tick), new UnityEngine.Vector2(longitude, 0f));
        }
    }
}
