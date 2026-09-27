using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using TheNetwork.Core;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Integration
{
    /// <summary>
    /// "Prepare save for removal" (SAVE_AND_MIGRATION § 10), Phase 1 scope: turn every Network site
    /// into a plain vanilla site (comp unbound, tag removed, timeouts kept), invalidate running searches
    /// with the technical-invalidation refund, strip remaining TheNetwork.* tags, and make the Network
    /// inert. Afterwards the only error on removal is the missing WorldComponent class.
    /// </summary>
    public static class RemovalPreparer
    {
        public static string Prepare(NetworkRuntime rt)
        {
            if (rt == null) return "No Network in this game.";
            int sites = 0, searches = 0, refunded = 0, tags = 0;

            List<IntelRequest> requests = new List<IntelRequest>(rt.State.intel.requests);
            for (int i = 0; i < requests.Count; i++)
            {
                IntelRequest r = requests[i];
                if (!r.IsActive) continue;
                int before = r.TotalRefunded();
                rt.Ctx.Intel.Invalidate(r, "NetworkRemoved", false);
                refunded += r.TotalRefunded() - before;
                searches++;
            }

            List<Opportunity> opps = new List<Opportunity>(rt.State.opportunities.opportunities);
            for (int i = 0; i < opps.Count; i++)
            {
                Opportunity o = opps[i];
                if (o.site == null) continue;
                WorldObject wo = rt.SiteAdapter.Resolve(o.site);
                if (wo == null) continue;
                SiteAdapter.Unbind(wo, o.id);
                sites++;
            }

            // Any remaining TheNetwork.* tag on world objects (defensive: tags are strings only).
            List<WorldObject> all = Find.WorldObjects.AllWorldObjects;
            for (int i = 0; i < all.Count; i++)
            {
                List<string> qt = all[i].questTags;
                if (qt == null) continue;
                tags += qt.RemoveAll(t => t != null && t.StartsWith("TheNetwork.", System.StringComparison.Ordinal));
                WorldObjectComp_NetworkSite comp = all[i].GetComponent<WorldObjectComp_NetworkSite>();
                if (comp != null && comp.IsBound)
                {
                    comp.networkOpportunityId = 0;
                    sites++;
                }
            }

            rt.Root.preparedForRemoval = true;
            StateVersion.Bump();
            string summary = "TheNetwork_RemovalSummary".Translate(sites, searches, refunded).Resolve();
            NetLog.Info(LogCategory.Save, "Prepared for removal: " + sites + " sites unbound, " + searches + " searches invalidated (" + refunded + " silver refunded), " + tags + " extra tags removed.");
            return summary;
        }

        /// <summary>Undo: re-binds live, non-terminal sites and resumes. Invalidated searches stay invalidated.</summary>
        public static void Resume(NetworkRuntime rt)
        {
            if (rt == null || !rt.Root.preparedForRemoval) return;
            List<Opportunity> opps = rt.State.opportunities.opportunities;
            for (int i = 0; i < opps.Count; i++)
            {
                Opportunity o = opps[i];
                if (o.IsTerminal || o.site == null) continue;
                WorldObject wo = rt.SiteAdapter.Resolve(o.site);
                WorldObjectComp_NetworkSite comp = wo?.GetComponent<WorldObjectComp_NetworkSite>();
                if (comp == null) continue;
                comp.networkOpportunityId = o.id.Value;
                QuestUtility.AddQuestTag(wo, SiteAdapter.TagFor(o.id));
            }
            rt.Root.preparedForRemoval = false;
            NetValidator.Run(rt, ValidationMode.Full);
            NetLog.Info(LogCategory.Save, "Resumed The Network after a removal preparation.");
        }
    }
}
