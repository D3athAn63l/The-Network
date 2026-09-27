using System.Collections.Generic;
using TheNetwork.Kernel;

namespace TheNetwork.Domain.Ports
{
    // Narrow adapter interfaces between Domain and Integration (ARCHITECTURE § 4, § 11). Domain code
    // sees plain facts and results; only Integration implementations touch live RimWorld objects.
    // Headless tests provide fakes: they test Domain logic, never vanilla behaviour.

    /// <summary>What the Domain may know about a live faction, read once when needed.</summary>
    public sealed class FactionFacts
    {
        public int loadId;
        public string name;
        public string defName;
        public string defLabel;
        public string defPackageId;

        /// <summary>The def comes from Core or an official DLC (Ludeon content).</summary>
        public bool defIsLudeon;

        /// <summary>RimWorld TechLevel as an int (0 Undefined … 7 Archotech).</summary>
        public int techLevel;

        public bool isPlayer;
        public bool hostileToPlayer;
        public int goodwill;
        public bool defeated;
        public bool hidden;
        public bool temporary;
        public bool humanlike;
        public bool permanentEnemy;
        public bool isMechanoid;

        /// <summary>Can generate combat groups and own a guarded site part (vanilla FactionCanOwn).</summary>
        public bool canGuardSite;

        /// <summary>Approximate tiles from the nearest player home to this faction's nearest settlement; -1 if none.</summary>
        public int nearestSettlementTiles = -1;

        public override string ToString()
        {
            return name + " (" + defName + ", load " + loadId + ")";
        }
    }

    /// <summary>What the Domain may know about a catalog item (a runtime ThingDef, as plain data).</summary>
    public sealed class ItemFacts
    {
        public string defName;
        public string label;
        public string packageId;
        public string modName;
        public bool isLudeon;
        public int techLevel;
        public float marketValue;
        public int stackLimit = 1;
        public bool hasQuality;
        public bool madeFromStuff;
        public bool isBuilding;
        public bool isWeapon;
        public bool isApparel;
        public bool isResource;
        public bool craftable;
        public bool tradeable;
        public bool unique;
        public bool mineable;
    }

    public interface ICommsAccess
    {
        /// <summary>True when the player can contact the Network right now (a usable Comms Console).</summary>
        bool CanContact(out string reasonKey);
    }

    public interface IPayment
    {
        /// <summary>Can the player pay this fee right now (beacon-reachable silver)?</summary>
        bool CanCharge(int amount, out string reasonKey);

        /// <summary>Takes the silver. Returns false (and takes nothing) when it cannot.</summary>
        bool TryCharge(int amount, out string reasonKey);

        /// <summary>Returns silver to the player. Returns false when no destination exists right now.</summary>
        bool TryRefund(int amount, out string reasonKey);
    }

    /// <summary>Read-only world facts for generation (called rarely: at resolution time).</summary>
    public interface IWorldFacts
    {
        List<FactionFacts> LiveFactions();

        /// <summary>Default site threat points now (vanilla storyteller scaling).</summary>
        float BaseThreatPoints();

        /// <summary>Picks a stuff for a stuff-made def under a pushed vanilla seed; null if none.</summary>
        string PickStuff(string thingDefName, int seed);

        /// <summary>Approximate travel distance in tiles from the nearest player home to a tile; -1 if unknown.</summary>
        int TilesFromPlayerHome(TileRef tile);

        /// <summary>Estimated caravan travel time (ticks) from the nearest player home; -1 if unknown. Called once per lead.</summary>
        int TravelTicksFromPlayerHome(TileRef tile);

        /// <summary>The player's own faction, as facts (for the PlayerProxy binding).</summary>
        FactionFacts PlayerFaction();
    }

    public interface ICatalog
    {
        /// <summary>Is this def requestable right now (verdict + override + runtime failures)?</summary>
        bool IsRequestable(string defName, out string reasonKey);

        /// <summary>Facts for a def that exists in this session; null when it does not.</summary>
        ItemFacts Facts(string defName);

        /// <summary>Believable extra-cargo candidates up to a tech level, in a stable order.</summary>
        IList<ItemFacts> ExtraCargoPool(int maxTechLevel);

        /// <summary>A def whose Things could not be created: unusable for the rest of the session.</summary>
        void MarkUnusable(string defName, string reason);
    }

    public sealed class MaterializeResult
    {
        public bool ok;
        public WorldObjectRef site;
        public string threatProfileUsed;
        public string failureReason;

        /// <summary>True when creating the payload Things failed (a catalog runtime failure).</summary>
        public bool thingCreationFailed;
        public string failedDefName;
    }

    public interface ISiteAdapter
    {
        /// <summary>Finds a new site tile near the player under a pushed vanilla seed. Committed by the caller.</summary>
        bool TryFindTile(int seed, int minDist, int maxDist, out TileRef tile);

        /// <summary>Creates the vanilla Site for an opportunity (ItemStash + threat part + timeout + comp binding + tag).</summary>
        MaterializeResult Materialize(Opportunities.Opportunity opp);

        /// <summary>Is the bound site world object still present?</summary>
        bool SiteExists(WorldObjectRef site);

        /// <summary>Does the site currently have a generated map?</summary>
        bool SiteHasMap(WorldObjectRef site);

        /// <summary>
        /// Counts the target def still on the site map (ground, containers, pawns' inventories) and notes
        /// transporters currently in flight from the site tile. Returns false when there is no map.
        /// </summary>
        bool TrySampleRemaining(Opportunities.Opportunity opp, out int remaining);

        /// <summary>Target-def count in transporters launched from the site tile and not yet counted.</summary>
        int CountUncountedTransporterCargo(Opportunities.Opportunity opp);

        /// <summary>Destroys (or leaves for vanilla) a site no longer needed. Never touches a site with a map.</summary>
        void ReleaseSite(Opportunities.Opportunity opp, bool destroyIfNoMap);
    }
}
