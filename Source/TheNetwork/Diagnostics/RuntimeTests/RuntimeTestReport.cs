using System;
using System.Collections.Generic;
using System.Text;

namespace TheNetwork.Diagnostics.RuntimeTests
{
    /// <summary>Plain-text reports of a run. Stable formatting: the same session always prints the same text.</summary>
    public static class RuntimeTestReport
    {
        public static string Tag(RuntimeTestOutcome o)
        {
            switch (o)
            {
                case RuntimeTestOutcome.Pass: return "PASS";
                case RuntimeTestOutcome.Fail: return "FAIL";
                case RuntimeTestOutcome.Warn: return "WARN";
                default: return "SKIP";
            }
        }

        /// <summary>"[PASS] RT-PROC-001 Post to accepted (12 ms)"</summary>
        public static string Line(RuntimeTestResult r)
        {
            return "[" + Tag(r.Outcome) + "] " + r.Id + " " + r.Name + " (" + Ms(r.ElapsedMs) + ")";
        }

        public static string Detail(RuntimeTestResult r, bool includeStack)
        {
            StringBuilder sb = new StringBuilder();
            if (!string.IsNullOrEmpty(r.Message)) sb.AppendLine("    " + r.Message);
            if (r.Expected != null) sb.AppendLine("    Expected: " + r.Expected);
            if (r.Actual != null) sb.AppendLine("    Actual:   " + r.Actual);
            if (r.Step > 0 && r.Outcome == RuntimeTestOutcome.Fail) sb.AppendLine("    Step:     " + r.Step + (r.TimedOut ? " (TIMEOUT)" : ""));
            foreach (string e in r.Entities) sb.AppendLine("    Entity:   " + e);
            foreach (string w in r.Warnings) sb.AppendLine("    Warning:  " + w);
            if (r.ExceptionType != null) sb.AppendLine("    Exception: " + r.ExceptionType + ": " + r.ExceptionMessage);
            foreach (string n in r.Notes) sb.AppendLine("    Note:     " + n);
            if (r.Preserved) sb.AppendLine("    The failed sandbox is preserved (Runtime tests: Inspect preserved failure).");
            if (r.Outcome == RuntimeTestOutcome.Fail)
            {
                foreach (string l in r.CapturedLog) sb.AppendLine("    Log:      " + l);
                if (includeStack && !string.IsNullOrEmpty(r.StackTrace)) sb.AppendLine("    --- stack trace ---").AppendLine(Indent(r.StackTrace, "    "));
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>The closing summary (also logged when a run completes).</summary>
        public static string Summary(RuntimeTestSession s)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[TheNetwork] Runtime regression " + StateWord(s));
            sb.AppendLine("PASS: " + s.Count(RuntimeTestOutcome.Pass));
            sb.AppendLine("FAIL: " + s.Count(RuntimeTestOutcome.Fail));
            sb.AppendLine("WARN: " + s.Count(RuntimeTestOutcome.Warn));
            sb.AppendLine("SKIP: " + s.Count(RuntimeTestOutcome.Skip));
            sb.Append("Elapsed: " + Ms(s.ElapsedMs));
            return sb.ToString();
        }

        /// <summary>The whole report: header, every result in order with the detail of everything that is not a plain pass.</summary>
        public static string Full(RuntimeTestSession s, string buildInfo)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[TheNetwork] Runtime regression report");
            sb.AppendLine("Suite:    " + s.Plan.Name);
            sb.AppendLine("Build:    " + (buildInfo ?? "-"));
            sb.AppendLine("Started:  " + s.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Options:  stopOnFirstFailure=" + s.Options.StopOnFirstFailure + " verbose=" + s.Options.Verbose + " preserveFailedSandbox=" + s.Options.PreserveFailedSandbox);
            sb.AppendLine("State:    " + StateWord(s).ToUpperInvariant() + (s.StoppedEarly ? " (stopped at the first failure: " + (s.Plan.Cases.Count - s.NextIndex) + " tests not run)" : "") + " (" + s.Results.Count + " results of " + s.Plan.Cases.Count + " planned tests plus infrastructure checks)");
            sb.AppendLine("PASS: " + s.Count(RuntimeTestOutcome.Pass) + "  FAIL: " + s.Count(RuntimeTestOutcome.Fail) + "  WARN: " + s.Count(RuntimeTestOutcome.Warn) + "  SKIP: " + s.Count(RuntimeTestOutcome.Skip) + "  Elapsed: " + Ms(s.ElapsedMs));
            sb.AppendLine();
            for (int i = 0; i < s.Results.Count; i++)
            {
                RuntimeTestResult r = s.Results[i];
                sb.AppendLine(Line(r));
                if (r.Outcome != RuntimeTestOutcome.Pass || s.Options.Verbose)
                {
                    string d = Detail(r, true);
                    if (d.Length > 0) sb.AppendLine(d);
                }
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>runtime-tests-YYYYMMDD-HHMMSS.txt</summary>
        public static string FileName(DateTime when)
        {
            return "runtime-tests-" + when.ToString("yyyyMMdd-HHmmss") + ".txt";
        }

        private static string StateWord(RuntimeTestSession s)
        {
            if (s.State == RuntimeRunState.Cancelled) return "CANCELLED";
            if (s.State == RuntimeRunState.Running) return "running";
            return s.StoppedEarly ? "stopped at the first failure" : "complete";
        }

        private static string Ms(double ms)
        {
            return Math.Round(ms).ToString("0") + " ms";
        }

        private static string Indent(string text, string prefix)
        {
            return prefix + text.Replace("\r\n", "\n").Replace("\n", "\n" + prefix);
        }
    }
}
