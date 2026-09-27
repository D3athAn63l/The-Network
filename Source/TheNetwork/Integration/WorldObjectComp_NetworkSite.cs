using System;
using RimWorld;
using RimWorld.Planet;
using TheNetwork.Core;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork
{
    /// <summary>
    /// Injected into the vanilla <c>Site</c> WorldObjectDef by the single XML patch. It holds only the
    /// bound opportunity id and forwards callbacks (RIMWORLD_INTEGRATION § 2.4–2.5). Unbound (id 0) it
    /// does nothing at all, so it is inert on every ordinary site. Its data is one value inside the
    /// site node: if the mod is removed the def no longer lists the comp and vanilla ignores the node.
    /// </summary>
    public class WorldObjectCompProperties_NetworkSite : WorldObjectCompProperties
    {
        public WorldObjectCompProperties_NetworkSite()
        {
            compClass = typeof(WorldObjectComp_NetworkSite);
        }
    }

    public class WorldObjectComp_NetworkSite : WorldObjectComp
    {
        public int networkOpportunityId;

        public bool IsBound => networkOpportunityId > 0;

        public OpportunityId Opportunity => new OpportunityId(networkOpportunityId);

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref networkOpportunityId, "networkOpportunityId", 0);
        }

        public override void PostMapGenerate()
        {
            if (!IsBound) return;
            Forward("map", r => r.Sites.OnMapGenerated(this));
        }

        public override void PostCaravanFormed(Caravan caravan)
        {
            if (!IsBound) return;
            Forward("caravan", r => r.Sites.OnCaravanFormed(this, caravan));
        }

        /// <summary>
        /// The final claim sample. Vanilla has no comp hook before a site map is removed, but
        /// <c>MapParent.TickInterval</c> ticks the comps and then calls <c>CheckRemoveMapNow</c> in the same
        /// call: when <c>ShouldRemoveMapNow</c> is true here, the map is removed right after this returns,
        /// with anything that left by pods or shuttle already off it. Unbound or map-less: two checks.
        /// </summary>
        public override void CompTickInterval(int delta)
        {
            if (!IsBound) return;
            MapParent mp = parent as MapParent;
            if (mp == null || !mp.HasMap || mp.Map.mapPawns.AnyPawnBlockingMapRemoval) return;
            bool alsoRemoveWorldObject;
            if (!mp.ShouldRemoveMapNow(out alsoRemoveWorldObject)) return;
            Forward("mapAboutToBeRemoved", r => r.Sites.OnMapAboutToBeRemoved(this));
        }

        public override void PostMyMapRemoved()
        {
            if (!IsBound) return;
            Forward("mapRemoved", r => r.Sites.OnMapRemoved(this));
        }

        public override void PostDestroy()
        {
            if (!IsBound) return;
            Forward("destroy", r => r.Sites.OnSiteDestroyed(this));
        }

        public override string CompInspectStringExtra()
        {
            if (!IsBound) return null;
            try
            {
                return NetworkRuntime.Current?.Sites.InspectString(this);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void Forward(string what, Action<NetworkRuntime> call)
        {
            NetworkRuntime runtime = NetworkRuntime.Current;
            // Starts the Network on first use; ignored after a failed start-up or when prepared for removal.
            if (runtime == null || !runtime.Active) return;
            try
            {
                call(runtime);
            }
            catch (Exception ex)
            {
                NetLog.ErrorOnce(LogCategory.Sites, "comp:" + what + ":" + networkOpportunityId, "Site callback '" + what + "' failed for O" + networkOpportunityId + ": " + ex);
            }
        }
    }
}
