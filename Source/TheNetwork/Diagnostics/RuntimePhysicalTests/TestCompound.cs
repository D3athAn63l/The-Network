using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using TheNetwork.Core;
using TheNetwork.Domain.Physical;
using TheNetwork.Integration.Physical;
using Verse;
using Verse.AI;

namespace TheNetwork.Diagnostics.RuntimePhysicalTests
{
    /// <summary>Real vanilla structures on the suite's exact disposable map. All ownership is derived from that map and fixed cells.</summary>
    public static class TestCompound
    {
        public const int MinimumMapSide = 60;
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

        private static IEnumerable<IntVec3> ConstructionCells(IntVec3 center)
        {
            foreach (IntVec3 cell in VisitorCells(center)) yield return cell;
            foreach (IntVec3 cell in PrisonFootprint(center)) yield return cell;
        }

        private static bool IsConstructionCell(IntVec3 center, IntVec3 cell)
        {
            return PrisonFootprint(center).Contains(cell)
                || (cell.x >= center.x - VisitorRadius && cell.x <= center.x + VisitorRadius
                    && cell.z >= center.z - VisitorRadius && cell.z <= center.z + VisitorRadius);
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
            return pieces;
        }

        private static string ScopeRefusal(Map map)
        {
            if (map == null || !TestSite.IsTestMap(map) || !ReferenceEquals(map, TestSite.Map)) return "construction target is not the exact current TestSite map";
            MapParent parent = map.Parent;
            if (parent == null || parent.GetType() != typeof(MapParent) || parent.Faction != null || map.IsPlayerHome)
                return "TestSite must remain a plain factionless non-home MapParent";
            string defRefusal = TestSite.DefRefusal(parent.def);
            if (defRefusal != null) return defRefusal;
            if (map.Size.x < MinimumMapSide || map.Size.z < MinimumMapSide) return "TestSite is smaller than the audited 60-square layout";
            if (map.regionAndRoomUpdater == null || !map.regionAndRoomUpdater.Enabled || map.reachability == null)
                return "the actual map region/room/path services are unavailable";
            return null;
        }

        private static string PlanRefusal(Map map)
        {
            if (ThingDefOf.Wall == null || ThingDefOf.Door == null || ThingDefOf.Bed == null || ThingDefOf.BlocksGranite == null
                || ThingDefOf.WoodLog == null || TerrainDefOf.Soil == null || RoofDefOf.RoofConstructed == null || Faction.OfPlayer == null)
                return "the vanilla structure, terrain, roof or player-faction definitions are unavailable";
            if (ThingDefOf.Wall.size != new IntVec2(1, 1) || ThingDefOf.Door.size != new IntVec2(1, 1) || ThingDefOf.Bed.size != new IntVec2(1, 2))
                return "patched vanilla structure dimensions do not match the bounded compound plan";
            foreach (IntVec3 cell in ConstructionCells(map.Center)) if (!cell.InBounds(map)) return "compound footprint is outside map bounds at " + cell;
            foreach (Piece piece in Plan(map.Center))
                foreach (IntVec3 cell in piece.Footprint)
                    if (!cell.InBounds(map) || !PrisonFootprint(map.Center).Contains(cell)) return "structure footprint is outside the preflighted prison at " + cell;
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

        private static bool NaturalObstacle(Thing thing)
        {
            if (thing == null || thing is Pawn || thing.Faction != null || (thing.questTags != null && thing.questTags.Count > 0)) return false;
            return thing.def.category == ThingCategory.Plant || thing.def.building?.isNaturalRock == true
                || (ThingCategoryDefOf.StoneChunks != null && thing.def.IsWithinCategory(ThingCategoryDefOf.StoneChunks));
        }

        /// <summary>Valid sites return before any write. Invalid sites are rebuilt only after the complete bounded footprint is proven safe.</summary>
        public static bool Ensure(Map map, out string report)
        {
            string refusal = ScopeRefusal(map) ?? (map == null ? null : PlanRefusal(map));
            if (refusal != null) { report = "compound refused: " + refusal; return false; }
            if (Validate(map, out report)) return true;
            List<Piece> pieces = Plan(map.Center);
            HashSet<Thing> natural = new HashSet<Thing>();
            HashSet<Thing> structures = new HashSet<Thing>();
            foreach (Piece piece in pieces)
            {
                Thing existing = Existing(map, piece);
                if (existing != null) structures.Add(existing);
            }
            // This entire pass finishes before clearing, terrain/roof writes, construction or designation.
            foreach (IntVec3 cell in ConstructionCells(map.Center))
                foreach (Thing thing in map.thingGrid.ThingsListAt(cell))
                {
                    if (thing is Pawn) { report = "compound refused: a Pawn occupies the construction footprint at " + cell; return false; }
                    if (structures.Contains(thing))
                    {
                        if (thing is Building_Door door && (door.Open || door.HoldOpen || door.FreePassage))
                        { report = "compound refused: the existing prison door is open; no custody repair was attempted"; return false; }
                        if (thing is Building_Bed bed && (bed.Medical || (!bed.ForPrisoners && bed.OwnersForReading.Count > 0)))
                        { report = "compound refused: existing bed designation/owners cannot be changed safely"; return false; }
                        continue;
                    }
                    if (!NaturalObstacle(thing)) { report = "compound refused: unexpected Thing " + thing.def.defName + " at " + cell; return false; }
                    foreach (IntVec3 occupied in GenAdj.OccupiedRect(thing.Position, thing.Rotation, thing.def.size))
                        if (!IsConstructionCell(map.Center, occupied))
                        { report = "compound refused: natural obstacle extends outside the bounded construction footprint"; return false; }
                    natural.Add(thing);
                }
            try
            {
                // Remove overhead natural roof before clearing its supporting rocks; the operation is confined to the proven empty footprint.
                foreach (IntVec3 cell in ConstructionCells(map.Center)) map.roofGrid.SetRoof(cell, null);
                foreach (Thing thing in natural) thing.Destroy(DestroyMode.Vanish);
                foreach (IntVec3 cell in ConstructionCells(map.Center)) map.terrainGrid.SetTerrain(cell, TerrainDefOf.Soil);
                foreach (Piece piece in pieces)
                {
                    Thing thing = Existing(map, piece);
                    if (thing == null)
                    {
                        thing = ThingMaker.MakeThing(piece.def, piece.stuff);
                        thing.SetFaction(Faction.OfPlayer);
                        GenSpawn.Spawn(thing, piece.cell, map, Rot4.North);
                    }
                    if (thing is Building_Bed bed && !bed.ForPrisoners) bed.ForPrisoners = true;
                }
                foreach (IntVec3 cell in PrisonFootprint(map.Center)) map.roofGrid.SetRoof(cell, RoofDefOf.RoofConstructed);
                map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                foreach (IntVec3 cell in PrisonBedCells(map.Center))
                {
                    Building_Bed bed = BedAt(map, cell);
                    // Same public notifications used by vanilla's bed-owner-type interface; bed designation alone only dirties room stats.
                    bed?.GetDistrict()?.Notify_RoomShapeOrContainedBedsChanged();
                    bed?.GetRoom()?.Notify_RoomShapeChanged();
                }
                return Validate(map, out report);
            }
            catch (Exception ex)
            {
                report = "compound setup failed; existing objects preserved for inspection: " + ex.Message;
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
            if (refusal != null) { report = "compound invalid: " + refusal; return false; }
            foreach (Piece piece in Plan(map.Center))
                if (Existing(map, piece) == null) { report = "compound invalid: expected " + piece.def.defName + " missing or changed at " + piece.cell; return false; }
            Building_Door door = DoorAt(map);
            if (door == null || door.Open || door.HoldOpen || door.FreePassage)
            { report = "compound invalid: real player-owned prison door must already be closed"; return false; }
            Room room = null;
            foreach (IntVec3 cell in PrisonBedCells(map.Center))
            {
                Building_Bed bed = BedAt(map, cell);
                Room actual = bed?.GetRoom();
                if (bed == null || !bed.ForPrisoners || bed.Medical || actual == null || !actual.IsPrisonCell
                    || !actual.ProperRoom || actual.TouchesMapEdge || !Building_Bed.RoomCanBePrisonCell(actual) || (room != null && !ReferenceEquals(room, actual)))
                { report = "compound invalid: vanilla prison room/bed recognition failed at " + cell; return false; }
                room = actual;
            }
            foreach (IntVec3 cell in PrisonInterior(map.Center))
                // Real Bed is PassThroughOnly: its verified footprint is walkable, while every free interior cell must be standable.
                if (!(IsPlannedBedCell(map.Center, cell) ? cell.Walkable(map) : cell.Standable(map))
                    || !ReferenceEquals(cell.GetRoom(map), room) || map.roofGrid.RoofAt(cell) != RoofDefOf.RoofConstructed)
                { report = "compound invalid: enclosed prison interior is obstructed or changed at " + cell; return false; }
            int visitors = 0;
            foreach (IntVec3 cell in VisitorCells(map.Center))
            {
                if (!cell.Standable(map) || PrisonInterior(map.Center).Contains(cell))
                { report = "compound invalid: visitor courtyard is not safely walkable at " + cell; return false; }
                visitors++;
            }
            TraverseParms closedDoors = TraverseParms.For(TraverseMode.NoPassClosedDoors, Danger.Deadly);
            if (visitors < PhysicalLifecycleService.MaxMembers || !map.reachability.CanReachMapEdge(map.Center, closedDoors)
                || map.reachability.CanReachMapEdge(PrisonInterior(map.Center).CenterCell, closedDoors))
            { report = "compound invalid: courtyard edge path or closed prison enclosure failed vanilla reachability"; return false; }
            report = "compound validated on TestSite map " + map.uniqueID + " (" + map.Size.x + "x" + map.Size.z
                + "): 13x13 open courtyard, east 9x9 prison / 7x7 enclosed interior, closed player-owned door and two real prisoner beds; non-home disposable parent";
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
