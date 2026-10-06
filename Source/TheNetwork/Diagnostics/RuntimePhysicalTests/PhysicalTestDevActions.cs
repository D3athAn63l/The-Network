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

        [DebugAction(Cat, "RT-PHYX-009 — Unsupported custody: dev arrest quarantines — RETIRED in 3.2A, run RT-PHYX-020 (read-only)", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx009()
        {
            PhysicalTestSession.Retired(PhysicalScenarioTable.Get("RT-PHYX-009"));
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

        // ---- Phase 3.2A: held custody (S21). Each one leaves its own test map; 021–024 deliberately leave the person held by vanilla.

        [DebugAction(Cat, "RT-PHYX-020 — Arrest: held once, then freed and stored [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx020()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx020Arrest(rt, id), PhysicalScenarioTable.Get("RT-PHYX-020"));
        }

        [DebugAction(Cat, "RT-PHYX-021 — Recruitment: Defected, never stored [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx021()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx021Recruitment(rt, id), PhysicalScenarioTable.Get("RT-PHYX-021"));
        }

        [DebugAction(Cat, "RT-PHYX-022 — Enslavement: held as a slave [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx022()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx022Enslavement(rt, id), PhysicalScenarioTable.Get("RT-PHYX-022"));
        }

        [DebugAction(Cat, "RT-PHYX-023 — Kidnapped, then recruited by the captor [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx023()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx023Kidnapped(rt, id), PhysicalScenarioTable.Get("RT-PHYX-023"));
        }

        [DebugAction(Cat, "RT-PHYX-024 — Death while held [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx024()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx024DeathWhileHeld(rt, id), PhysicalScenarioTable.Get("RT-PHYX-024"));
        }

        [DebugAction(Cat, "RT-PHYX-025 — Held people after save/load (read-only)", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx025Verify()
        {
            PhysicalTestSession.StartReadOnly((rt, id) => new Phyx025HeldVerify(rt, id), PhysicalScenarioTable.Get("RT-PHYX-025"));
        }

        // Phase 3.2B: owned group fixtures. TestSite visibility is explicitly synthetic; these actions never use a home map.

        [DebugAction(Cat, "RT-PHYX-026 — Small crew first visit [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx026()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx026SmallFirst(rt, id), PhysicalScenarioTable.Get("RT-PHYX-026"));
        }

        [DebugAction(Cat, "RT-PHYX-027 — Small crew same-pawn second visit [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx027()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx027SmallSecond(rt, id), PhysicalScenarioTable.Get("RT-PHYX-027"));
        }

        [DebugAction(Cat, "RT-PHYX-028 — Large company ordinary presence [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx028()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx028LargePresence(rt, id), PhysicalScenarioTable.Get("RT-PHYX-028"));
        }

        [DebugAction(Cat, "RT-PHYX-029 — Anonymous arrest and atomic promotion [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx029()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx029LargeCapture(rt, id), PhysicalScenarioTable.Get("RT-PHYX-029"));
        }

        [DebugAction(Cat, "030A SAVE — concretized group [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx030A()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx030GroupSave(rt, id, false), PhysicalScenarioTable.Get("RT-PHYX-030"));
        }

        [DebugAction(Cat, "030B SAVE — anonymous arrest Pending [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx030B()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx030GroupSave(rt, id, true), PhysicalScenarioTable.Get("RT-PHYX-030"));
        }

        [DebugAction(Cat, "030V VERIFY — loaded group (read-only)", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx030V()
        {
            PhysicalTestSession.StartReadOnly((rt, id) => new Phyx030GroupVerify(rt, id), PhysicalScenarioTable.Get("RT-PHYX-030"));
        }

        [DebugAction(Cat, "RT-PHYX-031 — Medic organizational succession [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx031()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx031RoleSuccession(rt, id), PhysicalScenarioTable.Get("RT-PHYX-031"));
        }

        [DebugAction(Cat, "032A BUILD — 150 retained [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx032A()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx032RetentionBuild(rt, id, 150), PhysicalScenarioTable.Get("RT-PHYX-032"));
        }

        [DebugAction(Cat, "032B BUILD — 300 retained [armed]", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx032B()
        {
            PhysicalTestSession.Start((rt, id) => new Phyx032RetentionBuild(rt, id, 300), PhysicalScenarioTable.Get("RT-PHYX-032"));
        }

        [DebugAction(Cat, "032V OBSERVE — retained load (read-only)", allowedGameStates = AllowedGameStates.Playing)]
        public static void Phyx032V()
        {
            PhysicalTestSession.StartReadOnly((rt, id) => new Phyx032RetentionVerify(rt, id), PhysicalScenarioTable.Get("RT-PHYX-032"));
        }
    }
}
