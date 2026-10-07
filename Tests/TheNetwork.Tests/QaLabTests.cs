using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TheNetwork.Diagnostics.RuntimePhysicalTests;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Physical;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Tests
{
    public static class QaLabTests
    {
        public static void Register(List<KeyValuePair<string, Action>> tests)
        {
            tests.Add(new KeyValuePair<string, Action>("QaLab.Scope_RejectsEveryUnsafeTarget", Scope));
            tests.Add(new KeyValuePair<string, Action>("QaLab.Reset_AnyPawnAndActiveRunRefuse", ResetState));
            tests.Add(new KeyValuePair<string, Action>("QaLab.Reset_IncompleteMissingPawnAndUnreleasedBindingsBlock", Episodes));
            tests.Add(new KeyValuePair<string, Action>("QaLab.Reset_CompletedHistoryAndOtherMapsDoNotBlock", History));
            tests.Add(new KeyValuePair<string, Action>("QaLab.Reset_RetainedPhysicalObligationsBlock", Retained));
            tests.Add(new KeyValuePair<string, Action>("QaLab.Layout_RoomCampPrisonAndEdges", Layout));
            tests.Add(new KeyValuePair<string, Action>("QaLab.DevActions_CreateResetAndRuntimeAreSeparate", Actions));
            tests.Add(new KeyValuePair<string, Action>("QaLab.Reset_FullPreflightBeforeAnyWrite", Preflight));
            tests.Add(new KeyValuePair<string, Action>("QaLab.Validate_TransitiveHelpersHaveNoMapWrites", ReadOnly));
            tests.Add(new KeyValuePair<string, Action>("QaLab.Runtime_RefusesBeforeFixtureAndKeepsCustodyFlow", Runtime));
        }

        private static string Source(string file) => PhysicalLifecycleTests.Code(PhysicalLifecycleTests.Src("Diagnostics/RuntimePhysicalTests/" + file));
        private static string Body(string code, string signature)
        {
            int start = code.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) throw new Exception("missing source boundary: " + signature);
            int open = code.IndexOf('{', start), depth = 0;
            for (int i = open; i < code.Length; i++)
            {
                if (code[i] == '{') depth++;
                if (code[i] == '}' && --depth == 0) return code.Substring(open, i - open + 1);
            }
            throw new Exception("unterminated source boundary: " + signature);
        }

        private static void Scope()
        {
            T.Eq(null, QaLabRules.ScopeRefusal(true, true, true, true, false, true, 60, 60), "exact dedicated map accepted");
            T.Eq(null, QaLabRules.ScopeRefusal(true, true, true, true, false, true, 100, 100), "legacy map is normalized without resizing");
            for (int i = 0; i < 6; i++)
            {
                bool[] f = { true, true, true, true, false, true }; f[i] = !f[i];
                T.Check(QaLabRules.ScopeRefusal(f[0], f[1], f[2], f[3], f[4], f[5], 60, 60) != null,
                    "null/ordinary/subclass/faction/home/widened-def target refused: " + i);
            }
            foreach (IntVec2 size in new[] { new IntVec2(59, 60), new IntVec2(60, 100), new IntVec2(80, 80), new IntVec2(100, 60) })
                T.Check(!QaLabRules.ApprovedSize(size.x, size.z), "unapproved dimensions refused: " + size);
            string report;
            T.Check(!QaLab.Validate(null, out report) && report != null, "real null-map validation fails closed");
        }

        private static void ResetState()
        {
            T.Eq(null, QaLabRules.ResetStateRefusal(false, true, 0), "only proven zero Pawns permits reset");
            foreach (int count in new[] { -1, 1, 2, 100, int.MaxValue })
                T.Check(QaLabRules.ResetStateRefusal(false, true, count) != null, "every positive or unknown Pawn census refuses: " + count);
            T.Check(QaLabRules.ResetStateRefusal(true, true, 0) != null, "executing physical run blocks reset");
            T.Check(QaLabRules.ResetStateRefusal(false, false, 0) != null, "unresolved ownership cannot authorize a reset");
        }

        private static PhysicalEpisode Episode(EpisodeState state = EpisodeState.Open)
        {
            return new PhysicalEpisode { id = new EpisodeId(22), state = state, whereMapId = 4,
                whereTile = new TileRef { tileId = 7, layerId = 0 }, members = new List<EpisodeMember> { new EpisodeMember { slot = 0, pawn = new PawnRef { thingIdNumber = 123 } } } };
        }

        private static void Episodes()
        {
            PhysicalEpisode e = Episode();
            TileRef tile = new TileRef { tileId = 7 };
            T.Check(QaLabRules.EpisodeBlocksReset(e, 4, tile, false), "missing/unspawned bound Pawn cannot hide target-map ownership");
            e.whereMapId = -1;
            T.Check(QaLabRules.EpisodeBlocksReset(e, 4, tile, false), "tile-only incomplete target still blocks");
            e.whereMapId = 9;
            T.Check(QaLabRules.EpisodeBlocksReset(e, 4, tile, true), "actual member on map overrides differing Episode target");
            e = Episode(EpisodeState.Closed);
            T.Check(QaLabRules.EpisodeBlocksReset(e, 4, tile, false), "CLOSED with unreleased bindings fails closed");
            T.Eq(EpisodeState.Closed, e.state, "policy never reconciles the unfinished Episode");
            T.Check(!e.releaseApplied && e.members[0].pawn.thingIdNumber == 123, "policy never changes history or bindings");
        }

        private static void History()
        {
            PhysicalEpisode e = Episode(EpisodeState.Closed); e.releaseApplied = true; e.followUpApplied = true; e.publishedTick = 10;
            T.Check(!QaLabRules.EpisodeBlocksReset(e, 4, new TileRef { tileId = 7 }, false), "completed released target history does not block");
            e = Episode(); e.whereMapId = 9;
            T.Check(!QaLabRules.EpisodeBlocksReset(e, 4, new TileRef { tileId = 7 }, false), "other actual map remains independent even on same tile");
            e.whereMapId = -1; e.whereTile.layerId = 1;
            T.Check(!QaLabRules.EpisodeBlocksReset(e, 4, new TileRef { tileId = 7, layerId = 0 }, false), "another planet layer is not this TestSite");
        }

        private static void Retained()
        {
            KnownCharacter c = new KnownCharacter { id = new CharacterId(8), pawn = new PawnRef { thingIdNumber = 11 }, custody = CustodyState.OutOfCustody };
            T.Check(QaLabRules.RetainedBlocksReset(c, 4, true, null), "held retained person blocks even without active Episode");
            T.Check(!QaLabRules.RetainedBlocksReset(c, 4, false, null), "off-map retained people are preserved without blocking this empty map");
            c.custody = CustodyState.Deployed;
            T.Check(QaLabRules.RetainedBlocksReset(c, 4, false, Episode()), "deployed durable link blocks even with missing pointer");
            c.custody = CustodyState.Stored;
            T.Check(!QaLabRules.RetainedBlocksReset(c, 4, false, Episode(EpisodeState.Closed)), "stored off-map person and historical link are not an active map obligation");
            T.Eq(11, c.pawn.thingIdNumber, "retained binding stays intact");
        }

        private static void Layout()
        {
            foreach (int side in new[] { 60, 100 })
            {
                IntVec3 center = new IntVec3(side / 2, 0, side / 2);
                CellRect ordinary = QaLab.OrdinaryFootprint(center), prison = QaLab.PrisonFootprint(center);
                foreach (IntVec3 cell in ordinary.Concat(prison).Concat(QaLab.VisitorCells(center)))
                    T.Check(cell.x >= 5 && cell.z >= 5 && cell.x < side - 5 && cell.z < side - 5, "layout leaves open map-edge routes");
                T.Check(!ordinary.Any(prison.Contains), "ordinary and prison rooms never overlap");
                T.Check(QaLab.OrdinaryInterior(center).Contains(QaLab.OrdinaryBedCell(center)), "ordinary bed is enclosed");
                HashSet<IntVec3> table = new HashSet<IntVec3>(GenAdj.OccupiedRect(QaLab.TableCell(center), Rot4.North, new IntVec2(2, 2)));
                T.Check(QaLab.StoolCells(center).Distinct().Count() == 4 && !QaLab.StoolCells(center).Any(table.Contains), "four distinct stools outside table footprint");
                T.Check(!table.Contains(center) && QaLab.VisitorCells(center).Count(c => !table.Contains(c) && !QaLab.StoolCells(center).Contains(c)) >= PhysicalLifecycleService.MaxMembers,
                    "center and sufficient camp staging cells stay open");
            }
        }

        private static void Actions()
        {
            string actions = Source("PhysicalTestDevActions.cs"), world = Source("PhysicalTestWorld.cs"), runner = Source("PhysicalTestRunner.cs");
            T.Check(actions.Contains("PHYX — Create 60×60 Test Map") && actions.Contains("PHYX — Initialize / Reset QA Lab [DESTRUCTIVE]"), "explicit developer labels");
            string create = Body(world, "public static Map Create(");
            T.Check(create.Contains("GetOrGenerateMapUtility.GetOrGenerateMap") && create.Contains("if (!parent.HasMap)"), "creation reuses an existing map and only generates when absent");
            T.Check(!Regex.IsMatch(create, @"InitializeOrReset|SetTerrain|SetRoof|\.Destroy\(|GenSpawn|GeneratePawn|GroupFixtures|\.Plan\("), "Create never normalizes or makes test subjects");
            T.Check(Body(runner, "public static void CreateTestMap(").Contains("Arm.Spend(") && Body(runner, "public static string ProvisioningRefusal(").Contains("Arm.IsArmedFor(Current.Game)"), "Create uses the existing one-action arm");
            foreach (string f in PhysicalLifecycleTests.AllSources())
            {
                string code = PhysicalLifecycleTests.Code(File.ReadAllText(f));
                if (code.Contains("QaLab.InitializeOrReset("))
                    T.Check(PhysicalLifecycleTests.Rel(f).EndsWith("PhysicalTestRunner.cs", StringComparison.Ordinal), "only the explicit setup entry can call reset");
                if (code.Contains("TestSite.Create("))
                    T.Check(PhysicalLifecycleTests.Rel(f).EndsWith("PhysicalTestRunner.cs", StringComparison.Ordinal), "only the explicit setup entry can create the map");
            }
            T.Check(Body(runner, "public static void ResetQaLab(").Contains("QaLab.InitializeOrReset(TestSite.Map"), "destructive action takes only the current dedicated map");
        }

        public static void AssertDestructionBoundary(string code)
        {
            string reset = Body(code, "public static bool InitializeOrReset("), preflight = Body(code, "private static string ResetPreflight(");
            T.Eq(1, Regex.Matches(reset, @"\.Destroy\(").Count, "sole Destroy resides inside explicit reset");
            T.Check(reset.IndexOf("ResetPreflight(map, out contents)", StringComparison.Ordinal) < reset.IndexOf("map.roofGrid.SetRoof(", StringComparison.Ordinal)
                && reset.IndexOf("if (refusal != null)", StringComparison.Ordinal) < reset.IndexOf("map.roofGrid.SetRoof(", StringComparison.Ordinal), "all hard guards before first write");
            T.Check(reset.Contains("foreach (Thing thing in contents)") && reset.Contains("if (thing is Pawn || thing.Map != map) throw")
                && reset.Contains("thing.Destroy(DestroyMode.Vanish);"), "only snapshotted non-Pawn exact-map Things reach Vanish");
            T.Check(preflight.Contains("ThingOwnerUtility.GetAllThingsRecursively") && preflight.Contains("allowUnreal: true")
                && preflight.Contains("foreach (Pawn pawn in map.mapPawns.AllPawns)") && preflight.Contains("if (thing is Pawn) pawns++")
                && preflight.Contains("ResetStateRefusal(false, true, pawns)"), "whole-map, held, corpse and directly spawned Pawn census precedes mutation");
            T.Check(code.Contains("ReferenceEquals(map, TestSite.Map)") && code.Contains("TestSite.Count == 1") && code.Contains("map?.IsPlayerHome"), "exact unique non-home TestSite scope");
            T.Check(!Regex.IsMatch(code, @"\.Discard\(|\.Kill\(|PassToWorld\(|NaturalObstacle|Filth_RubbleRock|AncientShipBeacon"), "no Pawn deletion/transfer or growing debris whitelist");
        }

        private static void Preflight()
        {
            string lab = Source("QaLab.cs"); AssertDestructionBoundary(lab);
            string preflight = Body(lab, "private static string ResetPreflight(");
            T.Check(preflight.Contains("ScopeRefusal(map)") && preflight.Contains("PhysicalTestSession.IsRunning")
                && preflight.Contains("pointersResolved") && preflight.Contains("ObligationRefusal(") && preflight.Contains("!thing.def.destroyable"), "preflight proves scope, runtime, ownership and vanilla removal support");
            T.Check(preflight.Contains("bed.OwnersForReading.Count != 0") && preflight.Contains("thing.questTags.Count > 0"), "off-map bed owners and quest-linked Things cannot be affected by vanilla destruction callbacks");
            T.Check(preflight.Contains("CompAssignableToPawn") && preflight.Contains("AssignedPawnsForReading.Count > 0")
                && preflight.Contains("Props.explodeOnDestroyed == true"), "assignment and unconditional-explosion callbacks fail before any map mutation");
            T.Check(preflight.Contains("thing.TryGetComp<CompHasPawnSources>() != null")
                && !lab.Contains("RemovePawnSources("), "vanilla Pawn-source removal callbacks refuse before writes without changing WorldPawns");
            T.Check(preflight.Contains("thing.TryGetComp<CompTreeConnection>() != null"),
                "connected-Pawn and dryad destruction callbacks refuse before writes even when their Pawns are off map");
            T.Check(preflight.Contains("thing.TryGetComp<CompObelisk_Abductor>() != null"),
                "linked labyrinth removal callbacks refuse before writes without removing another map");
            T.Check(!Regex.IsMatch(preflight, @"\.Destroy\(|\.Spawn\(|SetTerrain\(|SetRoof\(|SetFaction\("), "entire preflight is read-only");
            string reset = Body(lab, "public static bool InitializeOrReset(");
            T.Check(reset.Contains("foreach (IntVec3 cell in map.AllCells) map.terrainGrid.SetTerrain(cell, Floor)")
                && reset.Contains("foreach (IntVec3 cell in map.AllCells) map.roofGrid.SetRoof(cell, null)"), "reset normalizes every cell of only the approved map");
            T.Check(reset.Contains("Arm.Spend(Current.Game") && reset.Contains("ProvisioningRefusal()"), "every reset including repeated empty reset requires a new arm");
        }

        private static void ReadOnly()
        {
            string lab = Source("QaLab.cs");
            foreach (string signature in new[] { "public static bool Validate(", "private static string ScopeRefusal(", "private static string PlanRefusal(",
                "private static Thing Existing(", "private static bool Matches(", "private static Building_Bed BedAt(", "private static Building_Door DoorAt(", "private static List<Piece> Plan(" })
                T.Check(!Regex.IsMatch(Body(lab, signature), @"GenSpawn|ThingMaker|\.Destroy\(|SetTerrain\(|SetRoof\(|SetFaction\(|ForPrisoners\s*=\s*(?:true|false)|ClaimBed|InitializeOrReset|TryRebuildDirtyRegions"), "validation dependency performs no map write: " + signature);
            string validate = Body(lab, "public static bool Validate(");
            foreach (string fact in new[] { "map.AllCells", "TerrainAt(cell) != Floor", "RoofAt(cell)", "OrdinaryInterior", "ordinaryRoom.IsPrisonCell", "room", "CanReachMapEdge", "door.Open", "door.HoldOpen", "door.FreePassage" })
                T.Check(validate.Contains(fact), "actual derived lab fact checked: " + fact);
            T.Check(!Regex.IsMatch(lab, @"ExposeData|Scribe_|GameComponent|WorldComponent"), "no persisted prepared marker or building registry");
        }

        private static void Runtime()
        {
            string runner = Source("PhysicalTestRunner.cs"), group = Source("PhysicalGroupScenarios.cs"), world = Source("PhysicalTestWorld.cs");
            string start = Body(runner, "public static bool Start(");
            T.Check(start.IndexOf("TestSite.GetPrepared(", StringComparison.Ordinal) < start.IndexOf("make(rt,", StringComparison.Ordinal), "all armed scenarios refuse an invalid lab before creating their fixtures");
            T.Check(Body(group, "protected StepResult PlaceGroup(").Contains("TestSite.GetPrepared(") && Body(runner, "protected StepResult MaterializeOnTestMap(").Contains("TestSite.GetPrepared("), "every placement revalidates without provisioning");
            string readOnly = Body(runner, "public static bool StartReadOnly(");
            T.Check(readOnly.Contains("RT-PHYX-030") && readOnly.Contains("RT-PHYX-032") && readOnly.Contains("TestSite.GetPrepared("), "group save/load and retention observations also require prepared lab");
            string prepared = Body(world, "public static Map GetPrepared(");
            T.Check(prepared.Contains("GetExisting(") && prepared.Contains("QaLab.Validate(") && prepared.Contains("QaLab.SetupInstruction"), "exact prepared path reports explicit setup instruction");
            T.Check(!Regex.IsMatch(group, @"InitializeOrReset|SetTerrain\(|SetRoof\(|GenSpawn|ThingMaker|TestSite.Create\(|\.Ensure\("), "026-032 never construct or repair infrastructure");
            T.Eq(1, Regex.Matches(group, @"\.CapturedBy\(").Count, "shared 029/030B still performs one real arrest");
            T.Check(group.Contains("QaLab.TryPreparePrisoner(e, capturedMember, captive") && group.Contains("QaLab.TryClaimPrisonerBed(captive, bed")
                && group.Contains("GroupQaRules.PendingCaptureHolds") && group.Contains("2 * PhysicalLifecycleService.WatchPeriod"), "custody setup and sustained guard remain the same architecture");
        }
    }
}
