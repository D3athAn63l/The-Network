using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Physical;
using TheNetwork.Integration.Physical;
using TheNetwork.Kernel;
using UnityEngine;
using Verse;

namespace TheNetwork.Diagnostics.RuntimePhysicalTests
{
    public enum StepResult
    {
        /// <summary>Call the same step again next frame.</summary>
        Wait,
        Next,

        /// <summary>Stop the run now and PRESERVE everything for inspection (a failure).</summary>
        Abort
    }

    /// <summary>
    /// One physical scenario run: an ordered list of steps pumped once per rendered frame, waiting on GAME ticks (a paused game never
    /// times out). It drives the PRODUCTION lifecycle (Plan → Materialize, then vanilla and the episode watch do the rest) and only
    /// observes; the few deliberate test actions (dev damage, a dev arrest, removing the suite's own map) are named in the log. On a
    /// failure nothing is cleaned up: the evidence is preserved.
    /// </summary>
    public abstract class PhysicalRun
    {
        private sealed class Step
        {
            public string name;
            public Func<StepResult> body;
            public int timeoutTicks;
        }

        public readonly PhysicalScenarioInfo info;
        public readonly string runId;
        public readonly PhysicalVerdict v;
        protected readonly NetworkRuntime rt;
        protected readonly DomainContext ctx;
        protected readonly RimWorldPhysicalWorldPort port;
        protected readonly PhysicalLifecycleService lc;
        public readonly object game;

        private readonly List<Step> steps = new List<Step>();
        private int index;
        private int stepStartTick = -1;
        public string StepName => index < steps.Count ? steps[index].name : "finished";
        public readonly int startTick;

        public bool OwnsEpisode(PhysicalEpisode episode)
        {
            return episode != null && episodes.Contains(episode);
        }

        protected LogMark logMark;
        protected PhysicalSentinel before;
        protected readonly HashSet<int> testPeople = new HashSet<int>();
        protected readonly HashSet<int> runFactions = new HashSet<int>();
        protected readonly List<PhysicalEpisode> episodes = new List<PhysicalEpisode>();
        protected PhysicalSignalObserver observer;

        /// <summary>Checked every frame while the run is active (the per-frame invariants of M1 and of the authority gate).</summary>
        protected Action everyFrame;

        protected PhysicalRun(PhysicalScenarioInfo info, string runId, NetworkRuntime rt)
        {
            this.info = info;
            this.runId = runId;
            this.rt = rt;
            ctx = rt.Ctx;
            port = rt.PhysicalWorld;
            lc = ctx.Lifecycle;
            game = Current.Game;
            v = new PhysicalVerdict(info.id, runId);
            startTick = PhysLog.Tick;
        }

        protected void Then(string name, Func<StepResult> body, int timeoutTicks = -1)
        {
            steps.Add(new Step { name = name, body = body, timeoutTicks = timeoutTicks });
        }

        /// <summary>Builds the steps. Called once when the run starts.</summary>
        protected abstract void Script();

        public void Begin(PhysicalSignalObserver obs)
        {
            observer = obs;
            logMark = LogMark.Take();
            before = PhysicalSentinel.Capture(ctx);
            port.visitTicksOverride = PhysicalTestIds.TestVisitTicks;
            PhysLog.Info("===== " + info.Label + " — run " + runId + " started at tick " + startTick + " =====");
            Script();
        }

        /// <summary>One frame. False when the run is over (finished, aborted or abandoned).</summary>
        public bool Pump()
        {
            if (!ReferenceEquals(Current.Game, game) || NetworkRuntime.Current != rt)
            {
                v.stopped = true;
                v.Note("abandoned: a different game or runtime is active now");
                End(false);
                return false;
            }
            try
            {
                everyFrame?.Invoke();
                while (index < steps.Count)
                {
                    Step s = steps[index];
                    int now = PhysLog.Tick;
                    if (stepStartTick < 0)
                    {
                        stepStartTick = now;
                        PhysLog.Info(info.id + " step " + (index + 1) + "/" + steps.Count + ": " + s.name);
                    }
                    StepResult r = s.body();
                    if (r == StepResult.Abort)
                    {
                        v.Note("stopped at step \"" + s.name + "\"; everything is preserved for inspection (Episode Monitor, PHYX — Show status)");
                        End(false);
                        return false;
                    }
                    if (r == StepResult.Wait)
                    {
                        if (s.timeoutTicks > 0 && now - stepStartTick > s.timeoutTicks)
                        {
                            v.Fail("timed out after " + s.timeoutTicks + " game ticks at step \"" + s.name + "\"");
                            v.Note("stopped at step \"" + s.name + "\"; everything is preserved for inspection");
                            End(false);
                            return false;
                        }
                        return true;
                    }
                    index++;
                    stepStartTick = -1;
                }
                End(true);
                return false;
            }
            catch (Exception ex)
            {
                v.Fail("the run threw at step \"" + StepName + "\": " + ex);
                End(false);
                return false;
            }
        }

        public void Stop()
        {
            v.stopped = true;
            v.Note("stopped by the owner at step \"" + StepName + "\"; nothing was undone");
            End(false);
        }

        private bool ended;

        private void End(bool clean)
        {
            if (ended) return;
            ended = true;
            if (port.visitTicksOverride > 0) port.visitTicksOverride = -1;
            v.finished = clean;
            try
            {
                if (clean) Finish();
                List<string> ours = logMark.Since(LogMessageType.Error, "TheNetwork");
                v.Check(ours.Count == 0, "no Network error was logged during the run" + (ours.Count > 0 ? ": " + ours[0] : ""));
                List<string> already = logMark.Since(null, "already here");
                v.Check(already.Count == 0, "vanilla never logged \"already here\" (no second insertion into WorldPawns)" + (already.Count > 0 ? ": " + already[0] : ""));
                List<string> other = logMark.Since(LogMessageType.Error, null);
                if (other.Count > ours.Count) v.Note((other.Count - ours.Count) + " other error line(s) were logged during the run (not attributed to the Network): " + other[0]);
                if (before != null && ReferenceEquals(Current.Game, game)) before.CompareTo(PhysicalSentinel.Capture(ctx), ctx, testPeople, runFactions, v);
            }
            catch (Exception ex)
            {
                v.Fail("the end-of-run evidence could not be gathered: " + ex.Message);
            }
            string text = v.Render(info.Label);
            Verse.Log.Message(text);
            PhysicalTestSession.Record(this, text);
            PhysicalOutcome o = v.Outcome;
            Messages.Message("[TheNetwork] " + info.Label + ": " + o.ToString().ToUpperInvariant() + " (" + v.passed + " passed, " + v.failed + " failed, " + v.gaps + " inconclusive). See the log block "
                + PhysicalTestIds.LogPrefix + "===== " + info.id + ".", o == PhysicalOutcome.Pass ? MessageTypeDefOf.PositiveEvent : o == PhysicalOutcome.Fail ? MessageTypeDefOf.RejectInput : MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>The scenario's final assertions (after every step succeeded).</summary>
        protected virtual void Finish()
        {
        }

        // ================================================================== shared steps and checks

        protected KnownCharacter c;
        protected NetworkActor a;
        protected PhysicalEpisode e;
        protected Pawn p;

        protected StepResult PickSolo(SoloNeed need, ICollection<int> exclude = null)
        {
            string why;
            NetworkActor actor;
            KnownCharacter k = SoloPicker.Pick(ctx, need, exclude, out actor, out why);
            if (k == null)
            {
                v.Gap("no Solo could be used: " + why);
                return StepResult.Abort;
            }
            c = k;
            a = actor;
            testPeople.Add(c.id.Value);
            v.Note("Solo " + a.name?.Display + " (" + a.id + "), person " + c.name?.Display + " (" + c.id + "), custody " + c.custody + ", operational role " + c.opRole
                + (c.pawn != null && c.pawn.IsBound ? ", bound pawn #" + c.pawn.thingIdNumber : ", never materialized"));
            return StepResult.Next;
        }

        /// <summary>
        /// The PRODUCTION path: Plan (custody Deployed, the exclusive episode link) then Materialize (the encounter faction, the
        /// role-constrained first projection or the same-pawn catch-up, the write-once binding, placement with the visit Lord).
        /// </summary>
        protected StepResult MaterializeOnTestMap()
        {
            string report;
            Map map = TestSite.GetPrepared(out report);
            if (map == null)
            {
                v.Fail("test map: " + report);
                return StepResult.Abort;
            }
            v.Note(report);
            EpisodeRequest r = new EpisodeRequest { actor = a.id, purposeKey = "Visit", where = TileRef.Of(map.Tile), mapId = map.uniqueID };
            r.cause.devKey = PhysicalTestIds.DevKey(runId, info.id);
            r.named.Add(c.id);
            PhysicalEpisode ep;
            CommandResult plan = lc.Plan(r, out ep);
            if (!plan.ok)
            {
                v.Fail("the production Plan refused: " + plan);
                return StepResult.Abort;
            }
            e = ep;
            episodes.Add(e);
            v.Check(c.custody == CustodyState.Deployed && c.episode == e.id, "Plan: custody Deployed and the person's episode link is " + e.id + " (got " + c.custody + ", " + c.episode + ")");
            int present = lc.Materialize(e);
            if (e.faction != null && e.faction.loadId >= 0) runFactions.Add(e.faction.loadId);
            if (present != 1 || e.state != EpisodeState.Open)
            {
                v.Fail("Materialize placed " + present + " member(s), episode " + e.state + (e.closeReasonKey != null ? " (" + e.closeReasonKey + ", member " + e.members[0].outcome + ")" : "")
                    + (e.quarantineKey != null ? ", quarantine: " + e.quarantineKey : "") + (e.lastError != null ? ", last error: " + e.lastError : ""));
                return StepResult.Abort;
            }
            p = c.pawn?.pawn;
            v.Check(p != null && p.Spawned && p.Map == map, "the bound pawn is spawned on the test map (" + (p == null ? "no pawn" : "#" + p.thingIDNumber + " spawned " + p.Spawned) + ")");
            v.Check(e.members[0].pawn != null && e.members[0].pawn.SameBinding(c.pawn), "the member's binding mirrors the person's own binding");
            v.Check(PhysicalTags.Has(p, PhysicalTags.Episode(e.id)) && PhysicalTags.Has(p, PhysicalTags.Character(c.id)), "the pawn carries the episode and person routing tags");
            v.Check(port.Registry.Reserves(p), "M1: the registry reserves the pawn while it is spawned");
            return StepResult.Next;
        }

        protected StepResult WaitComplete(PhysicalEpisode ep)
        {
            return ep.IsComplete ? StepResult.Next : StepResult.Wait;
        }

        // ---- ADR-053 (the 3.1 correction pass): an ACTUAL Free retained named pawn is the M1 reservation FAILING --------------------------------

        /// <summary>The pawn is an ordinary <c>Free</c> world pawn right now (vanilla's redress and discard candidate pool).</summary>
        protected static bool IsActualFree(Pawn pawn)
        {
            return pawn != null && Find.WorldPawns != null && Find.WorldPawns.Contains(pawn) && Find.WorldPawns.GetSituation(pawn) == WorldPawnSituation.Free;
        }

        /// <summary>The lifecycle itself reported the failure: the episode is quarantined as ReservationBroken (<see cref="ObservedKind.ReservationBroken"/>).</summary>
        protected static bool IsBrokenReservation(PhysicalEpisode ep)
        {
            return ep != null && ep.state == EpisodeState.Quarantined && ep.quarantineKey != null
                && ep.quarantineKey.StartsWith(PhysicalLifecycleService.QuarantineReservation, StringComparison.Ordinal);
        }

        /// <summary>
        /// An ACTUAL Free is a FAIL here, never an acceptable return: M1 (ADR-053, P3-INV-032) reserves a retained named person from its binding
        /// on, so a Free observation means the reservation failed. The frames are counted by each scenario's per-frame invariants.
        /// </summary>
        protected void CheckNoActualFree(string where, int freeFrames, int brokenFrames)
        {
            v.Check(freeFrames == 0, where + ": the retained pawn was never an ACTUAL Free world pawn (" + freeFrames + " frames): an actual Free is the M1 reservation FAILING (ADR-053, P3-INV-032), never an acceptable return");
            v.Check(brokenFrames == 0, where + ": the lifecycle never reported ReservationBroken for the episode (" + brokenFrames + " frames): that quarantine is the same failure seen by the lifecycle");
        }

        /// <summary>Waits until the pawn has had some time on the map (it walks in from the edge) or reached its chill spot.</summary>
        protected Func<StepResult> WaitOnMap(int ticks)
        {
            int until = -1;
            return () =>
            {
                if (until < 0) until = PhysLog.Tick + ticks;
                if (p == null || !p.Spawned)
                {
                    v.Note("the pawn left the map before the planned action (tick " + PhysLog.Tick + ")");
                    return StepResult.Next;
                }
                return PhysLog.Tick >= until ? StepResult.Next : StepResult.Wait;
            };
        }

        /// <summary>The assertions every completed Returned episode must satisfy (exactly once, RELEASE complete, M1, authority reopened).</summary>
        protected void CheckReturnedAndStored(PhysicalEpisode ep, KnownCharacter k, Pawn pawn)
        {
            EpisodeMember m = ep.members[0];
            v.Check(ep.IsComplete, "the episode is COMPLETE (consequences, release, follow-up, publish)");
            v.Check(m.outcome == MemberOutcome.Returned && m.observed == ObservedKind.WorldFree, "the member's outcome is Returned, decided from WorldFree with exit evidence (got " + m.outcome + " from " + m.observed + ")");
            v.Check(k.custody == CustodyState.Stored, "custody is Stored (got " + k.custody + ")");
            v.Check(!k.episode.IsValid, "the person's episode link was cleared at COMPLETE");
            v.Check(AuthorityGate.CanSimulateAbstractly(k), "abstract authority is open again after COMPLETE");
            v.Check(k.pawn != null && ReferenceEquals(k.pawn.pawn, pawn), "the binding still points to the SAME Pawn object");
            v.Check(Find.WorldPawns.Contains(pawn), "the pawn is a world pawn");
            WorldPawnSituation s = Find.WorldPawns.Contains(pawn) ? Find.WorldPawns.GetSituation(pawn) : WorldPawnSituation.None;
            v.Check(s == WorldPawnSituation.ReservedByQuest, "vanilla sees it as ReservedByQuest (got " + s + ")");
            v.Check(pawn.Suspended, "the stored pawn is Suspended (it does not tick or age while stored)");
            v.Check(port.Registry.Reserves(pawn), "the registry reserves it");
            v.Check(!PhysicalTags.Has(pawn, PhysicalTags.Episode(ep.id)), "RELEASE stripped the episode routing tag");
            v.Check(PhysicalTags.Has(pawn, PhysicalTags.Character(k.id)), "the person routing tag stays while bound");
            v.Check(!Find.WorldPawns.GetPawnsBySituation(WorldPawnSituation.Free).Contains(pawn), "the pawn is NOT in vanilla's Free pool (the redress source)");
        }

        protected void Snap(Pawn pawn, string when)
        {
            if (pawn != null) v.Note("snapshot " + PawnSnap.Of(pawn, when).Line());
        }
    }

    /// <summary>
    /// The tier's session (§ 21.2): the arm, the one active run, the runtime-only history and the signal observer. Nothing here is saved.
    /// The idle cost is one static null check per frame.
    /// </summary>
    public static class PhysicalTestSession
    {
        public static readonly PhysicalTestArm Arm = new PhysicalTestArm();
        private static PhysicalRun active;
        private static object trackedGame;
        private static PhysicalSignalObserver observer;
        private static int runCounter;
        private static readonly List<string> history = new List<string>();

        public const string ArmWarning = "The physical tests create and change REAL game state in this save: a dedicated test map on an empty tile (never your "
            + "colony map), temporary hidden encounter and fixture factions, real pawns for Network Solo contractors (who become retained, stored people of "
            + "this save for good), disposable test pawns, a hidden registry quest, and deliberate dev damage, dev arrests, a dev recruitment, enslavement "
            + "and kidnapping (3.2A custody: those people stay held by vanilla) and test-map removals. Group tests also create owned organizations and selectively retained identities; "
            + "the 032 builders deliberately retain approximately 150 or 300 REAL Pawns permanently in this disposable save. Synthetic P0 is labelled and scoped to the owned test map. "
            + "Create provisions only the raw TestSite. Initialize / Reset QA Lab DESTRUCTIVELY clears every non-Pawn Thing, terrain and roof on that dedicated map, only with zero Pawns and no active physical obligations. "
            + "They never touch your colonists or maps. Use a DISPOSABLE save. One arm authorises exactly ONE action; it is never saved and is cleared on load and on quit.";

        /// <summary>A new game object (load, new game) clears the arm and forgets the old run. Called by FinalizeInit and every frame.</summary>
        public static void ResetForNewGame()
        {
            Arm.Clear("a game was loaded or started");
            active = null;
            observer = null;
            trackedGame = Current.Game;
        }

        /// <summary>Called once per rendered frame by the Network's world component. One static null check while no run is active.</summary>
        public static void PumpFrame()
        {
            PhysicalRun r = active;
            if (r == null) return;
            try
            {
                if (!r.Pump()) active = null;
            }
            catch (Exception ex)
            {
                active = null;
                Verse.Log.Error(PhysicalTestIds.LogPrefix + "the run stopped on an exception (state preserved for inspection; nothing was cleaned up): " + ex);
            }
        }

        public static bool IsRunning => active != null;

        /// <summary>The exact active run owns this Episode; a prior run's provenance alone grants no visibility override.</summary>
        public static bool IsActiveOwnedEpisode(PhysicalEpisode episode)
        {
            PhysicalRun run = active;
            return run != null && ReferenceEquals(run.game, Current.Game) && run.OwnsEpisode(episode)
                && GroupQaRules.OwnedByRun(episode, run.runId, run.info.id);
        }

        private static void EnsureGame()
        {
            if (ReferenceEquals(Current.Game, trackedGame)) return;
            ResetForNewGame();
        }

        public static string NextRunId()
        {
            runCounter++;
            return "r" + PhysLog.Tick + "-" + runCounter;
        }

        /// <summary>The facts the guard checks. Null = an armed action may proceed (the arm is then SPENT by the caller).</summary>
        public static string Refusal(bool needsArm)
        {
            EnsureGame();
            NetworkRuntime rt = NetworkRuntime.Current;
            int incomplete = rt?.Ctx?.episodes?.Incomplete().Count ?? 0;
            return PhysicalTestGuard.Refusal(Prefs.DevMode, !needsArm || Arm.IsArmedFor(Current.Game), rt != null && rt.Session.IsRunning && !rt.Inert,
                rt?.PhysicalWorld != null && rt.PhysicalWorld.Available, active != null, incomplete);
        }

        public static string ProvisioningRefusal()
        {
            EnsureGame();
            return PhysicalTestGuard.Refusal(Prefs.DevMode, Arm.IsArmedFor(Current.Game), true, true, active != null, 0);
        }

        public static void CreateTestMap()
        {
            string report = ProvisioningRefusal();
            if (report != null) { Messages.Message("[TheNetwork] Create TestSite refused: " + report, MessageTypeDefOf.RejectInput, false); return; }
            Arm.Spend(Current.Game, "create test map");
            Map map = TestSite.Create(out report);
            PhysLog.Info(report);
            Messages.Message("[TheNetwork] " + report, map == null ? MessageTypeDefOf.RejectInput : MessageTypeDefOf.NeutralEvent, false);
        }

        public static void ResetQaLab()
        {
            string report;
            bool ok = QaLab.InitializeOrReset(TestSite.Map, out report);
            PhysLog.Info(report);
            Messages.Message("[TheNetwork] " + report, ok ? MessageTypeDefOf.NeutralEvent : MessageTypeDefOf.RejectInput, false);
        }

        /// <summary>Spends the arm and starts a run, or refuses by name (and the arm is NOT spent on a refusal).</summary>
        public static bool Start(Func<NetworkRuntime, string, PhysicalRun> make, PhysicalScenarioInfo info)
        {
            string refusal = Refusal(true);
            if (refusal == null && TestSite.GetPrepared(out string labReport) == null) refusal = labReport;
            if (refusal != null)
            {
                Messages.Message("[TheNetwork] " + info.Label + " refused: " + refusal, MessageTypeDefOf.RejectInput, false);
                PhysLog.Warn(info.Label + " refused: " + refusal);
                return false;
            }
            Arm.Spend(Current.Game, info.id);
            NetworkRuntime rt = NetworkRuntime.Current;
            PhysicalRun run = make(rt, NextRunId());
            EnsureObserver();
            active = run;
            run.Begin(observer);
            return true;
        }

        /// <summary>A READ-ONLY run (no arm, nothing changed): allowed while episodes are incomplete and before the Network has started.</summary>
        public static bool StartReadOnly(Func<NetworkRuntime, string, PhysicalRun> make, PhysicalScenarioInfo info)
        {
            EnsureGame();
            NetworkRuntime rt = NetworkRuntime.Current;
            string refusal = !Prefs.DevMode ? "Dev Mode is off" : rt?.PhysicalWorld == null ? "no Network runtime in this game" : active != null ? "another physical test run is in progress" : null;
            if (refusal == null && (info.id == "RT-PHYX-030" || info.id == "RT-PHYX-032")
                && TestSite.GetPrepared(out string labReport) == null) refusal = labReport;
            if (refusal != null)
            {
                Messages.Message("[TheNetwork] " + info.Label + " refused: " + refusal, MessageTypeDefOf.RejectInput, false);
                return false;
            }
            PhysicalRun run = make(rt, NextRunId());
            EnsureObserver();
            active = run;
            run.Begin(observer);
            return true;
        }

        private static void EnsureObserver()
        {
            if (observer != null) return;
            observer = new PhysicalSignalObserver();
            Find.SignalManager.RegisterReceiver(observer);
        }

        /// <summary>A RETIRED scenario's menu item: it runs nothing, needs no arm, and says what superseded it (its id is never reused).</summary>
        public static void Retired(PhysicalScenarioInfo info)
        {
            string text = info.Label + ": " + (info.retired ?? "retired");
            PhysLog.Info(text);
            Messages.Message("[TheNetwork] " + text, MessageTypeDefOf.RejectInput, false);
        }

        public static void Record(PhysicalRun run, string text)
        {
            history.Add(text);
            if (history.Count > 20) history.RemoveAt(0);
        }

        public static void Stop()
        {
            PhysicalRun r = active;
            if (r == null)
            {
                Messages.Message("[TheNetwork] No physical test run is in progress.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            active = null;
            r.Stop();
        }

        public static string Status()
        {
            EnsureGame();
            NetworkRuntime rt = NetworkRuntime.Current;
            StringBuilder b = new StringBuilder();
            b.AppendLine(PhysicalTestIds.LogPrefix + "===== STATUS (read-only) =====");
            b.AppendLine("  arm: " + (Arm.IsArmedFor(Current.Game) ? "ARMED for one action" : "not armed") + " (" + Arm.LastChange + ")");
            b.AppendLine("  run: " + (active == null ? "none" : active.info.Label + " run " + active.runId + ", step \"" + active.StepName + "\""));
            Map m = TestSite.Map;
            b.AppendLine("  test site: " + (TestSite.FindParent() == null ? "none" : "world object " + TestSite.FindParent().ID + (m == null ? ", no map" : ", map " + m.uniqueID + " with " + m.mapPawns.AllPawnsSpawnedCount + " spawned pawns")));
            if (rt?.Ctx?.episodes != null)
            {
                List<PhysicalEpisode> open = rt.Ctx.episodes.Incomplete();
                b.AppendLine("  incomplete episodes: " + open.Count);
                for (int i = 0; i < open.Count && i < 10; i++) b.AppendLine("    " + open[i].id + " " + open[i].state + (open[i].quarantineKey != null ? " (" + open[i].quarantineKey + ")" : "") + ", cause " + open[i].cause?.devKey);
            }
            if (rt?.PhysicalWorld != null) b.AppendLine("  adapter: " + rt.PhysicalWorld.Describe());
            if (rt?.Ctx?.Lifecycle != null) b.AppendLine("  lifecycle: " + rt.Ctx.Lifecycle.counters);
            string refusal = Refusal(true);
            b.AppendLine("  guard: " + (refusal == null ? "an armed scenario may start" : refusal));
            b.Append("  finished runs this session: " + history.Count);
            return b.ToString();
        }

        public static string LastReports(int n)
        {
            if (history.Count == 0) return null;
            StringBuilder b = new StringBuilder();
            for (int i = Math.Max(0, history.Count - n); i < history.Count; i++) b.AppendLine(history[i]);
            return b.ToString();
        }

        /// <summary>
        /// Cleanup (armed): removes ONLY the suite's own things: the test map and site (refused while an incomplete episode has a member on
        /// it: that evidence is preserved; "PHYX — Remove test map now" ends those through vanilla's removal), leftover fixture factions.
        /// Never a bound pawn, never a Network person, never an episode's encounter faction (production releases those).
        /// </summary>
        public static void Cleanup()
        {
            EnsureGame();
            string refusal = PhysicalTestGuard.Refusal(Prefs.DevMode, Arm.IsArmedFor(Current.Game), true, true, active != null, 0);
            if (refusal != null)
            {
                Messages.Message("[TheNetwork] Cleanup refused: " + refusal, MessageTypeDefOf.RejectInput, false);
                return;
            }
            NetworkRuntime rt = NetworkRuntime.Current;
            int onMap = TestSite.IncompleteMembersOnMap(rt?.Ctx);
            if (onMap > 0)
            {
                Messages.Message("[TheNetwork] Cleanup refused: " + onMap + " member(s) of incomplete episodes are on the test map (evidence preserved). Use \"PHYX — Remove test map now\" to end them through vanilla's map removal, or let them finish.", MessageTypeDefOf.RejectInput, false);
                return;
            }
            Arm.Spend(Current.Game, "cleanup");
            List<string> done = new List<string>();
            foreach (Faction f in new List<Faction>(Find.FactionManager.AllFactionsListForReading))
            {
                if (TestFixtures.ReleaseFaction(f)) done.Add("fixture faction " + f.loadID + " handed to vanilla's temporary-faction removal");
            }
            done.Add(TestSite.RemoveMap(true));
            if (observer != null)
            {
                Find.SignalManager.DeregisterReceiver(observer);
                observer = null;
            }
            string text = PhysicalTestIds.LogPrefix + "===== CLEANUP =====\n  " + string.Join("\n  ", done.ToArray()) + "\n  kept on purpose: every bound Network pawn (stored people stay retained), every Network person, the registry quest, episode encounter factions (production releases them)\n"
                + PhysicalTestIds.LogPrefix + "===== end CLEANUP =====";
            Verse.Log.Message(text);
            Messages.Message("[TheNetwork] Physical test cleanup done (see the log).", MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>The escape hatch (armed): vanilla's map removal of the suite's own map, members included. Bound pawns are passed, never destroyed.</summary>
        public static void RemoveTestMapNow()
        {
            EnsureGame();
            string refusal = PhysicalTestGuard.Refusal(Prefs.DevMode, Arm.IsArmedFor(Current.Game), true, true, active != null, 0);
            if (refusal != null)
            {
                Messages.Message("[TheNetwork] Remove test map refused: " + refusal, MessageTypeDefOf.RejectInput, false);
                return;
            }
            Arm.Spend(Current.Game, "remove test map");
            string r = TestSite.RemoveMap(false);
            PhysLog.Info("Remove test map now: " + r + " (vanilla passed every pawn on it to the world; the episode watch reconciles them)");
            Messages.Message("[TheNetwork] " + r, MessageTypeDefOf.NeutralEvent, false);
        }
    }

    /// <summary>The arm: a modal that states what the suite will do and requires typing the exact phrase. Runtime only.</summary>
    public sealed class Dialog_ArmPhysicalTests : Window
    {
        private string typed = "";
        private string refusal;

        public Dialog_ArmPhysicalTests()
        {
            forcePause = true;
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
        }

        public override Vector2 InitialSize => new Vector2(640f, 440f);

        public override void DoWindowContents(Rect inRect)
        {
            Listing_Standard l = new Listing_Standard();
            l.Begin(inRect);
            Text.Font = GameFont.Medium;
            l.Label("Arm the physical tests (Dev Mode, disposable save only)");
            Text.Font = GameFont.Small;
            l.Gap(6f);
            l.Label(PhysicalTestSession.ArmWarning);
            l.Gap(10f);
            l.Label("To arm ONE physical test action, type exactly:  " + PhysicalTestIds.ArmPhrase);
            typed = l.TextEntry(typed);
            if (refusal != null) l.Label("Not armed: " + refusal);
            l.Gap(6f);
            if (l.ButtonText("Arm one physical test action"))
            {
                string why;
                if (PhysicalTestSession.Arm.Arm(Current.Game, typed, out why))
                {
                    PhysLog.Info("armed for one action (runtime only; cleared on load and on quit)");
                    Messages.Message("[TheNetwork] Physical tests armed for ONE action.", MessageTypeDefOf.NeutralEvent, false);
                    Close();
                }
                else refusal = why;
            }
            if (l.ButtonText("Cancel")) Close();
            l.End();
        }
    }
}
