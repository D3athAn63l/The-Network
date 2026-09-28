using System;
using System.Collections.Generic;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;

namespace TheNetwork.Diagnostics
{
    /// <summary>
    /// A synthetic world graph for the soak harness and the headless tests (never used in a game): a
    /// rectangular grid per layer, 8-neighbour moves, impassable tiles (sea), settlements. Routes are
    /// deterministic breadth-first paths; local searches are seeded. Layer 0 is the surface; extra
    /// layers (for cross-layer checks) have no settlements and no routes to other layers.
    /// </summary>
    public sealed class GridWorldGraph : ISpatialWorld
    {
        public readonly int width;
        public readonly int height;
        public readonly Dictionary<int, string> layers = new Dictionary<int, string> { { 0, "Surface" } };
        public readonly HashSet<int> impassable = new HashSet<int>();
        public readonly List<SettlementFacts> settlements = new List<SettlementFacts>();
        public bool ready = true;

        /// <summary>Route queries answered (tests read it to prove caching).</summary>
        public int routeQueries;

        public GridWorldGraph(int width, int height)
        {
            this.width = width;
            this.height = height;
        }

        public int Count => width * height;

        public TileRef Tile(int x, int y, int layer = 0)
        {
            return Ref(y * width + x, layer);
        }

        public int X(TileRef t) => t.tileId % width;
        public int Y(TileRef t) => t.tileId / width;

        private TileRef Ref(int id, int layer)
        {
            string def;
            layers.TryGetValue(layer, out def);
            int x = id % width, y = id / width;
            return new TileRef { tileId = id, layerId = layer, layerDef = def, regionKey = "S" + layer + ":R" + ((y * 6 / Math.Max(1, height)) * 8 + x * 8 / Math.Max(1, width)).ToString("00") };
        }

        public void AddSettlement(int x, int y, int factionLoadId, bool player = false)
        {
            settlements.Add(new SettlementFacts { tile = Tile(x, y), factionLoadId = factionLoadId, player = player });
        }

        public void Block(int x0, int y0, int x1, int y1)
        {
            for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) if (x >= 0 && y >= 0 && x < width && y < height) impassable.Add(y * width + x);
        }

        /// <summary>
        /// The default synthetic planet: 64 × 40 tiles, a sea band with one land bridge, and settlements
        /// for the given faction load ids spread over the land (plus one player colony).
        /// </summary>
        public static GridWorldGraph Default(IList<int> factionLoadIds, int seed)
        {
            GridWorldGraph g = new GridWorldGraph(64, 40);
            g.Block(30, 0, 33, 26);
            g.Block(30, 32, 33, 39);
            g.Block(0, 0, 63, 0);
            NetRng rng = new NetRng(seed, "grid.world");
            g.AddSettlement(8, 8, 1, true);
            int n = Math.Max(1, factionLoadIds?.Count ?? 0);
            for (int i = 0; i < 36; i++)
            {
                int fid = factionLoadIds != null && factionLoadIds.Count > 0 ? factionLoadIds[i % n] : 100 + i % 6;
                for (int tries = 0; tries < 20; tries++)
                {
                    int x = rng.Range(2, 62), y = rng.Range(2, 38);
                    if (g.impassable.Contains(y * 64 + x)) continue;
                    g.AddSettlement(x, y, fid);
                    break;
                }
            }
            return g;
        }

        // ------------------------------------------------------------------ ISpatialWorld

        public bool Ready => ready;

        public bool IsValid(TileRef t)
        {
            if (t == null || t.tileId < 0 || t.tileId >= Count) return false;
            string def;
            if (!layers.TryGetValue(t.layerId, out def)) return false;
            return string.IsNullOrEmpty(t.layerDef) || t.layerDef == def;
        }

        public bool IsPassable(TileRef t)
        {
            return IsValid(t) && (t.layerId != 0 || !impassable.Contains(t.tileId));
        }

        private bool Passable(int id, int layer)
        {
            return id >= 0 && id < Count && (layer != 0 || !impassable.Contains(id));
        }

        public List<SettlementFacts> Settlements()
        {
            return settlements;
        }

        private static readonly int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        private static readonly int[] DY = { 0, 0, 1, -1, 1, -1, 1, -1 };

        // Reused search buffers (single-threaded harness): distance per tile, a stamp marking this search.
        private int[] dist;
        private int[] parent;
        private int[] stamp;
        private int[] queue;
        private int searchId;

        /// <summary>Breadth-first distances from a tile over passable tiles (the start always counts). Returns the visit count.</summary>
        private int Flood(int start, int layer, int maxDist, List<int> visited)
        {
            if (dist == null)
            {
                dist = new int[Count];
                parent = new int[Count];
                stamp = new int[Count];
                queue = new int[Count];
            }
            searchId++;
            int head = 0, tail = 0;
            dist[start] = 0;
            parent[start] = -1;
            stamp[start] = searchId;
            queue[tail++] = start;
            while (head < tail)
            {
                int cur = queue[head++];
                visited?.Add(cur);
                int d = dist[cur];
                if (d >= maxDist) continue;
                int cx = cur % width, cy = cur / width;
                for (int k = 0; k < 8; k++)
                {
                    int nx = cx + DX[k], ny = cy + DY[k];
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int nid = ny * width + nx;
                    if (stamp[nid] == searchId || !Passable(nid, layer)) continue;
                    stamp[nid] = searchId;
                    dist[nid] = d + 1;
                    parent[nid] = cur;
                    queue[tail++] = nid;
                }
            }
            return tail;
        }

        private bool Reached(int id) => stamp[id] == searchId;

        public bool TryFindPassableNear(TileRef center, int minDist, int maxDist, int seed, out TileRef tile)
        {
            tile = null;
            if (!IsValid(center) || maxDist < minDist) return false;
            List<int> visited = new List<int>();
            Flood(center.tileId, center.layerId, maxDist, visited);
            List<int> ring = new List<int>();
            for (int i = 0; i < visited.Count; i++)
            {
                int id = visited[i];
                if (dist[id] >= minDist && dist[id] <= maxDist && Passable(id, center.layerId)) ring.Add(id);
            }
            if (ring.Count == 0) return false;
            ring.Sort();
            tile = Ref(ring[new NetRng(seed, "grid.near").Range(0, ring.Count)], center.layerId);
            return true;
        }

        public bool TryFindAnyPassable(int seed, out TileRef tile)
        {
            tile = null;
            // The same search the RimWorld adapter uses: seeded probes, then a guaranteed scan.
            int id = Domain.Spatial.SpatialSearch.FirstPassable(Count, seed, i => Passable(i, 0));
            if (id < 0) return false;
            tile = Ref(id, 0);
            return true;
        }

        public bool TryRoute(TileRef from, TileRef to, int maxSteps, List<int> steps, out string failureKey)
        {
            routeQueries++;
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
            if (!Passable(to.tileId, to.layerId))
            {
                failureKey = "Unreachable";
                return false;
            }
            Flood(from.tileId, from.layerId, maxSteps, null);
            if (!Reached(to.tileId))
            {
                Flood(from.tileId, from.layerId, int.MaxValue, null);
                failureKey = Reached(to.tileId) ? "TooFar" : "Unreachable";
                return false;
            }
            int cur = to.tileId;
            while (cur != from.tileId)
            {
                steps.Add(cur);
                cur = parent[cur];
            }
            steps.Reverse();
            return true;
        }

        public TileRef OnLayerOf(TileRef sameLayer, int tileId)
        {
            return sameLayer == null ? null : Ref(tileId, sameLayer.layerId);
        }

        public int ApproxDistance(TileRef a, TileRef b)
        {
            if (!IsValid(a) || !IsValid(b) || a.layerId != b.layerId) return int.MaxValue;
            return Math.Max(Math.Abs(X(a) - X(b)), Math.Abs(Y(a) - Y(b)));
        }
    }
}
