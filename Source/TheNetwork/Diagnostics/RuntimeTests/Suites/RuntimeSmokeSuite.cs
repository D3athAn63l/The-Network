using System;
using System.Collections.Generic;
using TheNetwork.Core;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Catalog;
using TheNetwork.Integration;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Diagnostics.RuntimeTests.Suites
{
    /// <summary>
    /// RT-SMOKE: seconds-or-less checks that the mod loaded and started in THIS RimWorld process (after replacing the DLL or loading a
    /// save). Game-only (it reads the live Network and real Defs); read-only; it never fails because a colony has no comms console or
    /// no home map: those are gameplay states.
    /// </summary>
    public static class RuntimeSmokeSuite
    {
        public const string Suite = "SMOKE";

        public static IEnumerable<RuntimeTestCase> Cases(IRuntimeTestHost host)
        {
            yield return RuntimeTestCase.Immediate(Suite, "RT-SMOKE-001", "NetworkWorldComponent exists", WorldComponentExists);
            yield return RuntimeTestCase.Immediate(Suite, "RT-SMOKE-002", "NetworkRuntime exists and starts", RuntimeStarts);
            yield return RuntimeTestCase.Immediate(Suite, "RT-SMOKE-003", "The save version is understood", SaveVersionUnderstood);
            yield return RuntimeTestCase.Immediate(Suite, "RT-SMOKE-004", "Network stores and indexes are present and addressable", StoresPresent);
            yield return RuntimeTestCase.Immediate(Suite, "RT-SMOKE-005", "The item catalog builds from the loaded Defs", CatalogBuilds);
            yield return RuntimeTestCase.Immediate(Suite, "RT-SMOKE-006", "The procurement contract kind is registered", ProcurementKindRegistered);
            yield return RuntimeTestCase.Immediate(Suite, "RT-SMOKE-007", "Actor indexes resolve the known actors consistently", ActorIndexes);
            yield return RuntimeTestCase.Immediate(Suite, "RT-SMOKE-008", "No subsystem failed at start-up", NoStartupFailure);
        }

        private static NetworkWorldComponent Root(RuntimeTestContext ctx)
        {
            NetworkWorldComponent root = NetworkWorldComponent.Instance;
            if (root == null) ctx.Assert.Fail("There is no NetworkWorldComponent: the Network is not part of this game (no world loaded, or the component was not created).");
            return root;
        }

        private static NetworkRuntime Rt(RuntimeTestContext ctx)
        {
            NetworkRuntime rt = Root(ctx).Runtime;
            ctx.Assert.NotNull(rt, "The Network runtime was not built (FinalizeInit failed: see the log).");
            return rt;
        }

        private static void WorldComponentExists(RuntimeTestContext ctx)
        {
            NetworkWorldComponent root = Root(ctx);
            ctx.Assert.NotNull(root.state, "the component holds its state");
            ctx.Note("save format " + root.saveVersion + ", created with " + root.createdWithModVersion + ", bootstrapped " + root.bootstrapped + (root.preparedForRemoval ? ", PREPARED FOR REMOVAL" : ""));
        }

        private static void RuntimeStarts(RuntimeTestContext ctx)
        {
            NetworkRuntime rt = Rt(ctx);
            // Already started in any game that has ticked, opened the Network tab or issued a command (then this changes nothing). In a game
            // loaded paused it is the game's own start-up gate, the very call the first tick makes; it is the one thing a run can cause in
            // the live Network, it is noted, and the runner does not count that slice as a live-state comparison.
            bool wasRunning = rt.Session.IsRunning;
            bool started = rt.EnsureStarted();
            if (!wasRunning && started) ctx.Note("the Network had not started in this session yet: this call performed the game's own start-up (the first tick would have done the same)");
            ctx.Assert.True(started, "EnsureStarted failed during " + (rt.Session.FailedStage ?? "?") + ": " + rt.Session.FailureMessage);
            ctx.Assert.True(rt.Session.IsRunning, "the session is running (" + rt.Session.State + ")");
            ctx.Note("session " + rt.Session.State + ", next due tick " + rt.Scheduler.NextDueTick + " (now " + rt.Clock.Now + ")");
        }

        private static void SaveVersionUnderstood(RuntimeTestContext ctx)
        {
            NetworkWorldComponent root = Root(ctx);
            if (!root.bootstrapped)
            {
                ctx.Note("this world has not bootstrapped yet (save format " + root.saveVersion + "): nothing to compare");
                return;
            }
            if (root.state.diagnostics.downgradedFrom > 0)
            {
                ctx.Warn("saved by a NEWER Network (format " + root.state.diagnostics.downgradedFrom + " > " + SaveMigrations.Current + "): loaded best-effort");
                return;
            }
            ctx.Assert.AtLeast(SaveMigrations.MinimumSupported, root.saveVersion, "the save format is at least the oldest this build can migrate");
            ctx.Assert.Equal(SaveMigrations.Current, root.saveVersion, "a loaded save has been migrated to this build's format");
        }

        private static void StoresPresent(RuntimeTestContext ctx)
        {
            NetworkState s = Root(ctx).state;
            ctx.Assert.NotNull(s.cast, "cast snapshot");
            ctx.Assert.NotNull(s.actors, "actor store");
            ctx.Assert.NotNull(s.characters, "character store");
            ctx.Assert.NotNull(s.intel, "intel store");
            ctx.Assert.NotNull(s.opportunities, "opportunity store");
            ctx.Assert.NotNull(s.contracts, "contract store");
            ctx.Assert.NotNull(s.operations, "operation store");
            ctx.Assert.NotNull(s.relations, "relation store");
            ctx.Assert.NotNull(s.knowledge, "knowledge store");
            ctx.Assert.NotNull(s.consequences, "consequence store");
            ctx.Assert.NotNull(s.history, "history ledger");
            ctx.Assert.NotNull(s.summaries, "summary store");
            ctx.Assert.NotNull(s.journal, "event journal");
            ctx.Assert.NotNull(s.diagnostics, "diagnostics");
            for (int i = 0; i < s.contracts.contracts.Count; i++)
            {
                Contract c = s.contracts.contracts[i];
                ctx.Assert.True(s.contracts.Get(c.id) == c, "contract " + c.id + " is addressable through its index");
            }
            for (int i = 0; i < s.operations.operations.Count; i++)
            {
                ctx.Assert.True(s.operations.Get(s.operations.operations[i].id) == s.operations.operations[i], "operation " + s.operations.operations[i].id + " is addressable through its index");
            }
            ctx.Note(s.actors.actors.Count + " actors, " + s.contracts.contracts.Count + " contracts, " + s.operations.operations.Count + " operations");
        }

        private static void CatalogBuilds(RuntimeTestContext ctx)
        {
            ItemCatalog c = CatalogCache.Get();
            ctx.Assert.NotNull(c, "the catalog was built");
            ctx.Assert.AtLeast(50, c.Count, "a loaded RimWorld has well over fifty requestable-or-classified defs (" + c.Count + ")");
            ctx.Note(c.Count + " defs classified in " + c.BuildMs.ToString("0.0") + " ms (built " + (CatalogCache.IsBuilt ? "and cached" : "now") + ")");
        }

        private static void ProcurementKindRegistered(RuntimeTestContext ctx)
        {
            ctx.Assert.True(ContractKindRegistry.Knows(ContractKinds.Procurement), "the Procurement kind rules are registered");
            List<NetworkContractKindDef> defs = DefDatabase<NetworkContractKindDef>.AllDefsListForReading;
            ctx.Assert.AtLeast(1, defs.Count, "at least one NetworkContractKindDef loaded from XML");
            ContractKindRules rules = ContractKindRegistry.Get(ContractKinds.Procurement);
            ctx.Assert.True(rules.awaitingPaymentGraceDays > 0 && rules.deliveryRetries > 0, "its rules are sane (grace " + rules.awaitingPaymentGraceDays + " days, " + rules.deliveryRetries + " delivery retries)");
        }

        private static void ActorIndexes(RuntimeTestContext ctx)
        {
            NetworkState s = Root(ctx).state;
            HashSet<int> seen = new HashSet<int>();
            for (int i = 0; i < s.actors.actors.Count; i++)
            {
                var a = s.actors.actors[i];
                ctx.Assert.True(seen.Add(a.id.Value), "actor id " + a.id.Value + " is unique");
                ctx.Assert.True(s.actors.Get(a.id) == a, "actor " + a.id + " resolves through the index to itself");
            }
            int proxies = 0;
            for (int i = 0; i < s.actors.actors.Count; i++) if (s.actors.actors[i].kind == Domain.Actors.ActorKind.PlayerProxy) proxies++;
            if (s.actors.actors.Count > 0) ctx.Assert.Equal(1, proxies, "exactly one PlayerProxy");
        }

        private static void NoStartupFailure(RuntimeTestContext ctx)
        {
            NetworkWorldComponent root = Root(ctx);
            NetworkRuntime rt = root.Runtime;
            ctx.Assert.NotNull(rt, "the runtime exists");
            ctx.Assert.False(rt.Session.IsFailed, "start-up did not fail (stage " + (rt.Session.FailedStage ?? "-") + "): " + rt.Session.FailureMessage);
            List<string> degraded = root.state.diagnostics.degradedSubsystems;
            if (degraded.Count > 0) ctx.Warn("subsystems degraded by a load failure: " + string.Join(", ", degraded.ToArray()) + " (see the log from the load)");
            else if (root.state.diagnostics.quarantine.Count > 0) ctx.Warn(root.state.diagnostics.quarantine.Count + " quarantined item(s) from this save or session (kept, skipped)");
        }
    }
}
