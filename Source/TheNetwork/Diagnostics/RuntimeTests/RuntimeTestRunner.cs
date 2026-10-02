using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using TheNetwork.Domain.Ports;

namespace TheNetwork.Diagnostics.RuntimeTests
{
    /// <summary>
    /// What a run needs from the world it runs in. In the game: the live Network (read-only), the real catalog and the log. In the
    /// headless suite: a synthetic world. The runner never calls the host to change anything.
    /// </summary>
    public interface IRuntimeTestHost
    {
        string Name { get; }

        /// <summary>Build/version text for the report header.</summary>
        string BuildInfo { get; }

        /// <summary>The fingerprint of the LIVE Network world, or null when there is none to protect. Read-only.</summary>
        LiveFingerprint CaptureFingerprint();

        /// <summary>The kinds of every job in the LIVE persisted scheduler (to prove no test-control job ever entered it). Empty when none.</summary>
        IEnumerable<string> LiveSchedulerKinds();

        /// <summary>Facts of a real, loaded def copied by value (null result when unknown). Never a reference to the live catalog.</summary>
        bool TryGetRealItemFacts(string defName, out ItemFacts facts);

        void Log(string line);

        /// <summary>The run finished or was cancelled (the game shows one Message; never a letter).</summary>
        void RunFinished(RuntimeTestSession session);
    }

    /// <summary>A failed sandbox kept (in memory only) so a developer can look at exactly what the failing scenario left behind.</summary>
    public sealed class RuntimePreservedFailure
    {
        public string TestId;
        public string Suite;
        public string Name;
        public string Message;
        public DateTime At;
        public RuntimeTestSandbox Sandbox;
        public RuntimeTestContext Context;

        /// <summary>The compact dump of the scenario's entities, produced on demand from the preserved sandbox.</summary>
        public string Describe()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[TheNetwork] Preserved failure: " + TestId + " " + Name);
            sb.AppendLine("  suite " + Suite + ", failed at " + At.ToString("yyyy-MM-dd HH:mm:ss") + ": " + Message);
            sb.AppendLine("  (an in-memory scratch world: never saved, never part of your colony)");
            if (Sandbox != null) sb.Append(Sandbox.Describe(Context));
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>One run of a plan: its options, results and bookkeeping. Runtime only.</summary>
    public sealed class RuntimeTestSession
    {
        public readonly RuntimeTestPlan Plan;
        public readonly RuntimeTestOptions Options;
        public readonly DateTime StartedAt;
        public readonly double StartedMs;
        public readonly List<RuntimeTestResult> Results = new List<RuntimeTestResult>();
        public readonly List<string> LiveChanges = new List<string>();
        public RuntimeRunState State = RuntimeRunState.Running;
        public int NextIndex;
        public RuntimeTestContext Current;
        public bool CancelRequested;
        public bool StoppedEarly;
        public double ElapsedMs;
        public int Slices;
        public int Steps;
        public int OverrideLeaks;
        public int OverrideRestoreMismatches;
        public int SandboxesCreated;
        public int SandboxesDiscarded;
        public int SandboxesPreserved;
        public int LiveChecks;
        public int LiveChecksSkipped;
        public string LastDump;

        /// <summary>The live fingerprint taken at the start of the slice in progress (compared at the end of that slice, or at the finish, whichever comes first).</summary>
        internal LiveFingerprint SliceBefore;

        /// <summary>True from the start of a slice until its live comparison has been made (a slice is compared exactly once).</summary>
        internal bool SliceOpen;

        internal RuntimeTestSession(RuntimeTestPlan plan, RuntimeTestOptions options, DateTime startedAt, double startedMs)
        {
            Plan = plan;
            Options = options;
            StartedAt = startedAt;
            StartedMs = startedMs;
        }

        public int Count(RuntimeTestOutcome o)
        {
            int n = 0;
            for (int i = 0; i < Results.Count; i++) if (Results[i].Outcome == o) n++;
            return n;
        }

        public bool Succeeded => Count(RuntimeTestOutcome.Fail) == 0 && State == RuntimeRunState.Completed;
    }

    /// <summary>
    /// The runtime test runner (Phase 2.9). It drives a plan of stable-id tests in a fixed order, a slice at a time: a pump runs
    /// steps until its small real-time budget is spent and then yields to the game, so a run never freezes a frame and a test
    /// that waits yields instead of looping. Every step is exception-contained, time-boxed and wrapped so that the process-wide
    /// dev overrides it touches are captured and restored exactly. The runner and its bookkeeping are RUNTIME ONLY: they use
    /// no Network scheduler job, no store, no Scribe, so nothing about a run can ever enter a save.
    /// </summary>
    public sealed class RuntimeTestRunner
    {
        private readonly IRuntimeTestHost host;
        private readonly Func<double> nowMs;
        private readonly Func<DateTime> wallClock;

        public RuntimeTestSession Session { get; private set; }

        /// <summary>The most recent run that finished (or was cancelled), kept so its report can be shown again.</summary>
        public RuntimeTestSession LastFinished { get; private set; }

        public RuntimePreservedFailure Preserved { get; private set; }

        public bool IsRunning => Session != null && Session.State == RuntimeRunState.Running;

        public RuntimeTestRunner(IRuntimeTestHost host, Func<double> nowMs = null, Func<DateTime> wallClock = null)
        {
            this.host = host;
            this.nowMs = nowMs ?? DefaultNowMs;
            this.wallClock = wallClock ?? (() => DateTime.Now);
        }

        private static double DefaultNowMs()
        {
            return Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
        }

        // ------------------------------------------------------------------ start / cancel

        public RuntimeTestSession Start(RuntimeTestPlan plan, RuntimeTestOptions options)
        {
            if (IsRunning) throw new InvalidOperationException("A runtime test run is already in progress (" + Session.Plan.Name + ").");
            plan.Validate();
            DiscardPreserved("a new run started");
            Session = new RuntimeTestSession(plan, options ?? RuntimeTestOptions.Full(), wallClock(), nowMs());
            host.Log("[TheNetwork] Runtime regression suite started: " + plan.Name + " (" + plan.Cases.Count + " tests)");
            return Session;
        }

        /// <summary>Stops after the current safe boundary (between steps). The sandbox is discarded, the overrides are already restored.</summary>
        public bool Cancel()
        {
            if (!IsRunning) return false;
            Session.CancelRequested = true;
            return true;
        }

        public void DiscardPreserved(string why)
        {
            if (Preserved == null) return;
            host.Log("[TheNetwork] Discarding the preserved failure of " + Preserved.TestId + " (" + why + ").");
            if (Preserved.Sandbox != null) Preserved.Sandbox.Dispose();
            Preserved = null;
        }

        // ------------------------------------------------------------------ pump

        /// <summary>Runs one slice. Returns true while the run still has work to do.</summary>
        public bool Pump()
        {
            RuntimeTestSession s = Session;
            if (s == null || s.State != RuntimeRunState.Running) return false;
            double sliceStart = nowMs();
            s.SliceBefore = SafeFingerprint();
            s.SliceOpen = true;
            s.Slices++;
            try
            {
                RunSlice(s, sliceStart);
            }
            catch (Exception ex)
            {
                // The runner itself failed: report it as an infrastructure failure and end the run. Never propagate to the game.
                s.Results.Add(Infra("RT-INFRA-000", "Runner internal error", RuntimeTestOutcome.Fail, "The runtime test runner threw outside any test: " + ex.GetType().Name + ": " + ex.Message, null, null, ex));
                s.StoppedEarly = true;
                DiscardCurrent(s);
                Finish(s, RuntimeRunState.Completed);
            }
            finally
            {
                CheckLiveUnchanged(s);
            }
            return s.State == RuntimeRunState.Running;
        }

        /// <summary>
        /// Compares the live fingerprint now with the one taken at the start of this slice (both inside one synchronous call, so the game
        /// cannot have legitimately changed anything between them). Any difference is a test touching live truth.
        /// </summary>
        private void CheckLiveUnchanged(RuntimeTestSession s)
        {
            if (!s.SliceOpen) return; // this slice was already compared (the final slice is compared by Finish, then again by Pump's finally)
            s.SliceOpen = false;
            LiveFingerprint before = s.SliceBefore;
            s.SliceBefore = null;
            LiveFingerprint after = SafeFingerprint();
            if (before == null || after == null)
            {
                // The host had nothing to compare at one end of this slice (a game whose Network the first tick has not started yet).
                if (before != null || after != null) s.LiveChecksSkipped++;
                return;
            }
            s.LiveChecks++;
            List<string> diff = before.Diff(after);
            for (int i = 0; i < diff.Count; i++) s.LiveChanges.Add("slice " + s.Slices + ": " + diff[i]);
        }

        private LiveFingerprint SafeFingerprint()
        {
            try
            {
                return host.CaptureFingerprint();
            }
            catch (Exception ex)
            {
                host.Log("[TheNetwork] Runtime tests: could not fingerprint the live Network (" + ex.GetType().Name + ": " + ex.Message + ").");
                return null;
            }
        }

        private void RunSlice(RuntimeTestSession s, double sliceStart)
        {
            int stepsThisSlice = 0;
            while (s.State == RuntimeRunState.Running)
            {
                if (s.CancelRequested)
                {
                    DiscardCurrent(s);
                    Finish(s, RuntimeRunState.Cancelled);
                    return;
                }
                if (s.Current == null)
                {
                    if (s.StoppedEarly || s.NextIndex >= s.Plan.Cases.Count)
                    {
                        Finish(s, RuntimeRunState.Completed);
                        return;
                    }
                    s.Current = new RuntimeTestContext(s.Plan.Cases[s.NextIndex++], host) { StartMs = nowMs() };
                }
                if (stepsThisSlice > 0 && nowMs() - sliceStart >= s.Options.SliceBudgetMs) return;
                RuntimeStepKind kind = ExecuteStep(s);
                stepsThisSlice++;
                if (kind == RuntimeStepKind.Wait) return;
            }
        }

        // ------------------------------------------------------------------ one step

        private RuntimeStepKind ExecuteStep(RuntimeTestSession s)
        {
            RuntimeTestContext ctx = s.Current;
            RuntimeTestCase c = ctx.Case;
            double stepStart = nowMs();
            if (stepStart - ctx.StartMs > c.TimeoutMs)
            {
                Complete(s, ctx, RuntimeTestOutcome.Fail, "TIMEOUT: the test did not finish within " + c.TimeoutMs + " ms (real time) at step " + ctx.StepIndex, null, null, null, true);
                return RuntimeStepKind.Fail;
            }
            if (ctx.Waits > c.MaxWaits)
            {
                Complete(s, ctx, RuntimeTestOutcome.Fail, "TIMEOUT: the test was still waiting after " + ctx.Waits + " slices at step " + ctx.StepIndex, null, null, null, true);
                return RuntimeStepKind.Fail;
            }
            ctx.StepIndex++;
            s.Steps++;

            RuntimeOverrideSnapshot before = RuntimeOverrideSnapshot.Capture();
            RuntimeOverrideSnapshot.Neutral().Restore();
            ctx.Log.Begin();
            RuntimeStep step = RuntimeStep.Pass();
            Exception error = null;
            List<string> leaked = null;
            try
            {
                if (c.NeedsSandbox && ctx.Sandbox == null)
                {
                    ctx.Sandbox = new RuntimeTestSandbox(c.Id);
                    s.SandboxesCreated++;
                }
                step = c.Run(ctx);
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                // Whatever a test set is never visible to the game, not even for a frame: leaks are recorded, then everything is put back.
                leaked = RuntimeOverrideSnapshot.Capture().Diff(RuntimeOverrideSnapshot.Neutral());
                ctx.Log.End();
                before.Restore();
                if (RuntimeOverrideSnapshot.Capture().Diff(before).Count > 0) s.OverrideRestoreMismatches++;
            }
            ctx.ElapsedMs += nowMs() - stepStart;

            if (error != null)
            {
                CompleteFromException(s, ctx, error);
                return RuntimeStepKind.Fail;
            }
            if (leaked.Count > 0)
            {
                // The step finished normally but left a dev override set: a defect in the test, reported (the overrides are already restored).
                s.OverrideLeaks++;
                ctx.LeakedOverrides.AddRange(leaked);
                Complete(s, ctx, RuntimeTestOutcome.Fail, "The test left dev overrides set (restored by the runner): " + string.Join("; ", leaked.ToArray()), "all dev overrides back to neutral", string.Join("; ", leaked.ToArray()), null, false);
                return RuntimeStepKind.Fail;
            }
            switch (step.Kind)
            {
                case RuntimeStepKind.Pass:
                    Complete(s, ctx, RuntimeTestOutcome.Pass, null, null, null, null, false);
                    return RuntimeStepKind.Pass;
                case RuntimeStepKind.Fail:
                    Complete(s, ctx, RuntimeTestOutcome.Fail, step.Message ?? "the test reported a failure", null, null, null, false);
                    return RuntimeStepKind.Fail;
                case RuntimeStepKind.Skip:
                    Complete(s, ctx, RuntimeTestOutcome.Skip, step.Message, null, null, null, false);
                    return RuntimeStepKind.Skip;
                case RuntimeStepKind.Wait:
                    ctx.Waits++;
                    return RuntimeStepKind.Wait;
                default:
                    return RuntimeStepKind.Continue;
            }
        }

        private void CompleteFromException(RuntimeTestSession s, RuntimeTestContext ctx, Exception error)
        {
            RuntimeAssertionException assertion = error as RuntimeAssertionException;
            if (assertion != null)
            {
                Complete(s, ctx, RuntimeTestOutcome.Fail, assertion.Message, assertion.Expected, assertion.Actual, null, false);
                return;
            }
            RuntimeSkipException skip = error as RuntimeSkipException;
            if (skip != null)
            {
                Complete(s, ctx, RuntimeTestOutcome.Skip, skip.Message, null, null, null, false);
                return;
            }
            RuntimeTimeoutException timeout = error as RuntimeTimeoutException;
            if (timeout != null)
            {
                Complete(s, ctx, RuntimeTestOutcome.Fail, "TIMEOUT: " + timeout.Message, null, null, null, true);
                return;
            }
            Complete(s, ctx, RuntimeTestOutcome.Fail, "The test threw " + error.GetType().Name + ": " + error.Message, null, null, error, false);
        }

        // ------------------------------------------------------------------ results

        private void Complete(RuntimeTestSession s, RuntimeTestContext ctx, RuntimeTestOutcome outcome, string message, string expected, string actual, Exception ex, bool timedOut)
        {
            if (timedOut && ctx.Sandbox != null && !ctx.Sandbox.Disposed)
            {
                // A timeout says where it was: the sandbox's own tick and how many jobs were still pending (the entities are listed with the result).
                message += " [sandbox tick " + ctx.Sandbox.Now + ", " + ctx.Sandbox.Scheduler.Count + " pending jobs]";
            }
            RuntimeTestResult r = new RuntimeTestResult
            {
                Id = ctx.Case.Id, Suite = ctx.Case.Suite, Name = ctx.Case.Name, Outcome = outcome, Message = message,
                Expected = expected, Actual = actual, ElapsedMs = ctx.ElapsedMs, Step = ctx.StepIndex, TimedOut = timedOut, AssertionsPassed = ctx.AssertionsPassed
            };
            if (ex != null)
            {
                r.ExceptionType = ex.GetType().FullName;
                r.ExceptionMessage = ex.Message;
                r.StackTrace = ex.StackTrace;
            }
            r.Notes.AddRange(ctx.Notes);
            r.Warnings.AddRange(ctx.Warnings);
            r.Entities.AddRange(ctx.EntityLabels());
            if (outcome == RuntimeTestOutcome.Pass)
            {
                if (ctx.Warnings.Count > 0)
                {
                    r.Outcome = RuntimeTestOutcome.Warn;
                    r.Message = ctx.Warnings[0];
                }
                else if (ctx.Log.Errors > 0)
                {
                    r.Outcome = RuntimeTestOutcome.Warn;
                    r.Message = "The scratch world logged " + ctx.Log.Errors + " error line(s) while the test passed (see the log lines below).";
                    r.Warnings.Add(r.Message);
                }
                if (r.Outcome == RuntimeTestOutcome.Warn) r.CapturedLog.AddRange(ctx.Log.Lines);
            }
            else if (outcome == RuntimeTestOutcome.Fail)
            {
                r.CapturedLog.AddRange(ctx.Log.Lines);
            }

            bool failed = r.Outcome == RuntimeTestOutcome.Fail;
            if (failed && s.Options.PreserveFailedSandbox && ctx.Sandbox != null && !ctx.Sandbox.Disposed)
            {
                if (Preserved != null)
                {
                    if (Preserved.Sandbox != null) { Preserved.Sandbox.Dispose(); s.SandboxesDiscarded++; if (s.SandboxesPreserved > 0) s.SandboxesPreserved--; }
                }
                Preserved = new RuntimePreservedFailure { TestId = r.Id, Suite = r.Suite, Name = r.Name, Message = r.Message, At = wallClock(), Sandbox = ctx.Sandbox, Context = ctx };
                s.SandboxesPreserved++;
                r.Preserved = true;
            }
            else if (ctx.Sandbox != null && !ctx.Sandbox.Disposed)
            {
                ctx.Sandbox.Dispose();
                s.SandboxesDiscarded++;
            }
            s.Results.Add(r);
            s.Current = null;
            if (failed || s.Options.Verbose) host.Log(RuntimeTestReport.Line(r) + (failed && r.Message != null ? " :: " + r.Message : ""));
            if (failed && s.Options.StopOnFirstFailure) s.StoppedEarly = true;
        }

        private RuntimeTestResult Infra(string id, string name, RuntimeTestOutcome outcome, string message, string expected, string actual, Exception ex = null)
        {
            RuntimeTestResult r = new RuntimeTestResult { Id = id, Suite = "INFRA", Name = name, Outcome = outcome, Message = message, Expected = expected, Actual = actual };
            if (ex != null)
            {
                r.ExceptionType = ex.GetType().FullName;
                r.ExceptionMessage = ex.Message;
                r.StackTrace = ex.StackTrace;
            }
            return r;
        }

        private void DiscardCurrent(RuntimeTestSession s)
        {
            RuntimeTestContext ctx = s.Current;
            if (ctx == null) return;
            if (ctx.Sandbox != null && !ctx.Sandbox.Disposed)
            {
                ctx.Sandbox.Dispose();
                s.SandboxesDiscarded++;
            }
            s.Current = null;
        }

        private void Finish(RuntimeTestSession s, RuntimeRunState state)
        {
            s.ElapsedMs = nowMs() - s.StartedMs;
            CheckLiveUnchanged(s); // the slice that is ending the run counts too
            AddInfrastructureResults(s);
            s.State = state;
            LastFinished = s;
            host.Log(RuntimeTestReport.Summary(s));
            try
            {
                host.RunFinished(s);
            }
            catch (Exception ex)
            {
                host.Log("[TheNetwork] Runtime tests: the finish notification failed (" + ex.GetType().Name + ": " + ex.Message + ").");
            }
        }

        /// <summary>The run's own safety proofs: the live Network untouched, overrides restored, no control job persisted, sandboxes discarded.</summary>
        private void AddInfrastructureResults(RuntimeTestSession s)
        {
            if (s.LiveChecks > 0)
            {
                if (s.LiveChanges.Count == 0) s.Results.Add(Infra("RT-INFRA-001", "Live Network state unchanged by the run", RuntimeTestOutcome.Pass, s.LiveChecks + " slices fingerprinted before and after: actors, contracts, ledgers, operations, history, relations, careers, scheduler and id counters identical." + (s.LiveChecksSkipped > 0 ? " (" + s.LiveChecksSkipped + " slice(s) could not be compared at both ends: the game's own start-up of the Network ran during them.)" : ""), null, null));
                else s.Results.Add(Infra("RT-INFRA-001", "Live Network state unchanged by the run", RuntimeTestOutcome.Fail, "The live Network changed during a slice of this run: " + string.Join("; ", s.LiveChanges.ToArray()), "no change", s.LiveChanges.Count + " change(s)"));
            }
            else
            {
                s.Results.Add(Infra("RT-INFRA-001", "Live Network state unchanged by the run", RuntimeTestOutcome.Skip, s.LiveChecksSkipped > 0 ? "No slice could be compared at both ends: the game's own start-up of the Network ran during this run (" + s.LiveChecksSkipped + " slice(s)). Run again to compare." : "This host has no live Network state to fingerprint.", null, null));
            }
            bool overridesOk = s.OverrideLeaks == 0 && s.OverrideRestoreMismatches == 0;
            s.Results.Add(Infra("RT-INFRA-002", "Dev overrides restored exactly", overridesOk ? RuntimeTestOutcome.Pass : RuntimeTestOutcome.Fail,
                overridesOk ? "Every step restored the previous values of the dev overrides and service toggles (" + s.Steps + " steps)." : s.OverrideLeaks + " step(s) left a dev override set and " + s.OverrideRestoreMismatches + " restore(s) did not match (all were put back).",
                "0 leaks, 0 mismatches", s.OverrideLeaks + " leaks, " + s.OverrideRestoreMismatches + " mismatches"));
            List<string> bad = new List<string>();
            foreach (string kind in host.LiveSchedulerKinds())
            {
                if (kind != null && (kind.StartsWith("devtest", StringComparison.OrdinalIgnoreCase) || kind.StartsWith("runtimetest", StringComparison.OrdinalIgnoreCase))) bad.Add(kind);
            }
            s.Results.Add(Infra("RT-INFRA-003", "No test-control job in the persisted scheduler", bad.Count == 0 ? RuntimeTestOutcome.Pass : RuntimeTestOutcome.Fail,
                bad.Count == 0 ? "The live scheduler holds no job of a runtime-test kind (the runner never uses it)." : "The live persisted scheduler holds runtime-test job kinds: " + string.Join(", ", bad.ToArray()), "none", bad.Count.ToString()));
            int open = s.SandboxesCreated - s.SandboxesDiscarded - s.SandboxesPreserved;
            s.Results.Add(Infra("RT-INFRA-004", "Sandboxes discarded", open == 0 ? RuntimeTestOutcome.Pass : RuntimeTestOutcome.Fail,
                open == 0 ? s.SandboxesCreated + " sandbox(es) created, " + s.SandboxesDiscarded + " discarded, " + s.SandboxesPreserved + " preserved for inspection." : open + " sandbox(es) were neither discarded nor preserved.", "0 open", open + " open"));
        }

        // ------------------------------------------------------------------ status

        public string StatusText()
        {
            RuntimeTestSession s = Session;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[TheNetwork] Runtime tests status");
            if (s == null)
            {
                sb.AppendLine("  no run yet this session");
                sb.AppendLine("  preserved failure: " + (Preserved == null ? "none" : Preserved.TestId + " " + Preserved.Name));
                return sb.ToString().TrimEnd();
            }
            double elapsed = s.State == RuntimeRunState.Running ? nowMs() - s.StartedMs : s.ElapsedMs;
            sb.AppendLine("  suite: " + s.Plan.Name + " (" + s.State + ")");
            sb.AppendLine("  running test: " + (s.Current == null ? "-" : s.Current.Case.Id + " " + s.Current.Case.Name) + ", step " + (s.Current == null ? 0 : s.Current.StepIndex));
            sb.AppendLine("  progress: " + s.NextIndex + " of " + s.Plan.Cases.Count + " tests started, elapsed " + Math.Round(elapsed).ToString("0") + " ms");
            sb.AppendLine("  PASS " + s.Count(RuntimeTestOutcome.Pass) + "  FAIL " + s.Count(RuntimeTestOutcome.Fail) + "  WARN " + s.Count(RuntimeTestOutcome.Warn) + "  SKIP " + s.Count(RuntimeTestOutcome.Skip));
            sb.AppendLine("  stop on first failure: " + (s.Options.StopOnFirstFailure ? "ON" : "off") + ", verbose: " + (s.Options.Verbose ? "on" : "off") + ", preserve failed sandbox: " + (s.Options.PreserveFailedSandbox ? "on" : "off"));
            sb.AppendLine("  preserved failure: " + (Preserved == null ? "none" : Preserved.TestId + " " + Preserved.Name + " (Runtime tests: Inspect preserved failure)"));
            return sb.ToString().TrimEnd();
        }
    }
}
