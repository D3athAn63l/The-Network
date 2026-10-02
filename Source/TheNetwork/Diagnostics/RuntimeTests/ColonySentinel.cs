using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace TheNetwork.Diagnostics.RuntimeTests
{
    /// <summary>
    /// The RimWorld half of the live safety fingerprint: a small, READ-ONLY, bounded sample of the colony and world state that the runtime
    /// tests promise never to change. It is added to the Network's durable fingerprint (<see cref="LiveFingerprint"/>) before and after every
    /// slice, so a test that accidentally spent silver, spawned or deleted cargo, destroyed a world object or sent a letter has a realistic
    /// chance of being caught (RT-INFRA-001).
    ///
    /// What it covers, per player home map: the beacon-reachable silver (the same query the payment adapter charges against), the number
    /// of spawned things, and a hash of every haulable item (identity, def and stack count: cargo appearing, vanishing or being spent).
    /// World-wide: the player home count, every world object (id, def, tile, faction), and the letter and archive counts.
    ///
    /// What it does NOT cover, on purpose: pawn stats, every Thing property, terrain, buildings, research, relations, storyteller state. It
    /// is a tripwire for the specific effects a runtime test could cause, not a proof that the whole game is untouched. It never scans
    /// pawns and never changes anything.
    /// </summary>
    public static class ColonySentinel
    {
        /// <summary>Above this many haulable items on one map only the count and the stack total are taken (keeps a huge colony cheap).</summary>
        public const int MaxItemsHashed = 100000;

        private const long Seed = 1469598103934665603L;

        private static long Mix(long h, long v)
        {
            unchecked
            {
                h ^= v;
                h *= 1099511628211L;
                h ^= (long)((ulong)h >> 29);
                return h;
            }
        }

        public static void AddTo(LiveFingerprint f)
        {
            int homes = 0;
            long homeIds = Seed, silverTotal = 0;
            List<Map> maps = Find.Maps;
            for (int m = 0; m < maps.Count; m++)
            {
                Map map = maps[m];
                if (!map.IsPlayerHome) continue;
                homes++;
                int id = map.uniqueID;
                homeIds = Mix(homeIds, id);

                long silver = 0;
                foreach (Thing t in TradeUtility.AllLaunchableThingsForTrade(map)) if (t.def == ThingDefOf.Silver) silver += t.stackCount;
                silverTotal += silver;
                f.With("colony.map" + id + ".silverByBeacon", silver);
                f.With("colony.map" + id + ".spawnedThings", map.listerThings.AllThings.Count);

                List<Thing> items = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver);
                long stacks = 0, hash = Seed;
                bool hashEach = items.Count <= MaxItemsHashed;
                for (int i = 0; i < items.Count; i++)
                {
                    Thing t = items[i];
                    stacks += t.stackCount;
                    if (hashEach) hash = Mix(Mix(Mix(hash, t.thingIDNumber), t.def.shortHash), t.stackCount);
                }
                f.With("colony.map" + id + ".items", items.Count);
                f.With("colony.map" + id + ".itemStacks", stacks);
                f.With("colony.map" + id + ".itemsHash", hashEach ? hash : 0);
            }
            f.With("colony.homeMaps", homes);
            f.With("colony.homeMapIds", homeIds);
            f.With("colony.silverByBeacon.total", silverTotal);

            List<WorldObject> objects = Find.WorldObjects.AllWorldObjects;
            long wh = Seed;
            for (int i = 0; i < objects.Count; i++)
            {
                WorldObject o = objects[i];
                wh = Mix(Mix(Mix(Mix(wh, o.ID), o.def.shortHash), o.Tile), o.Faction == null ? -1 : o.Faction.loadID);
            }
            f.With("world.objects", objects.Count);
            f.With("world.objectsHash", wh);

            f.With("colony.letters", Find.LetterStack.LettersListForReading.Count);
            f.With("colony.archive", Find.Archive.ArchivablesListForReading.Count);
        }
    }
}
