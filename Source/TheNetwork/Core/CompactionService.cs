using System.Collections.Generic;
using TheNetwork.Domain;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Kernel;

namespace TheNetwork.Core
{
    /// <summary>
    /// compact.sweep (EVENTS_AND_HISTORY § 11): terminal Intel requests, their leads and their
    /// opportunities are deleted a year after they closed. Their history records keep the story;
    /// references to them resolve to "archived". Budgeted, every 15 days.
    /// </summary>
    public sealed class CompactionService
    {
        public const int KeepTicks = Ticks.PerYear;
        public const int Budget = 500;

        private readonly NetworkState state;
        private readonly NetScheduler scheduler;
        private readonly IClock clock;

        public CompactionService(NetworkState state, NetScheduler scheduler, IClock clock)
        {
            this.state = state;
            this.scheduler = scheduler;
            this.clock = clock;
        }

        public void SweepJob(ScheduledJob job)
        {
            bool finished;
            int removed = Run(clock.Now, out finished);
            if (removed > 0) NetLog.Trace(LogCategory.Save, "Compaction removed " + removed + " archived entities.");
            scheduler.Schedule(JobKinds.CompactSweep, clock.Now + (finished ? JobKinds.SweepPeriod : Ticks.PerHour), 0);
        }

        public int Run(int now, out bool finished)
        {
            int removed = 0;
            finished = true;
            List<IntelRequest> requests = new List<IntelRequest>(state.intel.requests);
            for (int i = 0; i < requests.Count; i++)
            {
                if (removed >= Budget)
                {
                    finished = false;
                    break;
                }
                IntelRequest r = requests[i];
                if (r.state != IntelState.Closed || r.endedTick < 0 || now - r.endedTick < KeepTicks) continue;
                bool allOld = true;
                for (int k = 0; k < r.leads.Count; k++)
                {
                    Lead l = state.intel.Get(r.leads[k]);
                    Opportunity o = l == null ? null : state.opportunities.Get(l.opportunity);
                    if (o != null && (o.state != OpportunityState.Closed || o.endedTick < 0 || now - o.endedTick < KeepTicks)) allOld = false;
                }
                if (!allOld) continue;
                for (int k = 0; k < r.leads.Count; k++)
                {
                    Lead l = state.intel.Get(r.leads[k]);
                    if (l == null) continue;
                    Opportunity o = state.opportunities.Get(l.opportunity);
                    if (o != null)
                    {
                        state.opportunities.Remove(o);
                        removed++;
                    }
                    state.intel.Remove(l);
                    removed++;
                }
                state.intel.Remove(r);
                removed++;
            }
            if (removed > 0) StateVersion.Bump();
            return removed;
        }
    }
}
