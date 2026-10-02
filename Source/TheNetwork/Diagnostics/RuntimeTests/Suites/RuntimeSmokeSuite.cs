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
    /// save). Game-only (it reads the live Network and real Defs); STRICTLY read-only: it inspects whether the Network has started and
    /// never starts, repairs or reconciles it (no EnsureStarted, StartNow or RunStartup: ADR-047). A Network the game has not started
    /// yet is a gameplay state, reported as SKIP with an instruction to allow one normal game tick; it never fails because a colony
    /// has no comms console or no home map either.
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

        private const string NotStartedAdvice = "The Network has not started in this game session yet. Unpause for one game tick or use The Network normally, then rerun the runtime tests. (A runtime test never starts it on the game's behalf.)";

        /// <summary>The live runtime, which must exist (it is built when the world loads). Does NOT require it to have started.</summary>
        private static NetworkRuntime Rt(RuntimeTestContext ctx)
        {
            NetworkRuntime rt = Root(ctx).Runtime;
            ctx.Assert.NotNull(rt, "The Network runtime was not built (FinalizeInit failed: see the log).");
            return rt;
        }

        /// <summary>
        /// The live runtime once the GAME has started it. A failed start-up is reported by RT-SMOKE-002 / 008 (not repeated here); a Network that has
        /// not started yet is SKIP with the advice. This never starts anything: it only asks the host for the start-up state.
        /// </summary>
        private static NetworkRuntime Started(RuntimeTestContext ctx)
        {
            NetworkProbe p = ctx.Host.ProbeNetwork();
            if (p.State == NetworkStartState.Failed) ctx.Skip("The Network failed to start this session (see RT-SMOKE-002): nothing settled to inspect.");
            if (p.State == NetworkStartState.NotStarted) ctx.Skip(NotStartedAdvice);
            return Rt(ctx); // Absent falls through to Rt(), which fails with its own message
        }

        private static void WorldComponentExists(RuntimeTestContext ctx)
        {
            NetworkWorldComponent root = Root(ctx);
            ctx.Assert.NotNull(root.state, "the component holds its state");
            ctx.Note("save format " + root.saveVersion + ", created with " + root.createdWithModVersion + ", bootstrapped " + root.bootstrapped + (root.preparedForRemoval ? ", PREPARED FOR REMOVAL" : ""));
        }

        private static void RuntimeStarts(RuntimeTestContext ctx)
        {
            // Inspect only. The game starts the Network on its first tick, Network tab or command; a test never does it for the game.
            NetworkProbe p = ctx.Host.ProbeNetwork();
            switch (p.State)
            {
                case NetworkStartState.Absent:
                    ctx.Assert.Fail("There is no Network runtime in this game (the world component did not build it: FinalizeInit failed, see the log).");
                    break;
                case NetworkStartState.Failed:
                    ctx.Assert.Fail("The Network failed to start this session during " + (p.FailedStage ?? "?") + ": " + p.FailureMessage);
                    break;
                case NetworkStartState.NotStarted:
                    ctx.Skip(NotStartedAdvice + " (session state: " + p.Detail + ")");
                    break;
            }
            NetworkRuntime rt = Rt(ctx);
            ctx.Note("session " + rt.Session.State + ", next due tick " + rt.Scheduler.NextDueTick + " (now " + rt.Clock.Now + ")");
        }

        private static void SaveVersionUnderstood(RuntimeTestContext ctx)
        {
            Started(ctx); // the save is only settled once the game has run its own load reconciliation
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
            Started(ctx);
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
            Started(ctx);
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
            NetworkProbe p = ctx.Host.ProbeNetwork();
            ctx.Assert.True(p.State != NetworkStartState.Absent, "the runtime exists");
            ctx.Assert.True(p.State != NetworkStartState.Failed, "start-up did not fail (stage " + (p.FailedStage ?? "-") + "): " + p.FailureMessage);
            if (p.State == NetworkStartState.NotStarted) ctx.Skip(NotStartedAdvice);
            NetworkWorldComponent root = Root(ctx);
            List<string> degraded = root.state.diagnostics.degradedSubsystems;
            if (degraded.Count > 0) ctx.Warn("subsystems degraded by a load failure: " + string.Join(", ", degraded.ToArray()) + " (see the log from the load)");
            else if (root.state.diagnostics.quarantine.Count > 0) ctx.Warn(root.state.diagnostics.quarantine.Count + " quarantined item(s) from this save or session (kept, skipped)");
        }
    }
}
