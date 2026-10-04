using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Physical;
using TheNetwork.Integration.Physical;
using TheNetwork.Kernel;
using Verse;
using Verse.AI.Group;

namespace TheNetwork.Diagnostics.RuntimePhysicalTests
{
    // Every scenario drives the PRODUCTION lifecycle (Plan → Materialize; then vanilla AI, vanilla exits and the episode watch) and observes.
    // The deliberate test actions are few and named in the log: dev damage (003, 004, 006), a dev arrest (009), removing the suite's own
    // test map (003, 005, 009, 016, 010-B), the suite's own disposable pawns and fixture factions (007, 011, 012).

    /// <summary>RT-PHYX-001 — generate + bind + spawn exactly one named pawn on the test map; tags and binding agree; role truth holds.</summary>
    public sealed class Phyx001FirstMaterialization : PhysicalRun
    {
        private int created0, projections0;

        public Phyx001FirstMaterialization(NetworkRuntime rt, string runId) : base(PhysicalScenarioTable.Get("RT-PHYX-001"), runId, rt)
        {
        }

        protected override void Script()
        {
            Then("pick a never-materialized Solo", () => PickSolo(SoloNeed.Fresh));
            Then("materialize through the production lifecycle", () =>
            {
                created0 = lc.counters.created;
                projections0 = port.counters.projections;
                return MaterializeOnTestMap();
            });
            Then("check the first creation, the binding and the placement", () =>
            {
                CheckFirstCreation();
                return StepResult.Next;
            });
            Then("let the vanilla visit run and the episode complete", () => WaitComplete(e), 60000);
        }

        private void CheckFirstCreation()
        {
            v.Check(lc.counters.created == created0 + 1, "exactly one pawn was created for the person (" + (lc.counters.created - created0) + ")");
            v.Check(port.counters.projections == projections0 + 1, "exactly one role-constrained first projection ran");
            ProjectionResult r = port.lastProjection;
            v.Note("first projection: " + r);
            v.Check(r != null && ReferenceEquals(r.pawn, p), "the projection's pawn is the bound pawn");
            v.Check(r?.spec != null && RoleRules.Verify(r.spec, PawnRoleReader.Snapshot(p)).holds, "re-verified now: the bound pawn satisfies its operational role " + c.opRole + " (" + r?.spec?.Describe() + ")");
            v.Check(c.opRole != OperationalRole.Unset, "the person's operational role is stored (derived from immutable origin facts)");
            v.Check(c.pawn.boundTick >= 0 && c.pawn.agedThroughTick == c.pawn.boundTick, "the binding records boundTick and agedThroughTick = boundTick");
            v.Check(c.pawn.thingIdNumber == p.thingIDNumber, "the persisted thing id matches the pawn");
            NamePins pins = NamePins.From(c.name);
            NameTriple n = p.Name as NameTriple;
            if (pins.Any && n != null)
            {
                v.Check((pins.first == null || n.First == pins.first) && (pins.last == null || n.Last == pins.last), "established name facts pinned: \"" + n.ToStringFull + "\" for the record \"" + c.name?.Display + "\"");
            }
            else v.Note("name: " + p.Name?.ToStringFull + " (pins " + (pins.Any ? "present" : "none") + ")");
            Faction f = e.faction?.Resolve();
            v.Check(f != null && f.temporary && f.Hidden && p.Faction == f, "the pawn belongs to the episode's hidden temporary encounter faction");
            Lord lord = p.GetLord();
            v.Check(lord?.LordJob is LordJob_VisitColony, "vanilla AI: the pawn is driven by a LordJob_VisitColony (" + (lord?.LordJob?.GetType().Name ?? "no lord") + ")");
            v.Check(!Find.WorldPawns.Contains(p), "while spawned, the pawn is not a world pawn (no early insertion)");
            Quest q = port.Registry.FindQuest();
            v.Check(q != null && q.hidden && q.State == QuestState.Ongoing && q.QuestReserves(p), "the registry quest is hidden, Ongoing and reserves the spawned pawn (M1)");
        }

        protected override void Finish()
        {
            CheckReturnedAndStored(e, c, p);
        }
    }

    /// <summary>RT-PHYX-002 — the visit Lord runs, the pawn exits through the edge, the episode reconciles Returned ONCE.</summary>
    public sealed class Phyx002NormalExit : PhysicalRun
    {
        private int commits0, passes0, skipped0, refused0, releases0, wakeups0;
        private int authorityLeak, storedEarly, freeSeen;

        public Phyx002NormalExit(NetworkRuntime rt, string runId) : base(PhysicalScenarioTable.Get("RT-PHYX-002"), runId, rt)
        {
        }

        protected override void Script()
        {
            Then("pick a Solo", () => PickSolo(SoloNeed.Any));
            Then("materialize through the production lifecycle", () =>
            {
                commits0 = lc.counters.commits;
                passes0 = port.counters.passes;
                skipped0 = lc.counters.passSkippedAlreadyWorld;
                refused0 = lc.counters.passRefused;
                releases0 = lc.counters.releasesCompleted;
                wakeups0 = lc.counters.wakeups;
                StepResult r = MaterializeOnTestMap();
                everyFrame = Invariants;
                return r;
            });
            Then("let the visit end through vanilla's own exit and the episode complete", () => WaitComplete(e), 60000);
            Then("a duplicate dev wake-up is a no-op", () =>
            {
                bool again = lc.Reconcile(e, "dev duplicate (RT-PHYX-002)");
                v.Check(!again && lc.counters.commits == commits0 + 1, "the episode was committed exactly once (" + (lc.counters.commits - commits0) + " commits over " + (lc.counters.wakeups - wakeups0) + " wake-ups)");
                return StepResult.Next;
            });
        }

        private void Invariants()
        {
            if (e == null || c == null || p == null) return;
            if (!e.releaseApplied && AuthorityGate.CanSimulateAbstractly(c)) authorityLeak++;
            if (c.custody == CustodyState.Stored && !e.consequencesApplied) storedEarly++;
            if (Find.WorldPawns.Contains(p) && Find.WorldPawns.GetSituation(p) == WorldPawnSituation.Free) freeSeen++;
        }

        protected override void Finish()
        {
            CheckReturnedAndStored(e, c, p);
            PhysicalSignalObserver.Seen left = observer.First("LeftMap", p.thingIDNumber, startTick);
            if (left == null) v.Gap("no synchronous LeftMap was observed for the pawn, so the exit evidence cannot be confirmed");
            else v.Check(c.pawn.agedThroughTick == left.tick, "agedThroughTick is the observed exit tick " + left.tick + " (got " + c.pawn.agedThroughTick + ")");
            v.Check(port.counters.passes == passes0, "the Network called PassToWorld 0 times (vanilla's exit already passed the pawn)");
            v.Check(lc.counters.passSkippedAlreadyWorld > skipped0 && lc.counters.passRefused == refused0, "RELEASE recognised the world pawn and skipped the pass (skipped " + (lc.counters.passSkippedAlreadyWorld - skipped0) + ", refused " + (lc.counters.passRefused - refused0) + ")");
            v.Check(lc.counters.releasesCompleted == releases0 + 1, "RELEASE completed exactly once");
            v.Check(authorityLeak == 0, "abstract authority stayed closed on every frame until RELEASE completed (" + authorityLeak + " frames open early)");
            v.Check(storedEarly == 0, "custody became Stored only through the commit");
            v.Check(freeSeen == 0, "the pawn was never seen Free in WorldPawns (" + freeSeen + " frames)");
        }
    }

    /// <summary>RT-PHYX-003 — downed (dev damage), then recovers; both observed by the bounded watch; recovery truth correct at the end.</summary>
    public sealed class Phyx003DownedRecovery : PhysicalRun
    {
        private int commits0;
        private int downedTick, upTick;
        private int expectedWoundDays = -1;
        private int permanentAtExit = -1;

        public Phyx003DownedRecovery(NetworkRuntime rt, string runId) : base(PhysicalScenarioTable.Get("RT-PHYX-003"), runId, rt)
        {
        }

        protected override void Script()
        {
            Then("pick a Solo", () => PickSolo(SoloNeed.Any));
            Then("materialize through the production lifecycle", MaterializeOnTestMap);
            Then("let the visitor walk in", WaitOnMap(600));
            Then("dev damage until downed (no bleeding wounds)", () =>
            {
                if (!p.Spawned)
                {
                    v.Gap("the pawn left the map before it could be downed");
                    return StepResult.Abort;
                }
                commits0 = lc.counters.commits;
                HealthUtility.DamageUntilDowned(p, false);
                downedTick = PhysLog.Tick;
                Snap(p, "after dev damage");
                if (!p.Downed)
                {
                    v.Gap("vanilla's DamageUntilDowned did not down the pawn");
                    return StepResult.Abort;
                }
                return StepResult.Next;
            });
            Then("the watch observes the downed pawn; downed is not terminal", () =>
            {
                if (PhysLog.Tick < downedTick + 2 * PhysicalLifecycleService.WatchPeriod + 10) return StepResult.Wait;
                PhysicalObservation o = port.Observe(c.pawn, e.id);
                v.Check(o.kind == ObservedKind.Spawned && o.downed, "observed: Spawned and downed (" + o.kind + ", downed " + o.downed + ", health " + o.health.ToString("0.00") + ")");
                v.Check(e.state == EpisodeState.Open && e.members[0].outcome == MemberOutcome.Pending && lc.counters.commits == commits0, "two watch periods later the episode is still Open and nothing was committed");
                return StepResult.Next;
            });
            Then("dev first aid: remove temporary injuries until the pawn can stand", () =>
            {
                List<Hediff_Injury> injuries = new List<Hediff_Injury>();
                foreach (Hediff h in p.health.hediffSet.hediffs) if (h is Hediff_Injury i && !i.IsPermanent()) injuries.Add(i);
                injuries.Sort((x, y) => y.Severity.CompareTo(x.Severity));
                int removed = 0;
                for (int i = 0; i < injuries.Count && p.Downed; i++)
                {
                    p.health.RemoveHediff(injuries[i]);
                    removed++;
                }
                Snap(p, "after dev first aid (" + removed + " of " + injuries.Count + " injuries removed)");
                if (p.Downed)
                {
                    v.Gap("the pawn is still downed after the dev first aid (not an injury: " + string.Join(", ", PawnNormalization.Remaining(p).ToArray()) + ")");
                    return StepResult.Abort;
                }
                upTick = PhysLog.Tick;
                return StepResult.Next;
            });
            Then("the watch observes the recovery", () =>
            {
                if (PhysLog.Tick < upTick + 2 * PhysicalLifecycleService.WatchPeriod + 10) return StepResult.Wait;
                PhysicalObservation o = port.Observe(c.pawn, e.id);
                v.Check(o.kind == ObservedKind.Spawned && !o.downed, "recovery observed: Spawned and not downed (health " + o.health.ToString("0.00") + ")");
                v.Check(e.state == EpisodeState.Open && lc.counters.commits == commits0, "still Open and uncommitted after the recovery");
                return StepResult.Next;
            });
            Then("end the visit through vanilla's map removal (the recovered visitor has no Lord left)", () =>
            {
                PhysicalObservation o = port.Observe(c.pawn, e.id);
                expectedWoundDays = ReconciliationPlanner.WoundDaysFor(o);
                permanentAtExit = CountPermanent(p);
                v.Note("health at the exit " + o.health.ToString("0.00") + " ⇒ the designed recovery is " + expectedWoundDays + " day(s); " + permanentAtExit + " permanent injuries");
                v.Note(TestSite.RemoveMap(false));
                return StepResult.Next;
            });
            Then("wait for the episode to complete", () => WaitComplete(e), 30000);
        }

        private static int CountPermanent(Pawn pawn)
        {
            int n = 0;
            foreach (Hediff h in pawn.health.hediffSet.hediffs) if (h is Hediff_Injury i && i.IsPermanent()) n++;
            return n;
        }

        protected override void Finish()
        {
            CheckReturnedAndStored(e, c, p);
            if (expectedWoundDays > 0)
            {
                v.Check(c.status == CharacterStatus.Wounded && c.woundedUntilTick > e.committedTick, "the temporary injuries became the abstract recovery once: Wounded until " + c.woundedUntilTick + " (" + expectedWoundDays + " days designed)");
            }
            else
            {
                v.Check(c.status == CharacterStatus.Active, "healthy enough at the exit: no abstract recovery (status " + c.status + ")");
            }
            int temporary = 0;
            foreach (Hediff h in p.health.hediffSet.hediffs) if (h is Hediff_Injury i && !i.IsPermanent()) temporary++;
            v.Check(temporary == 0, "store-time normalization healed every temporary injury on the pawn (" + temporary + " left)");
            v.Check(CountPermanent(p) == permanentAtExit, "permanent injuries stayed on the pawn (" + CountPermanent(p) + ")");
        }
    }

    /// <summary>RT-PHYX-004 — killed (dev damage); death recorded once; the abstract layer cannot resurrect or regenerate.</summary>
    public sealed class Phyx004Killed : PhysicalRun
    {
        private int commits0, created0;

        public Phyx004Killed(NetworkRuntime rt, string runId) : base(PhysicalScenarioTable.Get("RT-PHYX-004"), runId, rt)
        {
        }

        protected override void Script()
        {
            Then("pick a never-materialized Solo (a stored person is not spent on this)", () => PickSolo(SoloNeed.Fresh));
            Then("materialize through the production lifecycle", MaterializeOnTestMap);
            Then("let the visitor walk in", WaitOnMap(300));
            Then("dev damage until dead", () =>
            {
                commits0 = lc.counters.commits;
                created0 = lc.counters.created;
                HealthUtility.DamageUntilDead(p);
                v.Check(p.Dead, "vanilla killed the pawn");
                return StepResult.Next;
            });
            Then("wait for the episode to complete", () => WaitComplete(e), 20000);
            Then("a duplicate wake-up and a new Plan change nothing", () =>
            {
                bool again = lc.Reconcile(e, "dev duplicate (RT-PHYX-004)");
                v.Check(!again && lc.counters.commits == commits0 + 1, "the death was committed exactly once");
                EpisodeRequest r = new EpisodeRequest { actor = a.id, purposeKey = "Visit", where = e.whereTile, mapId = e.whereMapId };
                r.cause.devKey = PhysicalTestIds.DevKey(runId, info.id + "-resurrect");
                r.named.Add(c.id);
                PhysicalEpisode ignored;
                CommandResult plan = lc.Plan(r, out ignored);
                v.Check(!plan.ok, "a new Plan for the dead person is refused (" + plan + "): no abstract or physical resurrection");
                return StepResult.Next;
            });
        }

        protected override void Finish()
        {
            v.Check(e.IsComplete && e.members[0].outcome == MemberOutcome.Killed, "the episode is complete with outcome Killed (" + e.members[0].outcome + ")");
            v.Check(!c.IsAlive && c.status == CharacterStatus.Dead && c.diedTick >= 0, "the person is Dead (died tick " + c.diedTick + ", cause " + c.deathCauseKey + ")");
            v.Check(lc.counters.created == created0, "no pawn was generated after the death");
            v.Check(c.pawn != null && ReferenceEquals(c.pawn.pawn, p), "the binding still points to the same (dead) pawn: never regenerated");
            v.Check(!port.Registry.Reserves(p), "the registry no longer reserves a dead person's pawn");
            v.Check(!a.IsActive, "the Solo actor ended with its person (" + a.status + ")");
        }
    }

    /// <summary>RT-PHYX-005 — the test map is removed with the pawn still on it; the person is observed, not erased.</summary>
    public sealed class Phyx005MapRemoved : PhysicalRun
    {
        private int leftMap0;

        public Phyx005MapRemoved(NetworkRuntime rt, string runId) : base(PhysicalScenarioTable.Get("RT-PHYX-005"), runId, rt)
        {
        }

        protected override void Script()
        {
            Then("pick a Solo", () => PickSolo(SoloNeed.Any));
            Then("materialize through the production lifecycle", MaterializeOnTestMap);
            Then("let the visitor walk in", WaitOnMap(300));
            Then("remove the test map with the pawn on it (vanilla removal)", () =>
            {
                if (!p.Spawned)
                {
                    v.Gap("the pawn left before the map removal; re-run");
                    return StepResult.Abort;
                }
                leftMap0 = observer.Count("LeftMap", p.thingIDNumber, startTick);
                int faction = p.Faction?.loadID ?? -1;
                v.Note(TestSite.RemoveMap(false));
                PawnSnap s = PawnSnap.Of(p, "immediately after the map removal");
                v.Note("snapshot " + s.Line());
                v.Check(s.worldPawn && s.situation == WorldPawnSituation.ReservedByQuest, "immediately after the removal the pawn is a world pawn reserved by the registry, never Free (" + s.situation + ")");
                v.Check(s.factionId == faction, "the pass did not rewrite the pawn's faction");
                v.Check(e.state == EpisodeState.Open && c.custody == CustodyState.Deployed, "the episode is still Open: nothing was decided by the removal itself");
                int leftMap = observer.Count("LeftMap", p.thingIDNumber, startTick) - leftMap0;
                if (leftMap == 0) v.Check(true, "no LeftMap for a non-player pawn on map removal (as audited)");
                else v.Note("AUDIT DEVIATION: " + leftMap + " LeftMap signal(s) arrived on map removal");
                return StepResult.Next;
            });
            Then("wait for the episode to complete", () => WaitComplete(e), 20000);
        }

        protected override void Finish()
        {
            CheckReturnedAndStored(e, c, p);
            v.Note("agedThroughTick " + c.pawn.agedThroughTick + " (no exit signal on map removal: the commit tick, " + e.committedTick + ")");
        }
    }

    /// <summary>
    /// RT-PHYX-006 — rematerialization: the SAME Pawn object (same thingIDNumber), name, permanent injury kept, TRUTHFULLY older (the full
    /// stored interval through the mothball path), no second insertion into WorldPawns. Then a permanent scar and a temporary bruise are
    /// given; after the next storage the scar stays and the bruise is normalized; a second rematerialization shows the scar again.
    /// </summary>
    public sealed class Phyx006Rematerialization : PhysicalRun
    {
        private Pawn stored;
        private int thingId;
        private string name0;
        private long birth0, bioStored;
        private int agedThrough0;
        private float rate;
        private List<string> permanent0;
        private string gear0;
        private int created0, remat0, catchUps0, passes0;
        private PhysicalEpisode first, second;
        private string scar;
        private HediffDef bruiseDef;

        public Phyx006Rematerialization(NetworkRuntime rt, string runId) : base(PhysicalScenarioTable.Get("RT-PHYX-006"), runId, rt)
        {
        }

        protected override void Script()
        {
            Then("pick a STORED Solo", () => PickSolo(SoloNeed.Stored));
            Then("record the stored person", () =>
            {
                stored = c.pawn.pawn;
                thingId = stored.thingIDNumber;
                name0 = stored.Name?.ToStringFull;
                birth0 = stored.ageTracker.BirthAbsTicks;
                bioStored = stored.ageTracker.AgeBiologicalTicks;
                agedThrough0 = c.pawn.agedThroughTick;
                rate = stored.ageTracker.BiologicalTicksPerTick;
                permanent0 = Permanent(stored);
                gear0 = Gear(stored);
                v.Check(Find.WorldPawns.Contains(stored) && Find.WorldPawns.GetSituation(stored) == WorldPawnSituation.ReservedByQuest && stored.Suspended, "stored: a suspended world pawn reserved by the registry");
                v.Note("stored person: #" + thingId + " \"" + name0 + "\", agedThroughTick " + agedThrough0 + ", biological " + bioStored + ", permanent injuries [" + string.Join(", ", permanent0.ToArray()) + "], gear " + gear0);
                return StepResult.Next;
            });
            Then("rematerialize through the production lifecycle", () => Rematerialize("first"));
            Then("let the visitor walk in", WaitOnMap(300));
            Then("dev damage: one PERMANENT scar and one temporary bruise", () =>
            {
                if (!p.Spawned)
                {
                    v.Gap("the pawn left before the injuries could be given");
                    return StepResult.Abort;
                }
                List<BodyPartRecord> parts = new List<BodyPartRecord>();
                foreach (BodyPartRecord part in p.health.hediffSet.GetNotMissingParts(BodyPartHeight.Undefined, BodyPartDepth.Outside)) if (part.def.hitPoints >= 15) parts.Add(part);
                if (parts.Count < 2)
                {
                    v.Gap("no two outside body parts to injure");
                    return StepResult.Abort;
                }
                Hediff_Injury cut = (Hediff_Injury)HediffMaker.MakeHediff(HediffDefOf.Cut, p, parts[0]);
                cut.Severity = 2f;
                HediffComp_GetsPermanent perm = cut.TryGetComp<HediffComp_GetsPermanent>();
                if (perm == null)
                {
                    v.Gap("the Cut hediff cannot become permanent in this game");
                    return StepResult.Abort;
                }
                perm.IsPermanent = true;
                p.health.AddHediff(cut, parts[0]);
                bruiseDef = DamageDefOf.Blunt?.hediff ?? HediffDefOf.Cut;
                Hediff bruise = HediffMaker.MakeHediff(bruiseDef, p, parts[1]);
                bruise.Severity = 2f;
                p.health.AddHediff(bruise, parts[1]);
                scar = cut.def.defName + "@" + parts[0].def.defName;
                v.Note("dev damage: permanent " + scar + ", temporary " + bruise.def.defName + "@" + parts[1].def.defName);
                return StepResult.Next;
            });
            Then("let the visit end and the episode complete", () => WaitComplete(first), 60000);
            Then("after storage: the scar stays, the bruise was normalized", () =>
            {
                CheckReturnedAndStored(first, c, p);
                v.Check(Permanent(p).Contains(scar), "the permanent scar " + scar + " stays on the stored pawn");
                bool bruise = false;
                foreach (Hediff h in p.health.hediffSet.hediffs) if (h.def == bruiseDef && h is Hediff_Injury && !h.IsPermanent()) bruise = true;
                v.Check(!bruise, "the temporary bruise was healed by store-time normalization");
                bioStored = p.ageTracker.AgeBiologicalTicks;
                agedThrough0 = c.pawn.agedThroughTick;
                rate = p.ageTracker.BiologicalTicksPerTick;
                return StepResult.Next;
            });
            Then("stored for a while: the pawn does not age while stored", () =>
            {
                if (PhysLog.Tick < agedThrough0 + 2500) return StepResult.Wait;
                v.Check(p.ageTracker.AgeBiologicalTicks == bioStored, "a stored (suspended) pawn did not age on its own (" + (p.ageTracker.AgeBiologicalTicks - bioStored) + " ticks)");
                return StepResult.Next;
            });
            Then("rematerialize again", () => Rematerialize("second"));
            Then("the scar is still there", () =>
            {
                v.Check(Permanent(p).Contains(scar), "the permanent scar " + scar + " is on the rematerialized pawn");
                return StepResult.Next;
            });
            Then("let the second visit end and the episode complete", () => WaitComplete(second), 60000);
        }

        private StepResult Rematerialize(string which)
        {
            created0 = lc.counters.created;
            remat0 = lc.counters.rematerialized;
            catchUps0 = port.counters.catchUps;
            passes0 = port.counters.passes;
            int now = PhysLog.Tick;
            long bio = p == null ? bioStored : p.ageTracker.AgeBiologicalTicks;
            StepResult r = MaterializeOnTestMap();
            if (r != StepResult.Next) return r;
            if (which == "first") first = e;
            else second = e;
            long expected = (long)Math.Round((now - (long)agedThrough0) * (double)rate);
            long delta = p.ageTracker.AgeBiologicalTicks - bio;
            v.Check(ReferenceEquals(p, stored), which + " rematerialization: the SAME Pawn object");
            v.Check(p.thingIDNumber == thingId && p.Name?.ToStringFull == name0, which + ": same thing id #" + thingId + " and name \"" + name0 + "\"");
            v.Check(p.ageTracker.BirthAbsTicks == birth0, which + ": BirthAbsTicks unchanged (chronological age stays truthful)");
            v.Check(Math.Abs(delta - expected) <= Math.Max(2L, expected / 1000), which + ": TRUTHFUL AGE: biological age advanced by " + delta + " ticks for the full stored interval " + (now - agedThrough0) + " ticks × rate " + rate.ToString("0.###") + " (expected " + expected + ")");
            v.Check(c.pawn.agedThroughTick == now, which + ": agedThroughTick advanced to now after the catch-up");
            v.Check(lc.counters.created == created0 && lc.counters.rematerialized == remat0 + 1 && port.counters.catchUps == catchUps0 + 1, which + ": no creation; one rematerialization; one catch-up");
            v.Check(!Find.WorldPawns.Contains(p), which + ": vanilla's spawn took the pawn out of WorldPawns (no Network removal, no second insertion)");
            List<string> perm = Permanent(p);
            bool kept = true;
            for (int i = 0; i < permanent0.Count; i++) if (!perm.Contains(permanent0[i])) kept = false;
            v.Check(kept, which + ": every permanent injury recorded at storage is still there [" + string.Join(", ", perm.ToArray()) + "]");
            string gear = Gear(p);
            if (gear != gear0) v.Note(which + ": gear differs (vanilla or another mod changed it): " + gear0 + " → " + gear);
            gear0 = gear;
            return StepResult.Next;
        }

        private static List<string> Permanent(Pawn pawn)
        {
            List<string> r = new List<string>();
            foreach (Hediff h in pawn.health.hediffSet.hediffs) if (h.IsPermanent()) r.Add(h.def.defName + "@" + (h.Part?.def?.defName ?? "body"));
            r.Sort(StringComparer.Ordinal);
            return r;
        }

        private static string Gear(Pawn pawn)
        {
            List<string> r = new List<string>();
            if (pawn.equipment?.Primary != null) r.Add(pawn.equipment.Primary.def.defName);
            if (pawn.apparel != null) foreach (Apparel ap in pawn.apparel.WornApparel) r.Add(ap.def.defName);
            r.Sort(StringComparer.Ordinal);
            return string.Join("+", r.ToArray());
        }

        protected override void Finish()
        {
            CheckReturnedAndStored(second, c, p);
            v.Check(port.counters.passes == passes0, "the Network never called PassToWorld for this pawn");
        }
    }

    /// <summary>
    /// RT-PHYX-007 — registry (S9r): a stored pawn is reserved (vanilla: ReservedByQuest, Suspended), outside vanilla's redress pool,
    /// kept by the world-pawn GC over several accumulation passes, and NOT redressed by N real forced generations whose request the stored
    /// pawn satisfies in every other respect (proved through vanilla's own private predicate, read by reflection). No real GC pass is
    /// forced (it would discard unrelated world pawns of this save); no unrelated world pawn is ever redressed (the pressure runs only
    /// when vanilla's candidate pool for the request is empty).
    /// </summary>
    public sealed class Phyx007Registry : PhysicalRun
    {
        public const int Pressure = 10;

        public Phyx007Registry(NetworkRuntime rt, string runId) : base(PhysicalScenarioTable.Get("RT-PHYX-007"), runId, rt)
        {
        }

        protected override void Script()
        {
            Then("pick a STORED Solo", () => PickSolo(SoloNeed.Stored));
            Then("read-only proofs: situation, pools, GC and lookup cost", () =>
            {
                p = c.pawn.pawn;
                WorldPawns wp = Find.WorldPawns;
                v.Check(wp.Contains(p) && wp.GetSituation(p) == WorldPawnSituation.ReservedByQuest, "vanilla sees the stored pawn as ReservedByQuest");
                v.Check(p.Suspended, "it is Suspended (not ticked, not aged while stored)");
                v.Check(!wp.GetPawnsBySituation(WorldPawnSituation.Free).Contains(p), "it is not in the Free pool (vanilla's redress source)");
                v.Check(!wp.GetPawnsBySituation(WorldPawnSituation.FactionLeader).Contains(p), "it is not in the FactionLeader pool (the other redress source)");
                Quest q = port.Registry.FindQuest();
                v.Check(q != null && q.QuestReserves(p), "the registry quest reserves it");
                v.Check(!Integration.Physical.PawnObserver.OtherQuestReserves(p, q), "no other quest reserves it (the reservation is the Network's own)");
                for (int pass = 1; pass <= 3; pass++)
                {
                    Dictionary<Pawn, string> kept = wp.gc.AccumulatePawnGCDataImmediate();
                    string reason;
                    v.Check(kept.TryGetValue(p, out reason), "GC accumulation pass " + pass + ": kept (" + (reason ?? "DISCARDED") + ")");
                }
                v.Note("a real WorldPawnGC pass is NOT forced: it would discard unrelated world pawns of this save; the verdict above is the accumulation the pass itself uses");
                List<Pawn> alive = new List<Pawn>(wp.AllPawnsAlive);
                int reserved;
                double us = port.Registry.MeasureLookup(alive, 20, out reserved);
                v.Note("registry lookup: " + us.ToString("0.000") + " µs per query over " + alive.Count + " living world pawns × 20 rounds; " + reserved + " reserved; " + port.Registry.Describe());
                return StepResult.Next;
            });
            Then("bounded redress pressure through vanilla's own generator", () =>
            {
                RedressPressure();
                return StepResult.Next;
            });
        }

        private PawnGenerationRequest Request(PawnKindDef kind, List<TraitDef> traits)
        {
            return new PawnGenerationRequest(kind, p.Faction, PawnGenerationContext.NonPlayer, null, forceGenerateNewPawn: false, allowDead: false, allowDowned: true,
                canGeneratePawnRelations: false, colonistRelationChanceFactor: 0f, allowGay: true, allowPregnant: true, allowAddictions: true, forcedTraits: traits,
                minChanceToRedressWorldPawn: 1f, developmentalStages: DevelopmentalStage.Adult);
        }

        private void RedressPressure()
        {
            if (!RedressProbe.Available)
            {
                v.Gap("vanilla's redress predicate cannot be read in this game version: the dynamic pressure is not run (the read-only proofs stand)");
                return;
            }
            List<TraitDef> traits = new List<TraitDef>();
            if (p.story?.traits?.allTraits != null) foreach (Trait t in p.story.traits.allTraits) if (t?.def != null && !traits.Contains(t.def)) traits.Add(t.def);
            List<PawnKindDef> kinds = new List<PawnKindDef> { p.kindDef };
            if (PawnKindDefOf.Villager != null && PawnKindDefOf.Villager.race == p.def && p.kindDef != PawnKindDefOf.Villager) kinds.Add(PawnKindDefOf.Villager);
            PawnGenerationRequest req = default(PawnGenerationRequest);
            bool found = false;
            for (int i = 0; i < kinds.Count && !found; i++)
            {
                if (kinds[i] == null) continue;
                req = Request(kinds[i], traits);
                if (RedressProbe.WouldBeCandidate(p, req) == true) found = true;
            }
            if (!found)
            {
                v.Gap("no request makes the stored pawn a valid redress candidate by vanilla's rule (kinds " + string.Join(", ", kinds.ConvertAll(k => k?.defName).ToArray()) + "): the pressure would be vacuous; not run");
                return;
            }
            v.Check(true, "non-vacuous: the stored pawn satisfies every condition of the request (kind " + req.KindDef.defName + ", faction " + (p.Faction?.Name ?? "none") + ", traits forced " + traits.Count + ") except the reservation");
            List<Pawn> pool = RedressProbe.Pool(req);
            if (pool.Count > 0)
            {
                v.Gap(pool.Count + " unrelated Free world pawn(s) of this save also match the request; running the pressure would redress them, so it is NOT run (first: #" + pool[0].thingIDNumber + ")");
                return;
            }
            v.Note("vanilla's candidate pool for the request is empty: every request must generate a NEW pawn unless the reservation fails");
            int fresh = 0;
            for (int i = 0; i < Pressure; i++)
            {
                int fence0 = Find.UniqueIDsManager.GetNextThingID();
                Pawn r = PawnGenerator.GeneratePawn(req);
                int fence1 = Find.UniqueIDsManager.GetNextThingID();
                if (r == null)
                {
                    v.Note("request " + (i + 1) + " returned no pawn");
                    continue;
                }
                if (ReferenceEquals(r, p))
                {
                    v.Fail("CRITICAL: request " + (i + 1) + " REDRESSED the stored Network pawn #" + p.thingIDNumber + " (preserved, nothing undone)");
                    return;
                }
                if (r.thingIDNumber <= fence0 || r.thingIDNumber >= fence1)
                {
                    v.Fail("request " + (i + 1) + " returned a pre-existing pawn #" + r.thingIDNumber + " that the empty pool did not predict (preserved untouched; the pressure stops)");
                    return;
                }
                fresh++;
                TestFixtures.Tag(r, runId);
                string refusal;
                if (!TestFixtures.TryDispose(r, runId, ctx, out refusal)) v.Fail("the disposal of new pressure pawn #" + r.thingIDNumber + " was refused (" + refusal + "); preserved");
                if (!Find.WorldPawns.Contains(p) || Find.WorldPawns.GetSituation(p) != WorldPawnSituation.ReservedByQuest)
                {
                    v.Fail("after request " + (i + 1) + " the stored pawn is no longer ReservedByQuest");
                    return;
                }
            }
            v.Check(fresh == Pressure, Pressure + " forced generations: " + fresh + " new pawns (each tagged with the run and disposed), the stored pawn was never redressed");
        }
    }

    /// <summary>RT-PHYX-008 — the temporary faction: created hidden and neutral with the seeded goodwill, removed after the episode, the pawn's faction nulled harmlessly.</summary>
    public sealed class Phyx008TemporaryFaction : PhysicalRun
    {
        private Faction f;

        public Phyx008TemporaryFaction(NetworkRuntime rt, string runId) : base(PhysicalScenarioTable.Get("RT-PHYX-008"), runId, rt)
        {
        }

        protected override void Script()
        {
            Then("pick a Solo", () => PickSolo(SoloNeed.Any));
            Then("materialize through the production lifecycle", MaterializeOnTestMap);
            Then("check the encounter faction", () =>
            {
                f = e.faction?.Resolve();
                if (f == null)
                {
                    v.Fail("the episode's faction does not resolve");
                    return StepResult.Abort;
                }
                int seeded = lc.EncounterGoodwill(a.id);
                v.Check(f.temporary && f.Hidden && !f.IsPlayer, "temporary, hidden, not the player's (def " + f.def.defName + ")");
                v.Check(f.Name == a.name?.Display, "named after the Network actor \"" + a.name?.Display + "\" (it is not the actor and never provenance)");
                v.Check(f.RelationKindWith(Faction.OfPlayer) == FactionRelationKind.Neutral, "neutral to the player (a 3.1 visit is never hostile)");
                v.Check(f.GoodwillWith(Faction.OfPlayer) == seeded, "goodwill seeded once from the Network relation: " + f.GoodwillWith(Faction.OfPlayer) + " (expected " + seeded + ")");
                v.Check(p.Faction == f, "the pawn is in it");
                v.Note("vanilla faction leader: " + (f.leader == null ? "none" : "#" + f.leader.thingIDNumber + " (" + (Find.WorldPawns.Contains(f.leader) ? Find.WorldPawns.GetSituation(f.leader).ToString() : "not a world pawn") + ")"));
                return StepResult.Next;
            });
            Then("let the visit end and the episode complete", () => WaitComplete(e), 60000);
            Then("vanilla removes the temporary faction", () => Find.FactionManager.AllFactionsListForReading.Contains(f) ? StepResult.Wait : StepResult.Next, 2500);
        }

        protected override void Finish()
        {
            CheckReturnedAndStored(e, c, p);
            v.Check(!Find.FactionManager.AllFactionsListForReading.Contains(f), "the temporary faction was removed after the episode");
            v.Check(p.Faction == null, "the stored pawn's faction was nulled by vanilla's removal (harmless: it stays reserved and bound)");
        }
    }

    /// <summary>RT-PHYX-009 — unsupported custody (a dev arrest) ⇒ Quarantined, pawn untouched, person blocked; ended by vanilla's map removal.</summary>
    public sealed class Phyx009UnsupportedCustody : PhysicalRun
    {
        private int arrestTick, commits0, passes0;
        private IntVec3 cell;

        public Phyx009UnsupportedCustody(NetworkRuntime rt, string runId) : base(PhysicalScenarioTable.Get("RT-PHYX-009"), runId, rt)
        {
        }

        protected override void Script()
        {
            Then("pick a Solo", () => PickSolo(SoloNeed.Any));
            Then("materialize through the production lifecycle", MaterializeOnTestMap);
            Then("let the visitor walk in", WaitOnMap(300));
            Then("dev arrest: the player's faction captures the visitor", () =>
            {
                if (!p.Spawned)
                {
                    v.Gap("the pawn left before the arrest");
                    return StepResult.Abort;
                }
                commits0 = lc.counters.commits;
                passes0 = port.counters.passes;
                p.guest.CapturedBy(Faction.OfPlayer);
                arrestTick = PhysLog.Tick;
                cell = p.Position;
                v.Check(p.IsPrisonerOfColony, "the dev arrest made the pawn a prisoner of the colony");
                return StepResult.Next;
            });
            Then("the watch quarantines the episode and touches nothing", () =>
            {
                if (PhysLog.Tick < arrestTick + 2 * PhysicalLifecycleService.WatchPeriod + 10) return StepResult.Wait;
                v.Check(e.state == EpisodeState.Quarantined && e.quarantineKey != null && e.quarantineKey.StartsWith("UnsupportedCustody:HeldByPlayer", StringComparison.Ordinal),
                    "Quarantined(UnsupportedCustody) — " + e.state + " " + e.quarantineKey);
                v.Check(e.members[0].state == MemberState.Present && e.members[0].outcome == MemberOutcome.Pending && lc.counters.commits == commits0, "nothing was committed: no capture is faked");
                v.Check(c.custody == CustodyState.Deployed && !AuthorityGate.CanSimulateAbstractly(c), "the person stays non-abstract (custody Deployed, authority closed)");
                v.Check(p.IsPrisonerOfColony && p.Spawned && TestSite.IsTestMap(p.Map), "the pawn is untouched by the Network: still the colony's prisoner, on the test map");
                if (p.Position != cell) v.Note("the prisoner moved on its own (vanilla AI) from " + cell + " to " + p.Position);
                v.Check(port.counters.passes == passes0, "no Network PassToWorld");
                return StepResult.Next;
            });
            Then("end it through vanilla's map removal (vanilla clears the guest status and passes the pawn)", () =>
            {
                v.Note(TestSite.RemoveMap(false));
                return StepResult.Next;
            });
            Then("the quarantined episode is re-observed and completes", () => WaitComplete(e), 20000);
        }

        protected override void Finish()
        {
            CheckReturnedAndStored(e, c, p);
        }
    }

    /// <summary>
    /// RT-PHYX-010 — owner-assisted save points. Each one sets up a meaningful state, PAUSES the game and asks the owner to save and load;
    /// "Verify after load" then checks the loaded state read-only. There is no save/reload automation.
    /// </summary>
    public sealed class Phyx010SavePoint : PhysicalRun
    {
        private readonly bool removeMap;

        public Phyx010SavePoint(NetworkRuntime rt, string runId, bool removeMap) : base(PhysicalScenarioTable.Get("RT-PHYX-010"), runId, rt)
        {
            this.removeMap = removeMap;
        }

        protected override void Script()
        {
            Then("pick a Solo", () => PickSolo(SoloNeed.Any));
            Then("materialize through the production lifecycle", MaterializeOnTestMap);
            Then("let the visitor walk in", WaitOnMap(120));
            Then(removeMap ? "pause, then remove the test map (vanilla passes the pawn; the watch has not run)" : "pause with the visitor present", () =>
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                if (removeMap)
                {
                    v.Note(TestSite.RemoveMap(false));
                    PawnSnap s = PawnSnap.Of(p, "after the removal, before saving");
                    v.Note("snapshot " + s.Line());
                    v.Check(s.worldPawn && s.situation == WorldPawnSituation.ReservedByQuest, "the pawn is a reserved world pawn, the episode still " + e.state);
                }
                else Snap(p, "before saving");
                v.Check(true, "save point " + (removeMap ? "B (map removed, not yet reconciled)" : "A (visitor present)") + " is set: episode " + e.id + ", person " + c.id + ", pawn #" + p.thingIDNumber);
                v.Note("NOW: save the game (the game is paused), load that save, then run \"RT-PHYX-010 — Verify after load (read-only)\" BEFORE unpausing, then unpause and run it again once the episode completed");
                return StepResult.Next;
            });
        }
    }

    /// <summary>RT-PHYX-011 — role-constrained creation on real pawns: every role, several seeds, two competence bands; a failure is a contained abort.</summary>
    public sealed class Phyx011RoleGeneration : PhysicalRun
    {
        public const int Seeds = 4;
        private static readonly ExperienceBand[] Bands = { ExperienceBand.Experienced, ExperienceBand.Elite };
        private Faction fixture;
        private readonly List<OperationalRole> roles = new List<OperationalRole>();
        private int cursor, total, made, aborted, contradictions, corrections, attempts, rejected;
        private double msMax, msTotal;
        private readonly HashSet<string> kinds = new HashSet<string>();
        private readonly HashSet<string> races = new HashSet<string>();
        private readonly Dictionary<string, int> failuresByRole = new Dictionary<string, int>();
        private int created0, bound0;

        public Phyx011RoleGeneration(NetworkRuntime rt, string runId) : base(PhysicalScenarioTable.Get("RT-PHYX-011"), runId, rt)
        {
        }

        protected override void Script()
        {
            foreach (OperationalRole r in Enum.GetValues(typeof(OperationalRole))) if (r != OperationalRole.Unset) roles.Add(r);
            total = roles.Count * Seeds * Bands.Length;
            Then("create a fixture faction", () =>
            {
                created0 = lc.counters.created;
                bound0 = Bound();
                fixture = TestFixtures.MakeFaction(runId);
                if (fixture == null)
                {
                    v.Fail("no faction def qualifies for a fixture");
                    return StepResult.Abort;
                }
                runFactions.Add(fixture.loadID);
                return StepResult.Next;
            });
            Then("project every role × " + Seeds + " seeds × " + Bands.Length + " bands (one per frame)", () =>
            {
                if (cursor >= total) return StepResult.Next;
                int i = cursor++;
                OperationalRole role = roles[i % roles.Count];
                ExperienceBand band = Bands[(i / roles.Count) % Bands.Length];
                ProjectionRequest req = new ProjectionRequest { role = role, capability = band, equipmentTier = 1 + i % 5, seed = NetHash.Combine(NetHash.Combine(0x5EED, runId), i), faction = FactionRef.Of(fixture) };
                ProjectionResult r = PawnProjection.Project(req, fixture);
                attempts += r.attempts;
                corrections += r.corrections;
                rejected += r.rejected;
                msTotal += r.ms;
                if (r.ms > msMax) msMax = r.ms;
                if (r.pawn == null)
                {
                    aborted++;
                    int n;
                    failuresByRole.TryGetValue(role.ToString(), out n);
                    failuresByRole[role.ToString()] = n + 1;
                    return StepResult.Wait;
                }
                made++;
                kinds.Add(r.kind);
                races.Add(r.pawn.def.defName);
                if (!RoleRules.Verify(r.spec, PawnRoleReader.Snapshot(r.pawn)).holds)
                {
                    contradictions++;
                    v.Fail("CONTRADICTION: a " + role + " (" + band + ") candidate that does not satisfy its role was returned: " + r);
                }
                TestFixtures.Tag(r.pawn, runId);
                string refusal;
                if (!TestFixtures.TryDispose(r.pawn, runId, ctx, out refusal)) v.Fail("disposal refused for #" + r.pawn.thingIDNumber + ": " + refusal);
                return StepResult.Wait;
            });
            Then("release the fixture faction", () =>
            {
                TestFixtures.ReleaseFaction(fixture);
                return StepResult.Next;
            });
        }

        private int Bound()
        {
            int n = 0;
            foreach (KnownCharacter k in ctx.characters.characters) if (k.pawn != null && k.pawn.IsBound) n++;
            return n;
        }

        protected override void Finish()
        {
            v.Check(contradictions == 0, made + " real pawns satisfied their role; 0 contradicting pawns returned");
            v.Note(total + " projections over " + roles.Count + " roles: " + made + " made, " + aborted + " contained aborts" + (failuresByRole.Count > 0 ? " (" + string.Join(", ", Describe(failuresByRole)) + ")" : "")
                + "; " + attempts + " attempts, " + rejected + " rejected candidates, " + corrections + " skill corrections; " + (total > 0 ? (msTotal / total).ToString("0.0") : "0") + " ms avg, " + msMax.ToString("0.0") + " ms max");
            v.Note("kinds used: " + string.Join(", ", new List<string>(kinds).ToArray()) + "; races: " + string.Join(", ", new List<string>(races).ToArray()));
            v.Check(made > 0, "at least one real pawn was made");
            v.Check(lc.counters.created == created0 && Bound() == bound0, "no Network person was bound or created by the probe");
        }

        private static string[] Describe(Dictionary<string, int> d)
        {
            List<string> r = new List<string>();
            foreach (KeyValuePair<string, int> kv in d) r.Add(kv.Key + " " + kv.Value);
            return r.ToArray();
        }
    }

    /// <summary>
    /// RT-PHYX-012 — the truthful-aging MECHANISM on the suite's own disposable pawns: the production catch-up (vanilla's mothball path)
    /// over 1 day, 1, 10 and 70 years. The game clock is never skipped and no Network person is ever falsified: the real person over a
    /// real stored interval is RT-PHYX-006.
    /// </summary>
    public sealed class Phyx012TruthfulAging : PhysicalRun
    {
        private static readonly long[] Intervals = { 60000L, 3600000L, 36000000L, 252000000L };
        private Faction fixture;
        private int cursor;

        public Phyx012TruthfulAging(NetworkRuntime rt, string runId) : base(PhysicalScenarioTable.Get("RT-PHYX-012"), runId, rt)
        {
        }

        protected override void Script()
        {
            Then("create a fixture faction", () =>
            {
                fixture = TestFixtures.MakeFaction(runId);
                if (fixture == null)
                {
                    v.Fail("no faction def qualifies for a fixture");
                    return StepResult.Abort;
                }
                runFactions.Add(fixture.loadID);
                return StepResult.Next;
            });
            Then("age one fresh 25-year-old disposable pawn per interval (1 day, 1, 10, 70 years)", () =>
            {
                if (cursor >= Intervals.Length) return StepResult.Next;
                AgeOne(Intervals[cursor++]);
                return StepResult.Wait;
            });
            Then("release the fixture faction", () =>
            {
                TestFixtures.ReleaseFaction(fixture);
                return StepResult.Next;
            });
        }

        private void AgeOne(long interval)
        {
            PawnKindDef kind = fixture.def.basicMemberKind ?? PawnKindDefOf.Villager;
            Pawn d = TestFixtures.Disposable(kind, fixture, 25f, runId);
            string label = (interval / 3600000.0).ToString("0.###") + " years";
            long birth = d.ageTracker.BirthAbsTicks, bio = d.ageTracker.AgeBiologicalTicks, chrono = d.ageTracker.AgeChronologicalTicks;
            int years = d.ageTracker.AgeBiologicalYears;
            float rate = d.ageTracker.BiologicalTicksPerTick;
            int hediffs = d.health.hediffSet.hediffs.Count;
            long applied;
            try
            {
                applied = PawnAging.CatchUp(d, interval);
            }
            catch (Exception ex)
            {
                v.Fail(label + ": the catch-up threw: " + ex.Message);
                Dispose(d);
                return;
            }
            long delta = d.ageTracker.AgeBiologicalTicks - bio;
            long expected = (long)Math.Round(interval * (double)rate);
            v.Check(applied == interval, label + ": the FULL interval was applied, uncapped (" + applied + " ticks)");
            v.Check(d.ageTracker.BirthAbsTicks == birth, label + ": BirthAbsTicks was never written");
            v.Check(d.ageTracker.AgeChronologicalTicks == chrono, label + ": chronological age is derived (unchanged while the clock did not move)");
            v.Check(Math.Abs(delta - expected) <= Math.Max(2L, expected / 1000), label + ": biological age advanced by " + delta + " ticks (rate " + rate.ToString("0.###") + ", expected " + expected + ")");
            List<string> added = new List<string>();
            for (int i = hediffs; i < d.health.hediffSet.hediffs.Count; i++) added.Add(d.health.hediffSet.hediffs[i].def.defName);
            v.Note(label + ": birthdays crossed " + (d.ageTracker.AgeBiologicalYears - years) + " (" + years + " → " + d.ageTracker.AgeBiologicalYears + "), vanilla birthday effects: " + (added.Count == 0 ? "none" : string.Join(", ", added.ToArray()))
                + (d.Dead ? "; the pawn DIED of age effects" : ""));
            Dispose(d);
        }

        private void Dispose(Pawn d)
        {
            string refusal;
            if (!TestFixtures.TryDispose(d, runId, ctx, out refusal)) v.Fail("disposal refused for #" + d.thingIDNumber + ": " + refusal);
        }

        protected override void Finish()
        {
            v.Note("the real person's truthful age over a real stored interval is proven by RT-PHYX-006; the game clock is never skipped");
        }
    }

    /// <summary>
    /// RT-PHYX-015 — the production normal-exit M1 regression (P3-INV-006, 029, 031, 032): at vanilla's synchronous LeftMap (already after
    /// vanilla's own PassToWorld) the bound pawn is ReservedByQuest, never Free, faction not rewritten, custody still Deployed; the
    /// Network never calls PassToWorld; Stored only through the commit; RELEASE once; authority closed until COMPLETE; then the SAME Pawn
    /// rematerializes and leaves again.
    /// </summary>
    public sealed class Phyx015NormalExitM1 : PhysicalRun
    {
        private int commits0, passes0, releases0, skipped0;
        private int spawnedUncovered, freeSeen, rewritten, authorityLeak, storedEarly;
        private int placedFaction = -1;
        private PhysicalEpisode first, second;

        public Phyx015NormalExitM1(NetworkRuntime rt, string runId) : base(PhysicalScenarioTable.Get("RT-PHYX-015"), runId, rt)
        {
        }

        protected override void Script()
        {
            Then("pick a Solo", () => PickSolo(SoloNeed.Any));
            Then("materialize through the production lifecycle", () =>
            {
                commits0 = lc.counters.commits;
                passes0 = port.counters.passes;
                releases0 = lc.counters.releasesCompleted;
                skipped0 = lc.counters.passSkippedAlreadyWorld;
                StepResult r = MaterializeOnTestMap();
                first = e;
                placedFaction = p?.Faction?.loadID ?? -1;
                everyFrame = Invariants;
                return r;
            });
            Then("let vanilla's visit and exit run; the episode completes", () => WaitComplete(first), 60000);
            Then("the LeftMap instant", () =>
            {
                PhysicalSignalObserver.Seen s = observer.First("LeftMap", p.thingIDNumber, startTick);
                if (s == null)
                {
                    v.Gap("no synchronous LeftMap was observed: the normal-exit instant is INCONCLUSIVE (never a PASS)");
                    return StepResult.Next;
                }
                v.Note("at LeftMap: " + s.snap.Line());
                v.Check(s.snap.worldPawn && s.snap.situation == WorldPawnSituation.ReservedByQuest, "at vanilla's LeftMap the pawn is already a world pawn, ReservedByQuest, NOT Free (" + s.snap.situation + ")");
                v.Check(s.snap.reserved, "at LeftMap the registry reserves it");
                v.Check(s.snap.factionId == placedFaction, "at LeftMap its faction is not rewritten");
                v.Check(s.characterCustodyAtSignal == (int)CustodyState.Deployed, "at LeftMap custody is still Deployed (the lifecycle decides later, from observation)");
                return StepResult.Next;
            });
            Then("the first exit reconciled once", () =>
            {
                CheckReturnedAndStored(first, c, p);
                v.Check(lc.counters.commits == commits0 + 1 && lc.counters.releasesCompleted == releases0 + 1, "one commit, one RELEASE");
                v.Check(port.counters.passes == passes0 && lc.counters.passSkippedAlreadyWorld > skipped0, "no Network PassToWorld; RELEASE skipped the already-passed pawn");
                bool again = lc.Reconcile(first, "dev duplicate (RT-PHYX-015)");
                v.Check(!again && lc.counters.commits == commits0 + 1, "a duplicate wake-up commits nothing");
                return StepResult.Next;
            });
            Then("rematerialize the SAME pawn", () =>
            {
                Pawn same = p;
                StepResult r = MaterializeOnTestMap();
                second = e;
                if (r != StepResult.Next) return r;
                v.Check(ReferenceEquals(p, same) && c.pawn.SameBinding(first.members[0].pawn), "the SAME Pawn object and binding");
                v.Check(!Find.WorldPawns.Contains(p), "vanilla's spawn took it out of WorldPawns (no second insertion)");
                placedFaction = p.Faction?.loadID ?? -1;
                return StepResult.Next;
            });
            Then("let the second visit end; the episode completes", () => WaitComplete(second), 60000);
        }

        private void Invariants()
        {
            if (e == null || c == null || p == null) return;
            if (p.Spawned && !port.Registry.Reserves(p)) spawnedUncovered++;
            if (Find.WorldPawns.Contains(p))
            {
                if (Find.WorldPawns.GetSituation(p) == WorldPawnSituation.Free) freeSeen++;
                if (p.Faction != null && p.Faction.loadID != placedFaction) rewritten++;
            }
            if (!e.releaseApplied && AuthorityGate.CanSimulateAbstractly(c)) authorityLeak++;
            if (c.custody == CustodyState.Stored && !e.consequencesApplied) storedEarly++;
        }

        protected override void Finish()
        {
            CheckReturnedAndStored(second, c, p);
            v.Check(port.counters.passes == passes0, "the Network called PassToWorld 0 times over both exits");
            v.Check(lc.counters.commits == commits0 + 2 && lc.counters.releasesCompleted == releases0 + 2, "exactly one commit and one RELEASE per exit");
            v.Check(spawnedUncovered == 0, "M1: the registry covered the pawn on every frame it was spawned (" + spawnedUncovered + " uncovered)");
            v.Check(freeSeen == 0, "never Free on any frame (" + freeSeen + ")");
            v.Check(rewritten == 0, "its faction was never rewritten to another faction (" + rewritten + ")");
            v.Check(authorityLeak == 0, "abstract authority stayed closed until RELEASE completed (" + authorityLeak + " frames open early)");
            v.Check(storedEarly == 0, "custody became Stored only through the commit");
        }
    }

    /// <summary>
    /// RT-PHYX-016 — the map-removal M1 regression: several retained named pawns still on the test map when vanilla removes it (NO
    /// LeftMap, materially different timing), in a populated world-pawn pool. The save/load between vanilla's pass and RELEASE is the
    /// owner-assisted RT-PHYX-010 save point B.
    /// </summary>
    public sealed class Phyx016MapRemovalM1 : PhysicalRun
    {
        public const int MaxPeople = 3;
        private readonly List<KnownCharacter> people = new List<KnownCharacter>();
        private readonly List<Pawn> pawns = new List<Pawn>();
        private readonly List<int> factions = new List<int>();
        private int commits0, passes0, releases0;
        private int spawnedUncovered, freeSeen;

        public Phyx016MapRemovalM1(NetworkRuntime rt, string runId) : base(PhysicalScenarioTable.Get("RT-PHYX-016"), runId, rt)
        {
        }

        protected override void Script()
        {
            Then("materialize up to " + MaxPeople + " Solos, one episode each", () =>
            {
                commits0 = lc.counters.commits;
                passes0 = port.counters.passes;
                releases0 = lc.counters.releasesCompleted;
                HashSet<int> used = new HashSet<int>();
                for (int i = 0; i < MaxPeople; i++)
                {
                    string why;
                    NetworkActor actor;
                    KnownCharacter k = SoloPicker.Pick(ctx, SoloNeed.Any, used, out actor, out why);
                    if (k == null) break;
                    used.Add(k.id.Value);
                    c = k;
                    a = actor;
                    testPeople.Add(k.id.Value);
                    StepResult r = MaterializeOnTestMap();
                    if (r != StepResult.Next) return r;
                    people.Add(c);
                    pawns.Add(p);
                    factions.Add(p.Faction?.loadID ?? -1);
                }
                if (people.Count == 0)
                {
                    v.Gap("no Solo could be used");
                    return StepResult.Abort;
                }
                if (people.Count < 2) v.Note("only " + people.Count + " Solo was available: the several-pawns part is reduced to one");
                everyFrame = Invariants;
                return StepResult.Next;
            });
            Then("let the visitors walk in", WaitOnMap(300));
            Then("remove the test map with every pawn on it (vanilla removal)", () =>
            {
                v.Note("populated world-pawn pool: " + Find.WorldPawns.AllPawnsAlive.Count + " living world pawns, " + Find.WorldPawns.GetPawnsBySituationCount(WorldPawnSituation.Free) + " Free");
                List<int> leftMap0 = new List<int>();
                for (int i = 0; i < pawns.Count; i++) leftMap0.Add(observer.Count("LeftMap", pawns[i].thingIDNumber, startTick));
                v.Note(TestSite.RemoveMap(false));
                for (int i = 0; i < pawns.Count; i++)
                {
                    PawnSnap s = PawnSnap.Of(pawns[i], "immediately after the map removal");
                    v.Note("snapshot " + s.Line());
                    v.Check(s.worldPawn && s.situation == WorldPawnSituation.ReservedByQuest && s.reserved, people[i].id + ": a reserved world pawn the instant vanilla passed it, never Free (" + s.situation + ")");
                    v.Check(s.factionId == factions[i], people[i].id + ": faction not rewritten by the pass");
                    v.Check(people[i].custody == CustodyState.Deployed && episodes[i].state == EpisodeState.Open, people[i].id + ": custody Deployed and the episode Open (nothing decided by the removal itself)");
                    int left = observer.Count("LeftMap", pawns[i].thingIDNumber, startTick) - leftMap0[i];
                    if (left == 0) v.Check(true, people[i].id + ": no LeftMap on map removal (as audited)");
                    else v.Note("AUDIT DEVIATION: " + people[i].id + " received " + left + " LeftMap signal(s) on map removal");
                }
                return StepResult.Next;
            });
            Then("every episode completes", () =>
            {
                for (int i = 0; i < episodes.Count; i++) if (!episodes[i].IsComplete) return StepResult.Wait;
                return StepResult.Next;
            }, 20000);
        }

        private void Invariants()
        {
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn x = pawns[i];
                if (x.Spawned && !port.Registry.Reserves(x)) spawnedUncovered++;
                if (Find.WorldPawns.Contains(x) && Find.WorldPawns.GetSituation(x) == WorldPawnSituation.Free) freeSeen++;
            }
        }

        protected override void Finish()
        {
            for (int i = 0; i < people.Count; i++) CheckReturnedAndStored(episodes[i], people[i], pawns[i]);
            v.Check(port.counters.passes == passes0, "the Network called PassToWorld 0 times");
            v.Check(lc.counters.commits == commits0 + people.Count && lc.counters.releasesCompleted == releases0 + people.Count, "exactly one commit and one RELEASE per person (" + people.Count + ")");
            v.Check(spawnedUncovered == 0 && freeSeen == 0, "M1 held on every frame: covered while spawned, never Free (" + spawnedUncovered + ", " + freeSeen + ")");
        }
    }

    /// <summary>
    /// RT-PHYX-010 — "Verify after load" (READ-ONLY, no arm): nothing generated, spawned, destroyed, rerolled or cloned by the load; the
    /// bindings resolve to exactly one Pawn each; the registry was rebuilt before the first tick; tags agree with the bindings; every
    /// incomplete episode is watched; no terminal outcome invented. With incomplete episodes it then follows them, read-only, to the end.
    /// </summary>
    public sealed class Phyx010VerifyAfterLoad : PhysicalRun
    {
        private bool follow;

        public Phyx010VerifyAfterLoad(NetworkRuntime rt, string runId) : base(PhysicalScenarioTable.Get("RT-PHYX-010"), runId, rt)
        {
        }

        protected override void Script()
        {
            Then("verify the loaded state (read-only)", () =>
            {
                Verify();
                return StepResult.Next;
            });
            Then("follow incomplete episodes to completion (read-only; unpause the game)", () =>
            {
                if (!follow) return StepResult.Next;
                List<PhysicalEpisode> open = ctx.episodes.Incomplete();
                for (int i = 0; i < open.Count; i++) if (PhysicalTestIds.IsTestDevKey(open[i].cause?.devKey) && open[i].state != EpisodeState.Quarantined) return StepResult.Wait;
                return StepResult.Next;
            }, 60000);
        }

        private void Verify()
        {
            v.Check(!PhysicalTestSession.Arm.IsArmedFor(Current.Game), "the session arm is not set after the load");
            v.Check(lc.counters.created == 0 && port.counters.projections == 0 && port.counters.placements == 0, "since this runtime was built nothing was generated or placed (created " + lc.counters.created + ", placements " + port.counters.placements + ")");
            Dictionary<int, int> byId = new Dictionary<int, int>();
            foreach (Pawn x in PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead)
            {
                int n;
                byId.TryGetValue(x.thingIDNumber, out n);
                byId[x.thingIDNumber] = n + 1;
            }
            int bound = 0, unresolved = 0, clones = 0, unreserved = 0, tagErrors = 0;
            foreach (KnownCharacter k in ctx.characters.characters)
            {
                if (k.pawn == null || !k.pawn.IsBound) continue;
                bound++;
                Pawn x = k.pawn.pawn;
                if (x == null || x.thingIDNumber != k.pawn.thingIdNumber)
                {
                    unresolved++;
                    continue;
                }
                int n;
                byId.TryGetValue(x.thingIDNumber, out n);
                if (n > 1) clones++;
                if (RetainedPawnRegistry.RetainedCustody(k) && !port.Registry.Reserves(x)) unreserved++;
                PhysicalEpisode ep = k.episode.IsValid ? ctx.episodes.Get(k.episode) : null;
                bool wantEp = ep != null && ep.IsActive;
                if (wantEp != PhysicalTags.Has(x, PhysicalTags.Episode(k.episode)) || (k.IsAlive && !PhysicalTags.Has(x, PhysicalTags.Character(k.id)))) tagErrors++;
                testPeople.Add(k.id.Value);
            }
            v.Check(unresolved == 0, bound + " binding(s): every one resolves to its persisted thing id (" + unresolved + " do not)");
            v.Check(clones == 0, "no bound pawn has a twin with the same thing id (" + clones + ")");
            v.Check(unreserved == 0, "the registry (rebuilt before the first tick) reserves every Deployed or Stored living person's pawn (" + unreserved + " not)");
            v.Check(tagErrors == 0, "routing tags agree with the bindings and episodes (" + tagErrors + " disagree)");
            Quest q = port.Registry.FindQuest();
            if (port.Registry.RetainedCount() > 0) v.Check(q != null && q.State == QuestState.Ongoing, "the registry quest exists and is Ongoing");
            List<PhysicalEpisode> open = ctx.episodes.Incomplete();
            int unwatched = 0;
            for (int i = 0; i < open.Count; i++)
            {
                PhysicalEpisode ep = open[i];
                if (rt.Scheduler.Find(PhysicalLifecycleService.WatchJob, ep.id.Value) == null) unwatched++;
                v.Note("incomplete " + ep.id + " " + ep.state + (ep.quarantineKey != null ? " (" + ep.quarantineKey + ")" : "") + ": " + MemberLine(ep));
                if (PhysicalTestIds.IsTestDevKey(ep.cause?.devKey))
                {
                    episodes.Add(ep);
                    follow = true;
                }
            }
            if (rt.Session.IsRunning) v.Check(unwatched == 0, open.Count + " incomplete episode(s), every one watched (" + unwatched + " not)");
            else v.Note("the Network has not started yet (paused right after the load): the watch is restored at the first tick; unpause and run this again");
        }

        private string MemberLine(PhysicalEpisode ep)
        {
            List<string> r = new List<string>();
            for (int i = 0; i < ep.members.Count; i++)
            {
                EpisodeMember m = ep.members[i];
                r.Add(m + " " + port.DescribeBinding(m.pawn));
            }
            return string.Join("; ", r.ToArray());
        }

        protected override void Finish()
        {
            for (int i = 0; i < episodes.Count; i++)
            {
                PhysicalEpisode ep = episodes[i];
                v.Note("after following: " + ep.id + " " + ep.state + ", complete " + ep.IsComplete + ", outcome " + ep.members[0].outcome);
                if (ep.state != EpisodeState.Quarantined) v.Check(ep.IsComplete && ep.members[0].outcome == MemberOutcome.Returned, ep.id + " completed after the load as Returned (no outcome invented, no reroll)");
            }
        }
    }
}
