using System;
using System.Collections.Generic;
using TheNetwork.Kernel;

namespace TheNetwork.Diagnostics.RuntimeTests
{
    // Phase 2.9: the in-game runtime regression facility (RUNTIME_TESTING.md). A SMALL framework, developer-only:
    // nothing here is saved, nothing here is part of gameplay, and nothing here runs unless a Dev Mode action
    // starts it. The model is Verse-free so the headless suite can test the runner itself.

    public enum RuntimeTestOutcome
    {
        Pass,
        Fail,
        Warn,
        Skip
    }

    public enum RuntimeRunState
    {
        Running,
        Completed,
        Cancelled
    }

    public enum RuntimeStepKind
    {
        /// <summary>The test is finished and everything held.</summary>
        Pass,

        /// <summary>Run the next step now (within the same slice if time remains).</summary>
        Continue,

        /// <summary>Not ready: ask again on a later slice (a bounded wait, never a loop).</summary>
        Wait,

        Fail,

        /// <summary>The condition this test needs is not there (a gameplay state, never a defect).</summary>
        Skip
    }

    /// <summary>What one step of a test says: continue, wait, or end the test.</summary>
    public struct RuntimeStep
    {
        public RuntimeStepKind Kind;
        public string Message;

        public static RuntimeStep Pass() { return new RuntimeStep { Kind = RuntimeStepKind.Pass }; }
        public static RuntimeStep Continue() { return new RuntimeStep { Kind = RuntimeStepKind.Continue }; }
        public static RuntimeStep Wait(string what) { return new RuntimeStep { Kind = RuntimeStepKind.Wait, Message = what }; }
        public static RuntimeStep Fail(string message) { return new RuntimeStep { Kind = RuntimeStepKind.Fail, Message = message }; }
        public static RuntimeStep Skip(string reason) { return new RuntimeStep { Kind = RuntimeStepKind.Skip, Message = reason }; }

        /// <summary>Continue when the condition holds, otherwise wait for a later slice (the runner enforces the deadline).</summary>
        public static RuntimeStep Until(bool condition, string what)
        {
            return condition ? Continue() : Wait(what);
        }
    }

    /// <summary>A failed assertion: ends the test it happened in. Never escapes the runner.</summary>
    public sealed class RuntimeAssertionException : Exception
    {
        public readonly string Expected;
        public readonly string Actual;

        public RuntimeAssertionException(string message, string expected, string actual) : base(message)
        {
            Expected = expected;
            Actual = actual;
        }
    }

    /// <summary>The state this test needs is not present in this world. Ends the test as SKIP.</summary>
    public sealed class RuntimeSkipException : Exception
    {
        public RuntimeSkipException(string reason) : base(reason) { }
    }

    /// <summary>A bounded wait ran out. Ends the test as a FAIL (TIMEOUT).</summary>
    public sealed class RuntimeTimeoutException : Exception
    {
        public RuntimeTimeoutException(string what) : base(what) { }
    }

    /// <summary>The fixed seed of every runtime test: stable, so two runs of one build agree.</summary>
    public static class RuntimeTestSeeds
    {
        public const int Phase29 = 20290;

        public static int ForTest(string testId)
        {
            return NetHash.Combine(Phase29, testId ?? "");
        }
    }

    /// <summary>One stable, addressable test (RT-SUITE-NNN).</summary>
    public sealed class RuntimeTestCase
    {
        public const int DefaultTimeoutMs = 20000;
        public const int DefaultMaxWaits = 4000;

        public readonly string Id;
        public readonly string Suite;
        public readonly string Name;
        public readonly Func<RuntimeTestContext, RuntimeStep> Run;

        /// <summary>The runner builds an isolated sandbox for this test (and discards it afterwards).</summary>
        public bool NeedsSandbox;

        /// <summary>Real milliseconds a test may take, in total, before it is failed as TIMEOUT.</summary>
        public int TimeoutMs = DefaultTimeoutMs;

        /// <summary>Slices a test may spend waiting before it is failed as TIMEOUT.</summary>
        public int MaxWaits = DefaultMaxWaits;

        private RuntimeTestCase(string suite, string id, string name, Func<RuntimeTestContext, RuntimeStep> run, bool sandbox)
        {
            Suite = suite;
            Id = id;
            Name = name;
            Run = run;
            NeedsSandbox = sandbox;
        }

        /// <summary>A test that finishes in one step: it passes when its body returns without a failed assertion.</summary>
        public static RuntimeTestCase Immediate(string suite, string id, string name, Action<RuntimeTestContext> body, bool sandbox = false)
        {
            return new RuntimeTestCase(suite, id, name, ctx => { body(ctx); return RuntimeStep.Pass(); }, sandbox);
        }

        /// <summary>
        /// A multi-step test: the runner calls <paramref name="step"/> again (<c>ctx.StepIndex</c> counts the calls) until it
        /// returns Pass, Fail or Skip. Wait yields to a later slice; Continue goes on at once.
        /// </summary>
        public static RuntimeTestCase Stepped(string suite, string id, string name, Func<RuntimeTestContext, RuntimeStep> step, bool sandbox = false)
        {
            return new RuntimeTestCase(suite, id, name, step, sandbox);
        }
    }

    /// <summary>An ordered set of tests. Order is the order given: runs are deterministic.</summary>
    public sealed class RuntimeTestPlan
    {
        public readonly string Name;
        public readonly List<RuntimeTestCase> Cases = new List<RuntimeTestCase>();

        public RuntimeTestPlan(string name)
        {
            Name = name;
        }

        public RuntimeTestPlan Add(RuntimeTestCase c)
        {
            Cases.Add(c);
            return this;
        }

        public RuntimeTestPlan AddRange(IEnumerable<RuntimeTestCase> cases)
        {
            Cases.AddRange(cases);
            return this;
        }

        /// <summary>Ids are stable and unique. A broken plan is refused before anything runs.</summary>
        public void Validate()
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < Cases.Count; i++)
            {
                RuntimeTestCase c = Cases[i];
                if (c == null || string.IsNullOrEmpty(c.Id) || string.IsNullOrEmpty(c.Suite) || string.IsNullOrEmpty(c.Name) || c.Run == null)
                {
                    throw new InvalidOperationException("Runtime test #" + i + " in '" + Name + "' is incomplete (it needs an id, a suite, a name and a body).");
                }
                if (!seen.Add(c.Id)) throw new InvalidOperationException("Duplicate runtime test id '" + c.Id + "' in '" + Name + "'.");
            }
        }
    }

    public sealed class RuntimeTestOptions
    {
        public bool StopOnFirstFailure;
        public bool Verbose;
        public bool PreserveFailedSandbox = true;

        /// <summary>The most real time one pump may spend before it yields to the game (a frame, not a pause).</summary>
        public double SliceBudgetMs = 8.0;

        public static RuntimeTestOptions Quick()
        {
            return new RuntimeTestOptions { StopOnFirstFailure = true, Verbose = false, PreserveFailedSandbox = true };
        }

        public static RuntimeTestOptions Full()
        {
            return new RuntimeTestOptions { StopOnFirstFailure = false, Verbose = true, PreserveFailedSandbox = true };
        }

        public static RuntimeTestOptions Live()
        {
            return new RuntimeTestOptions { StopOnFirstFailure = false, Verbose = true, PreserveFailedSandbox = false };
        }
    }

    /// <summary>What one test produced. Stable id, an explanation a person can act on, and the context for a failure.</summary>
    public sealed class RuntimeTestResult
    {
        public string Id;
        public string Suite;
        public string Name;
        public RuntimeTestOutcome Outcome;
        public string Message;
        public string Expected;
        public string Actual;
        public string ExceptionType;
        public string ExceptionMessage;
        public string StackTrace;
        public double ElapsedMs;
        public int Step;
        public bool TimedOut;
        public int AssertionsPassed;
        public bool Preserved;
        public readonly List<string> Entities = new List<string>();
        public readonly List<string> Notes = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> CapturedLog = new List<string>();
    }
}
