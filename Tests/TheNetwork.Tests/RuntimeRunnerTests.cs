using System;
using System.Collections.Generic;
using System.Text;
using TheNetwork.Diagnostics.RuntimeTests;
using TheNetwork.Diagnostics.RuntimeTests.Suites;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Operations;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;

namespace TheNetwork.Tests
{
    /// <summary>
    /// The runtime-test infrastructure itself (Phase 2.9): the runner's order, containment, timeouts, cancellation, override
    /// restoration, isolation from live state, and the sandbox suites run through the real runner against a synthetic host.
    /// </summary>
    public static class RuntimeRunnerTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            // The runner itself.
            t.Add(new KeyValuePair<string, Action>("Runner.TestsExecuteInStableOrder", StableOrder));
            t.Add(new KeyValuePair<string, Action>("Runner.ExceptionBecomesFailureNotCrash", ExceptionContained));
            t.Add(new KeyValuePair<string, Action>("Runner.StopOnFirstFailureWorks", StopOnFirst));
            t.Add(new KeyValuePair<string, Action>("Runner.ContinueAfterFailureWorks", ContinueAfterFailure));
            t.Add(new KeyValuePair<string, Action>("Runner.CancelRestoresOverrides", CancelRestores));
            t.Add(new KeyValuePair<string, Action>("Runner.TimeoutFailsCleanly", TimeoutFails));
            t.Add(new KeyValuePair<string, Action>("Runner.StableIdsAreUnique", StableIds));
            t.Add(new KeyValuePair<string, Action>("Runner.PreservedFailureIsRuntimeOnly", PreservedRuntimeOnly));
            t.Add(new KeyValuePair<string, Action>("Runner.CompletedSandboxIsDiscarded", SandboxDiscarded));
            t.Add(new KeyValuePair<string, Action>("Runner.LiveScanCannotMutateState", LiveScanReadOnly));
            t.Add(new KeyValuePair<string, Action>("Runner.ReportCountsAreExact", CountsExact));
            t.Add(new KeyValuePair<string, Action>("Runner.ExportFormattingStable", ExportStable));
            t.Add(new KeyValuePair<string, Action>("Runner.OverrideSnapshotRestoresPreviousValues", OverridesRestored));
            t.Add(new KeyValuePair<string, Action>("Runner.NoTestControlJobEntersPersistedScheduler", NoControlJobs));
            t.Add(new KeyValuePair<string, Action>("Runner.SafeSuiteDoesNotMutateLiveNetworkState", SafeSuiteReadOnly));
            t.Add(new KeyValuePair<string, Action>("Runner.SlicesYieldToTheGame", SlicesYield));
            t.Add(new KeyValuePair<string, Action>("Runner.FingerprintDetectsAMutation", FingerprintDetects));
            t.Add(new KeyValuePair<string, Action>("Runner.IdleCostIsOneNullCheck", IdleCost));
            t.Add(new KeyValuePair<string, Action>("Runner.FingerprintCostIsBounded", FingerprintCost));
            // Post-review safety correction: never start the live Network, fail closed, deep fingerprint, warnings surface.
            t.Add(new KeyValuePair<string, Action>("Runner.UnstartedNetworkIsSkippedNotStarted", UnstartedNetworkSkipped));
            t.Add(new KeyValuePair<string, Action>("Runner.StartupFromATestIsNotHidden", StartupNotHidden));
            t.Add(new KeyValuePair<string, Action>("Runner.FingerprintExceptionFailsClosed", FingerprintFailsClosed));
            t.Add(new KeyValuePair<string, Action>("Runner.NoLiveNetworkFingerprintMaySkip", NoLiveNetworkSkips));
            t.Add(new KeyValuePair<string, Action>("Runner.FingerprintDetectsRelationMutation", DetectsRelationMutation));
            t.Add(new KeyValuePair<string, Action>("Runner.FingerprintDetectsContractMutation", DetectsContractMutation));
            t.Add(new KeyValuePair<string, Action>("Runner.FingerprintDetectsOperationMutation", DetectsOperationMutation));
            t.Add(new KeyValuePair<string, Action>("Runner.FingerprintDetectsHistoryOrJournalMutation", DetectsHistoryOrJournalMutation));
            t.Add(new KeyValuePair<string, Action>("Runner.FingerprintIgnoresReadOnlyAccess", IgnoresReadOnlyAccess));
            t.Add(new KeyValuePair<string, Action>("Runner.CapturedWarningProducesWarn", CapturedWarningWarns));

            // The sandbox suites, through the real runner, on a synthetic host.
            t.Add(new KeyValuePair<string, Action>("Runner.SandboxProcurementSuite", () => RunSuite("Procurement", ProcurementRuntimeSuite.Cases(null))));
            t.Add(new KeyValuePair<string, Action>("Runner.SandboxCareerSuite", () => RunSuite("Career", CareerRuntimeSuite.Cases(null))));
            t.Add(new KeyValuePair<string, Action>("Runner.SandboxSpatialSuite", () => RunSuite("Spatial", SpatialRuntimeSuite.Cases(null))));
            t.Add(new KeyValuePair<string, Action>("Runner.SandboxPhysicalSuite", () => RunSuite("Physical", PhysicalRuntimeSuite.Cases(null))));
        }

        /// <summary>A synthetic "live" world standing in for the player's colony: the runner must leave all of it exactly as it found it.</summary>
        public sealed class FakeRuntimeHost : IRuntimeTestHost
        {
            public enum FakeNetwork { Running, NotStarted, Unavailable }

            public readonly TestNet live;
            public readonly List<string> log = new List<string>();
            public RuntimeTestSession finished;
            public bool realFacts = true;

            /// <summary>The live Network's start-up state as the game would report it.</summary>
            public FakeNetwork network = FakeNetwork.Running;

            /// <summary>A start-up that failed (reported by the probe as Failed).</summary>
            public bool startupFailed;

            /// <summary>When set, every capture of a RUNNING Network throws this (or only the Nth, see <see cref="throwOnCapture"/>).</summary>
            public Exception captureThrows;
            public int throwOnCapture; // 0 = every capture
            public bool returnNullCapture;
            public int captureCalls, availableCaptures;

            /// <summary>How many times anything started the live Network (only a deliberately bad test step ever calls <see cref="StartNetwork"/>).</summary>
            public int startupAttempts;

            public FakeRuntimeHost(int contractors = 12, bool rich = false)
            {
                live = ContractorTests.WorldWithCast(contractors, 31337);
                NetworkActorHelper.Populate(live);
                if (rich)
                {
                    // Enough life that every durable store has something in it to mutate: a finished job (history, relations, knowledge, journal),
                    // a second running operation, and a month of upkeep.
                    NetworkActor fixer = ProcurementTests.Fixer(live);
                    for (int i = 0; i < 3; i++) ProcurementTests.Awarded(live, fixer, ProcurementTests.Reliable(live), "TestSteel", 150);
                    live.AdvanceTo(live.clock.Now + 40 * Ticks.PerDay);
                    ProcurementTests.Awarded(live, fixer, ProcurementTests.Reliable(live), "TestSteel", 150);
                    live.AdvanceTo(live.clock.Now + 1 * Ticks.PerDay);
                }
            }

            public string Name => "headless host";
            public string BuildInfo => "headless";

            /// <summary>What a test must never do: start the game's Network. Counted so a test can prove nothing did.</summary>
            public void StartNetwork()
            {
                startupAttempts++;
                network = FakeNetwork.Running;
                live.pay.silver += 17; // a start-up repairs and reconciles: it changes live state
            }

            /// <summary>The fingerprint of the synthetic live world whatever the start-up state (the test's own ground truth).</summary>
            public LiveFingerprint RawFingerprint()
            {
                return LiveFingerprint.Of(live.ctx, live.ids, live.scheduler, live.journal)
                    .With("pay.silver", live.pay.silver).With("pay.charged", live.pay.charged).With("pay.refunded", live.pay.refunded).With("pay.chargeCalls", live.pay.chargeCalls)
                    .With("delivery.deliveries", live.delivery.deliveries).With("delivery.items", live.delivery.delivered);
            }

            public FingerprintCapture CaptureFingerprint()
            {
                captureCalls++;
                if (returnNullCapture) return null;
                if (network == FakeNetwork.Unavailable) return FingerprintCapture.NetworkUnavailable("headless: no Network");
                if (network == FakeNetwork.NotStarted) return FingerprintCapture.NetworkNotStarted("session state NotStarted");
                if (captureThrows != null && (throwOnCapture == 0 || captureCalls == throwOnCapture)) throw captureThrows;
                availableCaptures++;
                return FingerprintCapture.Available(RawFingerprint());
            }

            public NetworkProbe ProbeNetwork()
            {
                if (network == FakeNetwork.Unavailable) return new NetworkProbe { State = NetworkStartState.Absent };
                if (startupFailed) return new NetworkProbe { State = NetworkStartState.Failed, FailedStage = "load reconciliation", FailureMessage = "headless: start-up failed" };
                if (network == FakeNetwork.NotStarted) return new NetworkProbe { State = NetworkStartState.NotStarted, Detail = "NotStarted" };
                return new NetworkProbe { State = NetworkStartState.Running };
            }

            public IEnumerable<string> LiveSchedulerKinds()
            {
                foreach (ScheduledJob j in live.scheduler.AllJobs) yield return j.kind;
            }

            public bool TryGetRealItemFacts(string defName, out ItemFacts facts)
            {
                facts = null;
                if (!realFacts || defName != "Steel") return false;
                facts = new ItemFacts { defName = "Steel", label = "steel", packageId = "ludeon.rimworld", isLudeon = true, techLevel = 4, marketValue = 1.9f, stackLimit = 75, tradeable = true, isResource = true };
                return true;
            }

            public void Log(string line) { log.Add(line); }

            public void RunFinished(RuntimeTestSession session) { finished = session; }
        }

        internal static RuntimeTestSession Execute(FakeRuntimeHost host, RuntimeTestPlan plan, RuntimeTestOptions options = null, Func<double> clock = null, int maxPumps = 100000)
        {
            RuntimeTestRunner runner = new RuntimeTestRunner(host, clock, () => new DateTime(2026, 10, 2, 12, 0, 0));
            RuntimeTestSession s = runner.Start(plan, options ?? RuntimeTestOptions.Full());
            int guard = 0;
            while (runner.Pump() && guard++ < maxPumps) { }
            return s;
        }

        // ================================================================== helpers

        private static RuntimeTestCase Imm(string id, Action<RuntimeTestContext> body, bool sandbox = false)
        {
            return RuntimeTestCase.Immediate("TEST", id, "case " + id, body, sandbox);
        }

        private static RuntimeTestPlan Plan(string name, params RuntimeTestCase[] cases)
        {
            RuntimeTestPlan p = new RuntimeTestPlan(name);
            foreach (RuntimeTestCase c in cases) p.Add(c);
            return p;
        }

        /// <summary>A deterministic clock that moves a fixed amount every time it is read.</summary>
        private sealed class StepClock
        {
            public double now;
            public double step;
            public Func<double> Read => () => { double v = now; now += step; return v; };
        }

        private static RuntimeTestResult Result(RuntimeTestSession s, string id)
        {
            foreach (RuntimeTestResult r in s.Results) if (r.Id == id) return r;
            return null;
        }

        private static List<RuntimeTestResult> NonInfra(RuntimeTestSession s)
        {
            List<RuntimeTestResult> l = new List<RuntimeTestResult>();
            foreach (RuntimeTestResult r in s.Results) if (r.Suite != "INFRA") l.Add(r);
            return l;
        }

        private static string InfraLine(RuntimeTestSession s, string id)
        {
            RuntimeTestResult r = Result(s, id);
            return r == null ? id + " missing" : id + " " + r.Outcome + ": " + r.Message;
        }

        // ================================================================== the runner

        private static void StableOrder()
        {
            List<string> ran = new List<string>();
            Func<RuntimeTestPlan> build = () => Plan("order",
                Imm("RT-TEST-001", c => ran.Add(c.Id)),
                RuntimeTestCase.Stepped("TEST", "RT-TEST-002", "stepped", c => { if (c.StepIndex == 1) { ran.Add(c.Id + ".1"); return RuntimeStep.Wait("once"); } ran.Add(c.Id + ".2"); return RuntimeStep.Pass(); }),
                Imm("RT-TEST-003", c => ran.Add(c.Id)),
                Imm("RT-TEST-004", c => ran.Add(c.Id)));
            FakeRuntimeHost host = new FakeRuntimeHost();
            RuntimeTestSession a = Execute(host, build());
            List<string> first = new List<string>(ran);
            ran.Clear();
            RuntimeTestSession b = Execute(host, build());
            T.Eq("RT-TEST-001,RT-TEST-002.1,RT-TEST-002.2,RT-TEST-003,RT-TEST-004", string.Join(",", first.ToArray()), "tests run in the order given, a stepped test resuming where it left off");
            T.Eq(string.Join(",", first.ToArray()), string.Join(",", ran.ToArray()), "and a second run does exactly the same");
            List<RuntimeTestResult> ra = NonInfra(a);
            T.Eq(4, ra.Count, "four results");
            T.Check(ra[0].Id == "RT-TEST-001" && ra[1].Id == "RT-TEST-002" && ra[2].Id == "RT-TEST-003" && ra[3].Id == "RT-TEST-004", "results are in plan order");
            T.Eq(RuntimeRunState.Completed, a.State, "completed");
            T.Eq(RuntimeRunState.Completed, b.State, "completed again");
        }

        private static void ExceptionContained()
        {
            FakeRuntimeHost host = new FakeRuntimeHost();
            List<string> ran = new List<string>();
            RuntimeTestPlan plan = Plan("boom",
                Imm("RT-TEST-001", c => { throw new InvalidOperationException("kaboom"); }),
                Imm("RT-TEST-002", c => ran.Add("after")),
                Imm("RT-TEST-003", c => c.Assert.Equal(1, 2, "one is not two")));
            RuntimeTestSession s = Execute(host, plan);
            RuntimeTestResult r = Result(s, "RT-TEST-001");
            T.Check(r != null && r.Outcome == RuntimeTestOutcome.Fail && r.ExceptionType == "System.InvalidOperationException" && r.ExceptionMessage == "kaboom", "a throwing test is a FAIL carrying the exception");
            T.Check(r.StackTrace != null && r.StackTrace.Length > 0, "with its stack trace for the detailed report");
            T.Eq(1, ran.Count, "the next test still ran");
            RuntimeTestResult a = Result(s, "RT-TEST-003");
            T.Check(a.Outcome == RuntimeTestOutcome.Fail && a.Expected == "1" && a.Actual == "2", "a failed assertion is a FAIL with expected and actual");
            T.Eq(RuntimeRunState.Completed, s.State, "the run completed: nothing crashed");
            T.Check(host.log.Exists(l => l.Contains("RT-TEST-001")), "and the failure was logged");
        }

        private static void StopOnFirst()
        {
            FakeRuntimeHost host = new FakeRuntimeHost();
            List<string> ran = new List<string>();
            RuntimeTestPlan plan = Plan("stop",
                Imm("RT-TEST-001", c => ran.Add(c.Id)),
                Imm("RT-TEST-002", c => c.Assert.Fail("deliberate")),
                Imm("RT-TEST-003", c => ran.Add(c.Id)),
                Imm("RT-TEST-004", c => ran.Add(c.Id)));
            RuntimeTestOptions o = RuntimeTestOptions.Full();
            o.StopOnFirstFailure = true;
            RuntimeTestSession s = Execute(host, plan, o);
            T.Eq("RT-TEST-001", string.Join(",", ran.ToArray()), "nothing after the first failure ran");
            T.Eq(2, NonInfra(s).Count, "two results: the pass and the failure");
            T.Check(s.StoppedEarly, "the run says it stopped early");
            T.Eq(RuntimeRunState.Completed, s.State, "and completed (with its infrastructure checks)");
            T.Check(Result(s, "RT-INFRA-002") != null, "the infrastructure checks still ran");
            T.Check(RuntimeTestReport.Full(s, "x").Contains("stopped at the first failure"), "the report says so");
        }

        private static void ContinueAfterFailure()
        {
            FakeRuntimeHost host = new FakeRuntimeHost();
            List<string> ran = new List<string>();
            RuntimeTestPlan plan = Plan("go on",
                Imm("RT-TEST-001", c => ran.Add(c.Id)),
                Imm("RT-TEST-002", c => c.Assert.Fail("deliberate")),
                Imm("RT-TEST-003", c => ran.Add(c.Id)),
                Imm("RT-TEST-004", c => ran.Add(c.Id)));
            RuntimeTestSession s = Execute(host, plan, RuntimeTestOptions.Full());
            T.Eq("RT-TEST-001,RT-TEST-003,RT-TEST-004", string.Join(",", ran.ToArray()), "every test ran despite the failure");
            T.Eq(1, s.Count(RuntimeTestOutcome.Fail), "one failure");
            T.Check(!s.StoppedEarly && !s.Succeeded, "not stopped early, and the run did not succeed");
        }

        private static void CancelRestores()
        {
            FakeRuntimeHost host = new FakeRuntimeHost();
            RuntimeOverrideSnapshot owner = RuntimeOverrideSnapshot.Capture();
            try
            {
                // The owner already has dev overrides on, and a service switched off.
                IntelDevOverrides.commsGateOverride = true;
                IntelDevOverrides.waiveFees = true;
                ProcurementDevOverrides.forceBand = OutcomeBand.Failure;
                ProcurementDevOverrides.forceSecured = 7;
                ServiceToggles.ProcurementEnabled = false;
                RuntimeOverrideSnapshot before = RuntimeOverrideSnapshot.Capture();
                int waits = 0;
                RuntimeTestPlan plan = Plan("cancel",
                    Imm("RT-TEST-001", c => c.Assert.True(ServiceToggles.ProcurementEnabled && !IntelDevOverrides.commsGateOverride, "the test sees the neutral state")),
                    RuntimeTestCase.Stepped("TEST", "RT-TEST-002", "waits", c => { waits++; return RuntimeStep.Wait("forever"); }),
                    Imm("RT-TEST-003", c => { }));
                RuntimeTestRunner runner = new RuntimeTestRunner(host, null, () => new DateTime(2026, 10, 2));
                RuntimeTestSession s = runner.Start(plan, RuntimeTestOptions.Full());
                runner.Pump();
                runner.Pump();
                T.Check(waits >= 1 && s.State == RuntimeRunState.Running, "the run is in progress, waiting");
                T.Check(RuntimeOverrideSnapshot.Capture().Diff(before).Count == 0, "between slices the owner's overrides are exactly as they were (a waiting test's overrides are never left set)");
                T.Check(runner.Cancel(), "cancel is accepted");
                bool more = runner.Pump();
                T.Check(!more && s.State == RuntimeRunState.Cancelled, "cancelled at the next safe boundary");
                T.Eq(0, RuntimeOverrideSnapshot.Capture().Diff(before).Count, "after the cancel every override is back exactly (" + string.Join("; ", RuntimeOverrideSnapshot.Capture().Diff(before).ToArray()) + ")");
                T.Check(!runner.IsRunning && runner.LastFinished == s, "no run is active; the cancelled run is the last report");
                T.Check(RuntimeTestReport.Summary(s).Contains("CANCELLED"), "reported CANCELLED");
                T.Eq(s.SandboxesCreated, s.SandboxesDiscarded + s.SandboxesPreserved, "no sandbox is left behind");
                T.Check(Result(s, "RT-TEST-003") == null, "the test after the cancel point never ran");
            }
            finally
            {
                owner.Restore();
            }
        }

        private static void TimeoutFails()
        {
            FakeRuntimeHost host = new FakeRuntimeHost();
            // Real-time limit: the clock jumps 40 ms per reading, the test allows 100 ms and waits forever.
            StepClock clock = new StepClock { step = 40 };
            RuntimeTestCase forever = RuntimeTestCase.Stepped("TEST", "RT-TEST-001", "never ready", c => RuntimeStep.Wait("a condition that never holds"));
            forever.TimeoutMs = 100;
            RuntimeTestCase after = Imm("RT-TEST-002", c => { });
            RuntimeTestSession s = Execute(host, Plan("timeout", forever, after), RuntimeTestOptions.Full(), clock.Read, 1000);
            RuntimeTestResult r = Result(s, "RT-TEST-001");
            T.Check(r != null && r.Outcome == RuntimeTestOutcome.Fail && r.TimedOut && r.Message.StartsWith("TIMEOUT", StringComparison.Ordinal), "a test that never finishes fails as TIMEOUT");
            T.Check(r.Step >= 1, "naming the step it was waiting at (" + r.Step + ")");
            T.Eq(RuntimeTestOutcome.Pass, Result(s, "RT-TEST-002").Outcome, "the next test still runs");
            T.Eq(RuntimeRunState.Completed, s.State, "the runner is not stuck");

            // Wait-count limit: no clock involved, three waits allowed.
            RuntimeTestCase few = RuntimeTestCase.Stepped("TEST", "RT-TEST-003", "waits too often", c => RuntimeStep.Wait("again"));
            few.MaxWaits = 3;
            RuntimeTestSession s2 = Execute(host, Plan("waits", few), RuntimeTestOptions.Full(), null, 1000);
            RuntimeTestResult r2 = Result(s2, "RT-TEST-003");
            T.Check(r2.Outcome == RuntimeTestOutcome.Fail && r2.TimedOut, "too many waits is a TIMEOUT too");

            // A sandbox wait that runs out is a TIMEOUT, not an endless loop.
            RuntimeTestSession s3 = Execute(host, Plan("sandbox wait", Imm("RT-TEST-004", c => c.Assert.Eventually(() => false, "something that never happens", 3 * Ticks.PerDay), true)), RuntimeTestOptions.Full(), null, 1000);
            RuntimeTestResult r3 = Result(s3, "RT-TEST-004");
            T.Check(r3.Outcome == RuntimeTestOutcome.Fail && r3.TimedOut && r3.Message.Contains("something that never happens"), "Eventually is bounded by sandbox time and fails as TIMEOUT");
        }

        private static void StableIds()
        {
            T.Throws(() => Plan("dup", Imm("RT-TEST-001", c => { }), Imm("RT-TEST-001", c => { })).Validate(), "a duplicate id is refused");
            FakeRuntimeHost host = new FakeRuntimeHost();
            RuntimeTestRunner runner = new RuntimeTestRunner(host);
            T.Throws(() => runner.Start(Plan("dup", Imm("RT-TEST-001", c => { }), Imm("RT-TEST-001", c => { })), null), "and the runner will not start such a plan");
            T.Check(!runner.IsRunning, "nothing is running after the refusal");
            foreach (RuntimeTestPlan plan in new[] { RuntimeTestPlans.SandboxOnly(null), RuntimeTestPlans.FullSafe(null), RuntimeTestPlans.QuickSmoke(null), RuntimeTestPlans.LiveScan(null) })
            {
                plan.Validate();
                HashSet<string> ids = new HashSet<string>();
                foreach (RuntimeTestCase c in plan.Cases)
                {
                    T.Check(ids.Add(c.Id), plan.Name + ": " + c.Id + " is unique");
                    T.Check(System.Text.RegularExpressions.Regex.IsMatch(c.Id, "^RT-[A-Z]+-[0-9]{3}$"), c.Id + " has the stable RT-SUITE-NNN shape");
                }
            }
            HashSet<string> all = new HashSet<string>();
            foreach (RuntimeTestCase c in RuntimeTestPlans.FullSafe(null).Cases) all.Add(c.Id);
            foreach (string must in new[] { "RT-SMOKE-001", "RT-LIVE-001", "RT-PROC-007", "RT-CAR-010", "RT-SPAT-005", "RT-PHYS-001", "RT-PHYS-026", "RT-PHYS-029" }) T.Check(all.Contains(must), "the documented id " + must + " exists");
            T.Eq(8 + 6 + 11 + 14 + 8 + 23, all.Count, "the Full safe regression holds exactly the documented tests");
            // Phase 3.0 implements exactly the safe in-game RT-PHYS cases its design assigns (§ 21.1); 007, 011, 015, 027 and 028 are headless-only,
            // 020–024 and 030 belong to 3.1/3.2, and no destructive RT-PHYX case exists.
            foreach (string id in all) T.Check(!id.StartsWith("RT-PHYX-", StringComparison.Ordinal), id + " is not a physical-tier case");
            foreach (string now in new[] { "RT-PHYS-020", "RT-PHYS-021", "RT-PHYS-022", "RT-PHYS-030" }) T.Check(all.Contains(now), now + " exists (Phase 3.1)");
            foreach (string notYet in new[] { "RT-PHYS-023", "RT-PHYS-024" }) T.Check(!all.Contains(notYet), notYet + " is not implemented before its subphase (3.2)");
        }

        private static void PreservedRuntimeOnly()
        {
            FakeRuntimeHost host = new FakeRuntimeHost();
            RuntimeTestSandbox kept = null;
            RuntimeTestPlan plan = Plan("keep",
                Imm("RT-TEST-001", c =>
                {
                    RuntimeTestSandbox sb = c.RequireSandbox();
                    kept = sb;
                    NetworkActor team = sb.AddContractor();
                    Contract k = sb.Award(sb.AddFixer(), team, RuntimeTestSandbox.Steel, 100, false);
                    c.Track(k);
                    c.Track(team);
                    c.Assert.Fail("deliberate failure to preserve the sandbox");
                }, true),
                Imm("RT-TEST-002", c => { }, true));
            RuntimeTestRunner runner = new RuntimeTestRunner(host, null, () => new DateTime(2026, 10, 2, 9, 30, 0));
            RuntimeTestSession s = runner.Start(plan, RuntimeTestOptions.Full());
            while (runner.Pump()) { }
            T.Check(runner.Preserved != null && runner.Preserved.TestId == "RT-TEST-001" && !kept.Disposed, "the failed sandbox is preserved, alive");
            T.Check(Result(s, "RT-TEST-001").Preserved, "the result says so");
            T.Check(Result(s, "RT-INFRA-004").Outcome == RuntimeTestOutcome.Pass, "preserved sandboxes are accounted for");
            string d = runner.Preserved.Describe();
            T.Check(d.Contains("RT-TEST-001") && d.Contains("CONTRACT") && d.Contains("CONTRACTOR") && d.Contains("OPERATION") && d.Contains("SCHEDULER") && d.Contains("ledger"), "the inspector shows the contract, operation, contractor, ledger and scheduler");
            T.Check(!d.Contains("never saved") == false, "and says it is an in-memory scratch world");
            // Runtime only: nothing about a run is part of any persisted type.
            foreach (Type type in new[] { typeof(RuntimeTestSession), typeof(RuntimePreservedFailure), typeof(RuntimeTestSandbox), typeof(RuntimeTestResult), typeof(RuntimeTestRunner), typeof(RuntimeTestContext) })
            {
                T.Check(!typeof(Verse.IExposable).IsAssignableFrom(type), type.Name + " is not IExposable (never Scribed)");
            }
            foreach (Type store in new[] { typeof(TheNetwork.Core.NetworkState), typeof(TheNetwork.NetworkWorldComponent) })
            {
                foreach (System.Reflection.FieldInfo f in store.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    T.Check(f.FieldType.Namespace == null || !f.FieldType.Namespace.StartsWith("TheNetwork.Diagnostics.RuntimeTests", StringComparison.Ordinal), store.Name + "." + f.Name + " does not hold runtime-test state");
                }
            }
            // Starting another run discards it.
            runner.Start(Plan("next", Imm("RT-TEST-009", c => { })), RuntimeTestOptions.Full());
            T.Check(runner.Preserved == null && kept.Disposed, "starting a new run discards the preserved failure (and logs it)");
            T.Check(host.log.Exists(l => l.Contains("Discarding the preserved failure of RT-TEST-001")), "the discard was logged");
            // With preservation off nothing is kept.
            while (runner.Pump()) { }
            RuntimeTestOptions off = RuntimeTestOptions.Full();
            off.PreserveFailedSandbox = false;
            RuntimeTestSandbox gone = null;
            RuntimeTestSession s2 = Execute(host, Plan("off", Imm("RT-TEST-010", c => { gone = c.Sandbox; c.Assert.Fail("x"); }, true)), off);
            T.Check(gone.Disposed && s2.SandboxesPreserved == 0, "with preservation off a failed sandbox is discarded too");
        }

        private static void SandboxDiscarded()
        {
            FakeRuntimeHost host = new FakeRuntimeHost();
            RuntimeTestSandbox seen = null;
            RuntimeTestSession s = Execute(host, Plan("discard", Imm("RT-TEST-001", c => { seen = c.RequireSandbox(); c.Assert.Equal(0, c.Sandbox.Ctx.contracts.contracts.Count, "a new sandbox is empty"); }, true)));
            T.Check(seen != null && seen.Disposed, "a completed test's sandbox is discarded");
            T.Check(Result(s, "RT-INFRA-004").Outcome == RuntimeTestOutcome.Pass && s.SandboxesCreated == 1 && s.SandboxesDiscarded == 1, "and the run accounts for it (1 created, 1 discarded)");
            T.Throws(() => seen.AddFixer(), "a discarded sandbox refuses further use");
        }

        private static void LiveScanReadOnly()
        {
            FakeRuntimeHost host = new FakeRuntimeHost();
            LiveFingerprint before = host.RawFingerprint();
            LiveInvariants.Result clean = LiveInvariants.Scan(host.live.ctx, host.live.ids, host.live.scheduler);
            T.Eq(0, clean.Violations.Count, "a healthy world has no violation (" + string.Join("; ", clean.Violations.ToArray()) + ")");
            T.Check(clean.Actors > 0 && clean.Contracts > 0 && clean.Operations > 0 && clean.Jobs > 0, "the scan covered actors, contracts, operations and jobs (" + clean.Counted + ")");
            T.Check(before.Same(host.RawFingerprint()), "scanning a healthy world changed nothing");

            // A damaged world: the scan reports it and STILL repairs nothing (unlike NetValidator, which would reschedule, quarantine and prune).
            Contract c = host.live.ctx.contracts.contracts[0];
            c.ledger.Add(new MoneyRecord { silver = -5, direction = MoneyDirection.PlayerPaid, purpose = MoneyPurpose.Deposit });
            host.live.scheduler.Schedule(JobKinds.ContractDelivery, host.live.clock.Now + 5000, 424242); // a job whose target does not exist
            foreach (NetworkActor a in host.live.ctx.actors.actors) { ContractorSimulation sim = a.Get<ContractorSimulation>(); if (sim != null) { sim.funds = int.MaxValue; break; } }
            LiveFingerprint damaged = host.RawFingerprint();
            LiveInvariants.Result r = LiveInvariants.Scan(host.live.ctx, host.live.ids, host.live.scheduler);
            T.Check(r.Violations.Exists(v => v.Contains("negative silver")), "the negative ledger record is reported");
            T.Check(r.Violations.Exists(v => v.Contains("outside the bound")), "and the impossible funds");
            T.Check(r.Notes.Exists(v => v.Contains("no target")), "and the orphaned job (a note: a validation would remove it)");
            T.Check(damaged.Same(host.RawFingerprint()), "yet the scan repaired and changed NOTHING (the orphan job, the ledger and the funds are all still there)");
            T.Eq(1, CountJobs(host, 424242), "the orphaned job is still scheduled");
        }

        private static int CountJobs(FakeRuntimeHost host, int target)
        {
            int n = 0;
            foreach (ScheduledJob j in host.live.scheduler.AllJobs) if (j.target == target) n++;
            return n;
        }

        private static void CountsExact()
        {
            FakeRuntimeHost host = new FakeRuntimeHost();
            RuntimeTestPlan plan = Plan("counts",
                Imm("RT-TEST-001", c => { }), Imm("RT-TEST-002", c => { }), Imm("RT-TEST-003", c => { }),
                Imm("RT-TEST-004", c => c.Assert.Fail("a")), Imm("RT-TEST-005", c => { throw new Exception("b"); }),
                Imm("RT-TEST-006", c => c.Warn("careful")),
                Imm("RT-TEST-007", c => c.Skip("not here")), Imm("RT-TEST-008", c => c.Skip("nor here")));
            RuntimeTestSession s = Execute(host, plan);
            // The planned tests first; the four infrastructure checks are counted separately below.
            int pass = 0, fail = 0, warn = 0, skip = 0;
            foreach (RuntimeTestResult r in NonInfra(s))
            {
                if (r.Outcome == RuntimeTestOutcome.Pass) pass++;
                else if (r.Outcome == RuntimeTestOutcome.Fail) fail++;
                else if (r.Outcome == RuntimeTestOutcome.Warn) warn++;
                else skip++;
            }
            T.Eq(3, pass, "three tests passed");
            T.Eq(2, fail, "two failed (an assertion and an exception)");
            T.Eq(1, warn, "one warned");
            T.Eq(2, skip, "two were skipped");
            T.Eq(8, NonInfra(s).Count, "every planned test has exactly one result");
            T.Eq(s.Results.Count, s.Count(RuntimeTestOutcome.Pass) + s.Count(RuntimeTestOutcome.Fail) + s.Count(RuntimeTestOutcome.Warn) + s.Count(RuntimeTestOutcome.Skip), "the four counts add up to the results (infrastructure checks included)");
            string summary = RuntimeTestReport.Summary(s);
            T.Check(summary.Contains("PASS: 7") && summary.Contains("FAIL: 2") && summary.Contains("WARN: 1") && summary.Contains("SKIP: 2"), "the summary prints the exact counts: 3 passes + 4 infrastructure passes, 2 failures, 1 warning, 2 skips (" + summary.Replace("\n", " ") + ")");
            T.Check(!s.Succeeded, "a run with failures did not succeed");
        }

        private static void ExportStable()
        {
            Func<string> run = () =>
            {
                FakeRuntimeHost host = new FakeRuntimeHost();
                StepClock clock = new StepClock { step = 3 };
                RuntimeTestRunner runner = new RuntimeTestRunner(host, clock.Read, () => new DateTime(2026, 10, 2, 12, 34, 56));
                RuntimeTestSession s = runner.Start(Plan("export", Imm("RT-TEST-001", c => { }), Imm("RT-TEST-002", c => c.Assert.Equal("a", "b", "letters differ")), Imm("RT-TEST-003", c => c.Skip("none"))), RuntimeTestOptions.Full());
                while (runner.Pump()) { }
                return RuntimeTestReport.Full(s, "build X");
            };
            string a = run(), b = run();
            T.Eq(a, b, "the same run prints the same report");
            T.Check(a.Contains("[PASS] RT-TEST-001") && a.Contains("[FAIL] RT-TEST-002") && a.Contains("[SKIP] RT-TEST-003"), "stable ids and tags");
            T.Check(a.Contains("Expected: a") && a.Contains("Actual:   b"), "a failure shows expected and actual");
            T.Check(a.Contains("Suite:    export") && a.Contains("Build:    build X") && a.Contains("Started:  2026-10-02 12:34:56"), "the header names the suite, the build and the start time");
            T.Eq("runtime-tests-20261002-123456.txt", RuntimeTestReport.FileName(new DateTime(2026, 10, 2, 12, 34, 56)), "the export file name is runtime-tests-YYYYMMDD-HHMMSS.txt");
            T.Check(a.Contains("[PASS] RT-INFRA-002"), "the infrastructure checks are part of the report");
        }

        private static void OverridesRestored()
        {
            FakeRuntimeHost host = new FakeRuntimeHost();
            RuntimeOverrideSnapshot owner = RuntimeOverrideSnapshot.Capture();
            try
            {
                ProcurementDevOverrides.forceBand = OutcomeBand.Disaster;
                ProcurementDevOverrides.forceSecured = 3;
                ProcurementDevOverrides.forceDelayTicks = 999;
                ProcurementDevOverrides.forceDeliveryFailures = 4;
                ProcurementDevOverrides.forceTroubled = "Missing";
                ProcurementDevOverrides.forceWorseThanExpected = true;
                ProcurementDevOverrides.forceFollowUp = true;
                ProcurementDevOverrides.forceNewcomer = true;
                ProcurementDevOverrides.forceNotTroubled = true;
                ProcurementDevOverrides.forceTroubledFound = false;
                IntelDevOverrides.forceLead = true;
                IntelDevOverrides.forceNoLead = true;
                IntelDevOverrides.forceDivergence = LeadDivergence.Bad;
                IntelDevOverrides.forceSourceKind = SourceKind.Trader;
                IntelDevOverrides.commsGateOverride = true;
                IntelDevOverrides.waiveFees = true;
                ServiceToggles.IntelEnabled = false;
                ServiceToggles.ProcurementEnabled = false;
                RuntimeOverrideSnapshot mine = RuntimeOverrideSnapshot.Capture();
                T.Eq(18, CountDifferences(RuntimeOverrideSnapshot.Neutral(), mine), "all eighteen overrides and toggles are non-neutral going in");
                RuntimeTestSession s = Execute(host, RuntimeTestPlans.SandboxOnly(host));
                T.Eq(0, s.Count(RuntimeTestOutcome.Fail), "the sandbox suites pass even though the owner's overrides were all set (the runner presents a neutral state to every step): " + FirstFailure(s));
                T.Eq(0, RuntimeOverrideSnapshot.Capture().Diff(mine).Count, "afterwards every override and toggle is exactly the owner's (" + string.Join("; ", RuntimeOverrideSnapshot.Capture().Diff(mine).ToArray()) + ")");
                T.Check(Result(s, "RT-INFRA-002").Outcome == RuntimeTestOutcome.Pass, "and the run proves it");

                // A test that forgets to clear what it set is caught and reported; the owner's values are still restored.
                RuntimeTestSession leak = Execute(host, Plan("leak", Imm("RT-TEST-001", c => { ProcurementDevOverrides.forceBand = OutcomeBand.Triumph; IntelDevOverrides.waiveFees = true; })));
                RuntimeTestResult r = Result(leak, "RT-TEST-001");
                T.Check(r.Outcome == RuntimeTestOutcome.Fail && r.Message.Contains("forceBand") && r.Message.Contains("waiveFees"), "a leaked override fails its test, naming it (" + r.Message + ")");
                T.Check(Result(leak, "RT-INFRA-002").Outcome == RuntimeTestOutcome.Fail, "and fails the infrastructure check");
                T.Eq(0, RuntimeOverrideSnapshot.Capture().Diff(mine).Count, "but the owner's values are back anyway");
            }
            finally
            {
                owner.Restore();
            }
        }

        private static int CountDifferences(RuntimeOverrideSnapshot a, RuntimeOverrideSnapshot b)
        {
            return a.Diff(b).Count;
        }

        private static string FirstFailure(RuntimeTestSession s)
        {
            foreach (RuntimeTestResult r in s.Results) if (r.Outcome == RuntimeTestOutcome.Fail) return r.Id + ": " + r.Message;
            return "";
        }

        private static void NoControlJobs()
        {
            FakeRuntimeHost host = new FakeRuntimeHost();
            LiveFingerprint before = host.RawFingerprint();
            int jobs = 0;
            foreach (ScheduledJob j in host.live.scheduler.AllJobs) jobs++;
            RuntimeTestSession s = Execute(host, RuntimeTestPlans.SandboxOnly(host));
            int after = 0;
            foreach (ScheduledJob j in host.live.scheduler.AllJobs)
            {
                after++;
                T.Check(!j.kind.StartsWith("devtest", StringComparison.OrdinalIgnoreCase) && !j.kind.StartsWith("runtimetest", StringComparison.OrdinalIgnoreCase), "no job of a test-control kind: " + j.kind);
            }
            T.Eq(jobs, after, "the live persisted scheduler holds exactly the jobs it held");
            T.Check(Result(s, "RT-INFRA-003").Outcome == RuntimeTestOutcome.Pass, "the run proves it");
            T.Check(before.Same(host.RawFingerprint()), "scheduler hash, counts and id counters unchanged");
            // The runner types have no way to reach a scheduler at all.
            foreach (Type type in new[] { typeof(RuntimeTestRunner), typeof(RuntimeTestSession), typeof(RuntimeTestContext), typeof(RuntimeTestCase), typeof(RuntimeTestPlan), typeof(RuntimeTestResult), typeof(RuntimePreservedFailure) })
            {
                foreach (System.Reflection.FieldInfo f in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    T.Check(f.FieldType != typeof(NetScheduler) && f.FieldType != typeof(SchedulerState), type.Name + "." + f.Name + " holds no scheduler");
                }
            }
            // Detection: a host whose persisted scheduler DOES contain a control job fails INFRA-003.
            host.live.scheduler.RegisterKind("devtest.step", job => { }, true, false);
            host.live.scheduler.Schedule("devtest.step", host.live.clock.Now + 100, 1);
            RuntimeTestSession bad = Execute(host, Plan("bad", Imm("RT-TEST-001", c => { })));
            T.Check(Result(bad, "RT-INFRA-003").Outcome == RuntimeTestOutcome.Fail, "a runtime-test job in the persisted scheduler would be reported as a failure");
        }

        private static void SafeSuiteReadOnly()
        {
            FakeRuntimeHost host = new FakeRuntimeHost(20);
            LiveFingerprint before = host.RawFingerprint();
            int silver = host.live.pay.silver, charged = host.live.pay.charged, refunded = host.live.pay.refunded, deliveries = host.live.delivery.deliveries;
            int actors = host.live.ctx.actors.actors.Count, contracts = host.live.ctx.contracts.contracts.Count, ops = host.live.ctx.operations.operations.Count, history = host.live.ledger.records.Count, relations = host.live.ctx.relations.Count, jobs = host.live.scheduler.Count;
            string careers = CareerDigest(host.live);
            RuntimeTestSession s = Execute(host, RuntimeTestPlans.SandboxOnly(host));
            T.Eq(0, s.Count(RuntimeTestOutcome.Fail), "the whole safe suite passes: " + FirstFailure(s));
            Console.WriteLine("  sandbox-only safe suite (headless, " + s.Plan.Cases.Count + " tests + infrastructure checks, " + s.Slices + " slices): " + RuntimeTestReport.Summary(s).Replace("\r", "").Replace("\n", " | "));
            T.Check(s.Count(RuntimeTestOutcome.Pass) >= 33, "and did real work (" + s.Count(RuntimeTestOutcome.Pass) + " passes)");
            LiveFingerprint after = host.RawFingerprint();
            T.Eq(0, before.Diff(after).Count, "the live world's fingerprint is identical (" + string.Join("; ", before.Diff(after).ToArray()) + ")");
            T.Eq(actors, host.live.ctx.actors.actors.Count, "same live actors");
            T.Eq(contracts, host.live.ctx.contracts.contracts.Count, "same live contracts");
            T.Eq(ops, host.live.ctx.operations.operations.Count, "same live operations");
            T.Eq(history, host.live.ledger.records.Count, "same history");
            T.Eq(relations, host.live.ctx.relations.Count, "same relations");
            T.Eq(careers, CareerDigest(host.live), "same career state (fame score, funds, record, equipment of every contractor)");
            T.Eq(jobs, host.live.scheduler.Count, "same scheduler jobs");
            T.Check(silver == host.live.pay.silver && charged == host.live.pay.charged && refunded == host.live.pay.refunded, "same silver adapter state (silver, charged, refunded)");
            T.Eq(deliveries, host.live.delivery.deliveries, "no delivery reached the live delivery port");
            T.Eq(0, host.live.recorder.Count(EventKeys.ContractCompleted) - 0, "no live event was published by a sandbox");
            T.Check(Result(s, "RT-INFRA-001").Outcome == RuntimeTestOutcome.Pass, "the run's own check says the same");
            T.Check(s.LiveChecks > 0 && s.LiveChanges.Count == 0, "checked on every slice (" + s.LiveChecks + " slices, " + s.LiveChanges.Count + " changes)");
        }

        private static string CareerDigest(TestNet n)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (NetworkActor a in n.ctx.actors.actors)
            {
                ContractorSimulation sim = a.Get<ContractorSimulation>();
                if (sim == null) continue;
                CareerRecord r = sim.career;
                sb.Append(a.id.Value).Append(':').Append(a.reputation.score).Append(',').Append(sim.funds).Append(',').Append(sim.equipment.tier).Append(',').Append(r.triumphs).Append(r.successes).Append(r.partials).Append(r.failures).Append(r.disasters).Append(r.careerEarnings).Append(';');
            }
            return sb.ToString();
        }

        private static void SlicesYield()
        {
            FakeRuntimeHost host = new FakeRuntimeHost();
            List<string> ran = new List<string>();
            RuntimeTestPlan plan = Plan("yield", Imm("RT-TEST-001", c => ran.Add("a")), Imm("RT-TEST-002", c => ran.Add("b")), Imm("RT-TEST-003", c => ran.Add("c")));
            RuntimeTestOptions o = RuntimeTestOptions.Full();
            o.SliceBudgetMs = 0; // every slice does at most one step
            StepClock clock = new StepClock { step = 1 };
            RuntimeTestRunner runner = new RuntimeTestRunner(host, clock.Read, () => new DateTime(2026, 10, 2));
            RuntimeTestSession s = runner.Start(plan, o);
            int pumps = 0;
            while (runner.Pump() && pumps < 100) pumps++;
            T.Eq("a,b,c", string.Join(",", ran.ToArray()), "all tests ran");
            T.Check(s.Slices >= 3, "but across several slices (" + s.Slices + "): a slice never runs unbounded, it yields to the game");
            // A test that waits yields at once, however much budget is left.
            int calls = 0;
            RuntimeTestSession w = Execute(host, Plan("wait", RuntimeTestCase.Stepped("TEST", "RT-TEST-004", "wait twice", c => { calls++; return c.StepIndex < 3 ? RuntimeStep.Wait("w") : RuntimeStep.Pass(); })));
            T.Check(calls == 3 && w.Slices >= 3, "each Wait ended its slice (" + w.Slices + " slices for 3 steps)");
        }

        private static void FingerprintDetects()
        {
            FakeRuntimeHost host = new FakeRuntimeHost();
            // A test that (wrongly) touches the LIVE world: the run must say so, naming what moved.
            RuntimeTestSession s = Execute(host, Plan("bad", Imm("RT-TEST-001", c => host.live.ctx.actors.PlayerProxy.reputation.SetScore(777))));
            RuntimeTestResult r = Result(s, "RT-INFRA-001");
            T.Check(r.Outcome == RuntimeTestOutcome.Fail && r.Message.Contains("actors.hash"), "a touched live actor is reported by RT-INFRA-001 (" + r.Message + ")");
            FakeRuntimeHost h2 = new FakeRuntimeHost();
            RuntimeTestSession s2 = Execute(h2, Plan("bad2", Imm("RT-TEST-001", c => h2.live.pay.silver -= 5)));
            T.Check(Result(s2, "RT-INFRA-001").Outcome == RuntimeTestOutcome.Fail && Result(s2, "RT-INFRA-001").Message.Contains("pay.silver"), "spent live silver is reported");
            FakeRuntimeHost h3 = new FakeRuntimeHost();
            RuntimeTestSession s3 = Execute(h3, Plan("bad3", Imm("RT-TEST-001", c => h3.live.scheduler.Schedule(JobKinds.ContractExpire, h3.live.clock.Now + 77, 5))));
            T.Check(Result(s3, "RT-INFRA-001").Outcome == RuntimeTestOutcome.Fail && Result(s3, "RT-INFRA-001").Message.Contains("scheduler.jobs"), "a job added to the live scheduler is reported");
        }

        private static void IdleCost()
        {
            // With no run in progress the per-frame hook is one static reference check: no allocation, no scan, no enumeration.
            for (int i = 0; i < 1000; i++) RuntimeTestGame.PumpFrame(null);
            long mem0 = GC.GetTotalMemory(false);
            const int n = 5000000;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < n; i++) RuntimeTestGame.PumpFrame(null);
            sw.Stop();
            long mem1 = GC.GetTotalMemory(false);
            double ns = sw.Elapsed.TotalMilliseconds * 1e6 / n;
            Console.WriteLine("  idle runtime-test hook: " + ns.ToString("0.0") + " ns per call (" + n + " calls), allocated " + (mem1 - mem0) + " bytes");
            T.Check(!RuntimeTestGame.IsRunning, "no run is in progress");
            T.Check(ns < 200.0, "an idle frame costs a few nanoseconds (" + ns.ToString("0.0") + " ns)");
            T.Check(mem1 - mem0 < 64 * 1024, "and allocates nothing (" + (mem1 - mem0) + " bytes over " + n + " calls)");
        }

        private static IEnumerable<RuntimeTestCase> SmokeCases(FakeRuntimeHost host, params string[] ids)
        {
            List<string> want = new List<string>(ids);
            foreach (RuntimeTestCase c in RuntimeSmokeSuite.Cases(host)) if (want.Contains(c.Id)) yield return c;
        }

        // ================================================================== Finding 1: a runtime test never starts the live Network

        private static void UnstartedNetworkSkipped()
        {
            // The game has not started its Network (a save just loaded, nothing has ticked). Quick-smoke options (stop on first failure) and a
            // plan made of the real smoke tests that read start-up state.
            FakeRuntimeHost host = new FakeRuntimeHost();
            host.network = FakeRuntimeHost.FakeNetwork.NotStarted;
            LiveFingerprint before = host.RawFingerprint();
            RuntimeTestPlan plan = new RuntimeTestPlan("smoke-unstarted").AddRange(SmokeCases(host, "RT-SMOKE-002", "RT-SMOKE-003", "RT-SMOKE-004", "RT-SMOKE-007", "RT-SMOKE-008"));
            T.Eq(5, plan.Cases.Count, "the five smoke tests that read the Network's start-up state");
            RuntimeTestSession s = Execute(host, plan, RuntimeTestOptions.Quick());

            RuntimeTestResult r2 = Result(s, "RT-SMOKE-002");
            T.Check(r2.Outcome == RuntimeTestOutcome.Skip, "RT-SMOKE-002 is SKIP for a Network the game has not started (" + r2.Outcome + ": " + r2.Message + ")");
            T.Check(r2.Message.Contains("not started") && r2.Message.Contains("one game tick") && r2.Message.Contains("rerun"), "and tells the owner to allow one normal tick and rerun (" + r2.Message + ")");
            foreach (string id in new[] { "RT-SMOKE-003", "RT-SMOKE-004", "RT-SMOKE-007", "RT-SMOKE-008" })
            {
                T.Check(Result(s, id).Outcome == RuntimeTestOutcome.Skip, id + " is SKIP too, never a failure and never a start-up (" + Result(s, id).Outcome + ")");
            }
            T.Eq(0, s.Count(RuntimeTestOutcome.Fail), "an unstarted Network is a gameplay state, not a defect: no FAIL, so even a stop-on-first-failure run completes");
            T.Eq(5, s.Count(RuntimeTestOutcome.Skip) - (Result(s, "RT-INFRA-001").Outcome == RuntimeTestOutcome.Skip ? 1 : 0), "all five skipped");

            // Nothing started it, repaired it or changed anything.
            T.Eq(0, host.startupAttempts, "no start-up / reconciliation was attempted by the run");
            T.Check(host.network == FakeRuntimeHost.FakeNetwork.NotStarted, "the Network is still not started");
            T.Eq(0, before.Diff(host.RawFingerprint()).Count, "the live world is exactly as it was (" + string.Join("; ", before.Diff(host.RawFingerprint()).ToArray()) + ")");
            T.Eq(0, host.availableCaptures, "there was never a settled live state to fingerprint");

            // And RT-INFRA-001 needs no start-up blind spot: it says plainly that nothing was verified.
            RuntimeTestResult infra = Result(s, "RT-INFRA-001");
            T.Check(infra.Outcome == RuntimeTestOutcome.Skip && infra.Message.Contains("has not started") && infra.Message.Contains("did not start it") && infra.Message.Contains("nothing about the live Network was verified"), "RT-INFRA-001 SKIPs honestly, not as a pass (" + infra.Message + ")");
            T.Check(s.LiveChecks == 0 && s.LiveNotStartedSlices == s.Slices && s.LiveChanges.Count == 0 && s.FingerprintFailures.Count == 0, "every slice was recorded as 'not started', none compared, none failed");

            // A failed start-up is a defect (RT-SMOKE-002 / 008 FAIL); the others stay SKIP; still nothing is started.
            FakeRuntimeHost bad = new FakeRuntimeHost();
            bad.network = FakeRuntimeHost.FakeNetwork.NotStarted;
            bad.startupFailed = true;
            RuntimeTestSession sb = Execute(bad, new RuntimeTestPlan("smoke-failed").AddRange(SmokeCases(bad, "RT-SMOKE-002", "RT-SMOKE-003", "RT-SMOKE-008")), RuntimeTestOptions.Full());
            T.Check(Result(sb, "RT-SMOKE-002").Outcome == RuntimeTestOutcome.Fail && Result(sb, "RT-SMOKE-002").Message.Contains("load reconciliation"), "a failed start-up FAILS RT-SMOKE-002 with its stage (" + Result(sb, "RT-SMOKE-002").Message + ")");
            T.Check(Result(sb, "RT-SMOKE-008").Outcome == RuntimeTestOutcome.Fail, "and RT-SMOKE-008");
            T.Check(Result(sb, "RT-SMOKE-003").Outcome == RuntimeTestOutcome.Skip, "while the tests that need a settled Network SKIP");
            T.Eq(0, bad.startupAttempts, "and it still did not try to start it");
        }

        private static void StartupNotHidden()
        {
            // There is no startup exemption left in the runner: if a test (wrongly) starts the live Network, availability changes inside a slice and RT-INFRA-001 FAILS.
            FakeRuntimeHost host = new FakeRuntimeHost();
            host.network = FakeRuntimeHost.FakeNetwork.NotStarted;
            RuntimeTestSession s = Execute(host, Plan("bad-start", Imm("RT-TEST-001", c => host.StartNetwork())));
            RuntimeTestResult r = Result(s, "RT-INFRA-001");
            T.Check(r.Outcome == RuntimeTestOutcome.Fail && r.Message.Contains("availability changed") && r.Message.Contains("NetworkNotStarted") && r.Message.Contains("Available"), "a start-up from a test is reported, not excused (" + r.Message + ")");
            T.Eq(1, host.startupAttempts, "the probe confirms the one attempt");
            // And the same start-up between slices is the GAME's own (a different tick): the next slice then compares normally.
            FakeRuntimeHost game = new FakeRuntimeHost();
            game.network = FakeRuntimeHost.FakeNetwork.NotStarted;
            RuntimeTestOptions each = RuntimeTestOptions.Full();
            each.SliceBudgetMs = 0;
            RuntimeTestPlan two = Plan("game-starts", Imm("RT-TEST-001", c => { }), Imm("RT-TEST-002", c => { }), Imm("RT-TEST-003", c => { }));
            RuntimeTestRunner runner = new RuntimeTestRunner(game, new StepClock { step = 1 }.Read, () => new DateTime(2026, 10, 2));
            RuntimeTestSession gs = runner.Start(two, each);
            int pumps = 0;
            while (runner.Pump() && pumps < 100) { pumps++; if (pumps == 1) game.StartNetwork(); } // between two frames, by the game itself
            T.Check(Result(gs, "RT-INFRA-001").Outcome == RuntimeTestOutcome.Pass && gs.LiveNotStartedSlices >= 1 && gs.LiveChecks >= 1, "a start-up the GAME performs between frames is not a test's doing: earlier slices count as not started, later ones are compared (" + Result(gs, "RT-INFRA-001").Message + ")");
        }

        // ================================================================== Finding 2: the fingerprint fails closed

        private static void FingerprintFailsClosed()
        {
            // A RUNNING live Network whose fingerprint capture throws: the safety sentinel broke, so the run must FAIL, never skip or pass.
            FakeRuntimeHost host = new FakeRuntimeHost();
            host.captureThrows = new InvalidOperationException("boom: the fingerprint walked into something it could not read");
            RuntimeTestSession s = null;
            try
            {
                s = Execute(host, Plan("sentinel-broken", Imm("RT-TEST-001", c => c.Assert.True(true, "an otherwise fine test"))));
            }
            catch (Exception ex)
            {
                T.Check(false, "the runner crashed instead of containing a fingerprint failure: " + ex.Message);
                return;
            }
            RuntimeTestResult r = Result(s, "RT-INFRA-001");
            T.Check(r.Outcome == RuntimeTestOutcome.Fail, "RT-INFRA-001 FAILS when the capture throws on a running Network (" + r.Outcome + ")");
            T.Check(r.Message.Contains("could not be captured") && r.Message.Contains("cannot claim") && r.Message.Contains("slice 1") && r.Message.Contains("before") && r.Message.Contains("InvalidOperationException") && r.Message.Contains("boom"), "and says what failed: capture, which end, the slice, the exception type and message (" + r.Message + ")");
            T.Check(r.ExceptionType == "System.InvalidOperationException" && r.ExceptionMessage.Contains("boom") && !string.IsNullOrEmpty(r.StackTrace), "with the exception and its stack trace on the result");
            T.Check(s.FingerprintFailures.Count >= 2 && s.LiveChecks == 0, "both ends failed and nothing was compared (" + s.FingerprintFailures.Count + " failures, " + s.LiveChecks + " compared)");
            T.Check(s.Count(RuntimeTestOutcome.Fail) >= 1 && Result(s, "RT-TEST-001").Outcome == RuntimeTestOutcome.Pass, "the test itself passed, yet the run cannot be all green");
            string report = RuntimeTestReport.Full(s, "headless");
            T.Check(report.Contains("RT-INFRA-001") && report.Contains("boom") && report.Contains("InvalidOperationException"), "the exported report carries the failure");
            T.Check(host.log.Exists(l => l.Contains("fingerprint could not be captured")), "and the real log was told once, at the time");
            T.Check(RuntimeTestReport.Summary(s).Contains("FAIL: 1"), "and the summary counts it");

            // Only the 'after' capture throws: the same, naming the end.
            FakeRuntimeHost h2 = new FakeRuntimeHost();
            h2.captureThrows = new InvalidOperationException("late boom");
            h2.throwOnCapture = 2;
            RuntimeTestSession s2 = Execute(h2, Plan("after-broken", Imm("RT-TEST-001", c => { })));
            T.Check(Result(s2, "RT-INFRA-001").Outcome == RuntimeTestOutcome.Fail && Result(s2, "RT-INFRA-001").Message.Contains("after") && Result(s2, "RT-INFRA-001").Message.Contains("late boom"), "a capture that throws only at the end of the slice FAILS too (" + Result(s2, "RT-INFRA-001").Message + ")");

            // A host that returns nothing breaks its contract: also a failure, never a silent skip.
            FakeRuntimeHost h3 = new FakeRuntimeHost();
            h3.returnNullCapture = true;
            RuntimeTestSession s3 = Execute(h3, Plan("null-capture", Imm("RT-TEST-001", c => { })));
            T.Check(Result(s3, "RT-INFRA-001").Outcome == RuntimeTestOutcome.Fail, "a host that returns no capture fails closed (" + Result(s3, "RT-INFRA-001").Outcome + ")");
        }

        private static void NoLiveNetworkSkips()
        {
            // No live Network at all (a headless host): the one legitimate SKIP of RT-INFRA-001, and the run is otherwise green.
            FakeRuntimeHost host = new FakeRuntimeHost();
            host.network = FakeRuntimeHost.FakeNetwork.Unavailable;
            RuntimeTestSession s = Execute(host, Plan("headless", Imm("RT-TEST-001", c => { }), Imm("RT-TEST-002", c => c.Assert.True(true, "fine"))));
            RuntimeTestResult r = Result(s, "RT-INFRA-001");
            T.Check(r.Outcome == RuntimeTestOutcome.Skip && r.Message.Contains("no live Network"), "RT-INFRA-001 SKIPs when there is no live Network (" + r.Outcome + ": " + r.Message + ")");
            T.Eq(0, s.Count(RuntimeTestOutcome.Fail), "and nothing fails");
            T.Check(s.LiveUnavailableSlices >= 1 && s.LiveChecks == 0 && s.FingerprintFailures.Count == 0, "recorded as unavailable by design, not as a failure");
        }

        // ================================================================== Finding 3: the fingerprint sees mutations of existing durable truth

        /// <summary>Mutates one existing durable value of the synthetic live world and proves the mutation is detected, by name, with NO change in any count.</summary>
        private static void AssertDetected(string what, Action<FakeRuntimeHost> mutate, string expectedFragment, params string[] countParts)
        {
            FakeRuntimeHost host = new FakeRuntimeHost(12, true);
            LiveFingerprint before = host.RawFingerprint();
            mutate(host);
            LiveFingerprint after = host.RawFingerprint();
            List<string> diff = before.Diff(after);
            string all = string.Join(" | ", diff.ToArray());
            T.Check(diff.Count > 0, what + ": detected by a direct comparison");
            T.Check(all.Contains(expectedFragment), what + ": the difference names '" + expectedFragment + "' (" + all + ")");
            for (int i = 0; i < countParts.Length; i++) T.Eq(before.Get(countParts[i]), after.Get(countParts[i]), what + ": " + countParts[i] + " did not change (a count alone would have missed it)");

            // And through the runner: a test that does this to the live world fails the run.
            FakeRuntimeHost h2 = new FakeRuntimeHost(12, true);
            RuntimeTestSession s = Execute(h2, Plan("mutate", Imm("RT-TEST-001", c => mutate(h2))));
            RuntimeTestResult r = Result(s, "RT-INFRA-001");
            T.Check(r.Outcome == RuntimeTestOutcome.Fail && r.Message.Contains(expectedFragment), what + ": RT-INFRA-001 FAILS the run (" + r.Outcome + ")");
        }

        private static void DetectsRelationMutation()
        {
            FakeRuntimeHost probe = new FakeRuntimeHost(12, true);
            T.Check(probe.live.ctx.relations.edges.Count > 0, "the synthetic live world has relation edges to mutate (" + probe.live.ctx.relations.edges.Count + ")");
            AssertDetected("relation familiarity 0.25 -> 0.90", h => h.live.ctx.relations.edges[0].familiarity = h.live.ctx.relations.edges[0].familiarity == 0.9f ? 0.25f : 0.9f, "relations.edges[0]", "relations.edges.count");
            AssertDetected("relation standing", h => h.live.ctx.relations.edges[0].standing += 3f, "relations.edges[0]", "relations.edges.count");
            AssertDetected("a relation's salient record list", h => h.live.ctx.relations.edges[0].salient.Add(new HistoryRecordId(999)), "relations.edges[0]", "relations.edges.count");
            T.Check(probe.live.ctx.knowledge.books.Count > 0 && probe.live.ctx.knowledge.books[0].entries.Count > 0, "and knowledge books with entries");
            AssertDetected("a knowledge entry's experience", h => h.live.ctx.knowledge.books[0].entries[0].exp += 0.5f, "knowledge.books[0]", "knowledge.books.count");
            AssertDetected("a knowledge entry's topic", h => h.live.ctx.knowledge.books[0].entries[0].topic = "TestRewrittenTopic", "knowledge.books[0]", "knowledge.books.count");
        }

        private static void DetectsContractMutation()
        {
            FakeRuntimeHost probe = new FakeRuntimeHost(12, true);
            Contract c0 = probe.live.ctx.contracts.contracts[0];
            T.Check(probe.live.ctx.contracts.contracts.Count >= 1 && c0.ledger.Count > 0, "the synthetic live world has a contract with a ledger (" + probe.live.ctx.contracts.contracts.Count + " contracts)");
            AssertDetected("a contract's quoted price", h => h.live.ctx.contracts.contracts[0].terms.price += 1, "contracts.contracts[0]", "contracts.contracts.count");
            AssertDetected("a contract's payment terms balance", h => h.live.ctx.contracts.contracts[0].terms.balance += 1, "contracts.contracts[0]", "contracts.contracts.count");
            AssertDetected("a ledger record's silver", h => h.live.ctx.contracts.contracts[0].ledger[0].silver += 1, "contracts.contracts[0]", "contracts.contracts.count");
            AssertDetected("a ledger record's typed full-reversal flag", h => h.live.ctx.contracts.contracts[0].ledger[0].fullReversal = !h.live.ctx.contracts.contracts[0].ledger[0].fullReversal, "contracts.contracts[0]", "contracts.contracts.count");
            AssertDetected("the delivery state", h => h.live.ctx.contracts.contracts[0].Deliver.balancePaid = !h.live.ctx.contracts.contracts[0].Deliver.balancePaid, "contracts.contracts[0]", "contracts.contracts.count");
            AssertDetected("the acquisition state", h => h.live.ctx.contracts.contracts[0].Acquire.secured += 1, "contracts.contracts[0]", "contracts.contracts.count");
            AssertDetected("the sub-status", h => h.live.ctx.contracts.contracts[0].subStatus = SubStatus.AwaitingPayment, "contracts.contracts[0]", "contracts.contracts.count");
            // Editing an existing Field Log entry (add one first, in the world, so there is an entry to edit).
            FakeRuntimeHost log = new FakeRuntimeHost(12, true);
            Contract lc = log.live.ctx.contracts.contracts[0];
            lc.fieldLog.Add(new FieldLogEntry { tick = 5, key = "FieldLog.TestBeat" });
            LiveFingerprint before = log.RawFingerprint();
            lc.fieldLog[0].key = "FieldLog.TestBeatEdited";
            string d = string.Join(" | ", before.Diff(log.RawFingerprint()).ToArray());
            T.Check(d.Contains("contracts.contracts[0]"), "editing an existing Field Log entry is detected without adding one (" + d + ")");
            lc.fieldLog[0].args.Add("added-arg");
            T.Check(before.Diff(log.RawFingerprint()).Count > 0, "and so is its argument list");
            // An existing actor's career and funds (kept covered).
            AssertDetected("a contractor's career record", h => { foreach (NetworkActor a in h.live.ctx.actors.actors) { ContractorSimulation sim = a.Get<ContractorSimulation>(); if (sim != null) { sim.career.successes += 1; return; } } }, "actors.actors[", "actors.actors.count");
            AssertDetected("a contractor's equipment condition", h => { foreach (NetworkActor a in h.live.ctx.actors.actors) { ContractorSimulation sim = a.Get<ContractorSimulation>(); if (sim != null) { sim.equipment.condition -= 0.125f; return; } } }, "actors.actors[", "actors.actors.count");
            AssertDetected("an actor's spatial truth", h => { foreach (NetworkActor a in h.live.ctx.actors.actors) { ContractorSimulation sim = a.Get<ContractorSimulation>(); if (sim != null && sim.spatial != null) { sim.spatial.arrivalTick += 7; return; } } }, "actors.actors[", "actors.actors.count");
        }

        private static void DetectsOperationMutation()
        {
            FakeRuntimeHost probe = new FakeRuntimeHost(12, true);
            T.Check(probe.live.ctx.operations.operations.Count >= 1, "the synthetic live world has an operation (" + probe.live.ctx.operations.operations.Count + ")");
            AssertDetected("an operation checkpoint's due tick", h => h.live.ctx.operations.operations[0].checkpoints[0].dueTick += 1, "operations.operations[0]", "operations.operations.count");
            AssertDetected("an operation checkpoint's done flag", h => h.live.ctx.operations.operations[0].checkpoints[0].done = !h.live.ctx.operations.operations[0].checkpoints[0].done, "operations.operations[0]", "operations.operations.count");
            AssertDetected("an operation's phase", h => h.live.ctx.operations.operations[0].phase = h.live.ctx.operations.operations[0].phase == OpPhase.Done ? OpPhase.Preparing : OpPhase.Done, "operations.operations[0]", "operations.operations.count");
            AssertDetected("an operation's career flag", h => h.live.ctx.operations.operations[0].careerOutcomeApplied = !h.live.ctx.operations.operations[0].careerOutcomeApplied, "operations.operations[0]", "operations.operations.count");
            AssertDetected("an operation's eligibility", h => h.live.ctx.operations.operations[0].careerEligible = !h.live.ctx.operations.operations[0].careerEligible, "operations.operations[0]", "operations.operations.count");
            AssertDetected("an operation's resolver inputs", h => h.live.ctx.operations.operations[0].danger += 0.01f, "operations.operations[0]", "operations.operations.count");
            Operation withPlan = null;
            foreach (Operation o in probe.live.ctx.operations.operations) if (o.spatial != null) withPlan = o;
            if (withPlan != null) AssertDetected("an operation's spatial plan", h => h.live.ctx.operations.operations[0].spatial.incident = new TileRef { layerId = 9, tileId = 9 }, "operations.operations[0]", "operations.operations.count");
        }

        private static void DetectsHistoryOrJournalMutation()
        {
            FakeRuntimeHost probe = new FakeRuntimeHost(12, true);
            T.Check(probe.live.ledger.records.Count > 0, "the synthetic live world has history records (" + probe.live.ledger.records.Count + ")");
            AssertDetected("a history record's outcome key", h => h.live.ledger.records[0].outcomeKey = "Rewritten", "history.records[0]", "history.records.count");
            AssertDetected("a history record's magnitudes", h => h.live.ledger.records[0].magnitudes.value += 1, "history.records[0]", "history.records.count");
            AssertDetected("a history record's notes", h => h.live.ledger.records[0].notes.Add("k=v"), "history.records[0]", "history.records.count");
            AssertDetected("a history record's participants", h => h.live.ledger.records[0].participants.RemoveAt(0), "history.records[0]", "history.records.count");
            T.Check(probe.live.journal.entries.Count > 0, "and journal entries (" + probe.live.journal.entries.Count + ")");
            AssertDetected("a journal entry's importance", h => h.live.journal.entries[0].importance = h.live.journal.entries[0].importance == Importance.Major ? Importance.Minor : Importance.Major, "journal.entries[0]", "journal.entries.count");
            AssertDetected("a journal entry's tick", h => h.live.journal.entries[0].tick += 1, "journal.entries[0]", "journal.entries.count");
            AssertDetected("a journal entry's subjects", h => h.live.journal.entries[0].subjects.Clear(), "journal.entries[0]", "journal.entries.count");
            if (probe.live.ctx.summaries.actors.Count > 0) AssertDetected("an actor record summary", h => h.live.ctx.summaries.actors[0].deeds.Add(new TheNetwork.History.DeedCounter { key = "x", lifetime = 1 }), "summaries.actors[0]", "summaries.actors.count");
            if (probe.live.ctx.characters.characters.Count > 0) AssertDetected("a known character", h => h.live.ctx.characters.characters[0].notability += 0.25f, "characters.characters[0]", "characters.characters.count");
            AssertDetected("a cast entry", h => h.live.ctx.cast.entries[0].templateId = "Renamed", "cast.entries[0]", "cast.entries.count");
            AssertDetected("the live scheduler (kept)", h => h.live.scheduler.Schedule(JobKinds.ContractExpire, h.live.clock.Now + 77, 5), "scheduler.jobs");
            AssertDetected("live silver (kept)", h => h.live.pay.silver -= 5, "pay.silver");
        }

        private static void IgnoresReadOnlyAccess()
        {
            // The deep walker reads fields only. Everything a read-only suite does to the live world must leave it identical, or the sentinel would
            // cry wolf in the game: the invariant scan, index lookups, history queries, and career reads.
            FakeRuntimeHost host = new FakeRuntimeHost(12, true);
            LiveFingerprint before = host.RawFingerprint();
            for (int i = 0; i < 3; i++)
            {
                LiveInvariants.Scan(host.live.ctx, host.live.ids, host.live.scheduler);
                foreach (Contract c in host.live.ctx.contracts.contracts) { host.live.ctx.contracts.Get(c.id); host.live.ctx.Procurement.CurrentOperation(c); }
                foreach (NetworkActor a in host.live.ctx.actors.actors)
                {
                    host.live.ctx.actors.Get(a.id);
                    host.live.ctx.Contractors.Strength(a); // fills ContractorSimulation.cachedStrength: a runtime-only cache, not durable truth
                    host.live.ctx.Career.Tags(a);
                    host.live.ctx.Career.CurrentNeed(a);
                }
                foreach (Operation o in host.live.ctx.operations.operations) host.live.ctx.operations.Get(o.id);
                host.CaptureFingerprint();
                host.live.history.RebuildIndex(); // the ledger's runtime index (and HistoryRecord.narrativeSeed) is not durable truth
            }
            LiveFingerprint after = host.RawFingerprint();
            T.Eq(0, before.Diff(after).Count, "reading the live world changes nothing the fingerprint sees (" + string.Join("; ", before.Diff(after).ToArray()) + ")");
        }

        // ================================================================== Finding 4: captured warnings are visible as WARN

        private static void CapturedWarningWarns()
        {
            Action<NetLogLevel, string> original = NetLog.Sink;
            List<string> realLog = new List<string>();
            Action<NetLogLevel, string> sentinelSink = (lv, line) => realLog.Add(lv + " " + line);
            NetLog.Sink = sentinelSink;
            try
            {
                NetLog.WarnOnce(LogCategory.Kernel, "runtime-test-once-existing", "an existing once-warning");
                string[] keysBefore = NetLog.SnapshotOnceKeys();
                int realBefore = realLog.Count;
                FakeRuntimeHost host = new FakeRuntimeHost();
                RuntimeTestPlan plan = Plan("warns",
                    Imm("RT-TEST-001", c => NetLog.Warn(LogCategory.Kernel, "production code complained: something looked odd")),
                    Imm("RT-TEST-002", c => NetLog.WarnOnce(LogCategory.Kernel, "runtime-test-once-new", "a once-warning first seen during a test")),
                    Imm("RT-TEST-003", c => NetLog.Error(LogCategory.Kernel, "production code logged an error")),
                    Imm("RT-TEST-004", c => c.Assert.True(true, "a clean test")));
                RuntimeTestSession s = Execute(host, plan);

                RuntimeTestResult w = Result(s, "RT-TEST-001");
                T.Check(w.Outcome == RuntimeTestOutcome.Warn, "an otherwise passing test that makes production code warn is WARN (" + w.Outcome + ")");
                T.Check(w.Message.Contains("1 warning") && w.Message.Contains("something looked odd"), "the warning text is in the result (" + w.Message + ")");
                T.Check(w.CapturedLog.Exists(l => l.Contains("something looked odd")), "and kept as a captured log line");
                T.Check(Result(s, "RT-TEST-002").Outcome == RuntimeTestOutcome.Warn, "a once-warning is a WARN as well");
                RuntimeTestResult e = Result(s, "RT-TEST-003");
                T.Check(e.Outcome == RuntimeTestOutcome.Warn && e.Message.Contains("1 error"), "an error stays at least a WARN (existing policy) (" + e.Outcome + ")");
                T.Check(Result(s, "RT-TEST-004").Outcome == RuntimeTestOutcome.Pass, "a clean test stays PASS");
                T.Eq(0, s.Count(RuntimeTestOutcome.Fail), "a warning is never a FAIL");
                string report = RuntimeTestReport.Full(s, "headless");
                T.Check(report.Contains("something looked odd") && report.Contains("production code logged an error"), "the report carries the warning and error lines");
                T.Check(host.log.Exists(l => l.Contains("RT-TEST-001") && l.Contains("something looked odd")), "and the real log is told of the WARN (it is never silent)");

                // The scratch world's lines did not leak into the owner's log, and everything was put back exactly.
                T.Eq(realBefore, realLog.Count, "none of the captured lines reached the original sink");
                T.Check(ReferenceEquals(NetLog.Sink, sentinelSink), "the original NetLog sink is restored");
                string[] keysAfter = NetLog.SnapshotOnceKeys();
                Array.Sort(keysBefore, StringComparer.Ordinal);
                Array.Sort(keysAfter, StringComparer.Ordinal);
                T.Check(string.Join("|", keysBefore) == string.Join("|", keysAfter), "the once-keys are exactly as they were (the test's own once-warning did not use up the real one)");
                T.Check(Array.IndexOf(keysAfter, "W|Kernel|runtime-test-once-new") < 0 && Array.IndexOf(keysAfter, "W|Kernel|runtime-test-once-existing") >= 0, "the new key is gone and the existing key kept");
            }
            finally
            {
                NetLog.Sink = original;
            }
        }

        private static void FingerprintCost()
        {
            // While a run is in progress the runner fingerprints the live Network twice per slice (before and after). Its cost grows with the
            // world, so measure it on a large synthetic one: 300 contractors, 60 awarded contracts with operations and ledgers, 30 days of upkeep.
            FakeRuntimeHost host = new FakeRuntimeHost(300);
            TestNet n = host.live;
            NetworkActor fixer = ProcurementTests.Fixer(n);
            for (int i = 0; i < 60; i++) ProcurementTests.Awarded(n, fixer, ProcurementTests.Reliable(n), "TestSteel", 150);
            n.AdvanceTo(n.clock.Now + 30 * Ticks.PerDay);
            host.RawFingerprint(); // warm up
            const int reps = 200;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            LiveFingerprint last = null;
            for (int i = 0; i < reps; i++) last = host.RawFingerprint();
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds / reps;
            Console.WriteLine("  live fingerprint: " + ms.ToString("0.000") + " ms per capture, walker: " + LiveFingerprint.LastWalkStats + ", " + last.EntityCount + " entities, " + last.PartCount + " parts (" + n.ctx.actors.actors.Count + " actors, " + n.ctx.contracts.contracts.Count + " contracts, " + n.ctx.operations.operations.Count + " operations, " + n.ledger.records.Count + " history records)");
            T.Check(last != null && last.Diff(host.RawFingerprint()).Count == 0, "capturing is itself read-only and repeatable");
            T.Check(ms < 40.0, "a fingerprint of a large world is a few milliseconds, not a frame-budget problem (" + ms.ToString("0.000") + " ms)");

            // The one-time cost of compiling the per-type accessors (the first capture of a process), and the reflective fallback.
            LiveFingerprint.DiscardCompiledProfiles();
            System.Diagnostics.Stopwatch cold = System.Diagnostics.Stopwatch.StartNew();
            LiveFingerprint first = host.RawFingerprint();
            cold.Stop();
            LiveFingerprint.UseCompiledWalker = false;
            LiveFingerprint.DiscardCompiledProfiles();
            host.RawFingerprint();
            System.Diagnostics.Stopwatch refl = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 10; i++) host.RawFingerprint();
            refl.Stop();
            LiveFingerprint viaReflection = host.RawFingerprint();
            LiveFingerprint.UseCompiledWalker = true;
            Console.WriteLine("  live fingerprint, first capture of a process (compiles the per-type accessors): " + cold.Elapsed.TotalMilliseconds.ToString("0.0") + " ms; reflective fallback: " + (refl.Elapsed.TotalMilliseconds / 10).ToString("0.0") + " ms per capture");
            T.Eq(0, first.Diff(viaReflection).Count, "the compiled walker and the reflective fallback fingerprint the same world identically (" + string.Join("; ", first.Diff(viaReflection).ToArray()) + ")");
            T.Check(first.Diff(host.RawFingerprint()).Count == 0, "and a recompiled walker agrees with itself");
        }

        private static void RunSuite(string name, IEnumerable<RuntimeTestCase> cases)
        {
            FakeRuntimeHost host = new FakeRuntimeHost();
            RuntimeTestPlan plan = new RuntimeTestPlan(name).AddRange(cases);
            RuntimeTestSession s = Execute(host, plan);
            foreach (RuntimeTestResult r in s.Results)
            {
                T.Check(r.Outcome == RuntimeTestOutcome.Pass || r.Outcome == RuntimeTestOutcome.Skip, r.Id + " " + r.Name + " -> " + r.Outcome + ": " + r.Message + " [expected " + r.Expected + ", actual " + r.Actual + "] " + string.Join(" | ", r.Notes.ToArray()));
            }
            Console.WriteLine(RuntimeTestReport.Summary(s));
        }
    }

    /// <summary>Gives the synthetic live world a little of everything the fingerprint reads.</summary>
    internal static class NetworkActorHelper
    {
        public static void Populate(TestNet n)
        {
            n.pay.silver = 123456;
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor team = ProcurementTests.Reliable(n);
            ProcurementTests.Awarded(n, fixer, team);
            n.AdvanceTo(n.clock.Now + 2 * Ticks.PerDay);
        }
    }
}
