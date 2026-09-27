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
    /// Runs migrations after all stores load and before runtime caches are rebuilt. Version 1 is the
    /// first shipped save format, so the registry is empty: it exists so version 2 is a pure addition.
    /// </summary>
    public static class SaveMigrations
    {
        /// <summary>NetworkSaveVersion.</summary>
        public const int Current = 1;

        /// <summary>The oldest save version this build can migrate (SAVE_AND_MIGRATION § 6).</summary>
        public const int MinimumSupported = 1;

        public static readonly List<INetworkMigration> Registry = new List<INetworkMigration>();

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
}
