using LudeonTK;
using RimWorld;
using TheNetwork.Core;
using Verse;

namespace TheNetwork.Diagnostics.RuntimePhysicalTests
{
    /// <summary>
    /// Dev Mode → "The Network (PHYSICAL TESTS: disposable environment only)" (§ 21.2). A separate category, unreachable from Quick smoke
    /// and Full safe regression. Every scenario label starts with its id and name, so the owner never translates letters into menu items.
    /// The ONE exception is RT-PHYX-010's three save-matrix items: RimWorld truncates long labels in its narrow debug menu and the three
    /// were nearly identical, so they start with their unique part (010A / 010B / 010V); the scenario id is still RT-PHYX-010 everywhere else.
    /// Items marked [armed] spend the session arm (one arm = one action); the read-only ones never need it.
    /// </summary>
    public static class PhysicalTestDevActions
    {
        private const string Cat = PhysicalTestIds.Category;

        [DebugAction(Cat, "PHYX — Arm physical tests (type the phrase)...", allowedGameStates = AllowedGameStates.Playing)]
        public static void Arm()
        {
            Find.WindowStack.Add(new Dialog_ArmPhysicalTests());
        }

        [DebugAction(Cat, "PHYX — Show status (read-only)", allowedGameStates = AllowedGameStates.Playing)]
        public static void Status()
        {
            Log.Message(PhysicalTestSession.Status());
        }

        [DebugAction(Cat, "PHYX — Last reports (read-only)", allowedGameStates = AllowedGameStates.Playing)]
        public static void LastReports()
        {
            string text = PhysicalTestSession.LastReports(5);
            if (text == null) Messages.Message("[TheNetwork] No physical test has finished this session.", MessageTypeDefOf.RejectInput, false);
            else Log.Message(text);
        }

        [DebugAction(Cat, "PHYX — Stop current run (preserves evidence)", allowedGameStates = AllowedGameStates.Playing)]
        public static void Stop()
        {
            PhysicalTestSession.Stop();
        }

        [DebugAction(Cat, "PHYX — Remove test map now (vanilla map removal) [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void RemoveTestMap()
        {
            PhysicalTestSession.RemoveTestMapNow();
        }

        [DebugAction(Cat, "PHYX — Cleanup: test map and fixtures [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Cleanup()
        {
            PhysicalTestSession.Cleanup();
        }

        [DebugAction(Cat, "RT-PHYX-001 — First materialization [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx001()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx001FirstMaterialization(rt, id), PhysicalScenarioTable.Get("RT-PHYX-001"));
        }

        [DebugAction(Cat, "RT-PHYX-002 — Normal visitor exit [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx002()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx002NormalExit(rt, id), PhysicalScenarioTable.Get("RT-PHYX-002"));
        }

        [DebugAction(Cat, "RT-PHYX-003 — Downed and recovery [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx003()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx003DownedRecovery(rt, id), PhysicalScenarioTable.Get("RT-PHYX-003"));
        }

        [DebugAction(Cat, "RT-PHYX-004 — Killed: death recorded once [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx004()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx004Killed(rt, id), PhysicalScenarioTable.Get("RT-PHYX-004"));
        }

        [DebugAction(Cat, "RT-PHYX-005 — Test map removed while present [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx005()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx005MapRemoved(rt, id), PhysicalScenarioTable.Get("RT-PHYX-005"));
        }

        [DebugAction(Cat, "RT-PHYX-006 — Same-pawn rematerialization [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx006()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx006Rematerialization(rt, id), PhysicalScenarioTable.Get("RT-PHYX-006"));
        }

        [DebugAction(Cat, "RT-PHYX-007 — Registry, redress and GC protection [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx007()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx007Registry(rt, id), PhysicalScenarioTable.Get("RT-PHYX-007"));
        }

        [DebugAction(Cat, "RT-PHYX-008 — Temporary faction lifecycle [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx008()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx008TemporaryFaction(rt, id), PhysicalScenarioTable.Get("RT-PHYX-008"));
        }

        [DebugAction(Cat, "RT-PHYX-009 — Unsupported custody: dev arrest quarantines [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx009()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx009UnsupportedCustody(rt, id), PhysicalScenarioTable.Get("RT-PHYX-009"));
        }

        [DebugAction(Cat, "010A SAVE — visitor spawned [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx010A()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx010SavePoint(rt, id, false), PhysicalScenarioTable.Get("RT-PHYX-010"));
        }

        [DebugAction(Cat, "010B SAVE — post-map [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx010B()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx010SavePoint(rt, id, true), PhysicalScenarioTable.Get("RT-PHYX-010"));
        }

        [DebugAction(Cat, "010V VERIFY — loaded save", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx010Verify()
        {
            PhysicalTestSession.StartReadOnly((rt, id) => new Phyx010VerifyAfterLoad(rt, id), PhysicalScenarioTable.Get("RT-PHYX-010"));
        }

        [DebugAction(Cat, "RT-PHYX-011 — Role-constrained real pawn generation [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx011()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx011RoleGeneration(rt, id), PhysicalScenarioTable.Get("RT-PHYX-011"));
        }

        [DebugAction(Cat, "RT-PHYX-012 — Truthful aging mechanism [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx012()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx012TruthfulAging(rt, id), PhysicalScenarioTable.Get("RT-PHYX-012"));
        }

        [DebugAction(Cat, "RT-PHYX-015 — Normal-exit M1 regression [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx015()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx015NormalExitM1(rt, id), PhysicalScenarioTable.Get("RT-PHYX-015"));
        }

        [DebugAction(Cat, "RT-PHYX-016 — Map-removal M1 regression [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx016()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx016MapRemovalM1(rt, id), PhysicalScenarioTable.Get("RT-PHYX-016"));
        }
    }
}
