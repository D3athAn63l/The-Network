using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using TheNetwork.Core;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Integration
{
    /// <summary>
    /// "Prepare save for removal" (SAVE_AND_MIGRATION § 10): turn every Network site into a plain
    /// vanilla site (comp unbound, tag removed, timeouts kept), invalidate running searches and void
    /// every live contract with the technical-invalidation refund (a running operation is aborted; its
    /// people exist only as records, so nothing is left on any map), strip remaining TheNetwork.* tags,
    /// and make the Network inert. Drop pods already launched are ordinary vanilla objects. Afterwards
    /// the only error on removal is the missing WorldComponent class.
    /// </summary>
    public static class RemovalPreparer
    {
        public static string Prepare(NetworkRuntime rt)
        {
            if (rt == null) return "No Network in this game.";
            if (!rt.EnsureStarted()) return "The Network failed to start this session (see the log); nothing was changed. It can be removed without preparation.";
            int sites = 0, searches = 0, refunded = 0, tags = 0, contracts = 0;

            // Phase 3 (PHYSICAL_LIFECYCLE § 20): Planned / Open / Quarantined episodes settle FIRST, through the ordinary commit
            // (terminal observations apply, anything else is Detached, nothing is invented; RELEASE and FOLLOW-UP run, nothing is
            // published), so a linked operation is back on its own path before contracts are voided. No pawn is touched here
            // beyond the port's release actions. None exist in a live 3.0 game.
            int episodes = rt.Ctx.Lifecycle?.SettleForRemoval() ?? 0;
            // Then the registry reserves nobody, its quest is ended through vanilla's API, and every Network tag leaves our pawns
            // (§ 20 steps 2–3). The previously retained pawns become ordinary world pawns; nothing is deleted.
            string physical = rt.PhysicalWorld?.PrepareForRemoval() ?? "no physical adapter";

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

            List<Contract> live = rt.Ctx.Procurement.Live();
            for (int i = 0; i < live.Count; i++)
            {
                Contract c = live[i];
                int before = c.ExternalRefunded();
                rt.Ctx.Procurement.Void(c, Causes.PreparingForRemoval);
                refunded += c.ExternalRefunded() - before;
                contracts++;
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
            string summary = "TheNetwork_RemovalSummary".Translate(sites, searches + contracts, refunded).Resolve();
            NetLog.Info(LogCategory.Save, "Prepared for removal: " + sites + " sites unbound, " + searches + " searches invalidated, " + contracts + " contracts voided (" + refunded + " silver refunded), " + tags + " extra tags removed, " + episodes + " physical episodes settled; " + physical + ".");
            return summary;
        }

        /// <summary>Undo: re-binds live, non-terminal sites and resumes. Invalidated searches and voided contracts stay so.</summary>
        public static void Resume(NetworkRuntime rt)
        {
            if (rt == null || !rt.Root.preparedForRemoval || !rt.EnsureStarted()) return;
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
            // The registry reserves again (rebuilt from the stores) and the routing tags come back from the bindings.
            string physical = rt.PhysicalWorld?.Resume();
            if (physical != null) NetLog.Info(LogCategory.Save, physical);
            NetValidator.Run(rt, ValidationMode.Full);
            // Settled episodes stay Closed; a stage they still owe (their publication) resumes from its marker.
            rt.Ctx.Lifecycle?.OnLoaded();
            NetLog.Info(LogCategory.Save, "Resumed The Network after a removal preparation.");
        }
    }
}
