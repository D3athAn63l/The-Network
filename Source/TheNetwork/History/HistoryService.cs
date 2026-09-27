using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Kernel;
using TheNetwork.Persist.Events;

namespace TheNetwork.History
{
    /// <summary>
    /// Tiered retention (EVENTS_AND_HISTORY § 4). Pure, so it is tested headlessly.
    /// Notable: kept while a principal participant still lists it among its 25 most recent Notable
    /// records, or while younger than a year. Major: kept while any participant is active, or younger
    /// than five years. Legendary: forever (none exist in Phase 1).
    /// </summary>
    public static class RetentionPolicy
    {
        public const int NotablePerActor = 25;
        public const int NotableGlobalCap = 3000;
        public const int MajorGlobalCap = 1500;
        public const int NotableMinAgeTicks = Ticks.PerYear;
        public const int MajorMinAgeTicks = Ticks.PerYear * 5;
        public const int SweepBudget = 500;

        public static bool ShouldKeep(Importance importance, int ageTicks, bool amongRecentForAPrincipal, bool anyParticipantActive)
        {
            switch (importance)
            {
                case Importance.Legendary:
                    return true;
                case Importance.Major:
                    return anyParticipantActive || ageTicks < MajorMinAgeTicks;
                case Importance.Notable:
                    return amongRecentForAPrincipal || ageTicks < NotableMinAgeTicks;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// The History consumer (EVENTS_AND_HISTORY § 3): writes a record for meaningful Phase 1 events,
    /// updates summary counters for every event, and runs the budgeted retention sweep. Nothing else
    /// reads the ledger except the UI.
    /// </summary>
    public sealed class HistoryService : IEventConsumer
    {
        private readonly HistoryLedger ledger;
        private readonly SummaryStore summaries;
        private readonly ActorStore actors;
        private readonly IdAllocator ids;
        private readonly IClock clock;
        private readonly int networkSeed;

        /// <summary>Runtime index: actor → its Notable records, newest last (rebuilt on load).</summary>
        private readonly Dictionary<int, List<HistoryRecord>> notableByPrincipal = new Dictionary<int, List<HistoryRecord>>();
        private int notableCount;
        private int majorCount;

        public string Name => "History";

        public HistoryService(HistoryLedger ledger, SummaryStore summaries, ActorStore actors, IdAllocator ids, IClock clock, int networkSeed)
        {
            this.ledger = ledger;
            this.summaries = summaries;
            this.actors = actors;
            this.ids = ids;
            this.clock = clock;
            this.networkSeed = networkSeed;
        }

        /// <summary>The world's Network seed this service derives narrative seeds from.</summary>
        public int NetworkSeed => networkSeed;

        public HistoryLedger Ledger => ledger;
        public SummaryStore Summaries => summaries;

        public static readonly string[] ConsumedKeys =
        {
            EventKeys.IntelRequested, EventKeys.IntelLeadDelivered, EventKeys.IntelNoLead, EventKeys.IntelSearchContinued,
            EventKeys.IntelConcluded, EventKeys.IntelCancelled, EventKeys.IntelInvalidated,
            EventKeys.OpportunityEngaged, EventKeys.OpportunityClaimed, EventKeys.OpportunityAbandoned,
            EventKeys.OpportunityExpired, EventKeys.OpportunityDestroyed, EventKeys.OpportunityInvalidated
        };

        public void RebuildIndex()
        {
            notableByPrincipal.Clear();
            notableCount = 0;
            majorCount = 0;
            bool sorted = true;
            for (int i = 1; i < ledger.records.Count && sorted; i++) sorted = ledger.records[i - 1].id.Value < ledger.records[i].id.Value;
            if (!sorted) ledger.records.Sort((a, b) => a.id.Value.CompareTo(b.id.Value));
            for (int i = 0; i < ledger.records.Count; i++)
            {
                HistoryRecord r = ledger.records[i];
                if (r.narrativeSeed == 0) r.narrativeSeed = NetHash.Combine(networkSeed, r.id.Value);
                IndexRecord(r);
            }
        }

        private void IndexRecord(HistoryRecord r)
        {
            if (r == null) return;
            if (r.importance >= Importance.Major) majorCount++;
            if (r.importance != Importance.Notable) return;
            notableCount++;
            for (int i = 0; i < r.participants.Count; i++)
            {
                Participation p = r.participants[i];
                if (!p.principal || p.entity.Kind != EntityKind.Actor) continue;
                List<HistoryRecord> list;
                if (!notableByPrincipal.TryGetValue(p.entity.Id, out list))
                {
                    list = new List<HistoryRecord>();
                    notableByPrincipal[p.entity.Id] = list;
                }
                list.Add(r);
            }
        }

        // ------------------------------------------------------------------ consumer

        public void Handle(NetworkEvent evt)
        {
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
            int now = e.tick;
            ActorRecordSummary player = summaries.GetOrCreate(e.requester);
            ActorRecordSummary source = summaries.GetOrCreate(e.source);
            switch (e.typeKey)
            {
                case EventKeys.IntelRequested:
                    player?.Add("intel.requested", 1, now);
                    source?.Add("intel.served", 1, now);
                    if (e.silver > 0) player?.Add("silver.spent.intel", e.silver, now);
                    break;
                case EventKeys.IntelSearchContinued:
                    player?.Add("intel.continued", 1, now);
                    if (e.silver > 0) player?.Add("silver.spent.intel", e.silver, now);
                    break;
                case EventKeys.IntelNoLead:
                    source?.Add("intel.round.empty", 1, now);
                    break;
                case EventKeys.IntelLeadDelivered:
                {
                    player?.Add("intel.leads", 1, now);
                    source?.Add("intel.leads", 1, now);
                    HistoryRecord r = NewRecord(e, Importance.Notable);
                    AddActor(r, e.requester, "requester", true);
                    AddActor(r, e.source, "source", true);
                    r.participants.Add(new Participation { entity = e.lead.Ref, roleKey = "lead" });
                    r.participants.Add(new Participation { entity = e.opportunity.Ref, roleKey = "opportunity" });
                    r.awareness.scope = AwarenessScope.Involved;
                    r.magnitudes.count = e.round;
                    Commit(r);
                    break;
                }
                case EventKeys.IntelConcluded:
                {
                    bool nothing = e.leadsSoFar == 0;
                    if (nothing)
                    {
                        player?.Add("intel.nothing", 1, now);
                        source?.Add("intel.nothing", 1, now);
                    }
                    player?.Add("intel.concluded", 1, now);
                    HistoryRecord r = NewRecord(e, Importance.Notable);
                    AddActor(r, e.requester, "requester", true);
                    AddActor(r, e.source, "source", true);
                    r.awareness.scope = AwarenessScope.Involved;
                    r.magnitudes.count = e.leadsSoFar;
                    r.magnitudes.duration = e.round;
                    r.outcomeKey = e.reasonKey;
                    Commit(r);
                    break;
                }
                case EventKeys.IntelCancelled:
                    player?.Add("intel.cancelled", 1, now);
                    break;
                case EventKeys.IntelInvalidated:
                {
                    player?.Add("intel.invalidated", 1, now);
                    HistoryRecord r = NewRecord(e, Importance.Notable);
                    AddActor(r, e.requester, "requester", true);
                    AddActor(r, e.source, "source", false);
                    r.awareness.scope = AwarenessScope.Involved;
                    r.outcomeKey = e.reasonKey;
                    r.magnitudes.value = e.silver;
                    Commit(r);
                    break;
                }
            }
        }

        private void HandleOpportunity(OpportunityEvent e)
        {
            int now = e.tick;
            ActorId playerId = actors.PlayerProxyId;
            ActorRecordSummary player = summaries.GetOrCreate(playerId);
            ActorRecordSummary source = e.source.IsValid ? summaries.GetOrCreate(e.source) : null;
            ActorRecordSummary holder = e.holderActor.IsValid ? summaries.GetOrCreate(e.holderActor) : null;
            HistoryRecord r = null;
            switch (e.typeKey)
            {
                case EventKeys.OpportunityEngaged:
                    player?.Add("opp.engaged", 1, now);
                    // The player has now seen the site with their own eyes: the source's track record learns.
                    source?.Add(e.reportHeld ? "intel.report.held" : "intel.report.off", 1, now);
                    r = NewRecord(e, Importance.Notable);
                    break;
                case EventKeys.OpportunityClaimed:
                    player?.Add(e.recoveredBand == Domain.RecoveredBand.All ? "opp.claimed.full" : "opp.claimed.partial", 1, now);
                    holder?.Add("opp.lost", 1, now);
                    r = NewRecord(e, e.importance >= Importance.Major ? Importance.Major : Importance.Notable);
                    r.magnitudes.value = e.marketValue;
                    r.magnitudes.count = e.recoveredCount;
                    break;
                case EventKeys.OpportunityAbandoned:
                    player?.Add("opp.abandoned", 1, now);
                    r = NewRecord(e, Importance.Notable);
                    break;
                case EventKeys.OpportunityExpired:
                    player?.Add("opp.expired", 1, now);
                    r = NewRecord(e, Importance.Notable);
                    break;
                case EventKeys.OpportunityDestroyed:
                    player?.Add("opp.lostTrack", 1, now);
                    r = NewRecord(e, Importance.Notable);
                    break;
                case EventKeys.OpportunityInvalidated:
                    player?.Add("opp.invalidated", 1, now);
                    r = NewRecord(e, Importance.Notable);
                    break;
            }
            if (r == null) return;
            AddActor(r, playerId, "claimer", true);
            AddActor(r, e.source, "source", false);
            AddActor(r, e.holderActor, "holder", false);
            r.participants.Add(new Participation { entity = e.opportunity.Ref, roleKey = "opportunity" });
            r.awareness.scope = AwarenessScope.Public;
            r.outcomeKey = e.reasonKey;
            r.SetNote("holder", e.holderName);
            if (e.recoveredBand != Domain.RecoveredBand.None) r.SetNote("band", e.recoveredBand.ToString());
            Commit(r);
        }

        private HistoryRecord NewRecord(NetworkEvent e, Importance importance)
        {
            HistoryRecord r = new HistoryRecord
            {
                id = new HistoryRecordId(ids.NextId()),
                tick = e.tick,
                typeKey = e.typeKey,
                importance = importance,
                sourceEventSeq = e.seq,
                place = e.place?.Copy(),
                regionKey = e.place?.regionKey
            };
            r.narrativeSeed = NetHash.Combine(networkSeed, r.id.Value);
            IntelEvent ie = e as IntelEvent;
            if (ie != null)
            {
                r.subjectDef = new DefRef<ThingDef> { defName = ie.topicDefName, label = ie.topicLabel };
                r.SetNote("source", ie.sourceName);
            }
            OpportunityEvent oe = e as OpportunityEvent;
            if (oe != null)
            {
                // The source is a participant (actors are never deleted, only tombstoned with their name).
                r.subjectDef = new DefRef<ThingDef> { defName = oe.targetDefName, label = oe.targetLabel };
            }
            return r;
        }

        private static void AddActor(HistoryRecord r, ActorId id, string role, bool principal)
        {
            if (!id.IsValid) return;
            r.participants.Add(new Participation { entity = id.Ref, roleKey = role, principal = principal });
        }

        private void Commit(HistoryRecord r)
        {
            ledger.records.Add(r);
            IndexRecord(r);
            for (int i = 0; i < r.participants.Count; i++)
            {
                Participation p = r.participants[i];
                if (!p.principal || p.entity.Kind != EntityKind.Actor) continue;
                ActorRecordSummary s = summaries.GetOrCreate(p.entity.AsActor);
                if (s != null && (!s.bestRecord.IsValid || r.importance >= Importance.Major)) s.bestRecord = r.id;
            }
            if (notableCount > RetentionPolicy.NotableGlobalCap || majorCount > RetentionPolicy.MajorGlobalCap) EnforceGlobalCaps();
        }

        // ------------------------------------------------------------------ retention

        private bool AmongRecentForAPrincipal(HistoryRecord r)
        {
            for (int i = 0; i < r.participants.Count; i++)
            {
                Participation p = r.participants[i];
                if (!p.principal || p.entity.Kind != EntityKind.Actor) continue;
                List<HistoryRecord> list;
                if (!notableByPrincipal.TryGetValue(p.entity.Id, out list)) continue;
                int start = Math.Max(0, list.Count - RetentionPolicy.NotablePerActor);
                for (int k = list.Count - 1; k >= start; k--) if (list[k] == r) return true;
            }
            return false;
        }

        private bool AnyParticipantActive(HistoryRecord r)
        {
            for (int i = 0; i < r.participants.Count; i++)
            {
                Participation p = r.participants[i];
                if (p.entity.Kind != EntityKind.Actor) continue;
                NetworkActor a = actors.Get(p.entity.AsActor);
                if (a != null && a.status != ActorStatus.Tombstone) return true;
            }
            return false;
        }

        /// <summary>history.sweep: walks the ledger in id order from the cursor, at most 500 records per run.</summary>
        public int Sweep(int now, out bool finished)
        {
            int examined = 0;
            int dropped = 0;
            List<HistoryRecord> keep = new List<HistoryRecord>(ledger.records.Count);
            bool reachedBudget = false;
            int lastExamined = ledger.sweepCursor;
            for (int i = 0; i < ledger.records.Count; i++)
            {
                HistoryRecord r = ledger.records[i];
                if (r.id.Value <= ledger.sweepCursor || reachedBudget)
                {
                    keep.Add(r);
                    continue;
                }
                examined++;
                lastExamined = r.id.Value;
                if (RetentionPolicy.ShouldKeep(r.importance, now - r.tick, AmongRecentForAPrincipal(r), AnyParticipantActive(r))) keep.Add(r);
                else dropped++;
                if (examined >= RetentionPolicy.SweepBudget) reachedBudget = true;
            }
            finished = !reachedBudget;
            ledger.sweepCursor = finished ? 0 : lastExamined;
            if (dropped > 0)
            {
                ledger.records = keep;
                ledger.droppedCount += dropped;
                RebuildIndex();
                StateVersion.Bump();
            }
            return dropped;
        }

        /// <summary>Hard caps: beyond them the oldest Notable (then Major) records go first.</summary>
        public void EnforceGlobalCaps()
        {
            int notable = 0, major = 0;
            for (int i = 0; i < ledger.records.Count; i++)
            {
                if (ledger.records[i].importance == Importance.Notable) notable++;
                else if (ledger.records[i].importance == Importance.Major) major++;
            }
            if (notable <= RetentionPolicy.NotableGlobalCap && major <= RetentionPolicy.MajorGlobalCap) return;
            // Trim below the cap (hysteresis) so a full ledger is not rebuilt on every new record.
            int dropNotable = notable > RetentionPolicy.NotableGlobalCap ? notable - RetentionPolicy.NotableGlobalCap * 95 / 100 : 0;
            int dropMajor = major > RetentionPolicy.MajorGlobalCap ? major - RetentionPolicy.MajorGlobalCap * 95 / 100 : 0;
            List<HistoryRecord> keep = new List<HistoryRecord>(ledger.records.Count);
            for (int i = 0; i < ledger.records.Count; i++)
            {
                HistoryRecord r = ledger.records[i];
                if (r.importance == Importance.Notable && dropNotable > 0) { dropNotable--; ledger.droppedCount++; continue; }
                if (r.importance == Importance.Major && dropMajor > 0) { dropMajor--; ledger.droppedCount++; continue; }
                keep.Add(r);
            }
            ledger.records = keep;
            RebuildIndex();
        }

        public void SweepJob(ScheduledJob job, NetScheduler scheduler)
        {
            bool finished;
            int dropped = Sweep(clock.Now, out finished);
            if (dropped > 0) NetLog.Trace(LogCategory.History, "Retention sweep dropped " + dropped + " records.");
            int next = finished ? clock.Now + JobKinds.SweepPeriod : clock.Now + Ticks.PerHour;
            scheduler.Schedule(JobKinds.HistorySweep, next, 0);
        }
    }
}
