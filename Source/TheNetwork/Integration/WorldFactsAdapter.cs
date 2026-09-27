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
    /// Reads live factions and world facts for the Domain (ARCHITECTURE § 6.19). Called rarely
    /// (generation, the contacts list); the faction list is cached until the tick moves on by an hour
    /// or the faction count changes.
    /// </summary>
    public sealed class WorldFactsAdapter : IWorldFacts
    {
        private List<FactionFacts> cached;
        private int cachedAtTick = int.MinValue;
        private int cachedFactionCount = -1;

        public List<FactionFacts> LiveFactions()
        {
            FactionManager fm = Find.FactionManager;
            if (fm == null) return new List<FactionFacts>();
            int now = Find.TickManager?.TicksGame ?? 0;
            List<Faction> all = fm.AllFactionsListForReading;
            if (cached != null && cachedFactionCount == all.Count && now - cachedAtTick < Ticks.PerHour && now >= cachedAtTick) return cached;

            List<PlanetTile> homes = PlayerHomeTiles();
            Dictionary<int, int> nearest = NearestSettlementTiles(homes);
            Faction mechs = fm.OfMechanoids;
            SitePartDef outpost = DefDatabase<SitePartDef>.GetNamedSilentFail("Outpost");
            List<FactionFacts> list = new List<FactionFacts>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                Faction f = all[i];
                if (f == null || f.def == null) continue;
                FactionFacts ff = Facts(f, mechs, outpost);
                int d;
                if (nearest.TryGetValue(f.loadID, out d)) ff.nearestSettlementTiles = d;
                list.Add(ff);
            }
            cached = list;
            cachedAtTick = now;
            cachedFactionCount = all.Count;
            return list;
        }

        public void Invalidate()
        {
            cached = null;
        }

        private static FactionFacts Facts(Faction f, Faction mechs, SitePartDef outpost)
        {
            FactionFacts ff = new FactionFacts
            {
                loadId = f.loadID,
                name = f.Name,
                defName = f.def.defName,
                defLabel = f.def.label,
                defPackageId = ModClue.PackageOf(f.def),
                defIsLudeon = f.def.modContentPack != null && f.def.modContentPack.IsOfficialMod,
                techLevel = (int)f.def.techLevel,
                isPlayer = f.IsPlayer,
                defeated = f.defeated,
                hidden = f.Hidden,
                temporary = f.temporary,
                humanlike = f.def.humanlikeFaction,
                permanentEnemy = f.def.permanentEnemy,
                isMechanoid = mechs != null && f == mechs
            };
            if (!f.IsPlayer)
            {
                Faction player = Faction.OfPlayerSilentFail;
                try
                {
                    ff.hostileToPlayer = player != null && f.HostileTo(player);
                    ff.goodwill = player != null ? f.PlayerGoodwill : 0;
                }
                catch (Exception)
                {
                    ff.hostileToPlayer = false;
                }
                try
                {
                    ff.canGuardSite = !f.defeated && !f.Hidden && !f.temporary && f.def.humanlikeFaction
                        && (outpost == null || outpost.FactionCanOwn(f))
                        && PawnGroupMakerUtility.CanGenerateAnyNormalGroup(f, 300f);
                }
                catch (Exception)
                {
                    ff.canGuardSite = false;
                }
            }
            return ff;
        }

        public FactionFacts PlayerFaction()
        {
            Faction p = Faction.OfPlayerSilentFail;
            if (p == null) return null;
            return new FactionFacts { loadId = p.loadID, name = p.Name, defName = p.def?.defName, defPackageId = ModClue.PackageOf(p.def), isPlayer = true, humanlike = true };
        }

        public float BaseThreatPoints()
        {
            try
            {
                return StorytellerUtility.DefaultSiteThreatPointsNow();
            }
            catch (Exception)
            {
                return 300f;
            }
        }

        public string PickStuff(string thingDefName, int seed)
        {
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(thingDefName);
            if (def == null || !def.MadeFromStuff) return null;
            Rand.PushState(seed);
            try
            {
                ThingDef stuff = GenStuff.RandomStuffByCommonalityFor(def);
                return stuff?.defName ?? GenStuff.DefaultStuffFor(def)?.defName;
            }
            catch (Exception)
            {
                return GenStuff.DefaultStuffFor(def)?.defName;
            }
            finally
            {
                Rand.PopState();
            }
        }

        public int TilesFromPlayerHome(TileRef tile)
        {
            if (tile == null || !tile.IsValidNow) return -1;
            List<PlanetTile> homes = PlayerHomeTiles();
            int best = -1;
            PlanetTile t = tile.Tile;
            for (int i = 0; i < homes.Count; i++)
            {
                if (homes[i].Layer != t.Layer) continue;
                int d = (int)Math.Round(Find.WorldGrid.ApproxDistanceInTiles(homes[i], t));
                if (best < 0 || d < best) best = d;
            }
            return best;
        }

        public static List<PlanetTile> PlayerHomeTiles()
        {
            List<PlanetTile> list = new List<PlanetTile>();
            List<Map> maps = Find.Maps;
            if (maps == null) return list;
            for (int i = 0; i < maps.Count; i++)
            {
                if (maps[i].IsPlayerHome) list.Add(maps[i].Tile);
            }
            return list;
        }

        private static Dictionary<int, int> NearestSettlementTiles(List<PlanetTile> homes)
        {
            Dictionary<int, int> result = new Dictionary<int, int>();
            if (homes.Count == 0 || Find.WorldObjects == null) return result;
            List<Settlement> settlements = Find.WorldObjects.Settlements;
            for (int i = 0; i < settlements.Count; i++)
            {
                Settlement s = settlements[i];
                if (s.Faction == null || s.Faction.IsPlayer) continue;
                for (int h = 0; h < homes.Count; h++)
                {
                    if (homes[h].Layer != s.Tile.Layer) continue;
                    int d = (int)Math.Round(Find.WorldGrid.ApproxDistanceInTiles(homes[h], s.Tile));
                    int prev;
                    if (!result.TryGetValue(s.Faction.loadID, out prev) || d < prev) result[s.Faction.loadID] = d;
                }
            }
            return result;
        }
    }
}
