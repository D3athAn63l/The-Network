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
        public readonly ContractReadModels ContractsRead;
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
                summaries = State.summaries,
                ledger = State.history,
                relations = State.relations,
                knowledge = State.knowledge,
                contracts = State.contracts,
                operations = State.operations,
                consequences = State.consequences,
                catalog = new LazyCatalog(),
                comms = new CommsAccessAdapter(),
                payment = new PaymentAdapter(),
                world = WorldFacts,
                sites = SiteAdapter,
                delivery = new DropPodDelivery()
            };
            Ctx.Actors = new ActorService(Ctx);
            Ctx.Intel = new IntelService(Ctx);
            Ctx.Opportunities = new OpportunityService(Ctx);
            Ctx.Contractors = new Domain.Contractors.ContractorService(Ctx);
            Ctx.Upkeep = new Domain.Contractors.UpkeepService(Ctx);
            Ctx.Relations = new Domain.Relations.RelationService(Ctx);
            Ctx.Knowledge = new Domain.Knowledge.KnowledgeService(Ctx);
            Ctx.Procurement = new Domain.Contracts.ProcurementService(Ctx);
            Ctx.Operations = new Domain.Operations.OperationService(Ctx);
            Ctx.Consequences = new Domain.Consequences.ConsequenceEngine(Ctx);
            RegisterContractKinds();
            Ctx.tuning.targetProvider = () => NetworkMod.Settings?.targetContractorCount ?? 100;

            History = new HistoryService(State.history, State.summaries, State.actors, root.ids, clock, root.networkSeed);
            Sites = new SiteCallbacks(Ctx, SiteAdapter);
            Signals = new SignalBridge(Ctx);
            Compaction = new CompactionService(State, Scheduler, clock, Ctx);
            Commands = new NetworkCommands(this);
            Read = new NetworkReadModels(this);
            ContractsRead = new ContractReadModels(this);

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
            Scheduler.RegisterKind(JobKinds.ContractorUpkeep, Ctx.Upkeep.UpkeepJob, true, true);
            Scheduler.RegisterKind(JobKinds.PopulationWeekly, Ctx.Upkeep.PopulationJobRun, true, true);
            RegisterPhaseTwoJobs(Scheduler, Ctx);
            Scheduler.OnJobFailed = OnJobFailed;
        }

        /// <summary>Phase 2 contract and operation jobs (shared with the headless test harness). All state-guarded.</summary>
        public static void RegisterPhaseTwoJobs(NetScheduler scheduler, DomainContext ctx)
        {
            scheduler.RegisterKind(JobKinds.ContractBidding, ctx.Procurement.RunBiddingPass, true, true);
            scheduler.RegisterKind(JobKinds.ContractOffers, ctx.Procurement.ExpireOffers, true, true);
            scheduler.RegisterKind(JobKinds.ContractExpire, ctx.Procurement.ExpireJobRun, true, true);
            scheduler.RegisterKind(JobKinds.ContractDecision, ctx.Procurement.DecisionJobRun, true, true);
            scheduler.RegisterKind(JobKinds.ContractDelivery, ctx.Procurement.DeliveryJobRun, true, true);
            scheduler.RegisterKind(JobKinds.ContractRefund, ctx.Procurement.RetryRefunds, true, false);
            scheduler.RegisterKind(JobKinds.OperationCheckpoint, ctx.Operations.RunCheckpoint, true, true);
            scheduler.RegisterKind(JobKinds.OperationTroubled, ctx.Operations.TroubledDeadline, true, true);
            scheduler.RegisterKind(JobKinds.ConsequenceFollowUp, ctx.Consequences.FollowUpJobRun, true, false);
        }

        /// <summary>Contract kind rules from XML (NetworkContractKindDef); the built-in Procurement rules otherwise.</summary>
        private static void RegisterContractKinds()
        {
            try
            {
                List<NetworkContractKindDef> defs = Verse.DefDatabase<NetworkContractKindDef>.AllDefsListForReading;
                for (int i = 0; i < defs.Count; i++) Domain.Contracts.ContractKindRegistry.Register(Domain.Contracts.ContractKindRules.From(defs[i]));
            }
            catch (Exception ex)
            {
                NetLog.WarnOnce(LogCategory.Contracts, "kinddefs", "Could not read contract kind defs; using built-in rules: " + ex.Message);
            }
        }

        private void RegisterConsumers()
        {
            Bus.Register(ConsumerOrder.History, History, HistoryService.ConsumedKeys);
            Bus.Register(ConsumerOrder.Relationships, Ctx.Relations, Domain.Relations.RelationService.ConsumedKeys);
            Bus.Register(ConsumerOrder.Consequences, Ctx.Consequences, Domain.Consequences.ConsequenceEngine.ConsumedKeys);
            Bus.Register(ConsumerOrder.Presentation, new LetterConsumer(Ctx), LetterConsumer.ConsumedKeys);
            Bus.Register(ConsumerOrder.Presentation, new ContractLetterConsumer(Ctx), ContractLetterConsumer.ConsumedKeys);
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
            else if (job.kind.StartsWith("contract.", StringComparison.Ordinal) || job.kind.StartsWith("consequence.", StringComparison.Ordinal))
            {
                Domain.Contracts.Contract c = State.contracts.Get(new ContractId(job.target));
                if (c != null && c.quarantinedReason == null)
                {
                    c.quarantinedReason = reason;
                    entity = c.id.Ref;
                }
            }
            else if (job.kind.StartsWith("operation.", StringComparison.Ordinal))
            {
                Domain.Operations.Operation o = State.operations.Get(new OperationId(job.target));
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
