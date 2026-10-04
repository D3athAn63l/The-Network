using System;
using RimWorld;
using RimWorld.Planet;
using TheNetwork.Domain;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Integration
{
    /// <summary>
    /// Receives the injected comp's callbacks and turns live vanilla facts (maps, caravans) into plain
    /// calls on <see cref="OpportunityService"/>. A comp bound to an opportunity that no longer exists,
    /// or is already closed, unbinds itself and stays inert.
    /// </summary>
    public sealed class SiteCallbacks
    {
        private readonly DomainContext ctx;
        private readonly SiteAdapter adapter;

        public SiteCallbacks(DomainContext ctx, SiteAdapter adapter)
        {
            this.ctx = ctx;
            this.adapter = adapter;
        }

        private Opportunity Bound(WorldObjectComp_NetworkSite comp)
        {
            Opportunity opp = ctx.opportunities.Get(comp.Opportunity);
            if (opp == null || opp.state == OpportunityState.Closed)
            {
                SiteAdapter.Unbind(comp.parent, comp.Opportunity);
                return null;
            }
            return opp;
        }

        public void OnMapGenerated(WorldObjectComp_NetworkSite comp)
        {
            NetLog.Trace(LogCategory.Sites, "Site " + comp.parent?.ID + " (O" + comp.networkOpportunityId + "): PostMapGenerate");
            Opportunity opp = Bound(comp);
            if (opp != null) ctx.Opportunities.OnMapGenerated(opp.id);
        }

        public void OnCaravanFormed(WorldObjectComp_NetworkSite comp, Caravan caravan)
        {
            NetLog.Trace(LogCategory.Sites, "Site " + comp.parent?.ID + " (O" + comp.networkOpportunityId + "): PostCaravanFormed " + caravan?.Name);
            Opportunity opp = Bound(comp);
            if (opp == null || caravan == null || !caravan.IsPlayerControlled) return;
            ThingDef def = DefResolver<ThingDef>.Get(opp.Target?.thing?.defName);
            int carried = def == null ? 0 : SiteAdapter.CountInCaravan(caravan, def);
            // Diagnostics only (the player's own cargo is included); recovery comes from the re-sample.
            NetLog.Trace(LogCategory.Sites, opp.id + ": caravan left carrying " + carried + " of the target def (own cargo included)");
            ctx.Opportunities.OnCaravanFormed(opp.id, carried);
        }

        public void OnMapAboutToBeRemoved(WorldObjectComp_NetworkSite comp)
        {
            NetLog.Trace(LogCategory.Sites, "Site " + comp.parent?.ID + " (O" + comp.networkOpportunityId + "): map about to be removed (final sample)");
            Opportunity opp = Bound(comp);
            if (opp != null) ctx.Opportunities.OnMapAboutToBeRemoved(opp.id);
        }

        public void OnMapRemoved(WorldObjectComp_NetworkSite comp)
        {
            NetLog.Trace(LogCategory.Sites, "Site " + comp.parent?.ID + " (O" + comp.networkOpportunityId + "): PostMyMapRemoved");
            Opportunity opp = Bound(comp);
            if (opp != null) ctx.Opportunities.OnMapRemoved(opp.id);
        }

        public void OnSiteDestroyed(WorldObjectComp_NetworkSite comp)
        {
            NetLog.Trace(LogCategory.Sites, "Site " + comp.parent?.ID + " (O" + comp.networkOpportunityId + "): PostDestroy");
            Opportunity opp = Bound(comp);
            if (opp != null) ctx.Opportunities.OnSiteDestroyed(opp.id);
        }

        public void OnMapSettled(OpportunityId id)
        {
            NetLog.Trace(LogCategory.Sites, id + ": MapSettled signal");
            ctx.Opportunities.OnMapSettled(id);
        }

        /// <summary>World-map inspect line for a bound site: what the lead said, never the truth.</summary>
        public string InspectString(WorldObjectComp_NetworkSite comp)
        {
            Opportunity opp = ctx.opportunities.Get(comp.Opportunity);
            if (opp == null || opp.IsTerminal) return null;
            Lead lead = ctx.intel.Get(opp.lead);
            string source = lead == null ? "?" : ctx.actors.NameOf(lead.reportedBy);
            string item = opp.Target?.LabelSnapshot ?? "?";
            string text = "TheNetwork_SiteInspect".Translate(source, item).Resolve();
            if (lead != null && lead.reported != null && lead.reported.threatBand != ThreatBand.Unknown)
            {
                text += "\n" + "TheNetwork_SiteInspectThreat".Translate(("TheNetwork_Threat_" + lead.reported.threatBand).Translate()).Resolve();
            }
            return text;
        }
    }

    /// <summary>
    /// Receives <c>TheNetwork.*</c> quest-tag signals (RIMWORLD_INTEGRATION § 2.19). The SignalManager is
    /// not persisted, so this re-registers on every load; registration checks first, so the same
    /// instance never registers twice. Whether a receiver from an earlier runtime can survive in the
    /// same SignalManager (a reload within one game session) is runtime Spike S3, and is not assumed
    /// either way: the traces below number each bridge instance and count Network receivers, and a
    /// bridge that is not the current runtime's ignores signals (so a stale one could never act on old
    /// state or send a second letter). Handling is also idempotent: a settled opportunity is terminal
    /// and a second MapSettled does nothing. Phase 1 needs one signal: a Network site's map was
    /// settled, which the comp cannot observe (its hook is not virtual).
    /// </summary>
    public sealed class SignalBridge : ISignalReceiver
    {
        public const string SettledSuffix = ".MapSettled";

        private static int instances;

        private readonly DomainContext ctx;
        private readonly int instance;

        public SignalBridge(DomainContext ctx)
        {
            this.ctx = ctx;
            instance = ++instances;
        }

        /// <summary>Registers once; false only when there is no SignalManager (start-up then fails).</summary>
        public bool Register()
        {
            SignalManager sm = Find.SignalManager;
            if (sm == null) return false;
            if (sm.receivers.Contains(this))
            {
                NetLog.Trace(LogCategory.Sites, "Signal bridge #" + instance + " already registered.");
                return true;
            }
            sm.RegisterReceiver(this);
            int ours = 0;
            for (int i = 0; i < sm.receivers.Count; i++) if (sm.receivers[i] is SignalBridge) ours++;
            NetLog.Trace(LogCategory.Sites, "Signal bridge #" + instance + " registered (" + sm.receivers.Count + " receivers, " + ours + " Network bridge(s)).");
            if (ours > 1)
            {
                NetLog.WarnOnce(LogCategory.Sites, "signal-bridges", ours + " Network signal bridges are registered in this SignalManager (expected 1). Only the current one acts; please report this for Spike S3.");
            }
            return true;
        }

        public void Notify_SignalReceived(Signal signal)
        {
            string tag = signal.tag;
            if (tag == null) return;
            if (Physical.PhysicalTags.IsNetworkPawnTag(tag))
            {
                RoutePawnSignal(signal);
                return;
            }
            if (!tag.StartsWith(SiteAdapter.TagPrefix, StringComparison.Ordinal)) return;
            try
            {
                if (tag.EndsWith(SettledSuffix, StringComparison.Ordinal))
                {
                    string idPart = tag.Substring(SiteAdapter.TagPrefix.Length, tag.Length - SiteAdapter.TagPrefix.Length - SettledSuffix.Length);
                    int id;
                    Core.NetworkRuntime current = Core.NetworkRuntime.Current;
                    if (current == null || current.Signals != this)
                    {
                        NetLog.Trace(LogCategory.Sites, "Signal " + tag + " at stale bridge #" + instance + ": ignored.");
                        return;
                    }
                    NetLog.Trace(LogCategory.Sites, "Signal " + tag + " handled by bridge #" + instance + ".");
                    if (int.TryParse(idPart, out id) && id > 0 && current.Active) ctx.Opportunities.OnMapSettled(new OpportunityId(id));
                }
            }
            catch (Exception ex)
            {
                NetLog.ErrorOnce(LogCategory.Sites, "signal:" + tag, "Signal " + tag + " failed: " + ex);
            }
        }

        /// <summary>Signals routed (runtime diagnostics).</summary>
        public int pawnSignals;
        public int pawnSignalsIgnored;

        /// <summary>
        /// A Network pawn signal (PHYSICAL_LIFECYCLE § 14.3): "TheNetwork.Ep.&lt;id&gt;.&lt;Signal&gt;" or "TheNetwork.Char.&lt;id&gt;.&lt;Signal&gt;".
        /// SIGNALS WAKE; THEY DO NOT DECIDE. The SUBJECT pawn is proven against the persisted binding by REFERENCE EQUALITY (a tag alone is
        /// never identity: a copy another mod made is ignored), then the episode's watch is pulled forward to the next tick. Nothing here
        /// reconciles, reads final state (Killed fires mid-kill), mutates vanilla or throws. O(1): two dictionary lookups.
        /// </summary>
        private void RoutePawnSignal(Signal signal)
        {
            try
            {
                Core.NetworkRuntime current = Core.NetworkRuntime.Current;
                if (current == null || current.Signals != this || current.Inert) return;
                Pawn subject;
                if (!signal.args.TryGetArg("SUBJECT", out subject) || subject == null)
                {
                    pawnSignalsIgnored++;
                    return;
                }
                int id;
                string what;
                Domain.Physical.PhysicalEpisode e = null;
                if (Physical.PhysicalTags.TryParse(signal.tag, Physical.PhysicalTags.EpisodePrefix, out id, out what))
                {
                    e = ctx.episodes?.Get(new EpisodeId(id));
                    if (e != null && !BoundIn(e, subject)) e = null;
                }
                else if (Physical.PhysicalTags.TryParse(signal.tag, Physical.PhysicalTags.CharacterPrefix, out id, out what))
                {
                    Domain.Actors.KnownCharacter c = ctx.characters?.Get(new CharacterId(id));
                    if (c?.pawn != null && ReferenceEquals(c.pawn.pawn, subject) && c.episode.IsValid) e = ctx.episodes?.Get(c.episode);
                }
                if (e == null)
                {
                    pawnSignalsIgnored++;
                    return;
                }
                pawnSignals++;
                if (what == "LeftMap") current.PhysicalWorld?.NoteLeftMap(subject);
                ctx.Lifecycle?.Wake(e, what);
            }
            catch (Exception ex)
            {
                NetLog.ErrorOnce(LogCategory.Physical, "signal:" + signal.tag, "Pawn signal " + signal.tag + " could not be routed (the episode watch still observes): " + ex.Message);
            }
        }

        private static bool BoundIn(Domain.Physical.PhysicalEpisode e, Pawn subject)
        {
            for (int i = 0; i < e.members.Count; i++)
            {
                if (e.members[i].pawn != null && ReferenceEquals(e.members[i].pawn.pawn, subject)) return true;
            }
            return false;
        }
    }
}
