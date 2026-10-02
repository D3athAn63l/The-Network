// Headless test runner for The Network (DEBUGGING § 6, IMPLEMENTATION_PHASES A9).
//
// Runs the REAL TheNetwork.dll against the REAL RimWorld 1.6 Assembly-CSharp under Mono, outside Unity.
// What is real:  every Network type and service under test, and vanilla Scribe (XML save/load) where a
//                test round-trips data.
// What is fake:  the adapter ports (comms, payment, catalog facts, world facts, sites) — these tests prove
//                Network logic only. They are NOT evidence of vanilla behaviour (sites, maps, caravans,
//                letters, silver): that needs the runtime spikes in docs/spikes/.
// Test-only:     0Harmony stubs a few Unity-only Verse calls (Log output, DeepProfiler) so Scribe can run
//                outside the player. TheNetwork.dll itself references no Harmony (checked below).
//
// Build/run: Tests/run-tests.sh
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Tests
{
    public static class T
    {
        public static int checks, failures;
        public static string current;
        public static readonly List<string> failed = new List<string>();
        public static readonly List<string> vanillaLog = new List<string>();
        public static readonly List<string> netLog = new List<string>();

        public static void Check(bool ok, string what)
        {
            checks++;
            if (ok) return;
            failures++;
            string line = current + ": " + what;
            failed.Add(line);
            Console.WriteLine("  FAIL " + what);
        }

        public static void Eq<TV>(TV expected, TV actual, string what)
        {
            Check(EqualityComparer<TV>.Default.Equals(expected, actual), what + " (expected " + expected + ", got " + actual + ")");
        }

        public static void Throws(Action a, string what)
        {
            try
            {
                a();
            }
            catch (Exception)
            {
                Check(true, what);
                return;
            }
            Check(false, what + " (no exception)");
        }
    }

    public static class TestMain
    {
        private static bool LogPrefix(object __0)
        {
            T.vanillaLog.Add(Convert.ToString(__0));
            return false;
        }

        private static bool Skip()
        {
            return false;
        }

        /// <summary>
        /// In the game TheNetwork.dll is a running mod's assembly, so GenTypes knows its types. Outside the game
        /// nothing is loaded as a mod; this postfix gives GenTypes the same view (test environment only).
        /// </summary>
        private static void TypePostfix(string typeName, ref Type __result)
        {
            if (__result != null || typeName == null) return;
            Assembly net = typeof(NetScheduler).Assembly;
            __result = net.GetType(typeName, false) ?? Type.GetType(typeName + ", " + net.GetName().Name, false);
        }

        private static HarmonyMethod Stub(string name)
        {
            return new HarmonyMethod(typeof(TestMain).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static void InstallStubs()
        {
            Harmony h = new Harmony("TheNetwork.Tests");
            foreach (MethodInfo m in typeof(Log).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if ((m.Name == "Message" || m.Name == "Warning" || m.Name == "Error" || m.Name == "ErrorOnce" || m.Name == "WarningOnce") && m.GetParameters().Length >= 1)
                {
                    h.Patch(m, Stub("LogPrefix"));
                }
            }
            h.Patch(AccessTools.Method(typeof(DeepProfiler), "Start"), Stub("Skip"));
            h.Patch(AccessTools.Method(typeof(DeepProfiler), "End"), Stub("Skip"));
            h.Patch(AccessTools.Method(typeof(GenTypes), "GetTypeInAnyAssembly"), postfix: Stub("TypePostfix"));
        }

        public static int Main(string[] args)
        {
            InstallStubs();
            NetLog.Sink = (level, line) => T.netLog.Add(level + " " + line);
            string filter = args.Length > 0 ? args[0] : null;

            List<KeyValuePair<string, Action>> tests = new List<KeyValuePair<string, Action>>();
            KernelTests.Register(tests);
            SettingsTests.Register(tests);
            CatalogTests.Register(tests);
            IntelTests.Register(tests);
            OpportunityTests.Register(tests);
            HistoryTests.Register(tests);
            PersistenceTests.Register(tests);
            ContractorTests.Register(tests);
            RelationsKnowledgeTests.Register(tests);
            ProcurementTests.Register(tests);
            MoneyLineageTests.Register(tests);
            CorrectionTests.Register(tests);
            SpatialTests.Register(tests);
            FieldLogTests.Register(tests);
            SpatialCorrectionTests.Register(tests);
            CharterTests.Register(tests);
            CareerTests.Register(tests);
            RuntimeRunnerTests.Register(tests);
            StartupTests.Register(tests);

            int ran = 0;
            foreach (KeyValuePair<string, Action> t in tests)
            {
                if (filter != null && t.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                T.current = t.Key;
                int before = T.failures;
                NetLog.ResetOnceKeys();
                StateVersion.Bump();
                try
                {
                    t.Value();
                }
                catch (Exception ex)
                {
                    T.failures++;
                    T.failed.Add(t.Key + ": EXCEPTION " + ex);
                    Console.WriteLine("  EXCEPTION " + ex);
                }
                Console.WriteLine((T.failures == before ? "ok   " : "FAIL ") + t.Key);
                ran++;
            }
            Console.WriteLine();
            Console.WriteLine(ran + " tests, " + T.checks + " checks, " + T.failures + " failures");
            foreach (string f in T.failed) Console.WriteLine("  - " + f);
            return T.failures == 0 ? 0 : 1;
        }
    }
}
