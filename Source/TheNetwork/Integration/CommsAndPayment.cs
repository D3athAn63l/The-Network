using System;
using System.Collections.Generic;
using RimWorld;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Integration
{
    /// <summary>
    /// The Comms Console gate (ARCHITECTURE § 9, ADR-032): the player can contact the Network when a
    /// spawned <see cref="Building_CommsConsole"/> (any subclass, so modded consoles count) on a player
    /// home map reports <c>CanUseCommsNow</c> (powered, no electricity-disabling condition). Reading
    /// never needs a console, and losing one never touches anything in progress.
    /// </summary>
    public sealed class CommsAccessAdapter : ICommsAccess
    {
        public const string NoConsole = "NoUsableCommsConsole";
        public const string Unpowered = "CommsConsoleUnpowered";
        public const string ElectricityDisabled = "CommsElectricityDisabled";

        public bool CanContact(out string reasonKey)
        {
            reasonKey = NoConsole;
            List<Map> maps = Find.Maps;
            if (maps == null) return false;
            bool anyConsole = false;
            bool anyDisabledByCondition = false;
            for (int i = 0; i < maps.Count; i++)
            {
                Map map = maps[i];
                if (!map.IsPlayerHome) continue;
                foreach (Building_CommsConsole console in map.listerBuildings.AllBuildingsColonistOfClass<Building_CommsConsole>())
                {
                    if (!console.Spawned) continue;
                    anyConsole = true;
                    if (console.CanUseCommsNow)
                    {
                        reasonKey = null;
                        return true;
                    }
                    if (map.gameConditionManager.ElectricityDisabled(map)) anyDisabledByCondition = true;
                }
            }
            if (anyConsole) reasonKey = anyDisabledByCondition ? ElectricityDisabled : Unpowered;
            return false;
        }
    }

    /// <summary>
    /// Silver in and out (ARCHITECTURE § 6.19). Fees are taken with vanilla's beacon mechanism
    /// (<see cref="TradeUtility.LaunchSilver"/> from the home map with the most beacon-reachable
    /// silver); refunds arrive as silver by drop pod at a home map's trade drop spot. Spike S4 decides
    /// the final UX; this is the documented baseline.
    /// </summary>
    public sealed class PaymentAdapter : IPayment
    {
        public const string NoHomeMap = "NoHomeMap";
        public const string NotEnoughSilver = "NotEnoughBeaconSilver";

        public bool CanCharge(int amount, out string reasonKey)
        {
            reasonKey = null;
            if (amount <= 0) return true;
            Map map = PayingMap();
            if (map == null)
            {
                reasonKey = NoHomeMap;
                return false;
            }
            if (!TradeUtility.ColonyHasEnoughSilver(map, amount))
            {
                reasonKey = NotEnoughSilver;
                return false;
            }
            return true;
        }

        public bool TryCharge(int amount, out string reasonKey)
        {
            if (!CanCharge(amount, out reasonKey)) return false;
            if (amount <= 0) return true;
            try
            {
                TradeUtility.LaunchSilver(PayingMap(), amount);
                return true;
            }
            catch (Exception ex)
            {
                reasonKey = "PaymentFailed";
                NetLog.ErrorOnce(LogCategory.Payment, "charge", "Charging " + amount + " silver failed: " + ex);
                return false;
            }
        }

        public bool TryRefund(int amount, out string reasonKey)
        {
            reasonKey = null;
            if (amount <= 0) return true;
            Map map = RefundMap();
            if (map == null)
            {
                reasonKey = NoHomeMap;
                return false;
            }
            try
            {
                List<Thing> silver = new List<Thing>();
                int left = amount;
                while (left > 0)
                {
                    Thing s = ThingMaker.MakeThing(ThingDefOf.Silver);
                    s.stackCount = Math.Min(left, ThingDefOf.Silver.stackLimit);
                    left -= s.stackCount;
                    silver.Add(s);
                }
                IntVec3 spot = DropCellFinder.TradeDropSpot(map);
                DropPodUtility.DropThingsNear(spot, map, silver, 110, false, false, true, false);
                return true;
            }
            catch (Exception ex)
            {
                reasonKey = "RefundFailed";
                NetLog.ErrorOnce(LogCategory.Payment, "refund", "Refunding " + amount + " silver failed: " + ex);
                return false;
            }
        }

        private static Map PayingMap()
        {
            try
            {
                return TradeUtility.PlayerHomeMapWithMostLaunchableSilver();
            }
            catch (Exception)
            {
                return RefundMap();
            }
        }

        private static Map RefundMap()
        {
            Map best = null;
            List<Map> maps = Find.Maps;
            if (maps == null) return null;
            for (int i = 0; i < maps.Count; i++)
            {
                if (!maps[i].IsPlayerHome) continue;
                if (best == null || maps[i].mapPawns.FreeColonistsSpawnedCount > best.mapPawns.FreeColonistsSpawnedCount) best = maps[i];
            }
            return best;
        }
    }
}
