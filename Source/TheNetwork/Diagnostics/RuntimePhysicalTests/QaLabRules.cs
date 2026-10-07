using TheNetwork.Domain.Physical;
using TheNetwork.Domain.Actors;
using TheNetwork.Integration.Physical;
using TheNetwork.Kernel;

namespace TheNetwork.Diagnostics.RuntimePhysicalTests
{
    /// <summary>Read-only QA reset policy. No persisted state and no production lifecycle decisions.</summary>
    public static class QaLabRules
    {
        public static string ScopeRefusal(bool exists, bool exactSite, bool plainParent, bool factionless, bool home, bool narrowDef, int width, int height)
        {
            if (!exists) return "no TestSite map exists";
            if (!exactSite) return "map is not the unique current dedicated TestSite";
            if (!plainParent) return "parent is not exactly the approved plain MapParent";
            if (!factionless) return "TestSite parent has a faction";
            if (home) return "player home maps cannot be reset";
            if (!narrowDef) return "TestSite def has changed home/comp/incident semantics";
            if (!ApprovedSize(width, height)) return "only 60x60 and legacy 100x100 TestSites are supported";
            return null;
        }

        public static string ResetStateRefusal(bool activeRun, bool ownershipReady, int pawnCount)
        {
            if (activeRun) return "a physical runtime test is executing";
            if (!ownershipReady) return "Network physical ownership is unavailable or unresolved";
            if (pawnCount < 0) return "map Pawn census is unavailable";
            if (pawnCount > 0) return "contains " + pawnCount + " Pawn(s). Resolve/remove test subjects before destructive reset.";
            return null;
        }

        public static bool RetainedBlocksReset(KnownCharacter person, int mapId, bool pawnOnMap, PhysicalEpisode linked)
        {
            return RetainedPawnRegistry.RetainedCustody(person) && person.pawn?.IsBound == true
                && (pawnOnMap || (person.custody == CustodyState.Deployed && linked != null && linked.whereMapId == mapId));
        }

        public static bool ApprovedSize(int width, int height)
        {
            return (width == 60 && height == 60) || (width == 100 && height == 100);
        }

        public static bool EpisodeBlocksReset(PhysicalEpisode episode, int mapId, TileRef tile, bool memberOnMap)
        {
            if (episode == null) return false;
            bool sameTile = episode.whereTile != null && tile != null && episode.whereTile.tileId == tile.tileId
                && episode.whereTile.layerId == tile.layerId;
            bool associated = memberOnMap || episode.whereMapId == mapId || (episode.whereMapId < 0 && sameTile);
            if (!associated || episode.members == null || episode.members.Count == 0) return false;
            // IsComplete requires CLOSED, RELEASE, FOLLOWUP and PUBLISH. Historical references alone impose no obligation.
            return !episode.IsComplete;
        }
    }
}
