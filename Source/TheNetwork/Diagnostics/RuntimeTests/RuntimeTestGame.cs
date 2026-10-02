using System;
using System.Collections.Generic;
using System.IO;
using RimWorld;
using TheNetwork.Core;
using TheNetwork.Domain.Catalog;
using TheNetwork.Domain.Ports;
using TheNetwork.Integration;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Diagnostics.RuntimeTests
{
    /// <summary>
    /// The RimWorld side of the runtime tests (Dev Mode only). It reads the live Network and the real Defs, writes to the log, shows one
    /// Message when a run ends and exports a report file: it never changes the Network, never sends a letter and never spends silver.
    /// </summary>
    public sealed class GameRuntimeTestHost : IRuntimeTestHost
    {
        public string Name => "RimWorld " + VersionControl.CurrentVersionString;

        public string BuildInfo
        {
            get
            {
                return "The Network " + NetworkWorldComponent.ModVersion + ", save format " + SaveMigrations.Current + ", RimWorld " + VersionControl.CurrentVersionString;
            }
        }

        public LiveFingerprint CaptureFingerprint()
        {
            NetworkRuntime rt = NetworkRuntime.Current;
            // A Network the game has not started yet (a save just loaded, nothing has ticked) has no settled state to compare: its own
            // start-up gate (the same one the first tick, the Network tab and every command go through) is not a test's doing.
            if (rt == null || !rt.Session.IsRunning) return null;
            return LiveFingerprint.Of(rt.Ctx, rt.Root.ids, rt.Scheduler, rt.State.journal);
        }

        public IEnumerable<string> LiveSchedulerKinds()
        {
            NetworkRuntime rt = NetworkRuntime.Current;
            if (rt == null) yield break;
            foreach (ScheduledJob j in rt.Scheduler.AllJobs) yield return j.kind;
        }

        /// <summary>Facts of a real def, copied BY VALUE: a sandbox never holds a reference into the live catalog.</summary>
        public bool TryGetRealItemFacts(string defName, out ItemFacts facts)
        {
            facts = null;
            try
            {
                ItemFacts f = CatalogCache.Get().Facts(defName);
                if (f == null) return false;
                facts = new ItemFacts
                {
                    defName = f.defName, label = f.label, packageId = f.packageId, modName = f.modName, isLudeon = f.isLudeon, techLevel = f.techLevel,
                    marketValue = f.marketValue, stackLimit = f.stackLimit, hasQuality = f.hasQuality, madeFromStuff = f.madeFromStuff, isBuilding = f.isBuilding,
                    isWeapon = f.isWeapon, isApparel = f.isApparel, isResource = f.isResource, craftable = f.craftable, tradeable = f.tradeable, unique = f.unique,
                    mineable = f.mineable, categoryMedian = f.categoryMedian, recipeInputValue = f.recipeInputValue
                };
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Log(string line)
        {
            Verse.Log.Message(line);
        }

        public void RunFinished(RuntimeTestSession s)
        {
            if (s.State == RuntimeRunState.Cancelled)
            {
                Messages.Message("[TheNetwork] Runtime tests cancelled (" + s.Results.Count + " results so far). See Last report.", MessageTypeDefOf.NeutralEvent, false);
            }
            else if (s.Count(RuntimeTestOutcome.Fail) == 0)
            {
                Messages.Message("[TheNetwork] Runtime tests: all " + s.Count(RuntimeTestOutcome.Pass) + " passed (" + s.Count(RuntimeTestOutcome.Warn) + " warnings, " + s.Count(RuntimeTestOutcome.Skip) + " skipped) in " + Math.Round(s.ElapsedMs).ToString("0") + " ms.", MessageTypeDefOf.PositiveEvent, false);
            }
            else
            {
                Messages.Message("[TheNetwork] Runtime tests: " + s.Count(RuntimeTestOutcome.Fail) + " FAILED. See the log / Dev Mode > The Network > Runtime tests: Last report.", MessageTypeDefOf.RejectInput, false);
            }
        }
    }

    /// <summary>
    /// The only entry and exit points of the runtime tests in the running game. Idle cost: <see cref="PumpFrame"/> is one static
    /// reference check per frame while no run is in progress; the runner, its host, every suite and every sandbox are created only when a
    /// Dev Mode action starts a run. Nothing here is saved.
    /// </summary>
    public static class RuntimeTestGame
    {
        private static RuntimeTestRunner runner;

        /// <summary>Non-null only while a run is in progress (the cheap per-frame check).</summary>
        private static RuntimeTestRunner running;

        /// <summary>The world the run was started in: a run never continues into a different game.</summary>
        private static NetworkWorldComponent runningRoot;

        /// <summary>Called once per rendered frame by the world component. One static null check when nothing is running.</summary>
        public static void PumpFrame(NetworkWorldComponent caller)
        {
            RuntimeTestRunner r = running;
            if (r == null) return;
            try
            {
                if (caller != runningRoot)
                {
                    // A different world is now being updated (the player left the game and loaded another): end the run, it belongs to the old one.
                    Verse.Log.Warning("[TheNetwork] A runtime test run was abandoned: a different game was loaded while it was in progress.");
                    r.Cancel();
                }
                if (!r.Pump()) { running = null; runningRoot = null; }
            }
            catch (Exception ex)
            {
                running = null;
                runningRoot = null;
                Verse.Log.Error("[TheNetwork] The runtime test pump failed (the run was stopped; the game and the Network are unaffected): " + ex);
            }
        }

        public static bool IsRunning => running != null;

        private static RuntimeTestRunner Runner
        {
            get
            {
                if (runner == null) runner = new RuntimeTestRunner(new GameRuntimeTestHost());
                return runner;
            }
        }

        public static bool Start(Func<IRuntimeTestHost, RuntimeTestPlan> plan, RuntimeTestOptions options)
        {
            if (running != null)
            {
                Messages.Message("[TheNetwork] A runtime test run is already in progress (Runtime tests: Status, or Cancel current run).", MessageTypeDefOf.RejectInput, false);
                return false;
            }
            if (NetworkWorldComponent.Instance == null)
            {
                Messages.Message("[TheNetwork] No Network world component in this game.", MessageTypeDefOf.RejectInput, false);
                return false;
            }
            RuntimeTestRunner r = Runner;
            r.Start(plan(new GameRuntimeTestHost()), options);
            runningRoot = NetworkWorldComponent.Instance;
            running = r;
            return true;
        }

        public static void Cancel()
        {
            if (runner != null && runner.Cancel()) Messages.Message("[TheNetwork] Cancelling the runtime test run at the next safe boundary.", MessageTypeDefOf.NeutralEvent, false);
            else Messages.Message("[TheNetwork] No runtime test run is in progress.", MessageTypeDefOf.RejectInput, false);
        }

        public static string StatusText()
        {
            return runner == null ? "[TheNetwork] Runtime tests status\n  no run yet this session" : runner.StatusText();
        }

        public static RuntimeTestSession Last => runner?.LastFinished;

        public static string LastReportText()
        {
            RuntimeTestSession s = Last;
            if (s == null) return null;
            return RuntimeTestReport.Full(s, new GameRuntimeTestHost().BuildInfo);
        }

        /// <summary>Writes the last report under SaveDataFolderPath/TheNetwork/. Returns the path, or null when there is nothing to export.</summary>
        public static string Export()
        {
            string text = LastReportText();
            if (text == null) return null;
            string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "TheNetwork");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, RuntimeTestReport.FileName(Last.StartedAt));
            File.WriteAllText(path, text);
            return path;
        }

        public static string InspectPreserved()
        {
            return runner?.Preserved?.Describe();
        }
    }
}
