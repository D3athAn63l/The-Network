using System;
using System.Collections.Generic;
using RimWorld;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using Verse;

namespace TheNetwork.Integration
{
    /// <summary>
    /// Phase 2 delivery by vanilla drop pods (STATE_MACHINES § 4.4, spike S13). The preferred home map
    /// if it still exists, else another player home map; no home at all, or no valid spot anywhere,
    /// reports a failure the Domain turns into a retry or a Hold. Spots are searched without roof
    /// punching and never at random, so a failed search is reported instead of dropping pods blindly.
    /// Things are created from the committed payload only when the pods launch.
    /// </summary>
    public sealed class DropPodDelivery : IDelivery
    {
        public int DefaultHomeMapId(out string label)
        {
            label = null;
            Map m = Find.CurrentMap;
            if (m == null || !m.IsPlayerHome) m = Find.AnyPlayerHomeMap;
            if (m == null) return -1;
            label = LabelOf(m);
            return m.uniqueID;
        }

        public DeliveryPlan Plan(int preferredMapId, int seed)
        {
            List<Map> homes = Homes(preferredMapId);
            if (homes.Count == 0) return new DeliveryPlan { failureKey = "NoHomeMap" };
            for (int i = 0; i < homes.Count; i++)
            {
                IntVec3 cell;
                if (!TryFindSpot(homes[i], NetHash.Combine(seed, homes[i].uniqueID), out cell)) continue;
                return new DeliveryPlan
                {
                    ok = true,
                    mapId = homes[i].uniqueID,
                    mapLabel = LabelOf(homes[i]),
                    cellX = cell.x,
                    cellZ = cell.z,
                    rerouted = homes[i].uniqueID != preferredMapId
                };
            }
            return new DeliveryPlan { failureKey = "NoDropSpot" };
        }

        public DeliveryResult Deliver(DeliveryPlan plan, List<ItemPayload> payload, int seed)
        {
            DeliveryResult r = new DeliveryResult { mapId = plan.mapId, mapLabel = plan.mapLabel };
            Map map = null;
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++) if (maps[i].uniqueID == plan.mapId && maps[i].IsPlayerHome) map = maps[i];
            if (map == null)
            {
                r.failureKey = "MapGone";
                return r;
            }
            IntVec3 cell = new IntVec3(plan.cellX, 0, plan.cellZ);
            if (!cell.InBounds(map) || !DropCellFinder.IsGoodDropSpot(cell, map, false, false, false))
            {
                if (!TryFindSpot(map, NetHash.Combine(seed, map.uniqueID), out cell))
                {
                    r.failureKey = "NoDropSpot";
                    return r;
                }
            }
            List<Thing> things = new List<Thing>();
            for (int i = 0; i < payload.Count; i++)
            {
                string failure;
                if (!SiteAdapter.MakeThings(payload[i], NetHash.Combine(seed, "things." + i), things, out failure))
                {
                    Discard(things);
                    r.failureKey = failure;
                    r.thingCreationFailed = failure != "DefMissing";
                    r.failedDefName = payload[i].thing?.defName;
                    return r;
                }
            }
            int count = 0;
            for (int i = 0; i < things.Count; i++)
            {
                MinifiedThing m = things[i] as MinifiedThing;
                count += m != null ? 1 : things[i].stackCount;
            }
            Rand.PushState(NetHash.Combine(seed, "pods"));
            try
            {
                DropPodUtility.DropThingsNear(cell, map, things, 110, false, false, false, false, false);
            }
            catch (Exception ex)
            {
                NetLog.ErrorOnce(LogCategory.Delivery, "droppods", "Drop pod delivery failed on " + LabelOf(map) + ": " + ex);
                Discard(things);
                r.failureKey = "DropFailed";
                return r;
            }
            finally
            {
                Rand.PopState();
            }
            r.ok = true;
            r.delivered = count;
            return r;
        }

        /// <summary>Player home maps, the preferred one first, then in a stable order.</summary>
        private static List<Map> Homes(int preferredMapId)
        {
            List<Map> list = new List<Map>();
            List<Map> maps = Find.Maps;
            if (maps == null) return list;
            for (int i = 0; i < maps.Count; i++) if (maps[i] != null && maps[i].IsPlayerHome) list.Add(maps[i]);
            list.Sort((a, b) =>
            {
                if (a.uniqueID == preferredMapId) return -1;
                if (b.uniqueID == preferredMapId) return 1;
                return a.uniqueID.CompareTo(b.uniqueID);
            });
            return list;
        }

        /// <summary>
        /// Near an unroofed orbital trade beacon or comms console first, then any colony building, then the
        /// map centre. Never roof-punching, never fogged, never indoors.
        /// </summary>
        private static bool TryFindSpot(Map map, int seed, out IntVec3 cell)
        {
            cell = IntVec3.Invalid;
            List<IntVec3> centers = new List<IntVec3>();
            List<Building> buildings = map.listerBuildings?.allBuildingsColonist;
            if (buildings != null)
            {
                for (int i = 0; i < buildings.Count; i++) if (buildings[i].def.IsOrbitalTradeBeacon) centers.Add(buildings[i].Position);
                for (int i = 0; i < buildings.Count; i++) if (buildings[i].def.IsCommsConsole) centers.Add(buildings[i].Position);
                for (int i = 0; i < buildings.Count && centers.Count < 12; i++) centers.Add(buildings[i].Position);
            }
            centers.Add(map.Center);
            Rand.PushState(seed);
            try
            {
                for (int i = 0; i < centers.Count; i++)
                {
                    if (DropCellFinder.TryFindDropSpotNear(centers[i], map, out cell, false, false, false)) return true;
                }
            }
            catch (Exception ex)
            {
                NetLog.WarnOnce(LogCategory.Delivery, "dropspot:" + map.uniqueID, "Could not search a drop spot on " + LabelOf(map) + ": " + ex.Message);
            }
            finally
            {
                Rand.PopState();
            }
            cell = IntVec3.Invalid;
            return false;
        }

        private static string LabelOf(Map m)
        {
            string s = m.Parent?.LabelCap;
            return string.IsNullOrEmpty(s) ? "map " + m.uniqueID : s;
        }

        private static void Discard(List<Thing> things)
        {
            for (int i = 0; i < things.Count; i++)
            {
                try
                {
                    if (!things[i].Destroyed) things[i].Destroy(DestroyMode.Vanish);
                }
                catch (Exception)
                {
                    // Unspawned and unheld: nothing else to clean up.
                }
            }
            things.Clear();
        }
    }
}
