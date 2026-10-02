using LudeonTK;
using RimWorld;
using TheNetwork.Diagnostics.RuntimeTests;
using Verse;

namespace TheNetwork.Diagnostics
{
    /// <summary>
    /// Runtime regression tests (Phase 2.9, RUNTIME_TESTING.md): Dev Mode, The Network, "Runtime tests: ...". Developer infrastructure only:
    /// no normal player tab, no gameplay button, no setting. The safe runs execute in isolated scratch worlds and read the live game
    /// strictly read-only: they never start, reconcile or repair the live Network (a never-started Network gives SKIP), and a before/after
    /// fingerprint of its durable data plus selected colony state (silver, cargo, world objects, letters) checks that nothing moved.
    /// </summary>
    public static partial class NetworkDevActions
    {
        [DebugAction(Cat, "Runtime tests: Quick smoke", allowedGameStates = AllowedGameStates.Playing)]
        public static void RuntimeQuickSmoke()
        {
            RuntimeTestGame.Start(RuntimeTestPlans.QuickSmoke, RuntimeTestOptions.Quick());
        }

        [DebugAction(Cat, "Runtime tests: Full safe regression", allowedGameStates = AllowedGameStates.Playing)]
        public static void RuntimeFullSafe()
        {
            RuntimeTestGame.Start(RuntimeTestPlans.FullSafe, RuntimeTestOptions.Full());
        }

        [DebugAction(Cat, "Runtime tests: Live integration scan", allowedGameStates = AllowedGameStates.Playing)]
        public static void RuntimeLiveScan()
        {
            RuntimeTestGame.Start(RuntimeTestPlans.LiveScan, RuntimeTestOptions.Live());
        }

        [DebugAction(Cat, "Runtime tests: Status", allowedGameStates = AllowedGameStates.Playing)]
        public static void RuntimeStatus()
        {
            Log.Message(RuntimeTestGame.StatusText());
        }

        [DebugAction(Cat, "Runtime tests: Cancel current run", allowedGameStates = AllowedGameStates.Playing)]
        public static void RuntimeCancel()
        {
            RuntimeTestGame.Cancel();
        }

        [DebugAction(Cat, "Runtime tests: Last report", allowedGameStates = AllowedGameStates.Playing)]
        public static void RuntimeLastReport()
        {
            string text = RuntimeTestGame.LastReportText();
            if (text == null) Messages.Message("[TheNetwork] No runtime test report yet this session.", MessageTypeDefOf.RejectInput, false);
            else Log.Message(text);
        }

        [DebugAction(Cat, "Runtime tests: Export last report", allowedGameStates = AllowedGameStates.Playing)]
        public static void RuntimeExport()
        {
            string path = RuntimeTestGame.Export();
            if (path == null) Messages.Message("[TheNetwork] No runtime test report to export yet.", MessageTypeDefOf.RejectInput, false);
            else Log.Message("[TheNetwork] Runtime test report written to " + path);
        }

        [DebugAction(Cat, "Runtime tests: Inspect preserved failure", allowedGameStates = AllowedGameStates.Playing)]
        public static void RuntimeInspectFailure()
        {
            string text = RuntimeTestGame.InspectPreserved();
            if (text == null) Messages.Message("[TheNetwork] No preserved failure (a failed sandbox is kept, in memory only, until the next run).", MessageTypeDefOf.NeutralEvent, false);
            else Log.Message(text);
        }
    }
}
