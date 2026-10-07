using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RimWorld;
using TheNetwork.Diagnostics.RuntimePhysicalTests;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Physical;
using TheNetwork.Integration.Physical;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Tests
{
    public static class QaLabTests
    {
        public static void Register(List<KeyValuePair<string, Action>> tests)
        {
            tests.Add(new KeyValuePair<string, Action>("QaLab.Scope_RejectsEveryUnsafeTarget", Scope));
            tests.Add(new KeyValuePair<string, Action>("QaLab.Reset_ProtectedCountsAndActiveRunRefuse", ResetState));
            tests.Add(new KeyValuePair<string, Action>("QaLab.PawnPolicy_HumanlikeRegardlessOfOwnership", Humanlike));
            tests.Add(new KeyValuePair<string, Action>("QaLab.PawnPolicy_DisposableWildlifeAndMechFacts", Disposable));
            tests.Add(new KeyValuePair<string, Action>("QaLab.PawnPolicy_RealRecursiveCorpseCensusAndDetach", Corpses));
            tests.Add(new KeyValuePair<string, Action>("QaLab.PawnPolicy_NonHumanlikeActiveAndIncompleteEpisodeBindings", BoundEpisode));
            tests.Add(new KeyValuePair<string, Action>("QaLab.PawnPolicy_NonHumanlikeRetainedKnownCharacterBinding", BoundCharacter));
            tests.Add(new KeyValuePair<string, Action>("QaLab.PawnPolicy_RegistryCoverageAndReadOnlyDiagnostics", Registry));
            tests.Add(new KeyValuePair<string, Action>("QaLab.PawnPolicy_UnresolvedOwnershipAndBindingMismatchFailClosed", Unresolved));
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
            T.Eq(null, QaLabRules.ResetStateRefusal(false, true, 0, 0, 0), "empty map permits reset");
            foreach (int count in new[] { 1, 2, 100, int.MaxValue })
            {
                T.Check(QaLabRules.ResetStateRefusal(false, true, count, 0, 4) != null, "humanlike count refuses even with disposable Pawns: " + count);
                T.Check(QaLabRules.ResetStateRefusal(false, true, 0, count, 4) != null, "Network-bound count refuses regardless of race: " + count);
                T.Eq(null, QaLabRules.ResetStateRefusal(false, true, 0, 0, count), "disposable count alone permits reset: " + count);
            }
            T.Check(QaLabRules.ResetStateRefusal(false, true, -1, 0, 0) != null
                && QaLabRules.ResetStateRefusal(false, true, 0, -1, 0) != null
                && QaLabRules.ResetStateRefusal(false, true, 0, 0, -1) != null, "unknown census fails closed");
            T.Check(QaLabRules.ResetStateRefusal(true, true, 0, 0, 4) != null, "executing physical run blocks even disposable reset");
            T.Check(QaLabRules.ResetStateRefusal(false, false, 0, 0, 4) != null, "unresolved ownership cannot authorize a reset");
        }

        private static Pawn Pawn(bool humanlike, int id = 47001)
        {
            ThingDef def = Phase32bReservationTests.Shell<ThingDef>();
            def.defName = humanlike ? "Human" : "DisposableAnimal";
            def.stackLimit = 1;
            def.race = new RaceProperties { intelligence = humanlike ? Intelligence.Humanlike : Intelligence.Animal };
            return new Pawn { def = def, thingIDNumber = id };
        }

        private static void Humanlike()
        {
            Pawn human = Pawn(true);
            T.Check(human.RaceProps.Humanlike && QaLabRules.PawnBlocksReset(human.RaceProps.Humanlike, false), "actual vanilla Humanlike classification protects an unbound human");
            T.Check(QaLabRules.PawnBlocksReset(true, true), "a bound human remains protected");
            T.Check(QaLabRules.ResetStateRefusal(false, true, 2, 0, 3).Contains("2 Humanlike Pawn(s)"), "multiple humanlike Pawns are reported before destructive reset");
        }

        private static void Disposable()
        {
            Pawn animal = Pawn(false);
            T.Check(!animal.RaceProps.Humanlike && !QaLabRules.PawnBlocksReset(animal.RaceProps.Humanlike, false), "vanilla wildlife-style facts permit disposal");
            foreach (string kind in new[] { "deer", "rat", "insect", "ordinary mechanoid", "tame animal" })
            {
                T.Check(!QaLabRules.PawnBlocksReset(false, false), kind + ": no unnecessary kind/faction/assignment policy");
                T.Check(QaLabRules.PawnBlocksReset(false, true), kind + ": Network ownership overrides non-humanlike race");
            }
            TestNet n = new TestNet(9971);
            RetainedPawnRegistry registry = new RetainedPawnRegistry(n.ctx); registry.Rebuild();
            T.Check(!QaLabRules.NetworkOwnsPawn(n.ctx, registry, animal), "unrelated animal has no durable/registry owner");
            T.Eq(null, QaLabRules.ResetStateRefusal(false, true, 0, 0, 5), "several disposable Pawns allow reset");
        }

        private static void Corpses()
        {
            foreach (bool humanlike in new[] { true, false })
            {
                Pawn pawn = Pawn(humanlike);
                Corpse corpse = new Corpse(); corpse.InnerPawn = pawn;
                List<Thing> things = new List<Thing>();
                ThingOwnerUtility.GetAllThingsRecursively(corpse, things, allowUnreal: true);
                T.Check(things.Contains(pawn), "real recursive holder census includes corpse-contained Pawn");
                T.Eq(humanlike, QaLabRules.PawnBlocksReset(pawn.RaceProps.Humanlike, false), "corpse cannot hide protected race");
                T.Check(ReferenceEquals(corpse, pawn.ParentHolder), "read-only census preserves corpse holder");
                if (!humanlike)
                {
                    T.Check(pawn.holdingOwner.Remove(pawn), "vanilla holder detaches disposable corpse Pawn before explicit discard");
                    T.Check(corpse.InnerPawn == null && pawn.ParentHolder == null, "wrapper and Pawn no longer retain each other");
                }
            }
        }

        private static void BoundEpisode()
        {
            TestNet n = new TestNet(9972); Pawn pawn = Pawn(false);
            PhysicalEpisode e = Episode(); e.members[0].pawn = new PawnRef { pawn = pawn, thingIdNumber = pawn.thingIDNumber };
            n.ctx.episodes.Add(e);
            RetainedPawnRegistry registry = new RetainedPawnRegistry(n.ctx) { pointersResolved = true }; // Deliberately empty derived indexes.
            foreach (EpisodeState state in new[] { EpisodeState.Open, EpisodeState.Quarantined, EpisodeState.Closed })
            {
                e.state = state;
                T.Check(QaLabRules.NetworkOwnsPawn(n.ctx, registry, pawn), state + ": durable incomplete Episode protects even without registry coverage");
            }
            e.releaseApplied = true; e.followUpApplied = true; e.publishedTick = 10;
            T.Check(e.IsComplete && !QaLabRules.NetworkOwnsPawn(n.ctx, registry, pawn), "fully complete history alone imposes no physical obligation");
        }

        private static void BoundCharacter()
        {
            TestNet n = new TestNet(9973); Pawn pawn = Pawn(false);
            KnownCharacter person = new KnownCharacter { id = new CharacterId(8), custody = CustodyState.Stored,
                pawn = new PawnRef { pawn = pawn, thingIdNumber = pawn.thingIDNumber } };
            n.ctx.characters.Add(person);
            RetainedPawnRegistry registry = new RetainedPawnRegistry(n.ctx) { pointersResolved = true };
            foreach (CustodyState custody in new[] { CustodyState.Stored, CustodyState.Deployed, CustodyState.OutOfCustody })
            {
                person.custody = custody;
                T.Check(QaLabRules.NetworkOwnsPawn(n.ctx, registry, pawn) && QaLabRules.PawnBlocksReset(false, true), custody + ": retained binding protects non-humanlike Pawn independent of registry cache");
            }
            T.Check(ReferenceEquals(pawn, person.pawn.pawn) && person.pawn.thingIdNumber == pawn.thingIDNumber, "read-only protection preserves binding");
        }

        private static void Registry()
        {
            TestNet n = new TestNet(9974); Pawn pawn = Pawn(false);
            PhysicalEpisode e = Episode(); e.members[0].pawn = new PawnRef { pawn = pawn, thingIdNumber = pawn.thingIDNumber };
            n.ctx.episodes.Add(e);
            RetainedPawnRegistry registry = new RetainedPawnRegistry(n.ctx); registry.Rebuild();
            T.Eq(e.id, registry.EpisodeOf(pawn), "real M1 index reserves non-humanlike Episode Pawn");
            long queries = registry.queries, hits = registry.hits; int rebuilds = registry.rebuilds, resolves = registry.resolves;
            T.Check(QaLabRules.NetworkOwnsPawn(n.ctx, registry, pawn), "existing positive registry helper protects Pawn");
            T.Check(registry.queries == queries && registry.hits == hits && registry.rebuilds == rebuilds && registry.resolves == resolves, "preflight helper never changes registry diagnostics or rebuilds");
            T.Check(e.members[0].pawn.pawn == pawn && !e.releaseApplied && n.ctx.characters.Count == 0, "no reconciliation, release or identity creation");
        }

        private static void Unresolved()
        {
            TestNet n = new TestNet(9975); Pawn pawn = Pawn(false);
            RetainedPawnRegistry registry = new RetainedPawnRegistry(n.ctx) { pointersResolved = true };
            T.Check(QaLabRules.NetworkOwnsPawn(null, registry, pawn) && QaLabRules.NetworkOwnsPawn(n.ctx, null, pawn), "unknown stores/registry protect rather than authorize disposal");
            registry.pointersResolved = false;
            T.Check(QaLabRules.NetworkOwnsPawn(n.ctx, registry, pawn), "unresolved registry blocks");
            registry.pointersResolved = true; registry.inert = true;
            T.Check(QaLabRules.NetworkOwnsPawn(n.ctx, registry, pawn), "inert registry blocks");
            registry.inert = false;
            KnownCharacter person = new KnownCharacter { id = new CharacterId(8), pawn = new PawnRef { thingIdNumber = pawn.thingIDNumber } };
            n.ctx.characters.Add(person);
            T.Check(QaLabRules.NetworkOwnsPawn(n.ctx, registry, pawn), "durable id protects even when pointer missing");
            person.pawn.thingIdNumber++; person.pawn.pawn = pawn;
            T.Check(QaLabRules.NetworkOwnsPawn(n.ctx, registry, pawn), "actual pointer protects even when durable id disagrees");
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
            T.Check(reset.Contains("ResetPreflight(map, out contents, out disposablePawns)")
                && reset.IndexOf("ResetPreflight(map, out contents, out disposablePawns)", StringComparison.Ordinal) < reset.IndexOf("map.roofGrid.SetRoof(", StringComparison.Ordinal)
                && reset.IndexOf("if (refusal != null)", StringComparison.Ordinal) < reset.IndexOf("map.roofGrid.SetRoof(", StringComparison.Ordinal), "all hard guards before first write");
            T.Check(reset.Contains("foreach (Thing thing in contents)") && reset.Contains("if (thing is Pawn || thing.Map != map) throw")
                && reset.Contains("thing.Destroy(DestroyMode.Vanish);"), "only snapshotted non-Pawn exact-map Things reach Vanish");
            T.Check(preflight.Contains("ThingOwnerUtility.GetAllThingsRecursively") && preflight.Contains("allowUnreal: true")
                && preflight.Contains("foreach (Pawn pawn in map.mapPawns.AllPawns)") && preflight.Contains("pawn.RaceProps.Humanlike")
                && preflight.Contains("QaLabRules.NetworkOwnsPawn(runtime.Ctx, runtime.PhysicalWorld.Registry, pawn)")
                && preflight.Contains("if (!QaLabRules.PawnBlocksReset(humanlike, networkOwned)) disposablePawns.Add(pawn);")
                && preflight.Contains("ResetStateRefusal(false, true, humanlikeCount, networkOwnedCount, disposablePawns.Count)"),
                "whole-map, held, corpse and directly spawned Pawn census protects race AND Network ownership before writes");
            T.Check(code.Contains("ReferenceEquals(map, TestSite.Map)") && code.Contains("TestSite.Count == 1") && code.Contains("map?.IsPlayerHome"), "exact unique non-home TestSite scope");
            T.Eq(1, Regex.Matches(code, @"WorldPawns\.PassToWorld\(").Count, "sole QA lab disposal call");
            T.Eq(1, Regex.Matches(reset, @"WorldPawns\.PassToWorld\(").Count, "Pawn disposal resides only inside explicit reset");
            T.Eq(1, Regex.Matches(code, @"\.DeSpawn\(").Count, "no other lab Pawn movement path");
            T.Check(reset.Contains("foreach (Pawn pawn in disposablePawns)") && reset.Contains("ScopeRefusal(map) != null || pawn.MapHeld != map")
                && reset.Contains("QaLabRules.PawnBlocksReset(pawn.RaceProps.Humanlike, QaLabRules.NetworkOwnsPawn(runtime?.Ctx, runtime?.PhysicalWorld?.Registry, pawn))")
                && reset.Contains("throw new InvalidOperationException(\"disposable Pawn protection/scope changed after preflight\")"),
                "disposal rechecks exact map, vanilla race and durable/registry ownership before detaching any approved Pawn");
            T.Check(Regex.IsMatch(reset, @"if \(pawn.Spawned\) pawn.DeSpawn\(DestroyMode.Vanish\);\s*else if \(pawn.holdingOwner == null \|\| !pawn.holdingOwner.Remove\(pawn\)\)")
                && reset.Contains("Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.Discard);")
                && reset.Contains("if (!pawn.Discarded || Find.WorldPawns.Contains(pawn))"), "detachment then explicit Discard only, with no-world-retention postcondition");
            T.Check(!Regex.IsMatch(code, @"\.Discard\(|\.Kill\(|NaturalObstacle|Filth_RubbleRock|AncientShipBeacon"), "no direct Pawn discard/kill or growing debris whitelist");
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
            T.Check(!Regex.IsMatch(preflight, @"\.Destroy\(|\.Spawn\(|\.DeSpawn\(|\.Remove\(|PassToWorld\(|SetTerrain\(|SetRoof\(|SetFaction\("), "entire preflight is read-only");
            string rules = Source("QaLabRules.cs");
            T.Check(!Regex.IsMatch(Body(rules, "public static bool NetworkOwnsPawn("), @"\.Reserves\(|\.Rebuild\(|\.ResolvePointers\(|\.Note\(|\.Reconcile\(|\.Destroy\(|PassToWorld\("), "ownership helper only reads existing state, without diagnostic mutations");
            string reset = Body(lab, "public static bool InitializeOrReset(");
            T.Check(reset.Contains("foreach (IntVec3 cell in map.AllCells) map.terrainGrid.SetTerrain(cell, Floor)")
                && reset.Contains("foreach (IntVec3 cell in map.AllCells) map.roofGrid.SetRoof(cell, null)"), "reset normalizes every cell of only the approved map");
            T.Check(reset.Contains("Arm.Spend(Current.Game") && reset.Contains("ProvisioningRefusal()"), "every reset including repeated empty reset requires a new arm");
            T.Check(reset.Contains("disposable non-humanlike Pawns removed; 0 protected Pawns"), "success reports useful approved Pawn counts");
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
