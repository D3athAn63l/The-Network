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
            t.Add(new KeyValuePair<string, Action>("Runner.UnstartedNetworkIsNotAFalseAlarm", UnstartedNetwork));

            // The sandbox suites, through the real runner, on a synthetic host.
            t.Add(new KeyValuePair<string, Action>("Runner.SandboxProcurementSuite", () => RunSuite("Procurement", ProcurementRuntimeSuite.Cases(null))));
            t.Add(new KeyValuePair<string, Action>("Runner.SandboxCareerSuite", () => RunSuite("Career", CareerRuntimeSuite.Cases(null))));
            t.Add(new KeyValuePair<string, Action>("Runner.SandboxSpatialSuite", () => RunSuite("Spatial", SpatialRuntimeSuite.Cases(null))));
        }

        /// <summary>A synthetic "live" world standing in for the player's colony: the runner must leave all of it exactly as it found it.</summary>
        public sealed class FakeRuntimeHost : IRuntimeTestHost
        {
            public readonly TestNet live;
            public readonly List<string> log = new List<string>();
            public RuntimeTestSession finished;
            public bool realFacts = true;
            public bool started = true; // false: the game has not started its Network yet (no fingerprint to take)

            public FakeRuntimeHost(int contractors = 12)
            {
                live = ContractorTests.WorldWithCast(contractors, 31337);
                NetworkActorHelper.Populate(live);
            }

            public string Name => "headless host";
            public string BuildInfo => "headless";

            public LiveFingerprint CaptureFingerprint()
            {
                if (!started) return null;
                return LiveFingerprint.Of(live.ctx, live.ids, live.scheduler, live.journal)
                    .With("pay.silver", live.pay.silver).With("pay.charged", live.pay.charged).With("pay.refunded", live.pay.refunded).With("pay.chargeCalls", live.pay.chargeCalls)
                    .With("delivery.deliveries", live.delivery.deliveries).With("delivery.items", live.delivery.delivered);
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
            foreach (string must in new[] { "RT-SMOKE-001", "RT-LIVE-001", "RT-PROC-007", "RT-CAR-010", "RT-SPAT-005" }) T.Check(all.Contains(must), "the documented id " + must + " exists");
            T.Eq(8 + 6 + 11 + 14 + 8, all.Count, "the Full safe regression holds exactly the documented tests");
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
            LiveFingerprint before = host.CaptureFingerprint();
            LiveInvariants.Result clean = LiveInvariants.Scan(host.live.ctx, host.live.ids, host.live.scheduler);
            T.Eq(0, clean.Violations.Count, "a healthy world has no violation (" + string.Join("; ", clean.Violations.ToArray()) + ")");
            T.Check(clean.Actors > 0 && clean.Contracts > 0 && clean.Operations > 0 && clean.Jobs > 0, "the scan covered actors, contracts, operations and jobs (" + clean.Counted + ")");
            T.Check(before.Same(host.CaptureFingerprint()), "scanning a healthy world changed nothing");

            // A damaged world: the scan reports it and STILL repairs nothing (unlike NetValidator, which would reschedule, quarantine and prune).
            Contract c = host.live.ctx.contracts.contracts[0];
            c.ledger.Add(new MoneyRecord { silver = -5, direction = MoneyDirection.PlayerPaid, purpose = MoneyPurpose.Deposit });
            host.live.scheduler.Schedule(JobKinds.ContractDelivery, host.live.clock.Now + 5000, 424242); // a job whose target does not exist
            foreach (NetworkActor a in host.live.ctx.actors.actors) { ContractorSimulation sim = a.Get<ContractorSimulation>(); if (sim != null) { sim.funds = int.MaxValue; break; } }
            LiveFingerprint damaged = host.CaptureFingerprint();
            LiveInvariants.Result r = LiveInvariants.Scan(host.live.ctx, host.live.ids, host.live.scheduler);
            T.Check(r.Violations.Exists(v => v.Contains("negative silver")), "the negative ledger record is reported");
            T.Check(r.Violations.Exists(v => v.Contains("outside the bound")), "and the impossible funds");
            T.Check(r.Notes.Exists(v => v.Contains("no target")), "and the orphaned job (a note: a validation would remove it)");
            T.Check(damaged.Same(host.CaptureFingerprint()), "yet the scan repaired and changed NOTHING (the orphan job, the ledger and the funds are all still there)");
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
            LiveFingerprint before = host.CaptureFingerprint();
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
            T.Check(before.Same(host.CaptureFingerprint()), "scheduler hash, counts and id counters unchanged");
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
            LiveFingerprint before = host.CaptureFingerprint();
            int silver = host.live.pay.silver, charged = host.live.pay.charged, refunded = host.live.pay.refunded, deliveries = host.live.delivery.deliveries;
            int actors = host.live.ctx.actors.actors.Count, contracts = host.live.ctx.contracts.contracts.Count, ops = host.live.ctx.operations.operations.Count, history = host.live.ledger.records.Count, relations = host.live.ctx.relations.Count, jobs = host.live.scheduler.Count;
            string careers = CareerDigest(host.live);
            RuntimeTestSession s = Execute(host, RuntimeTestPlans.SandboxOnly(host));
            T.Eq(0, s.Count(RuntimeTestOutcome.Fail), "the whole safe suite passes: " + FirstFailure(s));
            Console.WriteLine("  sandbox-only safe suite (headless, " + s.Plan.Cases.Count + " tests + infrastructure checks, " + s.Slices + " slices): " + RuntimeTestReport.Summary(s).Replace("\r", "").Replace("\n", " | "));
            T.Check(s.Count(RuntimeTestOutcome.Pass) >= 33, "and did real work (" + s.Count(RuntimeTestOutcome.Pass) + " passes)");
            LiveFingerprint after = host.CaptureFingerprint();
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

        private static void UnstartedNetwork()
        {
            // A game loaded paused has not started its Network: the game's own start-up (not the test's doing) changes live state during the run.
            // That must be neither a false RT-INFRA-001 failure nor silently hidden; a later change by a test must still be caught.
            FakeRuntimeHost host = new FakeRuntimeHost();
            host.started = false;
            RuntimeTestOptions each = RuntimeTestOptions.Full();
            each.SliceBudgetMs = 0; // one step per slice, so the slices are the same on every machine
            RuntimeTestSession s = Execute(host, Plan("unstarted", Imm("RT-TEST-001", c => { host.started = true; host.live.pay.silver += 1; }), Imm("RT-TEST-002", c => c.Assert.True(true, "later steps run normally"))), each, new StepClock { step = 1 }.Read);
            RuntimeTestResult r = Result(s, "RT-INFRA-001");
            T.Check(r.Outcome == RuntimeTestOutcome.Pass, "the game's own start-up is not reported as a test touching live state (" + r.Message + ")");
            T.Check(s.LiveChecksSkipped == 1 && s.LiveChecks >= 1 && r.Message.Contains("could not be compared"), "but the report says one slice could not be compared (" + s.LiveChecksSkipped + " skipped, " + s.LiveChecks + " compared)");
            // And a run in which NO slice could be compared says so rather than claiming the live Network was verified.
            FakeRuntimeHost h0 = new FakeRuntimeHost();
            h0.started = false;
            RuntimeTestSession s0 = Execute(h0, Plan("unstarted0", Imm("RT-TEST-001", c => h0.started = true)));
            T.Check(Result(s0, "RT-INFRA-001").Outcome == RuntimeTestOutcome.Skip && Result(s0, "RT-INFRA-001").Message.Contains("start-up"), "a run that never had a settled live Network does not claim it was verified (" + Result(s0, "RT-INFRA-001").Message + ")");
            // With every step in its own slice the later mutation falls inside a fully-compared slice.
            RuntimeTestOptions one = RuntimeTestOptions.Full();
            one.SliceBudgetMs = 0;
            FakeRuntimeHost h3 = new FakeRuntimeHost();
            h3.started = false;
            RuntimeTestSession s3 = Execute(h3, Plan("unstarted3", Imm("RT-TEST-001", c => h3.started = true), Imm("RT-TEST-002", c => h3.live.pay.silver -= 3)), one, new StepClock { step = 1 }.Read);
            T.Check(Result(s3, "RT-INFRA-001").Outcome == RuntimeTestOutcome.Fail && Result(s3, "RT-INFRA-001").Message.Contains("pay.silver"), "a test that touches live state after the start-up is still caught (" + Result(s3, "RT-INFRA-001").Message + ")");
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
            host.CaptureFingerprint(); // warm up
            const int reps = 200;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            LiveFingerprint last = null;
            for (int i = 0; i < reps; i++) last = host.CaptureFingerprint();
            sw.Stop();
            double ms = sw.Elapsed.TotalMilliseconds / reps;
            Console.WriteLine("  live fingerprint: " + ms.ToString("0.000") + " ms per capture (" + n.ctx.actors.actors.Count + " actors, " + n.ctx.contracts.contracts.Count + " contracts, " + n.ctx.operations.operations.Count + " operations, " + n.ledger.records.Count + " history records)");
            T.Check(last != null && last.Diff(host.CaptureFingerprint()).Count == 0, "capturing is itself read-only and repeatable");
            T.Check(ms < 25.0, "a fingerprint of a large world is a few milliseconds at most (" + ms.ToString("0.000") + " ms)");
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
