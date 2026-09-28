using System;
using System.Collections.Generic;
using TheNetwork.Kernel;

namespace TheNetwork.Core
{
    public sealed class MigrationContext
    {
        public int networkSeed;
        public List<string> log = new List<string>();

        /// <summary>Work that must wait for the first tick (events, letters, scheduling).</summary>
        public List<Action> deferred = new List<Action>();
    }

    /// <summary>A forward-only semantic save migration (SAVE_AND_MIGRATION § 4.1).</summary>
    public interface INetworkMigration
    {
        int From { get; }
        int To { get; }
        string Name { get; }

        /// <summary>The subsystem it touches; a failure degrades only that subsystem.</summary>
        string Subsystem { get; }

        void Apply(NetworkState state, MigrationContext ctx);
    }

    /// <summary>
    /// Runs migrations after all stores load and before runtime caches are rebuilt. Version 1 was the
    /// first shipped save format (Phase 1); version 2 adds the Phase 2 stores.
    /// </summary>
    public static class SaveMigrations
    {
        /// <summary>
        /// NetworkSaveVersion. 2 = Phase 2 (contractors, contracts, operations, relations, knowledge).
        /// 3 = Phase 2.5 (contractor spatial state, operation spatial plans, contract Field Logs).
        /// </summary>
        public const int Current = 3;

        /// <summary>The oldest save version this build can migrate (SAVE_AND_MIGRATION § 6).</summary>
        public const int MinimumSupported = 1;

        public static readonly List<INetworkMigration> Registry = new List<INetworkMigration>
        {
            new V1ToV2PhaseTwoStores(),
            new V2ToV3SpatialContinuity()
        };

        /// <summary>
        /// Brings a loaded state to <see cref="Current"/>. Returns the resulting version. A throwing
        /// migration is recorded and its subsystem degraded; the chain continues for the others.
        /// </summary>
        public static int Run(NetworkState state, int loadedVersion, MigrationContext ctx, int tick)
        {
            int v = loadedVersion;
            List<INetworkMigration> ordered = new List<INetworkMigration>(Registry);
            ordered.Sort((a, b) => a.From.CompareTo(b.From));
            HashSet<string> stopped = new HashSet<string>();
            for (int i = 0; i < ordered.Count; i++)
            {
                INetworkMigration m = ordered[i];
                if (m.From < v || stopped.Contains(m.Subsystem)) continue;
                try
                {
                    m.Apply(state, ctx);
                    ctx.log.Add("Migration " + m.Name + " " + m.From + "→" + m.To + " applied.");
                }
                catch (Exception ex)
                {
                    stopped.Add(m.Subsystem);
                    state.diagnostics.failedMigrations.Add(new FailedMigrationRecord { name = m.Name, from = m.From, to = m.To, tick = tick, message = NetScribe.Truncate(ex.ToString(), 1000) });
                    if (!state.diagnostics.degradedSubsystems.Contains(m.Subsystem)) state.diagnostics.degradedSubsystems.Add(m.Subsystem);
                    NetLog.Error(LogCategory.Save, "Save migration " + m.Name + " failed; subsystem '" + m.Subsystem + "' is degraded: " + ex.Message);
                }
                v = m.To;
            }
            return Current;
        }
    }

    /// <summary>
    /// 1 → 2 (Phase 2). The contracts, operations, relations and knowledge slots were empty reserved
    /// nodes in version 1, so they load as empty stores and no data changes. Contractor actors are
    /// instantiated from the world's own cast snapshot at start-up, idempotently, for new and upgraded
    /// worlds alike (never from the current ModSettings roster). The version bump tells a Phase 1 build
    /// that this save is newer than it understands.
    /// </summary>
    public sealed class V1ToV2PhaseTwoStores : INetworkMigration
    {
        public int From => 1;
        public int To => 2;
        public string Name => "PhaseTwoStores";
        public string Subsystem => "contracts";

        public void Apply(NetworkState state, MigrationContext ctx)
        {
            ctx.log.Add("Phase 2 stores start empty; contractors are instantiated from the world snapshot at start-up.");
        }
    }

    /// <summary>
    /// 2 → 3 (Phase 2.5). Nothing is rewritten. A Phase 2 contractor has no spatial node and loads
    /// Uninitialized; it gets a deterministic anchor at start-up once world data is available, with no
    /// invented past journey. An operation already running keeps its Phase 2 lifecycle untouched (quote,
    /// ETA, frozen inputs, outcome, cargo, casualties, money, checkpoints): it has no spatial plan, and
    /// only operations started from now on are coupled to spatial continuity. Field Logs start with the
    /// next contract accepted.
    /// </summary>
    public sealed class V2ToV3SpatialContinuity : INetworkMigration
    {
        public int From => 2;
        public int To => 3;
        public string Name => "SpatialContinuity";
        public string Subsystem => "spatial";

        public void Apply(NetworkState state, MigrationContext ctx)
        {
            int contractors = 0, running = 0;
            for (int i = 0; i < state.actors.actors.Count; i++) if (state.actors.actors[i].Get<Persist.ContractorSimulation>() != null) contractors++;
            for (int i = 0; i < state.operations.operations.Count; i++) if (!state.operations.operations[i].IsFinished) running++;
            ctx.log.Add(contractors + " contractors start Uninitialized (anchored at start-up); " + running + " running operations keep their Phase 2 lifecycle without a spatial plan.");
        }
    }
}
