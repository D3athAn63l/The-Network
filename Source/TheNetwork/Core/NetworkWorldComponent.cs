using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using TheNetwork.Domain;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;
using TheNetwork.Persist.Events;
using TheNetwork.Core;
using Verse;

namespace TheNetwork
{
    /// <summary>
    /// The single persistence root and tick entry point (ARCHITECTURE § 6.1, SAVE_AND_MIGRATION § 2).
    /// <list type="bullet">
    /// <item>The constructor is trivial: no Find.*, no Def lookups (it runs during world generation
    /// and when the mod is added to an existing save).</item>
    /// <item>ExposeData reads <c>saveVersion</c> first, then every store in the fixed order.</item>
    /// <item>FinalizeInit rebuilds runtime structures only: no events, no world mutation.</item>
    /// <item>Start-up runs once per session on first use (normally the first WorldComponentTick): it
    /// bootstraps (new world or newly added mod) or runs the post-load reconciliation. If it throws, the
    /// Network stays inactive for the rest of the session (<see cref="SessionGate"/>). After a good
    /// start the idle tick is one integer comparison.</item>
    /// </list>
    /// </summary>
    public class NetworkWorldComponent : WorldComponent
    {
        public const string ModVersion = "0.1.0";

        public static NetworkWorldComponent Instance { get; private set; }

        // ---- persisted
        public int saveVersion;
        public string createdWithModVersion;
        public string lastSavedWithModVersion;
        public int networkSeed;
        public bool bootstrapped;
        public bool preparedForRemoval;
        public IdAllocator ids = new IdAllocator();
        public NetworkState state = new NetworkState();

        // ---- runtime
        private NetworkRuntime runtime;
        private int loadedVersion;
        private readonly List<string> loadFailures = new List<string>();
        private MigrationContext migrationContext;

        public NetworkRuntime Runtime => runtime;

        public NetworkWorldComponent(World world) : base(world)
        {
            Instance = this;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                if (state.diagnostics.downgradedFrom <= 0) saveVersion = bootstrapped ? SaveMigrations.Current : saveVersion;
                lastSavedWithModVersion = ModVersion;
                runtime?.Scheduler.WriteTo(state.scheduler);
            }
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                NetScribe.PendingQuarantine.Clear();
                loadFailures.Clear();
            }

            Scribe_Values.Look(ref saveVersion, "saveVersion", 0, true);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                loadedVersion = saveVersion;
                NetScribe.LoadingVersion = saveVersion;
            }
            Scribe_Values.Look(ref createdWithModVersion, "createdWithModVersion");
            Scribe_Values.Look(ref lastSavedWithModVersion, "lastSavedWithModVersion");
            Scribe_Values.Look(ref networkSeed, "networkSeed", 0);
            Scribe_Values.Look(ref bootstrapped, "bootstrapped", false);
            Scribe_Values.Look(ref preparedForRemoval, "preparedForRemoval", false);
            Scribe_Deep.Look(ref ids, "ids");
            if (ids == null) ids = new IdAllocator();
            state.ExposeStores(Scribe.mode == LoadSaveMode.LoadingVars ? loadFailures : null);

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                for (int i = 0; i < NetScribe.PendingQuarantine.Count; i++) state.diagnostics.AddQuarantine(NetScribe.PendingQuarantine[i]);
                NetScribe.PendingQuarantine.Clear();
                for (int i = 0; i < loadFailures.Count; i++)
                {
                    string subsystem = loadFailures[i].Split('|')[0];
                    if (!state.diagnostics.degradedSubsystems.Contains(subsystem)) state.diagnostics.degradedSubsystems.Add(subsystem);
                    NetLog.Error(LogCategory.Save, "Store failed to load (subsystem degraded, other stores unaffected): " + loadFailures[i]);
                }
            }
        }

        public override void FinalizeInit(bool fromLoad)
        {
            base.FinalizeInit(fromLoad);
            try
            {
                if (fromLoad && bootstrapped)
                {
                    RunLoadMigrations();
                }
                BuildRuntime();
            }
            catch (Exception ex)
            {
                NetLog.Error(LogCategory.Kernel, "FinalizeInit failed; The Network is inactive this session: " + ex);
                runtime = null;
            }
        }

        private void RunLoadMigrations()
        {
            migrationContext = new MigrationContext { networkSeed = networkSeed };
            if (loadedVersion > SaveMigrations.Current)
            {
                // Saved by a newer Network: best-effort, warn once (SAVE_AND_MIGRATION § 6).
                state.diagnostics.downgradedFrom = loadedVersion;
                return;
            }
            int from = loadedVersion <= 0 ? 1 : loadedVersion;
            if (from < SaveMigrations.MinimumSupported)
            {
                NetLog.Error(LogCategory.Save, "Save version " + from + " is older than the oldest supported (" + SaveMigrations.MinimumSupported + ").");
            }
            saveVersion = SaveMigrations.Run(state, from, migrationContext, Find.TickManager?.TicksGame ?? 0);
        }

        private void BuildRuntime()
        {
            state.RebuildIndexes();
            RepairIdCounters();
            // Before any service exists, so every one of them sees the world's real seed.
            EnsureNetworkSeed();
            runtime = new NetworkRuntime(this, new GameClock());
            runtime.History.RebuildIndex();
            runtime.LoadScheduler();
        }

        /// <summary>
        /// A world that has not bootstrapped yet derives its Network seed from the world's own seed now,
        /// so no runtime service is ever built with a placeholder. A bootstrapped save keeps its persisted
        /// seed: nothing already committed is ever recomputed.
        /// </summary>
        private void EnsureNetworkSeed()
        {
            if (bootstrapped || networkSeed != 0) return;
            WorldInfo info = world?.info ?? Find.World?.info;
            string seedSource = (info?.seedString ?? "") + "|" + (info?.persistentRandomValue ?? 0);
            networkSeed = NetHash.String(seedSource);
        }

        /// <summary>IDs must stay above every id in use, even after a hand-edited or partial save.</summary>
        private void RepairIdCounters()
        {
            int max = 0;
            for (int i = 0; i < state.actors.actors.Count; i++) max = Math.Max(max, state.actors.actors[i].id.Value);
            for (int i = 0; i < state.characters.characters.Count; i++) max = Math.Max(max, state.characters.characters[i].id.Value);
            for (int i = 0; i < state.intel.requests.Count; i++) max = Math.Max(max, state.intel.requests[i].id.Value);
            for (int i = 0; i < state.intel.leads.Count; i++) max = Math.Max(max, state.intel.leads[i].id.Value);
            for (int i = 0; i < state.opportunities.opportunities.Count; i++) max = Math.Max(max, state.opportunities.opportunities[i].id.Value);
            for (int i = 0; i < state.history.records.Count; i++) max = Math.Max(max, state.history.records[i].id.Value);
            if (ids.EnsureAbove(max)) NetLog.Warn(LogCategory.Kernel, "ID counter was behind the data; raised above " + max + ".");
            for (int i = 0; i < state.journal.entries.Count; i++) ids.EnsureEventSeqAbove(state.journal.entries[i].seq);
        }

        public override void WorldComponentTick()
        {
            if (runtime == null) return;
            // Failed start-up: nothing runs for the rest of the session (and nothing is logged again).
            if (!runtime.EnsureStarted() || preparedForRemoval) return;
            runtime.Tick();
        }

        /// <summary>
        /// Start-up, run once per session by <see cref="SessionGate"/> on first use: re-registers the signal
        /// receiver, then bootstraps or reconciles. Throwing leaves the session Failed; nothing here deletes
        /// or rewrites saved data, and every step is safe to run again on the next load.
        /// </summary>
        internal void RunStartup(SessionGate gate)
        {
            gate.Stage("signal registration");
            if (!runtime.Signals.Register()) throw new InvalidOperationException("the vanilla SignalManager is not available");
            if (preparedForRemoval) return;
            if (!bootstrapped)
            {
                gate.Stage("bootstrap");
                Bootstrap();
            }
            else
            {
                gate.Stage("load reconciliation");
                AfterLoad();
            }
        }

        private void Bootstrap()
        {
            DomainContext ctx = runtime.Ctx;
            EnsureNetworkSeed();
            ctx.networkSeed = networkSeed;
            createdWithModVersion = ModVersion;
            saveVersion = SaveMigrations.Current;

            ctx.Actors.EnsurePlayerProxy(ctx.world.PlayerFaction());
            ctx.Actors.EnsureExchange();

            NetworkSettings settings = NetworkMod.Settings;
            if (settings != null) NetworkStartup.EnsureRoster(settings, "bootstrap");
            List<string> report = new List<string>();
            ctx.Actors.ImportCast(settings?.roster, settings?.LoadedVersion ?? NetworkSettings.CurrentVersion, report);
            int fixers = ctx.Actors.InstantiateFixers();
            int contractors = ctx.Contractors.InstantiateFromSnapshot();

            ScheduleSweeps();
            SystemEvent boot = EventFactory.Make<SystemEvent>(EventKeys.NetworkBootstrapped, Importance.Minor);
            boot.count1 = state.actors.actors.Count;
            runtime.Bus.Publish(boot);
            SystemEvent cast = EventFactory.Make<SystemEvent>(EventKeys.CastImported, Importance.Minor);
            cast.count1 = state.cast.Count(Domain.Actors.CastEntryKind.Contractor);
            cast.count2 = state.cast.Count(Domain.Actors.CastEntryKind.Fixer);
            cast.count3 = fixers + contractors;
            cast.note = report.Count > 0 ? report[0] : null;
            runtime.Bus.Publish(cast);
            // Only once every step above has finished: a bootstrap that failed part-way is simply run
            // again on the next load (each step is idempotent).
            bootstrapped = true;
            NetLog.Info(LogCategory.Kernel, "The Network bootstrapped (save format " + saveVersion + "): " + fixers + " Fixers, the Exchange, "
                + contractors + " contractors from the world's cast snapshot. " + NetworkBuildStamp.Stamp);
        }

        private void AfterLoad()
        {
            DomainContext ctx = runtime.Ctx;
            ctx.Actors.EnsurePlayerProxy(ctx.world.PlayerFaction());
            ctx.Actors.EnsureExchange();
            if (state.diagnostics.downgradedFrom > 0 && state.diagnostics.FirstTime("downgraded:" + state.diagnostics.downgradedFrom))
            {
                NetLog.Warn(LogCategory.Save, "This save was made with a newer version of The Network (save format " + state.diagnostics.downgradedFrom + "); some Network data may be lost if you save.");
                Find.LetterStack?.ReceiveLetter("TheNetwork_DowngradeLabel".Translate(), "TheNetwork_DowngradeText".Translate(), LetterDefOf.NegativeEvent);
            }
            if (migrationContext != null)
            {
                for (int i = 0; i < migrationContext.deferred.Count; i++) migrationContext.deferred[i]();
                migrationContext = null;
            }
            // A world saved before Phase 2 holds contractor templates in its snapshot but no contractor
            // actors: they are instantiated now, from the world's own snapshot (idempotent).
            int newContractors = ctx.Contractors.InstantiateFromSnapshot();
            if (newContractors > 0) NetLog.Info(LogCategory.Actors, "Instantiated " + newContractors + " contractors from this world's cast snapshot.");
            ValidationReport report = NetValidator.Run(runtime, ValidationMode.OnLoad);
            ScheduleSweeps();
            SystemEvent loaded = EventFactory.Make<SystemEvent>(EventKeys.NetworkLoaded, Importance.Minor);
            loaded.count1 = report.Findings.Count;
            loaded.count2 = report.Repairs;
            runtime.Bus.Publish(loaded);
            NetLog.Info(LogCategory.Kernel, "The Network loaded: " + state.actors.actors.Count + " actors, " + state.intel.requests.Count + " intel requests, "
                + state.opportunities.opportunities.Count + " opportunities, " + state.history.records.Count + " history records, " + runtime.Scheduler.Count + " jobs; "
                + report.Summary() + ". " + NetworkBuildStamp.Stamp);
        }

        private void ScheduleSweeps()
        {
            int now = runtime.Clock.Now;
            runtime.Ctx.Upkeep.EnsurePopulationJob();
            if (!runtime.Scheduler.Has(JobKinds.HistorySweep, 0))
            {
                runtime.Scheduler.Schedule(JobKinds.HistorySweep, NetScheduler.StaggeredDue(now + JobKinds.SweepPeriod / 2, networkSeed, JobKinds.HistorySweep, JobKinds.SweepPeriod), 0);
            }
            if (!runtime.Scheduler.Has(JobKinds.CompactSweep, 0))
            {
                runtime.Scheduler.Schedule(JobKinds.CompactSweep, NetScheduler.StaggeredDue(now + JobKinds.SweepPeriod / 2, networkSeed, JobKinds.CompactSweep, JobKinds.SweepPeriod), 0);
            }
        }

        /// <summary>Dev / tests: forces the lazy start now. True when the Network is running.</summary>
        public bool StartNow()
        {
            return runtime != null && runtime.EnsureStarted();
        }
    }
}
