using System;
using System.Collections.Generic;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Catalog;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Domain.Ports;
using TheNetwork.History;
using TheNetwork.Integration;
using TheNetwork.Kernel;
using TheNetwork.Persist.Events;
using TheNetwork.UI;

namespace TheNetwork.Core
{
    /// <summary>ICatalog that builds the session catalog lazily on first use (never during world generation).</summary>
    public sealed class LazyCatalog : ICatalog
    {
        public ItemCatalog Catalog => CatalogCache.Get();

        public bool IsRequestable(string defName, out string reasonKey) { return Catalog.IsRequestable(defName, out reasonKey); }
        public ItemFacts Facts(string defName) { return defName == null ? null : Catalog.Facts(defName); }
        public IList<ItemFacts> ExtraCargoPool(int maxTechLevel) { return Catalog.ExtraCargoPool(maxTechLevel); }
        public void MarkUnusable(string defName, string reason) { Catalog.MarkUnusable(defName, reason); }
    }

    /// <summary>
    /// Runtime services, caches and adapters (ARCHITECTURE § 6.1). Rebuilt from persisted state in
    /// FinalizeInit; never saved. The only per-tick cost is <see cref="NetScheduler.NextDueTick"/>.
    /// Every entry point (tick, site callback, signal, command) goes through <see cref="EnsureStarted"/>
    /// or <see cref="Active"/>, so a failed start-up leaves nothing half-running.
    /// </summary>
    public sealed class NetworkRuntime
    {
        public static NetworkRuntime Current => NetworkWorldComponent.Instance?.Runtime;

        public readonly NetworkWorldComponent Root;
        public readonly NetworkState State;
        public readonly IClock Clock;
        public readonly NetScheduler Scheduler;
        public readonly NetworkEventBus Bus;
        public readonly DomainContext Ctx;
        public readonly HistoryService History;
        public readonly SiteAdapter SiteAdapter;
        public readonly SiteCallbacks Sites;
        public readonly SignalBridge Signals;
        public readonly CompactionService Compaction;
        public readonly NetworkCommands Commands;
        public readonly NetworkReadModels Read;
        public readonly WorldFactsAdapter WorldFacts;

        /// <summary>This session's start-up state (runtime only).</summary>
        public readonly SessionGate Session = new SessionGate();

        /// <summary>Prepared for removal: started, but nothing may change Network state.</summary>
        public bool Inert => Root.preparedForRemoval;

        // Cached so the per-tick check allocates nothing.
        private readonly Action<SessionGate> startup;

        /// <summary>Starts the Network on first use; false when start-up failed this session (or is still running).</summary>
        public bool EnsureStarted()
        {
            return Session.IsRunning || Session.Ensure(startup);
        }

        /// <summary>May gameplay change Network state now? Started successfully and not prepared for removal.</summary>
        public bool Active => EnsureStarted() && !Inert;

        public NetworkRuntime(NetworkWorldComponent root, IClock clock)
        {
            Root = root;
            State = root.state;
            startup = root.RunStartup;
            Clock = clock;
            Scheduler = new NetScheduler(root.ids, clock);
            Bus = new NetworkEventBus(root.ids, clock, State.journal, State.diagnostics);
            SiteAdapter = new SiteAdapter();
            WorldFacts = new WorldFactsAdapter();

            Ctx = new DomainContext
            {
                networkSeed = root.networkSeed,
                ids = root.ids,
                clock = clock,
                scheduler = Scheduler,
                bus = Bus,
                diagnostics = State.diagnostics,
                cast = State.cast,
                actors = State.actors,
                characters = State.characters,
                intel = State.intel,
                opportunities = State.opportunities,
                catalog = new LazyCatalog(),
                comms = new CommsAccessAdapter(),
                payment = new PaymentAdapter(),
                world = WorldFacts,
                sites = SiteAdapter
            };
            Ctx.Actors = new ActorService(Ctx);
            Ctx.Intel = new IntelService(Ctx);
            Ctx.Opportunities = new OpportunityService(Ctx);

            History = new HistoryService(State.history, State.summaries, State.actors, root.ids, clock, root.networkSeed);
            Sites = new SiteCallbacks(Ctx, SiteAdapter);
            Signals = new SignalBridge(Ctx);
            Compaction = new CompactionService(State, Scheduler, clock);
            Commands = new NetworkCommands(this);
            Read = new NetworkReadModels(this);

            RegisterJobs();
            RegisterConsumers();
        }

        private void RegisterJobs()
        {
            // Every Phase 1 handler is state-guarded (it re-checks the entity before acting), so all
            // kinds may be retried with backoff (ARCHITECTURE § 12).
            Scheduler.RegisterKind(JobKinds.IntelRound, Ctx.Intel.RunRound, true, true);
            Scheduler.RegisterKind(JobKinds.IntelClose, Ctx.Intel.CloseJob, true, true);
            Scheduler.RegisterKind(JobKinds.OppSample, Ctx.Opportunities.SampleJob, true, true);
            Scheduler.RegisterKind(JobKinds.OppWarn, Ctx.Opportunities.WarnJob, true, true);
            Scheduler.RegisterKind(JobKinds.OppClose, Ctx.Opportunities.CloseJob, true, true);
            Scheduler.RegisterKind(JobKinds.HistorySweep, job => History.SweepJob(job, Scheduler), true, true);
            Scheduler.RegisterKind(JobKinds.CompactSweep, Compaction.SweepJob, true, true);
            Scheduler.RegisterKind(JobKinds.RefundRetry, Ctx.Intel.RetryRefunds, true, false);
            Scheduler.OnJobFailed = OnJobFailed;
        }

        private void RegisterConsumers()
        {
            Bus.Register(ConsumerOrder.History, History, HistoryService.ConsumedKeys);
            Bus.Register(ConsumerOrder.Presentation, new LetterConsumer(Ctx), LetterConsumer.ConsumedKeys);
        }

        /// <summary>A job that failed on every attempt: quarantine its target entity (kept, skipped).</summary>
        private void OnJobFailed(ScheduledJob job, Exception ex)
        {
            string reason = "JobFailed:" + job.kind;
            EntityRef entity = EntityRef.None;
            if (job.kind.StartsWith("intel.", StringComparison.Ordinal) || job.kind == JobKinds.RefundRetry)
            {
                IntelRequest r = State.intel.Get(new IntelRequestId(job.target));
                if (r != null && r.quarantinedReason == null)
                {
                    r.quarantinedReason = reason;
                    entity = r.id.Ref;
                }
            }
            else if (job.kind.StartsWith("opp.", StringComparison.Ordinal))
            {
                Opportunity o = State.opportunities.Get(new OpportunityId(job.target));
                if (o != null && o.quarantinedReason == null)
                {
                    o.quarantinedReason = reason;
                    entity = o.id.Ref;
                }
            }
            if (entity.IsValid)
            {
                State.diagnostics.AddQuarantine(QuarantineRecord.ForEntity(job.kind, entity, reason + ": " + NetScribe.Truncate(ex.Message, 200), Clock.Now));
            }
            StateVersion.Bump();
        }

        /// <summary>Loads the persisted job list into the heap (FinalizeInit; no world mutation).</summary>
        public void LoadScheduler()
        {
            Scheduler.LoadFrom(State.scheduler);
        }

        public void Tick()
        {
            if (Clock.Now < Scheduler.NextDueTick) return;
            Scheduler.RunDue();
        }
    }
}
