using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using TheNetwork.Core;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Physical;
using TheNetwork.Integration.Physical;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Diagnostics.RuntimePhysicalTests
{
    // Phase 3.2A, S21 (PHYSICAL_LIFECYCLE § 9, § 25): held custody on real pawns. Every scenario drives the PRODUCTION lifecycle (Plan →
    // Materialize on the suite's own test map; then the episode watch and the custody watch) and observes. The deliberate test actions are the
    // vanilla custody changes a player or a raid would make, each named in the log: a dev arrest (CapturedBy), vanilla's prisoner release, a dev
    // recruitment (RecruitUtility.Recruit), the guest-status change vanilla's enslavement makes, a dev kidnapping by a real enemy faction
    // (KidnappedPawnsTracker.Kidnap) and the two steps of vanilla's own "kidnapped pawn joins the captor" event, dev damage, and the removal of
    // the suite's own test map. They never touch the player's colonists or home maps. 021–024 deliberately LEAVE the person held by vanilla
    // (recruited, enslaved, with another faction, or dead): in a disposable save that is the point. A caravan and a travelling transporter need a
    // player pawn to carry the person, and the tier never touches colonists: those two holders are proven headlessly (Custody.* tests).

    /// <summary>The shared steps and checks of the held-custody scenarios.</summary>
    public abstract class HeldRun : PhysicalRun
    {
        protected int commits0, custodyEpisodes0, passes0, holderChanges0;
        protected int authorityLeak, storedWhileHeld, freeSeen, brokenSeen;

        protected HeldRun(PhysicalScenarioInfo info, string runId, NetworkRuntime rt) : base(info, runId, rt)
        {
        }

        protected void Baseline()
        {
            commits0 = lc.counters.commits;
            custodyEpisodes0 = lc.counters.custodyEpisodes;
            holderChanges0 = lc.counters.custodyHolderChanges;
            passes0 = port.counters.passes;
            everyFrame = HeldInvariants;
        }

        /// <summary>Every frame from the first custody change: authority never opens while vanilla holds the person; never stored while held.</summary>
        protected void HeldInvariants()
        {
            if (c == null || p == null) return;
            if (c.custody == CustodyState.OutOfCustody && AuthorityGate.CanSimulateAbstractly(c)) authorityLeak++;
            if (c.custody == CustodyState.Stored && !p.Destroyed && (p.IsPrisonerOfColony || p.IsSlaveOfColony || (p.Faction != null && p.Faction.IsPlayer) || PawnUtility.IsKidnappedPawn(p))) storedWhileHeld++;
            if (IsActualFree(p)) freeSeen++;
            if (IsBrokenReservation(e)) brokenSeen++;
        }

        protected StepResult Arrest()
        {
            if (p == null || !p.Spawned)
            {
                v.Gap("the pawn left before the arrest");
                return StepResult.Abort;
            }
            Baseline();
            p.guest.CapturedBy(Faction.OfPlayer);
            v.Check(p.IsPrisonerOfColony, "the dev arrest made the pawn a prisoner of the colony");
            return StepResult.Next;
        }

        /// <summary>The mission episode closed on a held outcome and its RELEASE completed: the person is VanillaHeld now.</summary>
        protected StepResult WaitHeld()
        {
            if (e.IsComplete && c.custody == CustodyState.OutOfCustody && !c.episode.IsValid) return StepResult.Next;
            if (e.state == EpisodeState.Quarantined)
            {
                v.Fail("the episode was quarantined (" + e.quarantineKey + "): a held custody must be a terminal outcome in Phase 3.2A");
                return StepResult.Abort;
            }
            if (e.IsComplete)
            {
                v.Fail("the episode completed with " + e.members[0].outcome + " and custody " + c.custody + ": not held");
                return StepResult.Abort;
            }
            return StepResult.Wait;
        }

        protected void CheckHeldOnce(MemberOutcome outcome, HeldKind heldBy, CharacterStatus status)
        {
            EpisodeMember m = e.members[0];
            v.Check(e.IsComplete && m.outcome == outcome, "the mission episode closed ONCE with " + outcome + " (got " + m.outcome + ", observed " + m.observed + "): it does not stay open while the person is held");
            v.Check(lc.counters.commits == commits0 + 1, "exactly one commit for the capture (" + (lc.counters.commits - commits0) + ")");
            v.Check(c.custody == CustodyState.OutOfCustody && c.heldBy == heldBy && c.heldSinceTick >= 0, "custody OutOfCustody(" + heldBy + ") since tick " + c.heldSinceTick + " (got " + c.custody + ", " + c.heldBy + ")");
            v.Check(c.status == status, "story status " + status + " (got " + c.status + ")");
            v.Check(!c.episode.IsValid && AuthorityGate.AuthorityOf(c) == PersonAuthority.VanillaHeld && !AuthorityGate.CanSimulateAbstractly(c), "VanillaHeld after RELEASE COMPLETE; never abstract while held");
            v.Check(c.pawn != null && ReferenceEquals(c.pawn.pawn, p), "the SAME pawn is still bound");
            v.Check(port.Registry.Reserves(p), "M1 (ADR-056): the registry reserves the held person's pawn from its binding on");
            v.Check(!PhysicalTags.Has(p, PhysicalTags.Episode(e.id)) && PhysicalTags.Has(p, PhysicalTags.Character(c.id)), "RELEASE stripped only the episode tag; the person tag stays (custody wake-ups)");
            v.Check(WatchExists(), "the custody watch exists while someone is held");
            v.Check(port.counters.passes == passes0, "no Network PassToWorld");
        }

        protected bool WatchExists()
        {
            return ctx.scheduler.Has(PhysicalLifecycleService.CustodyWatchJob, PhysicalLifecycleService.CustodyWatchTarget);
        }

        /// <summary>Duplicate wake-ups (the closed episode, and the custody watch twice) change nothing when nothing changed physically.</summary>
        protected StepResult DuplicateWakeups()
        {
            int commits = lc.counters.commits, eps = lc.counters.custodyEpisodes;
            bool again = lc.Reconcile(e, "dev duplicate (" + info.id + ")");
            lc.ReconcileHeld(c, "dev duplicate (" + info.id + ")");
            lc.ReconcileHeld(c, "dev duplicate (" + info.id + ")");
            v.Check(!again && lc.counters.commits == commits && lc.counters.custodyEpisodes == eps, "duplicate wake-ups changed nothing (commits +" + (lc.counters.commits - commits) + ", custody episodes +" + (lc.counters.custodyEpisodes - eps) + ")");
            return StepResult.Next;
        }

        /// <summary>The newest Custody episode of the run's person (bounded by the episode store).</summary>
        protected PhysicalEpisode LatestCustodyEpisode()
        {
            PhysicalEpisode latest = null;
            foreach (PhysicalEpisode x in ctx.episodes.episodes)
            {
                if (x == null || !CustodyRules.IsCustodyEpisode(x) || x.members.Count != 1 || x.members[0].character != c.id) continue;
                if (latest == null || x.id.Value > latest.id.Value) latest = x;
            }
            return latest;
        }

        protected void CheckInvariants()
        {
            v.Check(authorityLeak == 0, "abstract authority never opened while vanilla held the person (" + authorityLeak + " frames)");
            v.Check(storedWhileHeld == 0, "the person was never Stored while vanilla still held the pawn (" + storedWhileHeld + " frames)");
            CheckNoActualFree(info.id, freeSeen, brokenSeen);
            v.Check(WatchExists() == (lc.HeldCount > 0), "the custody watch exists exactly while someone is held (" + lc.HeldCount + " held)");
        }
    }

    /// <summary>
    /// RT-PHYX-020 — an arrest is a supported held custody: the mission episode closes ONCE (HeldByPlayer; Captured, OutOfCustody(PlayerPrisoner))
    /// while the prisoner stays exactly as vanilla has them; then vanilla's own release lets the same pawn leave, and only that positive evidence
    /// (a free world pawn the registry reserves) returns the person to Stored, through a Custody episode, exactly once.
    /// </summary>
    public sealed class Phyx020Arrest : HeldRun
    {
        private int releaseTick = -1;
        private bool removed;

        public Phyx020Arrest(NetworkRuntime rt, string runId) : base(PhysicalScenarioTable.Get("RT-PHYX-020"), runId, rt)
        {
        }

        protected override void Script()
        {
            Then("pick a Solo", () => PickSolo(SoloNeed.Any));
            Then("materialize through the production lifecycle", MaterializeOnTestMap);
            Then("let the visitor walk in", WaitOnMap(300));
            Then("dev arrest: the player's faction captures the visitor", Arrest);
            Then("wait for the episode to close once on the held outcome and complete its RELEASE", WaitHeld, 12 * PhysicalLifecycleService.WatchPeriod);
            Then("check: held once, the pawn untouched, the custody watch running", () =>
            {
                CheckHeldOnce(MemberOutcome.HeldByPlayer, HeldKind.PlayerPrisoner, CharacterStatus.Captured);
                v.Check(p.IsPrisonerOfColony && p.Spawned, "the pawn is still the colony's prisoner, untouched by the Network");
                return StepResult.Next;
            });
            Then("duplicate wake-ups change nothing", DuplicateWakeups);
            Then("vanilla's own prisoner release (the pawn walks to the edge and exits)", () =>
            {
                GenGuest.PrisonerRelease(p);
                releaseTick = PhysLog.Tick;
                v.Note("released at tick " + releaseTick + "; the custody watch (or the LeftMap wake-up) observes the exit");
                return StepResult.Next;
            });
            Then("wait for the custody watch to return the same pawn (the test map is removed if the released pawn has not left within 4,000 ticks)", () =>
            {
                if (c.custody == CustodyState.Stored && !c.episode.IsValid) return StepResult.Next;
                if (!removed && p.Spawned && PhysLog.Tick - releaseTick > 4000)
                {
                    removed = true;
                    v.Note("the released pawn had not left: vanilla's map removal frees it instead (" + TestSite.RemoveMap(false) + ")");
                }
                return StepResult.Wait;
            }, 20000);
        }

        protected override void Finish()
        {
            PhysicalEpisode ce = LatestCustodyEpisode();
            v.Check(ce != null && ce.IsComplete && ce.members[0].outcome == MemberOutcome.Returned && ce.members[0].observed == ObservedKind.WorldFree,
                "a Custody episode returned the person from WorldFree, exactly once (" + (ce == null ? "none" : ce + ", " + ce.members[0].outcome + " from " + ce.members[0].observed) + ")");
            v.Check(lc.counters.custodyEpisodes == custodyEpisodes0 + 1, "exactly one Custody episode (" + (lc.counters.custodyEpisodes - custodyEpisodes0) + ")");
            v.Check(c.custody == CustodyState.Stored && c.heldBy == HeldKind.None && c.heldSinceTick < 0, "Stored; the holder and heldSinceTick cleared at the durable transition");
            v.Check(c.status == CharacterStatus.Active || c.status == CharacterStatus.Wounded, "the capture is resolved by the positive return (" + c.status + ")");
            v.Check(AuthorityGate.CanSimulateAbstractly(c), "abstract authority resumed only after the Custody episode's RELEASE completed");
            v.Check(c.pawn != null && ReferenceEquals(c.pawn.pawn, p), "the SAME Pawn object, never regenerated");
            WorldPawnSituation s = Find.WorldPawns.Contains(p) ? Find.WorldPawns.GetSituation(p) : WorldPawnSituation.None;
            v.Check(s == WorldPawnSituation.ReservedByQuest && port.Registry.Reserves(p), "a world pawn, ReservedByQuest by the Network's registry (got " + s + ")");
            v.Check(port.counters.passes == passes0, "no Network PassToWorld over the whole scenario (vanilla's exit passed the pawn)");
            CheckInvariants();
        }
    }

    /// <summary>
    /// RT-PHYX-021 — recruitment: an arrested contractor recruited by the player is Defected and OutOfCustody(PlayerColonist), through a Custody
    /// episode, exactly once; never Stored, never sent home, never cloned, the colonist untouched. LEAVES the person a colonist on the test map.
    /// </summary>
    public sealed class Phyx021Recruitment : HeldRun
    {
        public Phyx021Recruitment(NetworkRuntime rt, string runId) : base(PhysicalScenarioTable.Get("RT-PHYX-021"), runId, rt)
        {
        }

        protected override void Script()
        {
            Then("pick a never-materialized Solo (a stored person is not spent on this)", () => PickSolo(SoloNeed.Fresh));
            Then("materialize through the production lifecycle", MaterializeOnTestMap);
            Then("let the visitor walk in", WaitOnMap(300));
            Then("dev arrest", Arrest);
            Then("wait for the held commit", WaitHeld, 12 * PhysicalLifecycleService.WatchPeriod);
            Then("check: held once", () =>
            {
                CheckHeldOnce(MemberOutcome.HeldByPlayer, HeldKind.PlayerPrisoner, CharacterStatus.Captured);
                return StepResult.Next;
            });
            Then("dev recruitment (vanilla's RecruitUtility.Recruit into the player's faction)", () =>
            {
                RecruitUtility.Recruit(p, Faction.OfPlayer);
                v.Check(p.Faction == Faction.OfPlayer && !p.IsPrisoner, "vanilla made the pawn a colonist");
                return StepResult.Next;
            });
            Then("wait for the custody watch to record the recruitment", () => c.status == CharacterStatus.Defected && c.heldBy == HeldKind.PlayerColonist && !c.episode.IsValid ? StepResult.Next : StepResult.Wait,
                3 * PhysicalLifecycleService.CustodyWatchPeriod);
            Then("duplicate wake-ups change nothing", DuplicateWakeups);
        }

        protected override void Finish()
        {
            PhysicalEpisode ce = LatestCustodyEpisode();
            v.Check(ce != null && ce.IsComplete && ce.members[0].outcome == MemberOutcome.JoinedPlayer, "a Custody episode recorded JoinedPlayer once (" + ce + ")");
            v.Check(lc.counters.custodyEpisodes == custodyEpisodes0 + 1, "exactly one Custody episode");
            v.Check(c.status == CharacterStatus.Defected && c.custody == CustodyState.OutOfCustody && c.heldBy == HeldKind.PlayerColonist, "Defected, OutOfCustody(PlayerColonist): never Stored");
            v.Check(!AuthorityGate.CanSimulateAbstractly(c), "never abstract: the Network does not simulate a recruited person as if they went home");
            v.Check(p.Faction == Faction.OfPlayer && p.Spawned, "the colonist is untouched (still the player's, on the map): no faction change back, no removal, no clone");
            v.Check(c.pawn != null && ReferenceEquals(c.pawn.pawn, p), "the SAME pawn is bound");
            v.Note("the recruited contractor stays the player's colonist on the test map (deliberate). What a recruited contractor means professionally is an OPEN owner decision (O-20).");
            CheckInvariants();
        }
    }

    /// <summary>RT-PHYX-022 — enslavement (Ideology): a prisoner enslaved by the player is held as PlayerSlave (holder only; still Captured). LEAVES the slave.</summary>
    public sealed class Phyx022Enslavement : HeldRun
    {
        public Phyx022Enslavement(NetworkRuntime rt, string runId) : base(PhysicalScenarioTable.Get("RT-PHYX-022"), runId, rt)
        {
        }

        protected override void Script()
        {
            Then("Ideology must be active (vanilla slavery)", () =>
            {
                if (ModsConfig.IdeologyActive) return StepResult.Next;
                v.Gap("Ideology is not active in this game: slavery does not exist, so enslavement cannot be observed");
                return StepResult.Abort;
            });
            Then("pick a never-materialized Solo", () => PickSolo(SoloNeed.Fresh));
            Then("materialize through the production lifecycle", MaterializeOnTestMap);
            Then("let the visitor walk in", WaitOnMap(300));
            Then("dev arrest", Arrest);
            Then("wait for the held commit", WaitHeld, 12 * PhysicalLifecycleService.WatchPeriod);
            Then("check: held once", () =>
            {
                CheckHeldOnce(MemberOutcome.HeldByPlayer, HeldKind.PlayerPrisoner, CharacterStatus.Captured);
                return StepResult.Next;
            });
            Then("dev enslavement (the guest-status change vanilla's enslavement makes)", () =>
            {
                p.guest.SetGuestStatus(Faction.OfPlayer, GuestStatus.Slave);
                v.Check(p.IsSlaveOfColony, "vanilla made the pawn a slave of the colony");
                return StepResult.Next;
            });
            Then("wait for the custody watch to record the slave", () => c.heldBy == HeldKind.PlayerSlave ? StepResult.Next : StepResult.Wait, 3 * PhysicalLifecycleService.CustodyWatchPeriod);
            Then("duplicate wake-ups change nothing", DuplicateWakeups);
        }

        protected override void Finish()
        {
            v.Check(c.custody == CustodyState.OutOfCustody && c.heldBy == HeldKind.PlayerSlave && c.status == CharacterStatus.Captured, "OutOfCustody(PlayerSlave), still Captured");
            v.Check(lc.counters.custodyEpisodes == custodyEpisodes0 && lc.counters.custodyHolderChanges > holderChanges0, "a change of holder is bookkeeping only (no Custody episode, no event)");
            v.Check(!AuthorityGate.CanSimulateAbstractly(c) && p.IsSlaveOfColony, "never abstract; the slave untouched");
            CheckInvariants();
        }
    }

    /// <summary>
    /// RT-PHYX-023 — kidnapping by a real enemy faction (KidnappedPawnsTracker.Kidnap): the episode closes once (Kidnapped; Captured,
    /// OutOfCustody(Kidnapped)); then vanilla's own "the captor recruits a kidnapped pawn" event (its two steps) makes the same pawn a member of
    /// that faction: held by OtherFaction, never a free return, never Stored. LEAVES the person with that faction.
    /// </summary>
    public sealed class Phyx023Kidnapped : HeldRun
    {
        private Faction captor;

        public Phyx023Kidnapped(NetworkRuntime rt, string runId) : base(PhysicalScenarioTable.Get("RT-PHYX-023"), runId, rt)
        {
        }

        protected override void Script()
        {
            Then("a real enemy humanlike faction must exist (the captor)", () =>
            {
                captor = Find.FactionManager.RandomEnemyFaction(false, false, false);
                if (captor != null && captor.kidnapped != null) return StepResult.Next;
                v.Gap("no enemy humanlike faction exists in this game to kidnap the visitor");
                return StepResult.Abort;
            });
            Then("pick a never-materialized Solo", () => PickSolo(SoloNeed.Fresh));
            Then("materialize through the production lifecycle", MaterializeOnTestMap);
            Then("let the visitor walk in", WaitOnMap(300));
            Then("dev kidnapping by the enemy faction (vanilla's KidnappedPawnsTracker.Kidnap)", () =>
            {
                if (p == null || !p.Spawned)
                {
                    v.Gap("the pawn left before the kidnapping");
                    return StepResult.Abort;
                }
                Baseline();
                v.Note("captor: " + captor.Name + " (" + captor.loadID + ")");
                captor.kidnapped.Kidnap(p, null);
                v.Check(PawnUtility.IsKidnappedPawn(p) && !p.Spawned && Find.WorldPawns.Contains(p), "vanilla took the pawn off the map as a kidnapped world pawn");
                return StepResult.Next;
            });
            Then("wait for the held commit", WaitHeld, 12 * PhysicalLifecycleService.WatchPeriod);
            Then("check: held once by the kidnapper", () =>
            {
                CheckHeldOnce(MemberOutcome.Kidnapped, HeldKind.Kidnapped, CharacterStatus.Captured);
                WorldPawnSituation s = Find.WorldPawns.GetSituation(p);
                v.Check(s == WorldPawnSituation.Kidnapped, "vanilla still sees a Kidnapped world pawn (the reservation does not change it: got " + s + ")");
                return StepResult.Next;
            });
            Then("duplicate wake-ups change nothing", DuplicateWakeups);
            Then("vanilla's \"the captor recruits a kidnapped pawn\" event (its two steps: the captor's faction, off the kidnapped list)", () =>
            {
                p.SetFaction(captor);
                captor.kidnapped.RemoveKidnappedPawn(p);
                v.Check(!PawnUtility.IsKidnappedPawn(p) && p.Faction == captor, "the pawn is now a member of " + captor.Name);
                return StepResult.Next;
            });
            Then("wait for the custody watch to record the new allegiance", () => c.heldBy == HeldKind.OtherFaction ? StepResult.Next : StepResult.Wait, 3 * PhysicalLifecycleService.CustodyWatchPeriod);
        }

        protected override void Finish()
        {
            v.Check(c.custody == CustodyState.OutOfCustody && c.heldBy == HeldKind.OtherFaction && c.status == CharacterStatus.Captured, "held by OtherFaction, still Captured: never a free return, never Stored");
            v.Check(lc.counters.custodyEpisodes == custodyEpisodes0, "no Custody episode: a change of holder is bookkeeping only");
            WorldPawnSituation s = Find.WorldPawns.Contains(p) ? Find.WorldPawns.GetSituation(p) : WorldPawnSituation.None;
            v.Check(s == WorldPawnSituation.ReservedByQuest && port.Registry.Reserves(p), "the pawn is a world pawn the registry still reserves (got " + s + "), never an ordinary Free one");
            v.Check(c.pawn != null && ReferenceEquals(c.pawn.pawn, p), "the SAME pawn is bound");
            CheckInvariants();
        }
    }

    /// <summary>RT-PHYX-024 — death while held: an arrested contractor killed by dev damage is recorded dead ONCE (Released), monotonic; the Solo ends.</summary>
    public sealed class Phyx024DeathWhileHeld : HeldRun
    {
        public Phyx024DeathWhileHeld(NetworkRuntime rt, string runId) : base(PhysicalScenarioTable.Get("RT-PHYX-024"), runId, rt)
        {
        }

        protected override void Script()
        {
            Then("pick a never-materialized Solo", () => PickSolo(SoloNeed.Fresh));
            Then("materialize through the production lifecycle", MaterializeOnTestMap);
            Then("let the visitor walk in", WaitOnMap(300));
            Then("dev arrest", Arrest);
            Then("wait for the held commit", WaitHeld, 12 * PhysicalLifecycleService.WatchPeriod);
            Then("check: held once", () =>
            {
                CheckHeldOnce(MemberOutcome.HeldByPlayer, HeldKind.PlayerPrisoner, CharacterStatus.Captured);
                return StepResult.Next;
            });
            Then("dev damage until dead (while held)", () =>
            {
                HealthUtility.DamageUntilDead(p);
                v.Check(p.Dead, "vanilla killed the held pawn");
                return StepResult.Next;
            });
            Then("wait for the custody watch (or the Killed wake-up) to record the death", () => c.status == CharacterStatus.Dead && !c.episode.IsValid ? StepResult.Next : StepResult.Wait, 3 * PhysicalLifecycleService.CustodyWatchPeriod);
            Then("duplicate wake-ups and a new Plan change nothing", () =>
            {
                int commits = lc.counters.commits, eps = lc.counters.custodyEpisodes;
                lc.ReconcileHeld(c, "dev duplicate (RT-PHYX-024)");
                lc.Reconcile(LatestCustodyEpisode(), "dev duplicate (RT-PHYX-024)");
                v.Check(lc.counters.commits == commits && lc.counters.custodyEpisodes == eps, "the death was committed exactly once");
                return StepResult.Next;
            });
        }

        protected override void Finish()
        {
            PhysicalEpisode ce = LatestCustodyEpisode();
            v.Check(ce != null && ce.IsComplete && ce.members[0].outcome == MemberOutcome.Killed, "a Custody episode recorded the death once (" + ce + ")");
            v.Check(c.status == CharacterStatus.Dead && c.custody == CustodyState.Released && c.diedTick >= 0, "Dead, custody Released, died tick " + c.diedTick);
            v.Check(!port.Registry.Reserves(p), "the registry no longer reserves a dead person's pawn");
            v.Check(c.pawn != null && ReferenceEquals(c.pawn.pawn, p), "the binding still points to the same (dead) pawn: never regenerated");
            v.Check(!a.IsActive, "the Solo actor ended with its person (" + a.status + ")");
            CheckInvariants();
        }
    }

    /// <summary>
    /// RT-PHYX-025 — READ-ONLY, after a save and load: every person vanilla holds is still held, by the same bound pawn, reserved, watched, and
    /// not abstract; nothing was generated or returned by the load. Run it after saving and loading a game in which 020–024 left people held.
    /// </summary>
    public sealed class Phyx025HeldVerify : PhysicalRun
    {
        public Phyx025HeldVerify(NetworkRuntime rt, string runId) : base(PhysicalScenarioTable.Get("RT-PHYX-025"), runId, rt)
        {
        }

        protected override void Script()
        {
            Then("verify every held person (read-only)", () =>
            {
                List<int> held = lc.HeldIds();
                if (held.Count == 0)
                {
                    v.Gap("nobody is held by vanilla in this game: run RT-PHYX-021, 022 or 023, save, load, then this verification");
                    return StepResult.Next;
                }
                v.Check(ctx.scheduler.Has(PhysicalLifecycleService.CustodyWatchJob, PhysicalLifecycleService.CustodyWatchTarget), "the custody watch survived the load (" + held.Count + " held)");
                foreach (int id in held)
                {
                    KnownCharacter k = ctx.characters.Get(new CharacterId(id));
                    Pawn pawn = k?.pawn?.pawn;
                    string who = id + " (" + k?.name?.Display + ", " + k?.heldBy + ")";
                    v.Check(pawn != null && !pawn.Discarded && pawn.thingIDNumber == k.pawn.thingIdNumber, who + ": the binding resolves to the same live pawn #" + k?.pawn?.thingIdNumber);
                    v.Check(k != null && k.custody == CustodyState.OutOfCustody && k.heldBy != HeldKind.None, who + ": still OutOfCustody with a holder");
                    v.Check(k != null && !AuthorityGate.CanSimulateAbstractly(k), who + ": not abstract");
                    if (pawn != null && !pawn.Dead) v.Check(port.Registry.Reserves(pawn), who + ": the registry reserves the pawn after the load (two-stage registry)");
                    if (pawn != null) v.Note(who + ": " + port.Observe(k.pawn, EpisodeId.None) + " · " + port.DescribeBinding(k.pawn));
                }
                return StepResult.Next;
            });
        }
    }
}
