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
            if (runtime == null || runtime.Inert) return;
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
