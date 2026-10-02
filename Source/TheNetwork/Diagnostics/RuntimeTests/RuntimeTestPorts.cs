using System;
using System.Collections.Generic;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;

namespace TheNetwork.Diagnostics.RuntimeTests
{
    // The sandbox's adapter ports (Phase 2.9). They exist ONLY for runtime tests: deterministic, private to one sandbox, and
    // incapable of reaching the colony (no Thing, no map, no silver, no pod). They are not gameplay adapters and are never
    // handed to the live Network. A scan in the test run fails if anything under RuntimeTests names a RimWorld spawn, trade
    // or launch API outside the read-only Live suite.

    /// <summary>Comms the scenario can switch on and off.</summary>
    public sealed class SandboxComms : ICommsAccess
    {
        public bool usable = true;
        public string reason = "NoUsableCommsConsole";

        public bool CanContact(out string reasonKey)
        {
            reasonKey = usable ? null : reason;
            return usable;
        }
    }

    /// <summary>A private purse. Charging takes from it, refunding adds to it; the colony's silver is not involved.</summary>
    public sealed class SandboxPayment : IPayment
    {
        public int silver = 1000000;
        public bool canRefund = true;
        public int charged;
        public int refunded;
        public int chargeCalls;
        public int refundCalls;

        public bool CanCharge(int amount, out string reasonKey)
        {
            reasonKey = amount <= silver ? null : "NotEnoughBeaconSilver";
            return reasonKey == null;
        }

        public bool TryCharge(int amount, out string reasonKey)
        {
            if (!CanCharge(amount, out reasonKey)) return false;
            silver -= amount;
            charged += amount;
            chargeCalls++;
            return true;
        }

        public bool TryRefund(int amount, out string reasonKey)
        {
            reasonKey = canRefund ? null : "NoHomeMap";
            if (!canRefund) return false;
            silver += amount;
            refunded += amount;
            refundCalls++;
            return true;
        }
    }

    public enum SandboxDeliveryMode
    {
        /// <summary>The goods "land".</summary>
        Success,

        /// <summary>No drop spot: the contract's delivery retry / hold path.</summary>
        NoDropSpot,

        /// <summary>The payload cannot be created: the technical-invalidation path.</summary>
        CreationFailure
    }

    /// <summary>A delivery that only records what it was asked to deliver. It never creates a Thing.</summary>
    public sealed class SandboxDelivery : IDelivery
    {
        public SandboxDeliveryMode mode = SandboxDeliveryMode.Success;
        public int planCalls;
        public int deliverCalls;
        public int deliveries;
        public int deliveredItems;
        public readonly List<string> deliveredDefs = new List<string>();
        public int lastPayloadCount;

        public int DefaultHomeMapId(out string label)
        {
            label = "Sandbox home";
            return 1;
        }

        public DeliveryPlan Plan(int preferredMapId, int seed)
        {
            planCalls++;
            if (mode == SandboxDeliveryMode.NoDropSpot) return new DeliveryPlan { failureKey = "NoDropSpot" };
            return new DeliveryPlan { ok = true, mapId = 1, mapLabel = "Sandbox home" };
        }

        public DeliveryResult Deliver(DeliveryPlan plan, List<ItemPayload> payload, int seed)
        {
            deliverCalls++;
            if (mode == SandboxDeliveryMode.CreationFailure)
            {
                return new DeliveryResult { failureKey = "FailedToGenerate", thingCreationFailed = true, failedDefName = payload.Count > 0 ? payload[0].thing?.defName : null };
            }
            int n = 0;
            lastPayloadCount = payload.Count;
            foreach (ItemPayload p in payload)
            {
                n += p.count;
                if (p.thing?.defName != null && !deliveredDefs.Contains(p.thing.defName)) deliveredDefs.Add(p.thing.defName);
            }
            deliveries++;
            deliveredItems += n;
            return new DeliveryResult { ok = true, mapId = 1, mapLabel = "Sandbox home", delivered = n };
        }
    }

    /// <summary>The sandbox's item catalog: facts it was given (synthetic, or copied by value from the real catalog). Failures stay local.</summary>
    public sealed class SandboxCatalog : ICatalog
    {
        public readonly Dictionary<string, ItemFacts> items = new Dictionary<string, ItemFacts>();
        public readonly List<ItemFacts> extras = new List<ItemFacts>();
        public readonly HashSet<string> unusable = new HashSet<string>();

        public void Add(ItemFacts f)
        {
            if (f != null && f.defName != null) items[f.defName] = f;
        }

        public bool IsRequestable(string defName, out string reasonKey)
        {
            reasonKey = null;
            if (defName == null || !items.ContainsKey(defName)) reasonKey = "NotInCatalog";
            else if (unusable.Contains(defName)) reasonKey = "FailedToGenerate";
            return reasonKey == null;
        }

        public ItemFacts Facts(string defName)
        {
            ItemFacts f;
            return defName != null && items.TryGetValue(defName, out f) ? f : null;
        }

        public IList<ItemFacts> ExtraCargoPool(int maxTechLevel)
        {
            List<ItemFacts> list = new List<ItemFacts>();
            foreach (ItemFacts e in extras) if (e.techLevel <= maxTechLevel && !unusable.Contains(e.defName)) list.Add(e);
            return list;
        }

        /// <summary>Local only: a creation failure in a sandbox never marks a real def unusable.</summary>
        public void MarkUnusable(string defName, string reason)
        {
            if (defName != null) unusable.Add(defName);
        }
    }

    public sealed class SandboxWorldFacts : IWorldFacts
    {
        public readonly List<FactionFacts> factions = new List<FactionFacts>();
        public float threat = 450f;

        public List<FactionFacts> LiveFactions() { return factions; }
        public float BaseThreatPoints() { return threat; }
        public string PickStuff(string thingDefName, int seed) { return null; }
        public int TilesFromPlayerHome(TileRef tile) { return tile == null ? -1 : 6; }
        public int TravelTicksFromPlayerHome(TileRef tile) { return tile == null ? -1 : 108000; }
        public FactionFacts PlayerFaction() { return new FactionFacts { loadId = 1, name = "Sandbox Colony", defName = "PlayerColony", isPlayer = true, humanlike = true }; }

        /// <summary>A hostile humanlike faction that can guard a site (what a Last Known Location needs to exist).</summary>
        public static FactionFacts Raiders(int loadId = 21)
        {
            return new FactionFacts { loadId = loadId, name = "Sandbox Raiders", defName = "SandboxRaiders", defPackageId = "ludeon.rimworld", defIsLudeon = true, techLevel = 4, hostileToPlayer = true, goodwill = -80, humanlike = true, permanentEnemy = true, canGuardSite = true };
        }
    }

    /// <summary>Sites as plain ids: no world object is ever created. Placement near an incident uses the sandbox's own grid.</summary>
    public sealed class SandboxSites : ISiteAdapter
    {
        private int nextSiteId = 1000;
        public readonly HashSet<int> sites = new HashSet<int>();
        public GridWorldGraph graph;
        public bool noNearTile;
        public int nearRequests;

        public bool TryFindTileNear(TileRef near, int minDist, int maxDist, int seed, out TileRef tile)
        {
            nearRequests++;
            tile = null;
            if (noNearTile || graph == null) return false;
            return graph.TryFindPassableNear(near, minDist, maxDist, seed, out tile);
        }

        public bool TryFindTile(int seed, int minDist, int maxDist, out TileRef tile)
        {
            tile = new TileRef { tileId = Math.Abs(seed % 100000), layerId = 0, layerDef = "Surface", regionKey = "S0:R" + Math.Abs(seed % 48).ToString("00") };
            return true;
        }

        public MaterializeResult Materialize(Opportunity opp)
        {
            int id = nextSiteId++;
            sites.Add(id);
            return new MaterializeResult { ok = true, site = new WorldObjectRef { id = id, defName = "Site", label = "cache" }, threatProfileUsed = opp.threat.profileKey };
        }

        public bool SiteExists(WorldObjectRef site) { return site != null && sites.Contains(site.id); }
        public bool SiteHasMap(WorldObjectRef site) { return false; }
        public bool TrySampleRemaining(Opportunity opp, out int remaining) { remaining = 0; return false; }
        public void ReleaseSite(Opportunity opp, bool destroyIfNoMap) { if (opp.site != null && destroyIfNoMap) sites.Remove(opp.site.id); }
    }

    /// <summary>Records the sandbox's events (a Presentation consumer that shows nothing: no letter, no message).</summary>
    public sealed class SandboxEventRecorder : IEventConsumer
    {
        public readonly List<NetworkEvent> events = new List<NetworkEvent>();
        public string Name => "SandboxRecorder";
        public void Handle(NetworkEvent evt) { events.Add(evt); }

        public int Count(string key)
        {
            int n = 0;
            for (int i = 0; i < events.Count; i++) if (events[i].typeKey == key) n++;
            return n;
        }
    }
}
