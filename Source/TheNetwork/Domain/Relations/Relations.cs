using System;
using System.Collections.Generic;
using TheNetwork.Domain.Actors;
using TheNetwork.History;
using TheNetwork.Kernel;
using TheNetwork.Persist.Events;
using Verse;

namespace TheNetwork.Domain.Relations
{
    // Relationships core (DATA_MODEL § 12, ADR-021): sparse DIRECTED edges, created only when two actors
    // actually interact, decaying toward neutral lazily (on read and on write, from updatedTick; there is
    // no periodic sweep). Obligations, contacts, rivals, blacklists and gossip are later phases.

    public sealed class RelationCounters : IExposable
    {
        public int jobsTogether;
        public int successes;
        public int failures;
        public int betrayals;
        public int defaults;
        public int cancellations;

        public void ExposeData()
        {
            Scribe_Values.Look(ref jobsTogether, "jobs", 0);
            Scribe_Values.Look(ref successes, "successes", 0);
            Scribe_Values.Look(ref failures, "failures", 0);
            Scribe_Values.Look(ref betrayals, "betrayals", 0);
            Scribe_Values.Look(ref defaults, "defaults", 0);
            Scribe_Values.Look(ref cancellations, "cancellations", 0);
        }
    }

    public sealed class RelationEdge : IExposable
    {
        public const int MaxSalient = 3;

        public ActorId from;
        public ActorId to;

        /// <summary>Like / dislike, -100..100 (internal; the UI shows a descriptor).</summary>
        public float standing;

        /// <summary>Reliability expectation, 0..1; neutral 0.5.</summary>
        public float trust = 0.5f;

        public float familiarity;
        public int updatedTick;
        public RelationCounters counters = new RelationCounters();

        /// <summary>The most defining shared records (≤ 3). A pruned record reads as "forgotten".</summary>
        public List<HistoryRecordId> salient = new List<HistoryRecordId>();

        public long Key => RelationStore.KeyOf(from, to);

        public void ExposeData()
        {
            NetScribe.Look(ref from, "from");
            NetScribe.Look(ref to, "to");
            Scribe_Values.Look(ref standing, "standing", 0f);
            Scribe_Values.Look(ref trust, "trust", 0.5f);
            Scribe_Values.Look(ref familiarity, "familiarity", 0f);
            Scribe_Values.Look(ref updatedTick, "updated", 0);
            Scribe_Deep.Look(ref counters, "counters");
            NetScribe.LookIntList(ref salient, "salient", h => h.Value, v => new HistoryRecordId(v));
            if (Scribe.mode == LoadSaveMode.LoadingVars && counters == null) counters = new RelationCounters();
        }
    }

    public sealed class RelationStore : IExposable
    {
        public List<RelationEdge> edges = new List<RelationEdge>();

        private readonly Dictionary<long, RelationEdge> byKey = new Dictionary<long, RelationEdge>();

        public static long KeyOf(ActorId from, ActorId to)
        {
            return ((long)from.Value << 32) | (uint)to.Value;
        }

        public void ExposeData()
        {
            NetScribe.LookListTolerant(ref edges, "edges", "relations");
        }

        public void RebuildIndex()
        {
            byKey.Clear();
            for (int i = 0; i < edges.Count; i++)
            {
                RelationEdge e = edges[i];
                if (e != null && e.from.IsValid && e.to.IsValid) byKey[e.Key] = e;
            }
        }

        public RelationEdge Find(ActorId from, ActorId to)
        {
            RelationEdge e;
            return byKey.TryGetValue(KeyOf(from, to), out e) ? e : null;
        }

        public RelationEdge GetOrCreate(ActorId from, ActorId to, int now)
        {
            RelationEdge e = Find(from, to);
            if (e != null) return e;
            e = new RelationEdge { from = from, to = to, updatedTick = now };
            edges.Add(e);
            byKey[e.Key] = e;
            return e;
        }

        public void Remove(RelationEdge e)
        {
            edges.Remove(e);
            byKey.Remove(e.Key);
        }

        public int Count => edges.Count;
    }

    /// <summary>A change to one directed edge. Every value is internal.</summary>
    public struct RelationDelta
    {
        public float standing;
        public float trust;
        public float familiarity;
        public int jobs;
        public int successes;
        public int failures;
        public int betrayals;
        public int defaults;
        public int cancellations;
    }

    /// <summary>A decayed, read-only view of an edge (the neutral default when none exists).</summary>
    public struct RelationView
    {
        public bool exists;
        public float standing;
        public float trust;
        public float familiarity;
        public int jobs;
        public int successes;
        public int failures;
        public int betrayals;
        public int defaults;
    }

    /// <summary>
    /// Reads and updates relationships (ARCHITECTURE § 6.10). The Relationships consumer applies the
    /// deterministic Phase 2 update rules to contract and payment events, so a contractor whose employer
    /// defaulted will not behave the same way next time. Willingness and pricing read <see cref="Get"/>.
    /// </summary>
    public sealed class RelationService : IEventConsumer
    {
        public const int GlobalCap = 4000;
        public static readonly float StandingHalfLife = Ticks.PerYear * 2f;
        public static readonly float TrustHalfLife = Ticks.PerYear * 2f;
        public static readonly float FamiliarityHalfLife = Ticks.PerYear * 4f;

        public static readonly string[] ConsumedKeys =
        {
            EventKeys.ContractAwarded, EventKeys.ContractCompleted, EventKeys.ContractPartiallyCompleted, EventKeys.ContractFailed,
            EventKeys.ContractCancelled, EventKeys.PaymentDefaulted, EventKeys.PaymentReceived
        };

        private readonly DomainContext ctx;

        public RelationService(DomainContext ctx)
        {
            this.ctx = ctx;
        }

        public string Name => "Relations";

        private static float Decay(float value, float neutral, int dt, float halfLife)
        {
            if (dt <= 0) return value;
            return neutral + (value - neutral) * (float)Math.Pow(0.5, dt / (double)halfLife);
        }

        /// <summary>The edge from → to as of now, decayed; a neutral default when none exists. Pure.</summary>
        public RelationView Get(ActorId from, ActorId to)
        {
            RelationEdge e = ctx.relations.Find(from, to);
            if (e == null) return new RelationView { trust = 0.5f };
            int dt = ctx.Now - e.updatedTick;
            return new RelationView
            {
                exists = true,
                standing = Decay(e.standing, 0f, dt, StandingHalfLife),
                trust = Decay(e.trust, 0.5f, dt, TrustHalfLife),
                familiarity = Decay(e.familiarity, 0f, dt, FamiliarityHalfLife),
                jobs = e.counters.jobsTogether,
                successes = e.counters.successes,
                failures = e.counters.failures,
                betrayals = e.counters.betrayals,
                defaults = e.counters.defaults
            };
        }

        /// <summary>Applies decay up to now, then the delta (deterministic; the order never matters to the result).</summary>
        public RelationEdge Apply(ActorId from, ActorId to, RelationDelta d, HistoryRecordId salient)
        {
            if (!from.IsValid || !to.IsValid || from == to) return null;
            int now = ctx.Now;
            RelationEdge e = ctx.relations.GetOrCreate(from, to, now);
            int dt = now - e.updatedTick;
            e.standing = Decay(e.standing, 0f, dt, StandingHalfLife);
            e.trust = Decay(e.trust, 0.5f, dt, TrustHalfLife);
            e.familiarity = Decay(e.familiarity, 0f, dt, FamiliarityHalfLife);
            e.updatedTick = now;
            e.standing = Math.Max(-100f, Math.Min(100f, e.standing + d.standing));
            e.trust = Math.Max(0f, Math.Min(1f, e.trust + d.trust));
            e.familiarity = Math.Max(0f, Math.Min(1f, e.familiarity + d.familiarity));
            e.counters.jobsTogether += d.jobs;
            e.counters.successes += d.successes;
            e.counters.failures += d.failures;
            e.counters.betrayals += d.betrayals;
            e.counters.defaults += d.defaults;
            e.counters.cancellations += d.cancellations;
            if (salient.IsValid && !e.salient.Contains(salient))
            {
                e.salient.Add(salient);
                if (e.salient.Count > RelationEdge.MaxSalient) e.salient.RemoveAt(0);
            }
            if (ctx.relations.Count > GlobalCap) EvictWeakest();
            StateVersion.Bump();
            return e;
        }

        /// <summary>
        /// The player-facing descriptor (master § 43): Unknown, Familiar, Friendly, Professional, Trusted,
        /// Rival, Bitter Rival, Hostile. Built from standing, trust, familiarity and counters.
        /// </summary>
        public string Descriptor(ActorId from, ActorId to)
        {
            RelationView v = Get(from, to);
            bool strongFeeling = Math.Abs(v.standing) >= 5f || v.defaults > 0 || v.betrayals > 0;
            if (!v.exists || (v.familiarity < 0.05f && v.jobs == 0 && !strongFeeling)) return "Unknown";
            if (v.standing <= -50f) return "Hostile";
            if (v.standing <= -25f || v.defaults > 0 && v.standing < -10f) return "BitterRival";
            if (v.standing <= -10f) return "Rival";
            if (v.trust >= 0.75f && v.jobs >= 2) return "Trusted";
            if (v.standing >= 25f) return "Friendly";
            if (v.jobs >= 1) return "Professional";
            return "Familiar";
        }

        // ------------------------------------------------------------------ consumer

        public void Handle(NetworkEvent evt)
        {
            ContractEvent e = evt as ContractEvent;
            if (e == null || !e.contractor.IsValid || !e.issuer.IsValid) return;
            HistoryRecordId record = RecordFor(e.seq);
            ActorId c = e.contractor, p = e.issuer;
            switch (e.typeKey)
            {
                case EventKeys.ContractAwarded:
                    Apply(c, p, new RelationDelta { familiarity = 0.08f }, HistoryRecordId.None);
                    Apply(p, c, new RelationDelta { familiarity = 0.08f }, HistoryRecordId.None);
                    if (e.broker.IsValid)
                    {
                        Apply(c, e.broker, new RelationDelta { familiarity = 0.05f, standing = 1f }, HistoryRecordId.None);
                        Apply(e.broker, c, new RelationDelta { familiarity = 0.05f }, HistoryRecordId.None);
                    }
                    break;
                case EventKeys.ContractCompleted:
                    Apply(c, p, new RelationDelta { standing = 8f, trust = 0.05f, familiarity = 0.1f, jobs = 1, successes = 1 }, record);
                    Apply(p, c, new RelationDelta { standing = 10f, trust = 0.1f, familiarity = 0.1f, jobs = 1, successes = 1 }, record);
                    break;
                case EventKeys.ContractPartiallyCompleted:
                    Apply(c, p, new RelationDelta { standing = 3f, trust = 0.02f, familiarity = 0.08f, jobs = 1 }, record);
                    Apply(p, c, new RelationDelta { standing = 2f, trust = 0.02f, familiarity = 0.08f, jobs = 1 }, record);
                    break;
                case EventKeys.ContractFailed:
                    Apply(c, p, new RelationDelta { standing = -2f, familiarity = 0.06f, jobs = 1, failures = 1 }, record);
                    Apply(p, c, new RelationDelta { standing = -6f, trust = -0.15f, familiarity = 0.06f, jobs = 1, failures = 1 }, record);
                    break;
                case EventKeys.ContractCancelled:
                    // Cancelling after the contractor committed costs goodwill; before award it costs nothing.
                    if (e.causeKey == "IssuerCancelledAfterAward") Apply(c, p, new RelationDelta { standing = -6f, trust = -0.06f, cancellations = 1 }, record);
                    break;
                case EventKeys.PaymentDefaulted:
                    Apply(c, p, new RelationDelta { standing = -25f, trust = -0.3f, defaults = 1 }, record);
                    break;
                case EventKeys.PaymentReceived:
                    if (e.causeKey == "Balance") Apply(c, p, new RelationDelta { trust = 0.03f }, HistoryRecordId.None);
                    break;
            }
        }

        private HistoryRecordId RecordFor(long seq)
        {
            HistoryLedger ledger = ctx.ledger;
            if (ledger == null) return HistoryRecordId.None;
            for (int i = ledger.records.Count - 1; i >= 0 && i >= ledger.records.Count - 4; i--)
            {
                if (ledger.records[i].sourceEventSeq == seq) return ledger.records[i].id;
            }
            return HistoryRecordId.None;
        }

        // ------------------------------------------------------------------ bounds

        private void EvictWeakest()
        {
            RelationEdge weakest = null;
            float best = float.MaxValue;
            List<RelationEdge> all = ctx.relations.edges;
            for (int i = 0; i < all.Count; i++)
            {
                RelationEdge e = all[i];
                float score = Math.Abs(e.standing) / 100f + e.familiarity + e.counters.jobsTogether * 0.2f - (ctx.Now - e.updatedTick) / (float)Ticks.PerYear;
                if (score < best)
                {
                    best = score;
                    weakest = e;
                }
            }
            if (weakest != null) ctx.relations.Remove(weakest);
        }

        /// <summary>Compaction: edges touching an actor that ended more than two years ago are dropped.</summary>
        public int Compact()
        {
            int removed = 0;
            List<RelationEdge> all = ctx.relations.edges;
            for (int i = all.Count - 1; i >= 0; i--)
            {
                RelationEdge e = all[i];
                if (Gone(e.from) || Gone(e.to))
                {
                    ctx.relations.Remove(e);
                    removed++;
                }
            }
            return removed;
        }

        private bool Gone(ActorId id)
        {
            NetworkActor a = ctx.actors.Get(id);
            return a == null || (a.status != ActorStatus.Active && a.endedTick >= 0 && ctx.Now - a.endedTick > 2 * Ticks.PerYear);
        }
    }
}
