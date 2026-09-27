using System;
using System.Collections.Generic;
using RimWorld.Planet;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Intel;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Tests
{
    /// <summary>
    /// Start-up state (fail closed) and the fresh-world seed. The component and runtime tests use the
    /// real <see cref="NetworkWorldComponent"/> and <see cref="NetworkRuntime"/> with no game loaded, so
    /// start-up fails for a real reason (there is no SignalManager). They prove the Network's own gating,
    /// not how RimWorld behaves when a real start-up step fails.
    /// </summary>
    public static class StartupTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("Startup.GateRunsOnceAndNamesTheStage", GateOnce));
            t.Add(new KeyValuePair<string, Action>("Startup.GateReentrantCallRunsNothing", Reentrant));
            t.Add(new KeyValuePair<string, Action>("Startup.FailedStartupFailsClosed", FailsClosed));
            t.Add(new KeyValuePair<string, Action>("Startup.FreshWorldSeedBeforeServices", FreshSeed));
        }

        private static int Errors()
        {
            int n = 0;
            foreach (string l in T.netLog) if (l.StartsWith("Error", StringComparison.Ordinal)) n++;
            return n;
        }

        private static void GateOnce()
        {
            SessionGate ok = new SessionGate();
            int runs = 0;
            T.Eq(NetworkSession.NotStarted, ok.State, "not started");
            T.Check(ok.Ensure(g => { runs++; g.Stage("bootstrap"); }), "a start-up that returns normally is Running");
            T.Check(ok.Ensure(g => runs++), "…and stays Running");
            T.Eq(1, runs, "start-up runs once per session");
            T.Eq(NetworkSession.Running, ok.State, "running");

            SessionGate bad = new SessionGate();
            int errors = Errors();
            int badRuns = 0;
            bool started = bad.Ensure(g =>
            {
                badRuns++;
                g.Stage("bootstrap");
                throw new InvalidOperationException("boom");
            });
            T.Check(!started, "a start-up that throws is not running");
            T.Eq(NetworkSession.Failed, bad.State, "Failed");
            T.Eq("bootstrap", bad.FailedStage, "the failing stage is named");
            T.Check(bad.FailureMessage != null && bad.FailureMessage.Contains("boom"), "the failure is kept for the UI and Status");
            T.Eq(errors + 1, Errors(), "one error line");
            for (int i = 0; i < 50; i++) T.Check(!bad.Ensure(g => badRuns++), "still inactive");
            T.Eq(1, badRuns, "never retried in the same session");
            T.Eq(1, bad.Attempts, "one attempt");
            T.Eq(errors + 1, Errors(), "no further error lines");
        }

        private static void Reentrant()
        {
            SessionGate g = new SessionGate();
            bool innerRan = false;
            bool inner = true;
            T.Check(g.Ensure(x => { inner = g.Ensure(y => innerRan = true); }), "outer start-up completes");
            T.Check(!inner, "a call made during start-up is refused");
            T.Check(!innerRan, "and runs nothing");
            T.Eq(NetworkSession.Running, g.State, "running afterwards");
        }

        private static NetworkWorldComponent Component(string seedString, int prv)
        {
            World w = new World();
            w.info.seedString = seedString;
            w.info.persistentRandomValue = prv;
            NetworkWorldComponent c = new NetworkWorldComponent(w);
            c.FinalizeInit(false);
            return c;
        }

        private static void FailsClosed()
        {
            NetworkWorldComponent c = Component("startup-test", 777);
            NetworkRuntime rt = c.Runtime;
            T.Check(rt != null, "runtime built in FinalizeInit");
            T.Eq(NetworkSession.NotStarted, rt.Session.State, "nothing starts before first use");
            // A job already due: if the scheduler ran at all it would be consumed.
            rt.Scheduler.Schedule(JobKinds.IntelRound, 0, 999999);
            int jobs = rt.Scheduler.Count;
            int errors = Errors();

            for (int i = 0; i < 100; i++) c.WorldComponentTick();

            T.Eq(NetworkSession.Failed, rt.Session.State, "start-up failed (no game: no SignalManager)");
            T.Eq("signal registration", rt.Session.FailedStage, "failing stage named");
            T.Eq(1, rt.Session.Attempts, "not retried every tick");
            T.Eq(errors + 1, Errors(), "logged once, not once per tick");
            T.Eq(jobs, rt.Scheduler.Count, "the scheduler did not run: the due job is still queued");
            T.Check(!c.bootstrapped, "bootstrapped is not set when bootstrap did not complete");
            T.Check(!rt.Active, "not active");

            // Mutating commands are refused with a clear reason; nothing is charged or created.
            SourceKey exchange = SourceKey.ForActor(new ActorId(1));
            int actors = c.state.actors.actors.Count;
            CommandResult sub = rt.Commands.SubmitIntel(exchange, "Steel");
            T.Check(!sub.ok, "submit refused");
            T.Eq("NetworkStartupFailed", sub.reasonKey, "reason NetworkStartupFailed");
            T.Eq("NetworkStartupFailed", rt.Commands.CanSubmitIntel(exchange, "Steel").reasonKey, "CanSubmit gives the same reason");
            T.Eq("NetworkStartupFailed", rt.Commands.ContinueIntel(new IntelRequestId(1)).reasonKey, "continue refused");
            T.Eq("NetworkStartupFailed", rt.Commands.EndIntel(new IntelRequestId(1)).reasonKey, "end refused");
            T.Eq("NetworkStartupFailed", rt.Commands.CancelIntel(new IntelRequestId(1)).reasonKey, "cancel refused");
            T.Eq(0, c.state.intel.requests.Count, "no request created");
            T.Eq(actors, c.state.actors.actors.Count, "no actor created");

            // Site callbacks and signals are ignored (an unbound parent would make a forwarded callback log an error).
            WorldObjectComp_NetworkSite comp = new WorldObjectComp_NetworkSite { networkOpportunityId = 5 };
            comp.PostMapGenerate();
            comp.PostMyMapRemoved();
            comp.PostDestroy();
            rt.Signals.Notify_SignalReceived(new RimWorld.Signal("TheNetwork.Opp.5.MapSettled"));
            T.Eq(errors + 1, Errors(), "callbacks and signals were not forwarded");
            T.Eq(1, rt.Session.Attempts, "and did not retry start-up");

            T.Check(Integration.RemovalPreparer.Prepare(rt).Contains("failed to start"), "prepare-for-removal refuses and says why");
            T.Check(!c.preparedForRemoval, "nothing prepared");
            T.Eq(jobs, rt.Scheduler.Count, "still nothing ran");
        }

        private static void FreshSeed()
        {
            NetworkWorldComponent c = Component("fresh-world", 4321);
            int expected = NetHash.String("fresh-world|4321");
            T.Check(expected != 0, "test seed is not the placeholder");
            T.Eq(expected, c.networkSeed, "a fresh world's seed is derived before the runtime is built");
            T.Eq(expected, c.Runtime.Ctx.networkSeed, "the domain context uses it");
            T.Eq(expected, c.Runtime.History.NetworkSeed, "HistoryService uses it (not 0)");
            T.Check(!c.bootstrapped, "not bootstrapped yet: the seed is derived, nothing else is committed");

            NetworkWorldComponent again = Component("fresh-world", 4321);
            T.Eq(expected, again.networkSeed, "deterministic for the same world");

            // A bootstrapped save keeps its persisted seed; nothing is recomputed on load.
            World w = new World();
            w.info.seedString = "fresh-world";
            w.info.persistentRandomValue = 4321;
            NetworkWorldComponent loaded = new NetworkWorldComponent(w) { networkSeed = 1234567, bootstrapped = true };
            loaded.FinalizeInit(false);
            T.Eq(1234567, loaded.networkSeed, "a bootstrapped world's seed is never recomputed");
            T.Eq(1234567, loaded.Runtime.History.NetworkSeed, "and HistoryService uses the persisted seed");
        }
    }
}
