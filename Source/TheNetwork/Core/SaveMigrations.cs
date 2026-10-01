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
        /// 4 = Phase 2.75 (reputation score beneath the fame band, the career record, the operation career
        /// flags, the contractor-money attribution on contract ledgers).
        /// </summary>
        public const int Current = 4;

        /// <summary>The oldest save version this build can migrate (SAVE_AND_MIGRATION § 6).</summary>
        public const int MinimumSupported = 1;

        public static readonly List<INetworkMigration> Registry = new List<INetworkMigration>
        {
            new V1ToV2PhaseTwoStores(),
            new V2ToV3SpatialContinuity(),
            new V3ToV4ContractorCareers()
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

    /// <summary>
    /// 3 → 4 (Phase 2.75). Preserves every visible fact and invents no past. Each actor's reputation score
    /// starts at the FLOOR of its existing fame band (nobody is raised or lowered by the migration); each
    /// contractor's career record starts with <c>legacyResolved</c> equal to the jobs it had resolved and
    /// every other counter at zero (no wins, losses, income or upgrades are reconstructed from pruned
    /// history); an operation already running keeps its lifecycle but is NOT career-eligible, so it can
    /// never receive career credit even if it finishes later; contract ledger records carry no contractor
    /// attribution (money already credited stays where it is, and nothing is rescanned or credited again).
    /// Funds, equipment, skill, career stage, mobility, spatial state, contracts, operations and Field Logs
    /// are not touched.
    /// </summary>
    public sealed class V3ToV4ContractorCareers : INetworkMigration
    {
        public int From => 3;
        public int To => 4;
        public string Name => "ContractorCareers";
        public string Subsystem => "careers";

        public void Apply(NetworkState state, MigrationContext ctx)
        {
            int actors = 0, contractors = 0, running = 0;
            for (int i = 0; i < state.actors.actors.Count; i++)
            {
                Domain.Actors.NetworkActor a = state.actors.actors[i];
                if (a.reputation == null) continue;
                // The score is the truth from now on; the band stays exactly what it was.
                a.reputation.SetBand(a.reputation.fame);
                actors++;
                Persist.ContractorSimulation sim = a.Get<Persist.ContractorSimulation>();
                if (sim == null) continue;
                if (sim.career == null) sim.career = new Persist.CareerRecord();
                sim.career.legacyResolved = Math.Max(0, sim.opsCompleted);
                contractors++;
            }
            for (int i = 0; i < state.operations.operations.Count; i++)
            {
                Domain.Operations.Operation op = state.operations.operations[i];
                // A pre-2.75 operation is never eligible, finished or not.
                op.careerEligible = false;
                op.careerOutcomeApplied = false;
                if (!op.IsFinished) running++;
            }
            ctx.log.Add(actors + " reputation scores set to their band floors; " + contractors + " contractors' careers start with their resolved jobs as legacy; " + running + " running operations stay career-ineligible.");
        }
    }
}
