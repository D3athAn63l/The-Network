using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Integration
{
    /// <summary>
    /// The RimWorld world graph for the spatial layer (SPATIAL § 4). Every call is read-only: it never
    /// creates a WorldObject, a caravan or a path that outlives the call. Randomness runs under
    /// <c>Rand.PushState(seed)</c>, so the same world and seed give the same answer. Route queries check
    /// reachability first, so vanilla pathing never logs for an impossible journey; a query that still
    /// fails reports a reason key and the Domain degrades softly.
    /// </summary>
    public sealed class SpatialWorldAdapter : ISpatialWorld
    {
        private List<SettlementFacts> settlements;
        private int settlementsAtTick = int.MinValue;
        private int settlementsCount = -1;

        public bool Ready
        {
            get
            {
                try
                {
                    return Find.World != null && Find.WorldGrid != null && Find.WorldGrid.Surface != null;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public bool IsValid(TileRef tile)
        {
            if (tile == null) return false;
            try
            {
                return tile.IsValidNow;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool IsPassable(TileRef tile)
        {
            if (!IsValid(tile)) return false;
            try
            {
                return !Find.World.Impassable(tile.Tile);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public List<SettlementFacts> Settlements()
        {
            WorldObjectsHolder holder = Find.WorldObjects;
            if (holder == null) return new List<SettlementFacts>();
            int now = Find.TickManager?.TicksGame ?? 0;
            List<Settlement> all = holder.Settlements;
            if (settlements != null && settlementsCount == all.Count && now >= settlementsAtTick && now - settlementsAtTick < Ticks.PerHour) return settlements;
            List<SettlementFacts> list = new List<SettlementFacts>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                Settlement s = all[i];
                if (s == null || s.Destroyed || s.Faction == null) continue;
                try
                {
                    PlanetTile t = s.Tile;
                    if (!t.Valid || t.Layer == null || t.Layer.Def == null || t.Layer.Def.isSpace) continue;
                    TileRef r = TileRef.Of(t);
                    if (r == null) continue;
                    list.Add(new SettlementFacts { tile = r, factionLoadId = s.Faction.loadID, player = s.Faction.IsPlayer });
                }
                catch (Exception)
                {
                    // A modded settlement we cannot read is simply not evidence.
                }
            }
            settlements = list;
            settlementsAtTick = now;
            settlementsCount = all.Count;
            return list;
        }

        public bool TryFindPassableNear(TileRef center, int minDist, int maxDist, int seed, out TileRef tile)
        {
            tile = null;
            if (!IsValid(center) || maxDist < minDist) return false;
            PlanetTile found;
            Rand.PushState(seed);
            try
            {
                if (!TileFinder.TryFindPassableTileWithTraversalDistance(center.Tile, Math.Max(0, minDist), maxDist, out found, t => !Find.World.Impassable(t), true, TileFinderMode.Random, false, false)) return false;
            }
            catch (Exception ex)
            {
                NetLog.WarnOnce(LogCategory.Spatial, "spatial.near", "Local tile search failed: " + ex.Message);
                return false;
            }
            finally
            {
                Rand.PopState();
            }
            tile = TileRef.Of(found);
            return tile != null;
        }

        public bool TryFindAnyPassable(int seed, out TileRef tile)
        {
            tile = null;
            try
            {
                SurfaceLayer surface = Find.WorldGrid?.Surface;
                if (surface == null || surface.TilesCount <= 0) return false;
                Rand.PushState(seed);
                try
                {
                    for (int i = 0; i < 400; i++)
                    {
                        PlanetTile t = new PlanetTile(Rand.Range(0, surface.TilesCount), surface);
                        if (Find.World.Impassable(t) || Find.WorldGrid[t].WaterCovered) continue;
                        tile = TileRef.Of(t);
                        return tile != null;
                    }
                }
                finally
                {
                    Rand.PopState();
                }
            }
            catch (Exception ex)
            {
                NetLog.WarnOnce(LogCategory.Spatial, "spatial.any", "Fallback tile search failed: " + ex.Message);
            }
            return false;
        }

        public bool TryRoute(TileRef from, TileRef to, int maxSteps, List<int> steps, out string failureKey)
        {
            steps.Clear();
            failureKey = null;
            if (!IsValid(from) || !IsValid(to))
            {
                failureKey = "InvalidTile";
                return false;
            }
            if (from.layerId != to.layerId)
            {
                failureKey = "CrossLayer";
                return false;
            }
            if (from.tileId == to.tileId) return true;
            WorldPath path = null;
            try
            {
                PlanetTile start = from.Tile, end = to.Tile;
                if (Find.World.Impassable(end) || !Find.WorldReachability.CanReach(start, end))
                {
                    failureKey = "Unreachable";
                    return false;
                }
                path = start.Layer.Pather.FindPath(start, end, null);
                if (path == null || !path.Found)
                {
                    failureKey = "NoPath";
                    return false;
                }
                List<PlanetTile> nodes = path.NodesReversed;
                // NodesReversed runs from the destination (index 0) back to the start (last index).
                for (int i = nodes.Count - 2; i >= 0; i--) steps.Add(nodes[i].tileId);
                if (steps.Count > maxSteps)
                {
                    steps.Clear();
                    failureKey = "TooFar";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                steps.Clear();
                failureKey = "RouteError";
                NetLog.WarnOnce(LogCategory.Spatial, "spatial.route", "Route query failed: " + ex.Message);
                return false;
            }
            finally
            {
                if (path != null && path.Found) path.ReleaseToPool();
            }
        }

        public TileRef OnLayerOf(TileRef sameLayer, int tileId)
        {
            if (sameLayer == null) return null;
            try
            {
                return TileRef.Of(new PlanetTile(tileId, sameLayer.layerId)) ?? new TileRef { tileId = tileId, layerId = sameLayer.layerId, layerDef = sameLayer.layerDef };
            }
            catch (Exception)
            {
                return new TileRef { tileId = tileId, layerId = sameLayer.layerId, layerDef = sameLayer.layerDef };
            }
        }

        public int ApproxDistance(TileRef a, TileRef b)
        {
            if (!IsValid(a) || !IsValid(b) || a.layerId != b.layerId) return int.MaxValue;
            try
            {
                return (int)Math.Round(Find.WorldGrid.ApproxDistanceInTiles(a.Tile, b.Tile));
            }
            catch (Exception)
            {
                return int.MaxValue;
            }
        }
    }
}
