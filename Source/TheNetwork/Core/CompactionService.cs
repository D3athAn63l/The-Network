using System.Collections.Generic;
using TheNetwork.Domain;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Kernel;

namespace TheNetwork.Core
{
    /// <summary>
    /// compact.sweep (EVENTS_AND_HISTORY § 11): terminal Intel requests, their leads and their
    /// opportunities, and terminal contracts with their offers and operations, are deleted a year after
    /// they closed. Their history records keep the story; references to them resolve to "archived".
    /// Faded relationship edges and knowledge topics are compacted in the same sweep. Budgeted, every
    /// 15 days.
    /// </summary>
    public sealed class CompactionService
    {
        public const int KeepTicks = Ticks.PerYear;
        public const int Budget = 500;

        private readonly NetworkState state;
        private readonly NetScheduler scheduler;
        private readonly IClock clock;
        private readonly DomainContext ctx;

        public CompactionService(NetworkState state, NetScheduler scheduler, IClock clock, DomainContext ctx = null)
        {
            this.state = state;
            this.scheduler = scheduler;
            this.clock = clock;
            this.ctx = ctx;
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
            if (finished) removed += CompactContracts(now, Budget - removed, ref finished);
            if (finished) removed += CompactLooseOpportunities(now, Budget - removed, ref finished);
            if (ctx?.Relations != null) removed += ctx.Relations.Compact();
            if (ctx?.Knowledge != null) removed += ctx.Knowledge.Compact();
            if (removed > 0) StateVersion.Bump();
            return removed;
        }

        /// <summary>Terminal contracts closed over a year ago, once nothing is still owed and every child has ended too.</summary>
        private int CompactContracts(int now, int budget, ref bool finished)
        {
            int removed = 0;
            List<Contract> contracts = new List<Contract>(state.contracts.contracts);
            for (int i = 0; i < contracts.Count; i++)
            {
                if (removed >= budget)
                {
                    finished = false;
                    break;
                }
                Contract c = contracts[i];
                if (!c.IsTerminal || c.closedTick < 0 || now - c.closedTick < KeepTicks || c.HasPendingRefund()) continue;
                bool childrenDone = true;
                for (int k = 0; k < c.lineage.children.Count; k++)
                {
                    Contract child = state.contracts.Get(c.lineage.children[k]);
                    if (child != null && !child.IsTerminal) childrenDone = false;
                }
                if (!childrenDone) continue;
                for (int k = 0; k < c.offers.Count; k++)
                {
                    Offer o = state.contracts.Get(c.offers[k]);
                    if (o == null) continue;
                    state.contracts.Remove(o);
                    removed++;
                }
                for (int k = 0; k < c.operations.Count; k++)
                {
                    Domain.Operations.Operation op = state.operations.Get(c.operations[k]);
                    if (op == null || !op.IsFinished) continue;
                    state.operations.Remove(op);
                    removed++;
                }
                state.contracts.Remove(c);
                removed++;
            }
            return removed;
        }

        /// <summary>Opportunities with no lead (consequence follow-ups, debug sites) closed over a year ago.</summary>
        private int CompactLooseOpportunities(int now, int budget, ref bool finished)
        {
            int removed = 0;
            List<Opportunity> opps = new List<Opportunity>(state.opportunities.opportunities);
            for (int i = 0; i < opps.Count; i++)
            {
                if (removed >= budget)
                {
                    finished = false;
                    break;
                }
                Opportunity o = opps[i];
                if (o.lead.IsValid || o.state != OpportunityState.Closed || o.endedTick < 0 || now - o.endedTick < KeepTicks) continue;
                state.opportunities.Remove(o);
                removed++;
            }
            return removed;
        }
    }
}
