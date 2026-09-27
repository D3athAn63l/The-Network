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
    /// <item>The first WorldComponentTick bootstraps (new world or newly added mod) or runs the
    /// post-load reconciliation. After that, the idle tick is one integer comparison.</item>
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
        private bool started;
        private int loadedVersion;
        private readonly List<string> loadFailures = new List<string>();
        private MigrationContext migrationContext;

        public NetworkRuntime Runtime => runtime;
        public bool Started => started;

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
            runtime = new NetworkRuntime(this, new GameClock());
            runtime.History.RebuildIndex();
            runtime.LoadScheduler();
            started = false;
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
            if (!started) EnsureStarted();
            if (preparedForRemoval) return;
            runtime.Tick();
        }

        /// <summary>
        /// First tick after a new world, a newly added mod or a load. Bootstraps or reconciles, re-registers
        /// the signal receiver. Runs once per session; failures are contained.
        /// </summary>
        private void EnsureStarted()
        {
            started = true;
            try
            {
                runtime.Signals.Register();
                if (preparedForRemoval) return;
                if (!bootstrapped) Bootstrap();
                else AfterLoad();
            }
            catch (Exception ex)
            {
                NetLog.Error(LogCategory.Kernel, "Network start-up failed: " + ex);
            }
        }

        private void Bootstrap()
        {
            DomainContext ctx = runtime.Ctx;
            string seedSource = (Find.World?.info?.seedString ?? "") + "|" + (Find.World?.info?.persistentRandomValue ?? 0);
            networkSeed = NetHash.String(seedSource);
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
            bootstrapped = true;

            ScheduleSweeps();
            SystemEvent boot = EventFactory.Make<SystemEvent>(EventKeys.NetworkBootstrapped, Importance.Minor);
            boot.count1 = state.actors.actors.Count;
            runtime.Bus.Publish(boot);
            SystemEvent cast = EventFactory.Make<SystemEvent>(EventKeys.CastImported, Importance.Minor);
            cast.count1 = state.cast.Count(Domain.Actors.CastEntryKind.Contractor);
            cast.count2 = state.cast.Count(Domain.Actors.CastEntryKind.Fixer);
            cast.count3 = fixers;
            cast.note = report.Count > 0 ? report[0] : null;
            runtime.Bus.Publish(cast);
            NetLog.Info(LogCategory.Kernel, "The Network bootstrapped (save format " + saveVersion + "): " + fixers + " Fixers, the Exchange, "
                + state.cast.Count(Domain.Actors.CastEntryKind.Contractor) + " contractor templates held for later phases. " + NetworkBuildStamp.Stamp);
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
            if (!runtime.Scheduler.Has(JobKinds.HistorySweep, 0))
            {
                runtime.Scheduler.Schedule(JobKinds.HistorySweep, NetScheduler.StaggeredDue(now + JobKinds.SweepPeriod / 2, networkSeed, JobKinds.HistorySweep, JobKinds.SweepPeriod), 0);
            }
            if (!runtime.Scheduler.Has(JobKinds.CompactSweep, 0))
            {
                runtime.Scheduler.Schedule(JobKinds.CompactSweep, NetScheduler.StaggeredDue(now + JobKinds.SweepPeriod / 2, networkSeed, JobKinds.CompactSweep, JobKinds.SweepPeriod), 0);
            }
        }

        /// <summary>Dev / tests: forces the lazy start now.</summary>
        public void StartNow()
        {
            if (runtime != null && !started) EnsureStarted();
        }
    }
}
