using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace TheNetwork.Kernel
{
    // External references (DATA_MODEL § 2, ADR-004). Persisted as strings and IDs, resolved silently,
    // with a snapshot captured once so history still reads correctly after the target is gone. None of
    // them uses Scribe_Defs or Scribe_References.

    /// <summary>Per-session resolution caches (hits and misses) for every DefRef type.</summary>
    public static class DefRefCaches
    {
        private static readonly List<Action> clearers = new List<Action>();

        internal static void RegisterClearer(Action clear)
        {
            clearers.Add(clear);
        }

        /// <summary>Tests and "rebuild catalog" only: Defs never change during a normal session.</summary>
        public static void ClearAll()
        {
            for (int i = 0; i < clearers.Count; i++) clearers[i]();
        }
    }

    public static class DefResolver<T> where T : Def, new()
    {
        private static readonly Dictionary<string, T> cache = new Dictionary<string, T>();

        static DefResolver()
        {
            DefRefCaches.RegisterClearer(() => cache.Clear());
        }

        public static T Get(string defName)
        {
            if (string.IsNullOrEmpty(defName)) return null;
            T def;
            if (cache.TryGetValue(defName, out def)) return def;
            def = DefDatabase<T>.GetNamedSilentFail(defName);
            cache[defName] = def;
            return def;
        }
    }

    public static class ModClue
    {
        /// <summary>The source package of a Def, as evidence (never ownership). Null for runtime-made defs.</summary>
        public static string PackageOf(Def def)
        {
            ModContentPack pack = def?.modContentPack;
            if (pack == null) return null;
            string id = pack.PackageIdPlayerFacing;
            return string.IsNullOrEmpty(id) ? pack.PackageId : id;
        }

        public static string ModNameOf(Def def)
        {
            return def?.modContentPack?.Name;
        }
    }

    /// <summary>A Def referenced by defName. Missing targets resolve to null; the owner decides what that means.</summary>
    public sealed class DefRef<T> : IExposable where T : Def, new()
    {
        public string defName;
        public string label;
        public string packageId;
        public string modName;

        public DefRef()
        {
        }

        public DefRef(T def)
        {
            defName = def.defName;
            label = def.label;
            packageId = ModClue.PackageOf(def);
            modName = ModClue.ModNameOf(def);
        }

        public static DefRef<T> Of(T def)
        {
            return def == null ? null : new DefRef<T>(def);
        }

        public T Resolve()
        {
            return DefResolver<T>.Get(defName);
        }

        public bool IsResolvable => Resolve() != null;

        /// <summary>The label captured when the reference was made; still readable after removal.</summary>
        public string LabelSnapshot => string.IsNullOrEmpty(label) ? defName : label;

        public DefRef<T> Copy()
        {
            return new DefRef<T> { defName = defName, label = label, packageId = packageId, modName = modName };
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref defName, "defName");
            Scribe_Values.Look(ref label, "label");
            Scribe_Values.Look(ref packageId, "package");
            Scribe_Values.Look(ref modName, "mod");
        }

        public override string ToString()
        {
            return defName ?? "(null)";
        }
    }

    /// <summary>A faction referenced by loadID, with a snapshot (DATA_MODEL § 2).</summary>
    public sealed class FactionRef : IExposable
    {
        public int loadId = -1;
        public string name;
        public string defName;
        public string defPackageId;
        public bool wasPlayer;

        public FactionRef()
        {
        }

        public FactionRef(Faction faction)
        {
            loadId = faction.loadID;
            name = faction.Name;
            defName = faction.def?.defName;
            defPackageId = ModClue.PackageOf(faction.def);
            wasPlayer = faction.IsPlayer;
        }

        public static FactionRef Of(Faction faction)
        {
            return faction == null ? null : new FactionRef(faction);
        }

        public bool IsValid => loadId >= 0;

        /// <summary>Linear over live factions (tens). Resolution is rare: generation, validation, UI clicks.</summary>
        public Faction Resolve()
        {
            if (loadId < 0) return null;
            FactionManager fm = Find.FactionManager;
            if (fm == null) return null;
            List<Faction> all = fm.AllFactionsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].loadID == loadId) return all[i];
            }
            return null;
        }

        public string NameSnapshot => string.IsNullOrEmpty(name) ? (defName ?? "unknown faction") : name;

        public FactionRef Copy()
        {
            return new FactionRef { loadId = loadId, name = name, defName = defName, defPackageId = defPackageId, wasPlayer = wasPlayer };
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref loadId, "loadId", -1);
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref defName, "def");
            Scribe_Values.Look(ref defPackageId, "defPackage");
            Scribe_Values.Look(ref wasPlayer, "wasPlayer", false);
        }
    }

    /// <summary>A planet tile plus the layer def name, so a missing layer (Odyssey removed) is detectable.</summary>
    public sealed class TileRef : IExposable
    {
        public int tileId = -1;
        public int layerId;
        public string layerDef;
        public string regionKey;

        public TileRef()
        {
        }

        public static TileRef Of(PlanetTile tile)
        {
            if (!tile.Valid) return null;
            TileRef r = new TileRef { tileId = tile.tileId };
            try
            {
                PlanetLayer layer = tile.Layer;
                r.layerId = layer.LayerID;
                r.layerDef = layer.Def?.defName;
                r.regionKey = RegionKeyOf(tile, layer);
            }
            catch (Exception)
            {
                r.layerId = 0;
            }
            return r;
        }

        public PlanetTile Tile => new PlanetTile(tileId, layerId);

        /// <summary>
        /// True if the layer id still resolves to the same kind of layer (its def, when one was recorded)
        /// and the tile is inside it.
        /// </summary>
        public bool IsValidNow
        {
            get
            {
                if (tileId < 0) return false;
                WorldGrid grid = Find.WorldGrid;
                if (grid == null) return false;
                PlanetLayer layer;
                bool found = grid.PlanetLayers.TryGetValue(layerId, out layer) && layer != null;
                return ValidFor(tileId, layerDef, found, found ? layer.Def?.defName : null, found ? layer.TilesCount : 0);
            }
        }

        /// <summary>
        /// The check behind <see cref="IsValidNow"/>. A numeric layer id can be reused after DLC, mod or
        /// world-layer changes, so when the reference recorded its layer def, the layer found under that
        /// id must have the same def; otherwise the reference is invalid rather than silently pointing at
        /// a different layer. A reference with no recorded def (none was available when it was taken) is
        /// checked by id and range only, as before.
        /// </summary>
        public static bool ValidFor(int tileId, string recordedLayerDef, bool layerFound, string foundLayerDef, int foundTileCount)
        {
            if (tileId < 0 || !layerFound) return false;
            if (!string.IsNullOrEmpty(recordedLayerDef) && !string.Equals(recordedLayerDef, foundLayerDef, StringComparison.Ordinal)) return false;
            return tileId < foundTileCount;
        }

        /// <summary>
        /// Compact region quantization (DATA_MODEL § 7): layer id, latitude band (6), longitude sector (8).
        /// </summary>
        public static string RegionKeyOf(PlanetTile tile, PlanetLayer layer)
        {
            Vector2 longLat = layer.LongLatOf(tile);
            int lat = Mathf.Clamp((int)((longLat.y + 90f) / 30f), 0, 5);
            int lon = Mathf.Clamp((int)((longLat.x + 180f) / 45f), 0, 7);
            return "S" + layer.LayerID + ":R" + (lat * 8 + lon).ToString("00");
        }

        public TileRef Copy()
        {
            return new TileRef { tileId = tileId, layerId = layerId, layerDef = layerDef, regionKey = regionKey };
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref tileId, "tile", -1);
            Scribe_Values.Look(ref layerId, "layer", 0);
            Scribe_Values.Look(ref layerDef, "layerDef");
            Scribe_Values.Look(ref regionKey, "region");
        }

        public override string ToString()
        {
            return tileId + "," + layerId;
        }
    }

    /// <summary>A world object referenced by ID, with a snapshot.</summary>
    public sealed class WorldObjectRef : IExposable
    {
        public int id = -1;
        public string defName;
        public string label;
        public TileRef lastTile;

        public static WorldObjectRef Of(WorldObject wo)
        {
            if (wo == null) return null;
            return new WorldObjectRef { id = wo.ID, defName = wo.def?.defName, label = wo.Label, lastTile = TileRef.Of(wo.Tile) };
        }

        public bool IsValid => id >= 0;

        /// <summary>Linear over world objects; resolution is rare (validation, look-at clicks).</summary>
        public WorldObject Resolve()
        {
            if (id < 0) return null;
            WorldObjectsHolder holder = Find.WorldObjects;
            if (holder == null) return null;
            List<WorldObject> all = holder.AllWorldObjects;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].ID == id) return all[i];
            }
            return null;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id", -1);
            Scribe_Values.Look(ref defName, "def");
            Scribe_Values.Look(ref label, "label");
            Scribe_Deep.Look(ref lastTile, "lastTile");
        }
    }

    /// <summary>A display name captured once; names are display data, never identity.</summary>
    public sealed class NameSnapshot : IExposable
    {
        public string first;
        public string nick;
        public string last;
        public string display;

        public static NameSnapshot Org(string display)
        {
            return new NameSnapshot { display = display };
        }

        public static NameSnapshot Person(string first, string nick, string last)
        {
            return new NameSnapshot { first = first, nick = nick, last = last, display = Compose(first, nick, last) };
        }

        public static string Compose(string first, string nick, string last)
        {
            string s = first ?? "";
            if (!string.IsNullOrEmpty(nick)) s += (s.Length > 0 ? " " : "") + "\"" + nick + "\"";
            if (!string.IsNullOrEmpty(last)) s += (s.Length > 0 ? " " : "") + last;
            return s;
        }

        public string Display => string.IsNullOrEmpty(display) ? Compose(first, nick, last) : display;

        /// <summary>Short form for letters: the nickname if any, else the first name, else the display.</summary>
        public string Short => !string.IsNullOrEmpty(nick) ? nick : (!string.IsNullOrEmpty(first) ? first : Display);

        public NameSnapshot Copy()
        {
            return new NameSnapshot { first = first, nick = nick, last = last, display = display };
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref first, "first");
            Scribe_Values.Look(ref nick, "nick");
            Scribe_Values.Look(ref last, "last");
            Scribe_Values.Look(ref display, "display");
        }

        public override string ToString()
        {
            return Display;
        }
    }
}
