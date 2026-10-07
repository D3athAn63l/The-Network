using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Physical;
using TheNetwork.Domain.Actors;
using TheNetwork.Kernel;
using TheNetwork.Integration.Physical;
using Verse;
using Verse.AI;

namespace TheNetwork.Diagnostics.RuntimePhysicalTests
{
    /// <summary>Explicit armed provisioning and read-only validation of the dedicated QA map. Prepared state is derived from actual Things/cells.</summary>
    public static class QaLab
    {
        public const int MinimumMapSide = 60;
        public const string SetupInstruction = "Physical QA Lab is not initialized or is invalid. Use Dev Mode -> PHYX — Initialize / Reset QA Lab [DESTRUCTIVE] first (arm separately for Create and Reset).";
        private static TerrainDef Floor => DefDatabase<TerrainDef>.GetNamedSilentFail("Concrete");
        private static ThingDef CampTable => DefDatabase<ThingDef>.GetNamedSilentFail("Table2x2c");
        private static ThingDef CampStool => DefDatabase<ThingDef>.GetNamedSilentFail("Stool");
        public const int VisitorRadius = 6;
        public const int PrisonMinX = 15, PrisonMaxX = 23, PrisonMinZ = -4, PrisonMaxZ = 4;

        private sealed class Piece
        {
            public IntVec3 cell;
            public ThingDef def, stuff;
            public CellRect Footprint => GenAdj.OccupiedRect(cell, Rot4.North, def.size);
        }

        public static CellRect PrisonFootprint(IntVec3 center)
        {
            return new CellRect(center.x + PrisonMinX, center.z + PrisonMinZ, PrisonMaxX - PrisonMinX + 1, PrisonMaxZ - PrisonMinZ + 1);
        }

        public static CellRect PrisonInterior(IntVec3 center)
        {
            return new CellRect(center.x + PrisonMinX + 1, center.z + PrisonMinZ + 1, PrisonMaxX - PrisonMinX - 1, PrisonMaxZ - PrisonMinZ - 1);
        }

        public static IntVec3 PrisonDoorCell(IntVec3 center) => center + new IntVec3(PrisonMinX, 0, 0);

        public static IEnumerable<IntVec3> PrisonBedCells(IntVec3 center)
        {
            yield return center + new IntVec3(18, 0, -1);
            yield return center + new IntVec3(21, 0, -1);
        }

        public static IEnumerable<IntVec3> WallCells(IntVec3 center)
        {
            CellRect outer = PrisonFootprint(center);
            IntVec3 door = PrisonDoorCell(center);
            foreach (IntVec3 cell in outer)
                if ((cell.x == outer.minX || cell.x == outer.maxX || cell.z == outer.minZ || cell.z == outer.maxZ) && cell != door)
                    yield return cell;
        }

        public static IEnumerable<IntVec3> VisitorCells(IntVec3 center)
        {
            foreach (IntVec3 cell in new CellRect(center.x - VisitorRadius, center.z - VisitorRadius, VisitorRadius * 2 + 1, VisitorRadius * 2 + 1))
                yield return cell;
        }

        public static CellRect OrdinaryFootprint(IntVec3 center) => new CellRect(center.x - 23, center.z - 4, 9, 9);
        public static CellRect OrdinaryInterior(IntVec3 center) => new CellRect(center.x - 22, center.z - 3, 7, 7);
        public static IntVec3 OrdinaryDoorCell(IntVec3 center) => center + new IntVec3(-15, 0, 0);
        public static IntVec3 OrdinaryBedCell(IntVec3 center) => center + new IntVec3(-20, 0, -1);
        public static IntVec3 TableCell(IntVec3 center) => center + new IntVec3(0, 0, 4);
        public static IEnumerable<IntVec3> StoolCells(IntVec3 center)
        {
            yield return center + new IntVec3(-1, 0, 4);
            yield return center + new IntVec3(2, 0, 4);
            yield return center + new IntVec3(0, 0, 3);
            yield return center + new IntVec3(0, 0, 6);
        }

        private static bool IsPlannedBedCell(IntVec3 center, IntVec3 cell)
        {
            foreach (IntVec3 anchor in PrisonBedCells(center))
                if (GenAdj.OccupiedRect(anchor, Rot4.North, ThingDefOf.Bed.size).Contains(cell)) return true;
            return false;
        }

        private static List<Piece> Plan(IntVec3 center)
        {
            List<Piece> pieces = new List<Piece>();
            foreach (IntVec3 cell in WallCells(center)) pieces.Add(new Piece { cell = cell, def = ThingDefOf.Wall, stuff = ThingDefOf.BlocksGranite });
            pieces.Add(new Piece { cell = PrisonDoorCell(center), def = ThingDefOf.Door, stuff = ThingDefOf.WoodLog });
            foreach (IntVec3 cell in PrisonBedCells(center)) pieces.Add(new Piece { cell = cell, def = ThingDefOf.Bed, stuff = ThingDefOf.WoodLog });
            CellRect ordinary = OrdinaryFootprint(center);
            foreach (IntVec3 cell in ordinary)
                if ((cell.x == ordinary.minX || cell.x == ordinary.maxX || cell.z == ordinary.minZ || cell.z == ordinary.maxZ) && cell != OrdinaryDoorCell(center))
                    pieces.Add(new Piece { cell = cell, def = ThingDefOf.Wall, stuff = ThingDefOf.BlocksGranite });
            pieces.Add(new Piece { cell = OrdinaryDoorCell(center), def = ThingDefOf.Door, stuff = ThingDefOf.WoodLog });
            pieces.Add(new Piece { cell = OrdinaryBedCell(center), def = ThingDefOf.Bed, stuff = ThingDefOf.WoodLog });
            pieces.Add(new Piece { cell = TableCell(center), def = CampTable, stuff = ThingDefOf.WoodLog });
            foreach (IntVec3 cell in StoolCells(center)) pieces.Add(new Piece { cell = cell, def = CampStool, stuff = ThingDefOf.WoodLog });
            return pieces;
        }

        private static string ScopeRefusal(Map map)
        {
            MapParent parent = map?.Parent;
            string scope = QaLabRules.ScopeRefusal(map != null,
                map != null && TestSite.IsTestMap(map) && ReferenceEquals(map, TestSite.Map) && TestSite.Count == 1,
                parent != null && parent.GetType() == typeof(MapParent), parent != null && parent.Faction == null,
                map?.IsPlayerHome == true, TestSite.DefRefusal(parent?.def) == null, map?.Size.x ?? 0, map?.Size.z ?? 0);
            if (scope != null) return scope;
            if (map.regionAndRoomUpdater == null || !map.regionAndRoomUpdater.Enabled || map.reachability == null)
                return "the actual map region/room/path services are unavailable";
            return null;
        }

        private static string PlanRefusal(Map map)
        {
            if (ThingDefOf.Wall == null || ThingDefOf.Door == null || ThingDefOf.Bed == null || ThingDefOf.BlocksGranite == null
                || ThingDefOf.WoodLog == null || Floor == null || Floor.passability != Traversability.Standable || CampTable == null || CampStool == null || RoofDefOf.RoofConstructed == null || Faction.OfPlayer == null)
                return "the vanilla structure, terrain, roof or player-faction definitions are unavailable";
            if (ThingDefOf.Wall.size != new IntVec2(1, 1) || ThingDefOf.Door.size != new IntVec2(1, 1) || ThingDefOf.Bed.size != new IntVec2(1, 2) || CampTable.size != new IntVec2(2, 2) || CampStool.size != new IntVec2(1, 1))
                return "patched vanilla structure dimensions do not match the bounded compound plan";
            foreach (Piece piece in Plan(map.Center))
                foreach (IntVec3 cell in piece.Footprint)
                    if (!cell.InBounds(map)) return "structure footprint is outside the approved lab map at " + cell;
            return null;
        }

        private static bool Matches(Thing thing, Piece piece)
        {
            return thing != null && thing.Spawned && !thing.Destroyed && thing.def == piece.def && thing.Stuff == piece.stuff
                && thing.Position == piece.cell && (thing is Building_Door || thing.Rotation == Rot4.North) && thing.Faction == Faction.OfPlayer;
        }

        private static Thing Existing(Map map, Piece piece)
        {
            Thing found = null;
            foreach (Thing thing in map.thingGrid.ThingsListAt(piece.cell))
                if (Matches(thing, piece))
                {
                    if (found != null) return null; // Duplicate anchors are invalid, never repaired by deleting one.
                    found = thing;
                }
            return found;
        }

        /// <summary>Read durable ownership, including target-map Episodes whose Pawns are missing or unspawned. Completed history alone does not block reset.</summary>
        public static string ObligationRefusal(DomainContext ctx, Map map)
        {
            if (ctx?.episodes == null || ctx.characters == null) return "Network physical ownership stores are unavailable";
            foreach (PhysicalEpisode episode in ctx.episodes.episodes)
            {
                bool memberOnMap = false;
                if (episode?.members != null)
                    foreach (EpisodeMember member in episode.members)
                        if (member?.pawn?.pawn?.MapHeld == map) memberOnMap = true;
                if (QaLabRules.EpisodeBlocksReset(episode, map.uniqueID, TileRef.Of(map.Tile), memberOnMap))
                    return "incomplete or unreleased physical Episode " + episode.id + " is associated with TestSite map " + map.uniqueID;
            }
            foreach (KnownCharacter person in ctx.characters.characters)
                if (person != null && QaLabRules.RetainedBlocksReset(person, map.uniqueID, person.pawn?.pawn?.MapHeld == map, ctx.episodes.Get(person.episode)))
                    return "retained Network person " + person.id + " has a physical obligation on TestSite map " + map.uniqueID;
            return null;
        }

        /// <summary>Complete read-only preflight. Humanlike or Network-bound Pawns block, including corpses and holder contents.</summary>
        private static string ResetPreflight(Map map, out List<Thing> contents, out List<Pawn> disposablePawns)
        {
            contents = null;
            disposablePawns = new List<Pawn>();
            string refusal = ScopeRefusal(map);
            if (refusal != null) return refusal;
            NetworkRuntime runtime = NetworkRuntime.Current;
            refusal = QaLabRules.ResetStateRefusal(PhysicalTestSession.IsRunning,
                runtime != null && !runtime.Inert && runtime.Session.IsRunning && runtime.PhysicalWorld?.Registry?.pointersResolved == true
                    && !runtime.PhysicalWorld.Registry.inert && Find.WorldPawns != null, 0, 0, 0);
            if (refusal != null) return refusal;
            refusal = PlanRefusal(map);
            if (refusal != null) return refusal;
            List<Thing> all = new List<Thing>();
            ThingOwnerUtility.GetAllThingsRecursively(map, ThingRequest.ForGroup(ThingRequestGroup.Everything), all, allowUnreal: true);
            // Include the entire cell grid too, so a Thing omitted from a lister cannot evade Pawn protection or scope.
            HashSet<Thing> unique = new HashSet<Thing>(all);
            foreach (IntVec3 cell in map.AllCells)
                foreach (Thing thing in map.thingGrid.ThingsListAt(cell)) unique.Add(thing);
            foreach (Pawn pawn in map.mapPawns.AllPawns) unique.Add(pawn);
            int humanlikeCount = 0, networkOwnedCount = 0;
            foreach (Thing thing in unique)
                if (thing is Pawn pawn)
                {
                    if (pawn.MapHeld != map || pawn.RaceProps == null) return "a Pawn has unresolved/outside-map ownership or race";
                    bool humanlike = pawn.RaceProps.Humanlike;
                    bool networkOwned = QaLabRules.NetworkOwnsPawn(runtime.Ctx, runtime.PhysicalWorld.Registry, pawn);
                    if (humanlike) humanlikeCount++;
                    if (networkOwned) networkOwnedCount++;
                    if (!QaLabRules.PawnBlocksReset(humanlike, networkOwned)) disposablePawns.Add(pawn);
                }
            refusal = QaLabRules.ResetStateRefusal(false, true, humanlikeCount, networkOwnedCount, disposablePawns.Count);
            if (refusal != null) return "TestSite map " + map.uniqueID + " " + refusal;
            refusal = ObligationRefusal(runtime.Ctx, map);
            if (refusal != null) return refusal;
            contents = new List<Thing>();
            foreach (Thing thing in unique)
            {
                if (thing == null || thing.Destroyed || thing.MapHeld != map) return "a map Thing has unresolved/outside-map ownership";
                if (!thing.def.destroyable) return "STOP: vanilla cannot safely destroy " + thing.def.defName;
                if (thing is Pawn pawn)
                {
                    if (pawn.Discarded || Find.WorldPawns.Contains(pawn) || (!pawn.Spawned && pawn.holdingOwner == null))
                        return "STOP: disposable Pawn has inconsistent map/holder/world state";
                    // Owner-authorized disposable save: ordinary Pawn relation, quest and assignment cleanup may affect linked entities.
                    continue;
                }
                if (thing.questTags != null && thing.questTags.Count > 0) return "STOP: " + thing.def.defName + " has quest links outside disposable lab ownership";
                if (thing is Building_Bed bed && bed.OwnersForReading.Count != 0)
                    return "a bed still has Pawn owners; reset must not change any off-map Pawn's ownership";
                if (thing.TryGetComp<CompAssignableToPawn>()?.AssignedPawnsForReading.Count > 0)
                    return "a structure still has Pawn assignments; reset cannot change off-map subjects";
                if (thing.TryGetComp<CompHasPawnSources>() != null)
                    return "STOP: " + thing.def.defName + " has Pawn-source callbacks that modify WorldPawns during vanilla removal";
                if (thing.TryGetComp<CompTreeConnection>() != null)
                    return "STOP: " + thing.def.defName + " has connected-Pawn callbacks that can harm off-map subjects during vanilla removal";
                if (thing.TryGetComp<CompObelisk_Abductor>() != null)
                    return "STOP: " + thing.def.defName + " has linked-map callbacks that can remove another map during vanilla removal";
                if (thing.TryGetComp<CompExplosive>()?.Props.explodeOnDestroyed == true)
                    return "STOP: " + thing.def.defName + " would detonate even during vanilla Vanish removal";
                if (thing.Spawned) contents.Add(thing);
            }
            return null;
        }

        /// <summary>Only the explicit armed destructive Dev action calls this. Only preflight-approved non-humanlike, unbound Pawns may be discarded.</summary>
        public static bool InitializeOrReset(Map map, out string report)
        {
            string refusal = PhysicalTestSession.ProvisioningRefusal();
            List<Thing> contents = null;
            List<Pawn> disposablePawns = null;
            try
            {
                if (refusal == null) refusal = ResetPreflight(map, out contents, out disposablePawns);
            }
            catch (Exception ex)
            {
                report = "QA Lab reset refused before any map write: unable to prove the complete preflight: " + ex.Message;
                return false;
            }
            if (refusal != null) { report = "QA Lab reset refused: " + refusal; return false; }
            if (!PhysicalTestSession.Arm.Spend(Current.Game, "initialize/reset QA lab"))
            { report = "QA Lab reset refused: the PHYX arm was not available"; return false; }
            try
            {
                // Roofs first: removing supports must not cause roof collapse. All hard guards completed before this first write.
                foreach (IntVec3 cell in map.AllCells) map.roofGrid.SetRoof(cell, null);
                int removedPawns = 0;
                foreach (Pawn pawn in disposablePawns)
                {
                    NetworkRuntime runtime = NetworkRuntime.Current;
                    if (ScopeRefusal(map) != null || pawn.MapHeld != map || pawn.RaceProps == null
                        || QaLabRules.PawnBlocksReset(pawn.RaceProps.Humanlike, QaLabRules.NetworkOwnsPawn(runtime?.Ctx, runtime?.PhysicalWorld?.Registry, pawn))
                        || pawn.Discarded || Find.WorldPawns.Contains(pawn))
                        throw new InvalidOperationException("disposable Pawn protection/scope changed after preflight");
                    if (pawn.Spawned) pawn.DeSpawn(DestroyMode.Vanish);
                    else if (pawn.holdingOwner == null || !pawn.holdingOwner.Remove(pawn))
                        throw new InvalidOperationException("disposable Pawn could not be detached from its TestSite holder");
                    // Vanilla explicit Discard guards Destroy against re-entering WorldPawns, then establishes Destroyed before Discarded.
                    // Empty corpse/holder wrappers remain in the original non-Pawn snapshot and are vanished below, without spawning a corpse.
                    Find.WorldPawns.PassToWorld(pawn, PawnDiscardDecideMode.Discard);
                    if (!pawn.Discarded || Find.WorldPawns.Contains(pawn))
                        throw new InvalidOperationException("vanilla did not discard the disposable Pawn without world retention");
                    removedPawns++;
                }
                foreach (Thing thing in contents)
                {
                    if (thing.Destroyed || !thing.Spawned) continue; // a vanilla attachment may have been removed with its parent
                    if (thing is Pawn || thing.Map != map) throw new InvalidOperationException("reset target changed after preflight");
                    thing.Destroy(DestroyMode.Vanish);
                }
                // Never erase a newly spawned object from an unexpected destruction callback. Stop for inspection instead.
                if (map.listerThings.AllThings.Count != 0)
                    throw new InvalidOperationException("a vanilla removal left or spawned Things; no terrain/building writes attempted");
                foreach (IntVec3 cell in map.AllCells) map.terrainGrid.SetTerrain(cell, Floor);
                foreach (Piece piece in Plan(map.Center))
                {
                    Thing thing = ThingMaker.MakeThing(piece.def, piece.stuff);
                    thing.SetFaction(Faction.OfPlayer);
                    GenSpawn.Spawn(thing, piece.cell, map, Rot4.North);
                    if (thing is Building_Bed bed) bed.ForPrisoners = PrisonInterior(map.Center).Contains(piece.cell);
                }
                foreach (IntVec3 cell in map.AllCells)
                    if (PrisonFootprint(map.Center).Contains(cell) || OrdinaryFootprint(map.Center).Contains(cell))
                        map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                foreach (Piece piece in Plan(map.Center))
                    if (Existing(map, piece) is Building_Bed bed)
                    {
                        bed.GetDistrict()?.Notify_RoomShapeOrContainedBedsChanged();
                        bed.GetRoom()?.Notify_RoomShapeChanged();
                    }
                if (!Validate(map, out report)) { report = "QA Lab reset did not validate: " + report; return false; }
                report = "QA Lab initialized: world object " + map.Parent.ID + ", map " + map.uniqueID + ", " + map.Size.x + "x" + map.Size.z
                    + "; " + (map.Size.x * map.Size.z) + " Concrete cells normalized; " + contents.Count + " spawned Things selected for removal; "
                    + removedPawns + " disposable non-humanlike Pawns removed; 0 protected Pawns; "
                    + report + "; IsPlayerHome=false, parent faction=null, 0 Pawns. Initialization is not custody runtime proof.";
                return true;
            }
            catch (Exception ex)
            {
                report = "STOP: QA Lab reset encountered an unexpected vanilla removal/setup failure; map may be partially normalized, inspect before retry: " + ex.Message;
                return false;
            }
        }

        private static Building_Bed BedAt(Map map, IntVec3 cell)
        {
            return Existing(map, new Piece { cell = cell, def = ThingDefOf.Bed, stuff = ThingDefOf.WoodLog }) as Building_Bed;
        }

        private static Building_Door DoorAt(Map map)
        {
            return Existing(map, new Piece { cell = PrisonDoorCell(map.Center), def = ThingDefOf.Door, stuff = ThingDefOf.WoodLog }) as Building_Door;
        }

        /// <summary>Read actual vanilla map/room/bed/path facts. No construction, repair or AI changes.</summary>
        public static bool Validate(Map map, out string report)
        {
            string refusal = ScopeRefusal(map) ?? (map == null ? null : PlanRefusal(map));
            if (refusal != null) { report = "QA Lab invalid: " + refusal; return false; }
            foreach (Piece piece in Plan(map.Center))
                if (Existing(map, piece) == null) { report = "QA Lab invalid: expected " + piece.def.defName + " missing or changed at " + piece.cell; return false; }
            Building_Door door = DoorAt(map);
            if (door == null || door.Open || door.HoldOpen || door.FreePassage)
            { report = "QA Lab invalid: real player-owned prison door must already be closed"; return false; }
            Room room = null;
            foreach (IntVec3 cell in PrisonBedCells(map.Center))
            {
                Building_Bed bed = BedAt(map, cell);
                Room actual = bed?.GetRoom();
                if (bed == null || !bed.ForPrisoners || bed.Medical || actual == null || !actual.IsPrisonCell
                    || !actual.ProperRoom || actual.TouchesMapEdge || !Building_Bed.RoomCanBePrisonCell(actual) || (room != null && !ReferenceEquals(room, actual)))
                { report = "QA Lab invalid: vanilla prison room/bed recognition failed at " + cell; return false; }
                room = actual;
            }
            foreach (IntVec3 cell in PrisonInterior(map.Center))
                // Real Bed is PassThroughOnly: its verified footprint is walkable, while every free interior cell must be standable.
                if (!(IsPlannedBedCell(map.Center, cell) ? cell.Walkable(map) : cell.Standable(map))
                    || !ReferenceEquals(cell.GetRoom(map), room) || map.roofGrid.RoofAt(cell) != RoofDefOf.RoofConstructed)
                { report = "QA Lab invalid: enclosed prison interior is obstructed or changed at " + cell; return false; }
            Building_Bed ordinaryBed = BedAt(map, OrdinaryBedCell(map.Center));
            Room ordinaryRoom = ordinaryBed?.GetRoom();
            if (ordinaryBed == null || ordinaryBed.ForPrisoners || ordinaryBed.Medical || ordinaryRoom == null || !ordinaryRoom.ProperRoom
                || ordinaryRoom.IsPrisonCell || ordinaryRoom.TouchesMapEdge || ReferenceEquals(room, ordinaryRoom))
            { report = "QA Lab invalid: ordinary room/bed is missing or changed"; return false; }
            foreach (IntVec3 cell in OrdinaryInterior(map.Center))
                if (!cell.Walkable(map) || !ReferenceEquals(cell.GetRoom(map), ordinaryRoom))
                { report = "QA Lab invalid: ordinary room interior changed"; return false; }
            List<Piece> pieces = Plan(map.Center);
            HashSet<IntVec3> planned = new HashSet<IntVec3>();
            foreach (Piece piece in pieces) foreach (IntVec3 cell in piece.Footprint) planned.Add(cell);
            foreach (IntVec3 cell in map.AllCells)
            {
                bool roofed = PrisonFootprint(map.Center).Contains(cell) || OrdinaryFootprint(map.Center).Contains(cell);
                if (map.terrainGrid.TerrainAt(cell) != Floor || map.roofGrid.RoofAt(cell) != (roofed ? RoofDefOf.RoofConstructed : null)
                    || (!planned.Contains(cell) && !cell.Standable(map)))
                { report = "QA Lab invalid: Concrete, open field or expected roof changed at " + cell; return false; }
            }
            int visitors = 0;
            foreach (IntVec3 cell in VisitorCells(map.Center))
                if (!planned.Contains(cell) && cell.Standable(map)) visitors++;
            TraverseParms closedDoors = TraverseParms.For(TraverseMode.NoPassClosedDoors, Danger.Deadly);
            if (visitors < PhysicalLifecycleService.MaxMembers || !map.reachability.CanReachMapEdge(map.Center, closedDoors)
                || map.reachability.CanReachMapEdge(PrisonInterior(map.Center).CenterCell, closedDoors))
            { report = "QA Lab invalid: courtyard edge path or closed prison enclosure failed vanilla reachability"; return false; }
            report = "QA Lab validated on TestSite map " + map.uniqueID + " (" + map.Size.x + "x" + map.Size.z
                + "): 13x13 camp valid with table/four stools; west 9x9 ordinary room/one normal bed valid; east 9x9 prison/two prisoner beds/closed player-owned door valid; edge path valid";
            return true;
        }

        private static string OwnedAnonymousRefusal(PhysicalEpisode episode, EpisodeMember member, Pawn pawn)
        {
            if (episode == null || member == null || pawn == null || !PhysicalTestSession.IsActiveOwnedEpisode(episode)
                || !ReferenceEquals(NetworkRuntime.Current?.Ctx?.episodes?.Get(episode.id), episode) || episode.members == null || !episode.members.Contains(member)
                || episode.state != EpisodeState.Open || episode.releaseApplied || episode.consequencesApplied || member.IsNamed
                || member.state != MemberState.Present || member.outcome != MemberOutcome.Pending || member.pawn == null
                || !ReferenceEquals(member.pawn.pawn, pawn) || member.pawn.thingIdNumber <= 0 || member.pawn.thingIdNumber != pawn.thingIDNumber)
                return "the exact active run's anonymous Episode/Pawn binding is not proven";
            if (!pawn.Spawned || pawn.Dead || pawn.Destroyed || pawn.Discarded || pawn.guest == null || pawn.ownership == null
                || pawn.carryTracker?.CarriedThing != null) return "the owned Pawn is not a live, unencumbered placed human";
            RetainedPawnRegistry registry = RetainedPawnRegistry.Active;
            if (registry == null || !registry.Reserves(pawn) || !registry.IsTemporaryReserved(pawn) || registry.CharacterOf(pawn).IsValid
                || registry.EpisodeOf(pawn) != episode.id) return "the exact temporary Episode reservation is not proven";
            return ScopeRefusal(pawn.Map);
        }

        /// <summary>One explicitly owned fixture relocation, before vanilla CapturedBy. Never transports through WorldPawns or changes custody.</summary>
        public static bool TryPreparePrisoner(PhysicalEpisode episode, EpisodeMember member, Pawn pawn, out Building_Bed bed, out string report)
        {
            bed = null;
            string refusal = OwnedAnonymousRefusal(episode, member, pawn);
            if (refusal != null) { report = "prison setup refused: " + refusal; return false; }
            if (pawn.HostFaction != null || pawn.IsPrisoner || pawn.IsSlave)
            { report = "prison setup refused: the visitor already has vanilla custody"; return false; }
            if (!Validate(pawn.Map, out report)) return false;
            Map map = pawn.Map;
            IntVec3 holdingCell = IntVec3.Invalid;
            foreach (IntVec3 cell in PrisonBedCells(map.Center))
            {
                Building_Bed candidate = BedAt(map, cell);
                IntVec3 target = candidate.InteractionCell;
                if (candidate.OwnersForReading.Count != 0 || !candidate.AnyUnoccupiedSleepingSlot || !PrisonInterior(map.Center).Contains(target)
                    || !target.Standable(map) || !ReferenceEquals(target.GetRoom(map), candidate.GetRoom())) continue;
                bool occupied = false;
                foreach (Thing thing in map.thingGrid.ThingsListAt(target)) if (thing is Pawn) occupied = true;
                if (occupied) continue;
                bed = candidate;
                holdingCell = target;
                break;
            }
            if (bed == null) { report = "prison setup refused: neither real prison bed has an unowned, unoccupied safe holding cell; earlier captives are preserved"; return false; }
            try
            {
                // Actual 1.6 vanilla teleport path: Position maintains spawned grids, Notify_Teleported ends the old job and resets path/draw state.
                pawn.Position = holdingCell;
                pawn.Notify_Teleported();
                refusal = OwnedAnonymousRefusal(episode, member, pawn);
                if (refusal != null || !ReferenceEquals(pawn.Map, map) || !ReferenceEquals(pawn.GetRoom(), bed.GetRoom()))
                { report = "prison setup failed after the single relocation: " + (refusal ?? "actual map/room continuity failed"); return false; }
                report = "owned Pawn #" + pawn.thingIDNumber + " relocated once to validated prison cell " + holdingCell
                    + " before CapturedBy; unowned bed #" + bed.thingIDNumber + "; normal vanilla jobs remain enabled";
                return true;
            }
            catch (Exception ex) { report = "prison setup failed; Pawn preserved for inspection: " + ex.Message; return false; }
        }

        /// <summary>After the caller's one real CapturedBy, validate vanilla custody/bed eligibility and use ordinary bed ownership.</summary>
        public static bool TryClaimPrisonerBed(Pawn captive, Building_Bed bed, out string report)
        {
            RetainedPawnRegistry registry = RetainedPawnRegistry.Active;
            PhysicalEpisode episode = captive == null || registry == null ? null : NetworkRuntime.Current?.Ctx?.episodes?.Get(registry.EpisodeOf(captive));
            EpisodeMember member = null;
            if (episode?.members != null) foreach (EpisodeMember candidate in episode.members) if (ReferenceEquals(candidate.pawn?.pawn, captive)) member = candidate;
            string refusal = OwnedAnonymousRefusal(episode, member, captive);
            if (refusal != null) { report = "prison bed claim refused: " + refusal; return false; }
            Map map = captive.Map;
            bool plannedBed = false;
            foreach (IntVec3 cell in PrisonBedCells(map.Center)) if (ReferenceEquals(bed, BedAt(map, cell))) plannedBed = true;
            if (!plannedBed || bed == null || !captive.IsPrisonerOfColony || captive.HostFaction != Faction.OfPlayer
                || !Validate(map, out report) || !ReferenceEquals(captive.GetRoom(), bed.GetRoom()) || DoorAt(map).PawnCanOpen(captive)
                || map.reachability.CanReachMapEdge(captive.Position, TraverseParms.For(captive, Danger.Deadly)))
            { report = "prison bed claim refused: actual prisoner/room/closed-door custody prerequisites failed"; return false; }
            foreach (Pawn owner in bed.OwnersForReading)
                if (!ReferenceEquals(owner, captive)) { report = "prison bed claim refused: another captive owns this bed; no owner was evicted"; return false; }
            if (!RestUtility.IsValidBedFor(bed, captive, captive, checkSocialProperness: true, guestStatus: GuestStatus.Prisoner))
            { report = "prison bed claim refused: vanilla IsValidBedFor rejected the real bed"; return false; }
            try
            {
                if (!bed.IsOwner(captive) && !captive.ownership.ClaimBedIfNonMedical(bed))
                { report = "prison bed claim failed: vanilla ownership did not accept the real bed"; return false; }
                if (!bed.IsOwner(captive) || !ReferenceEquals(captive.ownership.OwnedBed, bed))
                { report = "prison bed claim failed: vanilla ownership does not resolve to the selected bed"; return false; }
                report = "real colony prisoner #" + captive.thingIDNumber + " owns validated prison bed #" + bed.thingIDNumber
                    + "; same Pawn and temporary Episode reservation, no warden/home requirement or escape suppression";
                return true;
            }
            catch (Exception ex) { report = "prison bed claim failed; captive preserved for inspection: " + ex.Message; return false; }
        }
    }
}
