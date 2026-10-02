using System;
using System.Collections.Generic;
using System.Text;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Operations;
using TheNetwork.Kernel;
using TheNetwork.Persist;

namespace TheNetwork.Diagnostics.RuntimeTests
{
    /// <summary>
    /// A read-only fingerprint of a Network world's mutable truth: the counts and a hash of every store the safe suites must
    /// never touch (actors, contracts and their ledgers, operations, history, relations, careers, the scheduler's jobs, the id
    /// allocator). Taken before and after every slice of a run, it PROVES the scratch worlds stayed isolated: if one value
    /// moved, a test touched live truth and the run says so (RT-INFRA-001). It only reads raw fields: it never creates, repairs,
    /// schedules or caches anything.
    /// </summary>
    public sealed class LiveFingerprint
    {
        private readonly SortedDictionary<string, long> parts = new SortedDictionary<string, long>(StringComparer.Ordinal);

        public int PartCount => parts.Count;

        public LiveFingerprint With(string name, long value)
        {
            parts[name] = value;
            return this;
        }

        public long Get(string name)
        {
            long v;
            return parts.TryGetValue(name, out v) ? v : 0;
        }

        public static LiveFingerprint Of(DomainContext ctx, IdAllocator ids, NetScheduler scheduler, EventJournal journal)
        {
            LiveFingerprint f = new LiveFingerprint();
            if (ids != null)
            {
                f.With("ids.nextId", ids.PeekNextId);
                f.With("ids.nextEventSeq", ids.PeekNextEventSeq);
                f.With("ids.nextJobSeq", ids.PeekNextJobSeq);
            }
            if (ctx == null) return f;

            HashAcc h = new HashAcc();
            int contractors = 0;
            for (int i = 0; i < ctx.actors.actors.Count; i++)
            {
                NetworkActor a = ctx.actors.actors[i];
                h.Add(a.id.Value).Add((long)a.status).Add(a.reputation == null ? -1 : a.reputation.score).Add(a.components.Count);
                ContractorSimulation sim = a.Get<ContractorSimulation>();
                if (sim == null) continue;
                contractors++;
                CareerRecord r = sim.career;
                h.Add(sim.funds).Add(sim.equipment == null ? -1 : sim.equipment.tier).Add(sim.commitments.Count).Add(sim.opsCompleted);
                if (r != null) h.Add(r.legacyResolved).Add(r.triumphs).Add(r.successes).Add(r.partials).Add(r.failures).Add(r.disasters).Add(r.careerEarnings).Add(r.reputationEarned).Add(r.casualtiesTaken).Add(r.advancementCount).Add(r.lastAdvancementTick);
                SpatialState sp = sim.spatial;
                if (sp != null) h.Add((long)sp.status).Add(sp.anchor == null ? -1 : sp.anchor.tileId).Add(sp.destination == null ? -1 : sp.destination.tileId).Add(sp.arrivalTick).Add(sp.journeys);
            }
            f.With("actors.count", ctx.actors.actors.Count);
            f.With("actors.contractors", contractors);
            f.With("actors.hash", h.Value);
            f.With("characters.count", ctx.characters.characters.Count);
            f.With("cast.entries", ctx.cast.entries.Count);

            h.Reset();
            long ledgerRecords = 0, ledgerSilver = 0, ledgerContractor = 0;
            for (int i = 0; i < ctx.contracts.contracts.Count; i++)
            {
                Contract c = ctx.contracts.contracts[i];
                h.Add(c.id.Value).Add((long)c.status).Add(c.subStatus == null ? 0 : NetHash.Combine(0, c.subStatus)).Add(c.ledger.Count).Add(c.fieldLog.Count);
                for (int k = 0; k < c.ledger.Count; k++)
                {
                    MoneyRecord m = c.ledger[k];
                    ledgerRecords++;
                    ledgerSilver += m.silver;
                    ledgerContractor += m.contractorSilver;
                    h.Add((long)m.direction).Add((long)m.purpose).Add(m.silver).Add(m.contractorSilver).Add(m.pending ? 1 : 0);
                }
            }
            f.With("contracts.count", ctx.contracts.contracts.Count);
            f.With("contracts.offers", ctx.contracts.offers.Count);
            f.With("contracts.hash", h.Value);
            f.With("ledger.records", ledgerRecords);
            f.With("ledger.silver", ledgerSilver);
            f.With("ledger.contractorSilver", ledgerContractor);

            h.Reset();
            for (int i = 0; i < ctx.operations.operations.Count; i++)
            {
                Operation o = ctx.operations.operations[i];
                h.Add(o.id.Value).Add((long)o.status).Add((long)o.phase).Add(o.outcome == null ? 0 : 1).Add(o.careerEligible ? 1 : 0).Add(o.careerOutcomeApplied ? 1 : 0);
            }
            f.With("operations.count", ctx.operations.operations.Count);
            f.With("operations.hash", h.Value);

            f.With("intel.requests", ctx.intel.requests.Count);
            f.With("intel.leads", ctx.intel.leads.Count);
            f.With("opportunities.count", ctx.opportunities.opportunities.Count);
            f.With("relations.count", ctx.relations.Count);
            f.With("knowledge.books", ctx.knowledge.books.Count);
            f.With("consequences.pending", ctx.consequences.pending.Count);
            f.With("history.records", ctx.ledger.records.Count);
            f.With("history.dropped", ctx.ledger.droppedCount);
            f.With("summaries.actors", ctx.summaries.actors.Count);
            if (journal != null)
            {
                f.With("journal.entries", journal.entries.Count);
                f.With("journal.dropped", journal.droppedCount);
            }

            if (scheduler != null)
            {
                h.Reset();
                int jobs = 0;
                foreach (ScheduledJob j in scheduler.AllJobs)
                {
                    jobs++;
                    h.Add(j.seq).Add(j.dueTick).Add(j.kind == null ? 0 : NetHash.Combine(0, j.kind)).Add(j.target).Add(j.arg);
                }
                f.With("scheduler.jobs", jobs);
                f.With("scheduler.hash", h.Value);
            }
            return f;
        }

        /// <summary>An FNV-style accumulator (reused, so a fingerprint allocates almost nothing).</summary>
        private sealed class HashAcc
        {
            public long Value = Seed;

            public void Reset() { Value = Seed; }

            public HashAcc Add(long v)
            {
                unchecked
                {
                    Value ^= v;
                    Value *= 1099511628211L;
                }
                return this;
            }
        }

        private const long Seed = 1469598103934665603L;

        /// <summary>The names of every value that differs from <paramref name="other"/>, "name: this -> other".</summary>
        public List<string> Diff(LiveFingerprint other)
        {
            List<string> d = new List<string>();
            foreach (KeyValuePair<string, long> kv in parts)
            {
                long o;
                if (!other.parts.TryGetValue(kv.Key, out o)) d.Add(kv.Key + ": " + kv.Value + " -> (missing)");
                else if (o != kv.Value) d.Add(kv.Key + ": " + kv.Value + " -> " + o);
            }
            foreach (KeyValuePair<string, long> kv in other.parts) if (!parts.ContainsKey(kv.Key)) d.Add(kv.Key + ": (missing) -> " + kv.Value);
            return d;
        }

        public bool Same(LiveFingerprint other)
        {
            return other != null && Diff(other).Count == 0;
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, long> kv in parts) sb.Append(kv.Key).Append('=').Append(kv.Value).Append(' ');
            return sb.ToString().TrimEnd();
        }
    }
}
