using System.Collections.Generic;
using System.Text;
using LudeonTK;
using RimWorld;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Physical;
using TheNetwork.Kernel;
using UnityEngine;
using Verse;

namespace TheNetwork.Diagnostics
{
    /// <summary>
    /// The Phase 3.1 Episode Monitor (Dev Mode → The Network → "Episode Monitor (read-only)"). READ-ONLY by construction: it reads the
    /// episode store, the people, the scheduler and the adapter's plain-text pawn description, and it has no button that changes
    /// anything (the destructive test actions live in the physical-test menu). The text is rebuilt at most twice a second while open.
    /// </summary>
    public static class EpisodeMonitor
    {
        public const int MaxClosedShown = 15;

        public static string Build(NetworkRuntime rt)
        {
            StringBuilder b = new StringBuilder();
            if (rt == null)
            {
                b.Append("No Network runtime in this game.");
                return b.ToString();
            }
            DomainContext ctx = rt.Ctx;
            b.AppendLine("Network " + (rt.Session.IsRunning ? "running" : "not started (" + rt.Session.State + ")") + (rt.Inert ? ", PREPARED FOR REMOVAL" : "") + ", tick " + ctx.Now);
            b.AppendLine("Physical port: " + (ctx.physicalPort == null ? "none" : ctx.physicalPort.Name + (ctx.physicalPort.Available ? "" : " (unavailable)")));
            if (rt.PhysicalWorld != null) b.AppendLine("Adapter: " + rt.PhysicalWorld.Describe());
            if (ctx.Lifecycle != null) b.AppendLine("Lifecycle: " + ctx.Lifecycle.counters);
            b.AppendLine("Authority gate: " + AuthorityGate.refusedWrites + " refused abstract writes" + (AuthorityGate.lastRefusal != null ? " (last: " + AuthorityGate.lastRefusal + ")" : ""));
            if (ctx.episodes == null)
            {
                b.Append("No episode store.");
                return b.ToString();
            }
            List<PhysicalEpisode> incomplete = ctx.episodes.Incomplete();
            b.AppendLine();
            b.AppendLine("INCOMPLETE EPISODES: " + incomplete.Count);
            for (int i = 0; i < incomplete.Count; i++) Episode(b, rt, incomplete[i]);
            if (ctx.Lifecycle != null) Held(b, rt);
            List<PhysicalEpisode> closed = new List<PhysicalEpisode>();
            foreach (PhysicalEpisode e in ctx.episodes.episodes) if (e.IsComplete) closed.Add(e);
            closed.Sort((x, y) => y.id.Value.CompareTo(x.id.Value));
            b.AppendLine();
            b.AppendLine("COMPLETE EPISODES: " + closed.Count + (closed.Count > MaxClosedShown ? " (newest " + MaxClosedShown + " shown)" : ""));
            for (int i = 0; i < closed.Count && i < MaxClosedShown; i++) Episode(b, rt, closed[i]);
            return b.ToString();
        }

        private static void Episode(StringBuilder b, NetworkRuntime rt, PhysicalEpisode e)
        {
            DomainContext ctx = rt.Ctx;
            NetworkActor a = ctx.actors.Get(e.actor);
            Faction f = e.faction?.Resolve();
            ScheduledJob watch = rt.Scheduler.Find(PhysicalLifecycleService.WatchJob, e.id.Value);
            b.AppendLine(e.id + "  " + e.state + (e.quarantineKey != null ? " (" + e.quarantineKey + ")" : "") + "  ·  actor " + (a?.name?.Display ?? "?") + " (" + e.actor + ")  ·  purpose " + e.purposeKey
                + "  ·  cause " + Cause(e.cause));
            b.AppendLine("    ticks: created " + e.createdTick + ", opened " + e.openedTick + ", closed " + e.closedTick + "  ·  where map " + e.whereMapId + "  ·  attempts " + e.attempts
                + (e.lastError != null ? "  ·  last error: " + e.lastError : ""));
            b.AppendLine("    temporary faction: " + (e.faction == null ? "none" : e.faction.loadId + " \"" + e.faction.name + "\" " + (f == null ? "(removed)" : f.temporary ? "(live, temporary" + (f.Hidden ? ", hidden" : "") + ")" : "(live, NOT temporary)")));
            b.AppendLine("    consequences " + (e.consequencesApplied ? "APPLIED @" + e.committedTick : "not applied") + "  ·  release " + (e.releaseApplied ? "APPLIED @" + e.releasedTick : "pending")
                + "  ·  follow-up " + (e.followUpApplied ? "applied" : "pending") + "  ·  publish cursor " + e.publishCursor + "/" + e.publications.Count + (e.PublishDone ? " (published @" + e.publishedTick + ")" : "")
                + "  ·  complete " + (e.IsComplete ? "YES" : "no"));
            b.AppendLine("    watch: " + (watch == null ? "none" : "due @" + watch.dueTick + " (in " + (watch.dueTick - ctx.Now) + " ticks)"));
            for (int i = 0; i < e.members.Count; i++)
            {
                EpisodeMember m = e.members[i];
                KnownCharacter c = m.IsNamed ? ctx.characters.Get(m.character) : null;
                b.Append("    • " + (c != null ? c.name?.Display + " (" + c.id + ")" : "slot " + m.slot + " " + m.tier) + "  seat " + m.seatRole + "  ·  " + m.state + "/" + m.outcome
                    + (m.observed != ObservedKind.None ? " from " + m.observed + " @" + m.observedTick : "") + "  ·  release step " + m.releaseStep);
                if (c != null)
                {
                    b.Append("  ·  custody " + c.custody + ", status " + c.status + ", authority " + AuthorityGate.AuthorityOf(c) + (c.episode.IsValid ? ", episode link " + c.episode : ", no episode link"));
                    if (c.pawn != null && c.pawn.IsBound) b.Append(", bound @" + c.pawn.boundTick + ", aged through " + c.pawn.agedThroughTick);
                }
                b.AppendLine();
                b.AppendLine("        pawn: " + (rt.PhysicalWorld != null ? rt.PhysicalWorld.DescribeBinding(m.pawn).ToString() : m.pawn == null ? "unbound" : "#" + m.pawn.thingIdNumber));
            }
        }

        /// <summary>Phase 3.2A: the people vanilla holds (custody OutOfCustody) and the custody watch that observes them.</summary>
        private static void Held(StringBuilder b, NetworkRuntime rt)
        {
            DomainContext ctx = rt.Ctx;
            List<int> ids = ctx.Lifecycle.HeldIds();
            ScheduledJob watch = rt.Scheduler.Find(PhysicalLifecycleService.CustodyWatchJob, PhysicalLifecycleService.CustodyWatchTarget);
            b.AppendLine();
            b.AppendLine("HELD BY VANILLA: " + ids.Count + "  ·  custody watch: " + (watch == null ? "none" : "due @" + watch.dueTick + " (in " + (watch.dueTick - ctx.Now) + " ticks)"));
            for (int i = 0; i < ids.Count; i++)
            {
                KnownCharacter c = ctx.characters?.Get(new CharacterId(ids[i]));
                if (c == null) continue;
                b.AppendLine("    • " + c.name?.Display + " (" + c.id + ")  ·  held by " + c.heldBy + " since " + c.heldSinceTick + " (" + (ctx.Now - c.heldSinceTick) + " ticks)  ·  status " + c.status
                    + ", authority " + AuthorityGate.AuthorityOf(c) + (c.episode.IsValid ? ", episode link " + c.episode + " (release pending)" : ""));
                b.AppendLine("        pawn: " + (rt.PhysicalWorld != null ? rt.PhysicalWorld.DescribeBinding(c.pawn).ToString() : c.pawn == null ? "unbound" : "#" + c.pawn.thingIdNumber));
            }
        }

        private static string Cause(EpisodeCause c)
        {
            if (c == null) return "none";
            if (c.contract.IsValid) return "contract " + c.contract;
            if (c.operation.IsValid) return "operation " + c.operation;
            if (c.opportunity.IsValid) return "opportunity " + c.opportunity;
            return "dev " + c.devKey;
        }
    }

    /// <summary>The monitor window: text only, a scroll view and a close box. No action buttons by design.</summary>
    public sealed class Dialog_EpisodeMonitor : Window
    {
        private Vector2 scroll;
        private string text = "";
        private float lastBuild = -10f;

        public Dialog_EpisodeMonitor()
        {
            doCloseX = true;
            draggable = true;
            resizeable = true;
            preventCameraMotion = false;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
        }

        public override Vector2 InitialSize => new Vector2(1000f, 700f);

        public override void DoWindowContents(Rect inRect)
        {
            if (Time.realtimeSinceStartup - lastBuild > 0.5f)
            {
                text = EpisodeMonitor.Build(NetworkRuntime.Current);
                lastBuild = Time.realtimeSinceStartup;
            }
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "Episode Monitor (read-only)");
            Text.Font = GameFont.Tiny;
            Rect outer = new Rect(inRect.x, inRect.y + 38f, inRect.width, inRect.height - 38f);
            float height = Text.CalcHeight(text, outer.width - 20f) + 20f;
            Rect view = new Rect(0f, 0f, outer.width - 20f, height);
            Widgets.BeginScrollView(outer, ref scroll, view);
            Widgets.Label(view, text);
            Widgets.EndScrollView();
            Text.Font = GameFont.Small;
        }
    }

    public static partial class NetworkDevActions
    {
        [DebugAction(Cat, "Episode Monitor (read-only)", allowedGameStates = AllowedGameStates.Playing)]
        public static void OpenEpisodeMonitor()
        {
            Find.WindowStack.Add(new Dialog_EpisodeMonitor());
        }
    }
}
