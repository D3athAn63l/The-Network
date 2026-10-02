using System;
using System.Collections.Generic;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Operations;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;

namespace TheNetwork.Diagnostics.RuntimeTests
{
    /// <summary>
    /// What a running test sees: its stable id and seed, its sandbox, its assertions and a small bag for multi-step state. Runtime
    /// only: a context is dropped with its test and is never saved.
    /// </summary>
    public sealed class RuntimeTestContext
    {
        public readonly RuntimeTestCase Case;
        public readonly IRuntimeTestHost Host;

        /// <summary>Derived from the test id and the fixed Phase 2.9 seed: two runs of one build make the same logical choices.</summary>
        public readonly int Seed;

        public readonly RuntimeAssert Assert;

        /// <summary>The isolated world of this test (null for a test that did not ask for one, e.g. a read-only live check).</summary>
        public RuntimeTestSandbox Sandbox { get; internal set; }

        /// <summary>How many times the runner has called this test (1 on the first call).</summary>
        public int StepIndex { get; internal set; }

        /// <summary>Free state between the steps of one test.</summary>
        public readonly Dictionary<string, object> Data = new Dictionary<string, object>();

        public readonly List<string> Notes = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<int> TrackedContracts = new List<int>();
        public readonly List<int> TrackedOperations = new List<int>();
        public readonly List<int> TrackedActors = new List<int>();
        public readonly List<string> LeakedOverrides = new List<string>();
        internal readonly RuntimeLogCapture Log = new RuntimeLogCapture();
        internal double StartMs;
        internal double ElapsedMs;
        internal int Waits;
        internal int AssertionsPassed;

        public string Id => Case.Id;

        public RuntimeTestContext(RuntimeTestCase c, IRuntimeTestHost host)
        {
            Case = c;
            Host = host;
            Seed = RuntimeTestSeeds.ForTest(c.Id);
            Assert = new RuntimeAssert(this);
        }

        /// <summary>A deterministic random stream for this test (never the global Rand).</summary>
        public NetRng Rng(string key)
        {
            return new NetRng(Seed, key);
        }

        /// <summary>
        /// Swaps this test's sandbox for another (a scenario that searches fixed, derived seeds for a suitable synthetic world, the way the
        /// headless suite tries seeds). The previous world is discarded; the runner discards the final one when the test ends.
        /// </summary>
        public RuntimeTestSandbox ReplaceSandbox(RuntimeTestSandbox next)
        {
            if (Sandbox != null) Sandbox.Dispose();
            Sandbox = next;
            return next;
        }

        public RuntimeTestSandbox RequireSandbox()
        {
            if (Sandbox == null) throw new InvalidOperationException(Id + " asked for a sandbox but this test was not declared with one.");
            return Sandbox;
        }

        /// <summary>A line of context for the report (what was done, which variant ran).</summary>
        public void Note(string text)
        {
            if (Notes.Count < 60) Notes.Add(text);
        }

        /// <summary>The test ran but something deserves attention: the result becomes WARN instead of PASS.</summary>
        public void Warn(string text)
        {
            Warnings.Add(text);
        }

        /// <summary>This world does not have what the test needs (a gameplay state, never a defect): ends the test as SKIP.</summary>
        /// <summary>
        /// Declares that this scenario deliberately provokes a production warning or error containing <paramref name="fragment"/> (for example a
        /// commit fault whose handling is the thing under test). Matching lines are kept in the report but do not turn the result into a WARN.
        /// Anything else production code logs still does.
        /// </summary>
        public void ExpectLog(string fragment)
        {
            Log.Expected.Add(fragment);
        }

        public void Skip(string reason)
        {
            throw new RuntimeSkipException(reason);
        }

        public void Track(Contract c) { if (c != null && !TrackedContracts.Contains(c.id.Value)) TrackedContracts.Add(c.id.Value); }
        public void Track(Operation o) { if (o != null && !TrackedOperations.Contains(o.id.Value)) TrackedOperations.Add(o.id.Value); }
        public void Track(NetworkActor a) { if (a != null && !TrackedActors.Contains(a.id.Value)) TrackedActors.Add(a.id.Value); }

        /// <summary>Puts every dev override back to the neutral state a scenario expects (and nothing else). A test that set one calls this before it ends.</summary>
        public void ClearOverrides()
        {
            RuntimeOverrideSnapshot.Neutral().Restore();
        }

        internal List<string> EntityLabels()
        {
            List<string> l = new List<string>();
            foreach (int id in TrackedContracts) l.Add("sandbox Contract " + id);
            foreach (int id in TrackedOperations) l.Add("sandbox Operation " + id);
            foreach (int id in TrackedActors) l.Add("sandbox Actor " + id);
            return l;
        }
    }

    /// <summary>
    /// The tiny assertion helper. A failed assertion ends the test it is in (with expected and actual), never crashes the game and
    /// never reaches Network gameplay. No third-party framework.
    /// </summary>
    public sealed class RuntimeAssert
    {
        private readonly RuntimeTestContext ctx;

        internal RuntimeAssert(RuntimeTestContext ctx)
        {
            this.ctx = ctx;
        }

        private void Passed()
        {
            ctx.AssertionsPassed++;
        }

        public void True(bool condition, string message)
        {
            if (!condition) throw new RuntimeAssertionException(message, "true", "false");
            Passed();
        }

        public void False(bool condition, string message)
        {
            if (condition) throw new RuntimeAssertionException(message, "false", "true");
            Passed();
        }

        public void Equal<T>(T expected, T actual, string message)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new RuntimeAssertionException(message, Str(expected), Str(actual));
            Passed();
        }

        public void NotEqual<T>(T unexpected, T actual, string message)
        {
            if (EqualityComparer<T>.Default.Equals(unexpected, actual)) throw new RuntimeAssertionException(message, "not " + Str(unexpected), Str(actual));
            Passed();
        }

        public void NotNull(object value, string message)
        {
            if (value == null) throw new RuntimeAssertionException(message, "not null", "null");
            Passed();
        }

        public void Null(object value, string message)
        {
            if (value != null) throw new RuntimeAssertionException(message, "null", Str(value));
            Passed();
        }

        public void Zero(long actual, string message)
        {
            if (actual != 0) throw new RuntimeAssertionException(message, "0", actual.ToString());
            Passed();
        }

        public void AtLeast(long minimum, long actual, string message)
        {
            if (actual < minimum) throw new RuntimeAssertionException(message, ">= " + minimum, actual.ToString());
            Passed();
        }

        public void AtMost(long maximum, long actual, string message)
        {
            if (actual > maximum) throw new RuntimeAssertionException(message, "<= " + maximum, actual.ToString());
            Passed();
        }

        public void Fail(string message)
        {
            throw new RuntimeAssertionException(message, "(no failure)", "failed");
        }

        /// <summary>
        /// Advances THIS test's sandbox clock in steps until the condition holds, for at most <paramref name="maxTicks"/> of sandbox
        /// time (default 80 days), then fails as a TIMEOUT. Bounded and synchronous: it never waits in real time and never loops
        /// without a limit.
        /// </summary>
        public void Eventually(Func<bool> condition, string what, int maxTicks = 80 * Ticks.PerDay, int stepTicks = Ticks.PerDay / 4)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            if (!sb.AdvanceUntil(condition, maxTicks, stepTicks)) throw new RuntimeTimeoutException("Timed out waiting for: " + what + " (sandbox tick " + sb.Now + ")");
            Passed();
        }

        private static string Str(object o)
        {
            return o == null ? "null" : o.ToString();
        }
    }
}
