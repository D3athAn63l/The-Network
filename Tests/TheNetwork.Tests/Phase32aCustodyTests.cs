using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using RimWorld;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Operations;
using TheNetwork.Domain.Physical;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;
using TheNetwork.Settings;
using Verse;

namespace TheNetwork.Tests
{
    /// <summary>
    /// Phase 3.2A — HELD CUSTODY (PHYSICAL_LIFECYCLE § 8.2, § 9, § 15.3; ADR-056), headless over the scriptable fake port: the custody rules,
    /// the episode → vanilla-held transition, the custody watch and its Custody episodes, save/load, prepare-for-removal and the validator; and
    /// the domain half of a rescue (the Troubled handoff, the deadline suspension, and the OnPhysicalResolved fault/retry matrix over a real
    /// contract). The rescue SITE is not built: S11 failed its source audit (docs/spikes/S11-rescue-site-holder.md).
    /// </summary>
    public static class Phase32aCustodyTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("Custody.Rules_ClassificationMatrix", RulesMatrix));
            t.Add(new KeyValuePair<string, Action>("Custody.Rules_MissionDecide", MissionDecide));
            t.Add(new KeyValuePair<string, Action>("Custody.CaptureCommitsTheEpisodeOnce", CaptureOnce));
            t.Add(new KeyValuePair<string, Action>("Custody.EpisodeCompletesWhileThePersonStaysHeld", EpisodeCompletesWhileHeld));
            t.Add(new KeyValuePair<string, Action>("Custody.HeldPersonIsNeverAdvancedAbstractly", NeverAdvancedAbstractly));
            t.Add(new KeyValuePair<string, Action>("Custody.UnknownOwnershipFailsClosed", UnknownFailsClosed));
            t.Add(new KeyValuePair<string, Action>("Custody.SamePawnSurvivesCaptureReturnAndRematerialization", SamePawnThroughout));
            t.Add(new KeyValuePair<string, Action>("Custody.ReturnIsExactlyOnceAndWaitsForRelease", ReturnOnce));
            t.Add(new KeyValuePair<string, Action>("Custody.RecruitmentIsDefectedAndNeverStored", RecruitmentNeverStored));
            t.Add(new KeyValuePair<string, Action>("Custody.DeathWhileHeldIsMonotonic", DeathWhileHeld));
            t.Add(new KeyValuePair<string, Action>("Custody.SignalsOnlyWakeAndDroppedSignalsConverge", SignalsOnlyWake));
            t.Add(new KeyValuePair<string, Action>("Custody.WatchJobExistsIffSomeoneIsHeld", WatchExistsIffHeld));
            t.Add(new KeyValuePair<string, Action>("Custody.SaveLoadWithAHeldPerson", SaveLoadHeld));
            t.Add(new KeyValuePair<string, Action>("Custody.UnresolvedBindingIsLostNeverRegenerated", LostNeverRegenerated));
            t.Add(new KeyValuePair<string, Action>("Custody.PrepareForRemovalLeavesHeldPeopleHeld", RemovalWithHeld));
            t.Add(new KeyValuePair<string, Action>("Custody.ValidatorReportsImpossibleCustody", ValidatorReports));
            t.Add(new KeyValuePair<string, Action>("Custody.KidnappedThenRecruitedByTheCaptor", KidnappedThenRecruited));
            t.Add(new KeyValuePair<string, Action>("Custody.CaravanAndTransportHolders", CaravanAndTransport));
            t.Add(new KeyValuePair<string, Action>("Custody.CustodyCommitFaultRestoresAndRetriesOnce", CustodyCommitFault));
            t.Add(new KeyValuePair<string, Action>("Custody.OrgLeaderCapturedThenDiesWhileHeld", OrgLeaderHeldThenDies));
            t.Add(new KeyValuePair<string, Action>("Custody.WorkIsBoundedByTheHeldPeople", BoundedWork));
            t.Add(new KeyValuePair<string, Action>("Custody.Scan_PureRulesNoHarmonyNoSaveFormatChange", ScanCustody));
            t.Add(new KeyValuePair<string, Action>("Custody.Docs_StatusIsHeadlessValidatedAndS11IsRecorded", DocsStatus));
            t.Add(new KeyValuePair<string, Action>("Custody.Correction_PlayerRecruitCannotRedeployForOriginalNpc", PlayerRecruitCannotRedeploy));
            t.Add(new KeyValuePair<string, Action>("Custody.Correction_TerminalMetadataSurvivesSaveLoad", TerminalMetadataSaveLoad));
            t.Add(new KeyValuePair<string, Action>("Custody.Correction_RevertedClearsLiveHolder", RevertedClearsHolder));
            t.Add(new KeyValuePair<string, Action>("Custody.Correction_HolderChangesKeepContinuousSinceTick", HolderChangesKeepSince));
            t.Add(new KeyValuePair<string, Action>("Custody.Correction_AllNonHeldStatesReportStaleMetadata", NonHeldMetadataValidation));
            t.Add(new KeyValuePair<string, Action>("Custody.Correction_TerminalRollbackRestoresLiveHolder", TerminalRollbackRestoresHolder));
            t.Add(new KeyValuePair<string, Action>("Rescue.HandoffSuspendsTheTroubledDeadline", HandoffSuspendsDeadline));
            t.Add(new KeyValuePair<string, Action>("Rescue.EpisodeAndAbstractPathCannotBothResolve", NoDoubleResolution));
            t.Add(new KeyValuePair<string, Action>("Rescue.OnPhysicalResolvedFaultMatrix_Found", FollowUpMatrixFound));
            t.Add(new KeyValuePair<string, Action>("Rescue.OnPhysicalResolvedFaultMatrix_WrittenOff", FollowUpMatrixWrittenOff));
            t.Add(new KeyValuePair<string, Action>("Rescue.OnPhysicalResolvedFaultMatrix_SoloHeldWrittenOff", FollowUpMatrixSoloHeld));
        }

        // ================================================================== helpers

        private static PhysicalLifecycleService L(TestNet n) { return n.ctx.Lifecycle; }

        private static NetworkActor Solo(TestNet n, string id) { return PhysicalLifecycleTests.Make(n, ContractorForm.Solo, id); }

        private static KnownCharacter Self(TestNet n, NetworkActor a) { return PhysicalLifecycleTests.Self(n, a); }

        private static bool WatchExists(TestNet n)
        {
            return n.scheduler.Has(PhysicalLifecycleService.CustodyWatchJob, PhysicalLifecycleService.CustodyWatchTarget);
        }

        /// <summary>Lets the custody watch (and anything else due) run once more.</summary>
        private static void RunWatch(TestNet n)
        {
            n.Advance(PhysicalLifecycleService.CustodyWatchPeriod + 1);
        }

        /// <summary>A Solo materialized and taken by a vanilla holder; the mission episode reconciled.</summary>
        private static PhysicalEpisode Capture(TestNet n, NetworkActor a, KnownCharacter c, ObservedKind kind, HeldKind holder)
        {
            PhysicalEpisode e = PhysicalLifecycleTests.Begin(n, a, new[] { c });
            n.physical.Hold(c.pawn, kind, holder);
            L(n).Reconcile(e, "watch");
            return e;
        }

        private static PhysicalEpisode Arrested(TestNet n, NetworkActor a, KnownCharacter c)
        {
            return Capture(n, a, c, ObservedKind.HeldByPlayer, HeldKind.PlayerPrisoner);
        }

        private static List<PhysicalEpisode> CustodyEpisodes(TestNet n, KnownCharacter c)
        {
            List<PhysicalEpisode> l = new List<PhysicalEpisode>();
            foreach (PhysicalEpisode e in n.ctx.episodes.episodes) if (CustodyRules.IsCustodyEpisode(e) && e.members.Count == 1 && e.members[0].character == c.id) l.Add(e);
            return l;
        }

        /// <summary>The person's physical and story record (what an abstract writer would change).</summary>
        private static string Rec(KnownCharacter c)
        {
            return c.status + "@" + c.statusTick + "|" + c.custody + "|" + c.heldBy + "@" + c.heldSinceTick + "|ep " + c.episode.Value + "|wound " + c.woundedUntilTick + "|died " + c.diedTick
                + "|pawn " + (c.pawn != null ? c.pawn.thingIdNumber + "/" + c.pawn.agedThroughTick : "none");
        }

        private static PhysicalObservation O(ObservedKind k, HeldKind holder = HeldKind.None, bool exit = false, bool other = false)
        {
            return new PhysicalObservation { kind = k, holder = holder, exitEvidence = exit, otherAllegiance = other };
        }

        private static void Expect(CustodyDecision d, CustodyTransitionKind kind, MemberOutcome outcome, HeldKind holder, bool captive, string what)
        {
            bool ok = d.kind == kind && (kind != CustodyTransitionKind.Reconcile || d.outcome == outcome) && (kind == CustodyTransitionKind.None || d.holder == holder) && d.captive == captive;
            T.Check(ok, what + " (got " + d + ")");
        }

        // ================================================================== the pure rules

        private static void RulesMatrix()
        {
            // The holder of a held observation: positive when the adapter read it; the coarse kind otherwise; unknown owners stay Unknown.
            T.Eq(HeldKind.PlayerSlave, CustodyRules.HolderOf(O(ObservedKind.HeldByPlayer, HeldKind.PlayerSlave)), "a positive holder wins");
            T.Eq(HeldKind.PlayerPrisoner, CustodyRules.HolderOf(O(ObservedKind.HeldByPlayer)), "player custody without a holder ⇒ prisoner");
            T.Eq(HeldKind.PlayerColonist, CustodyRules.HolderOf(O(ObservedKind.JoinedPlayer)), "joined ⇒ colonist");
            T.Eq(HeldKind.Kidnapped, CustodyRules.HolderOf(O(ObservedKind.Kidnapped)), "kidnapped");
            T.Eq(HeldKind.OtherFaction, CustodyRules.HolderOf(O(ObservedKind.HeldByOther)), "another faction");
            T.Eq(HeldKind.Transport, CustodyRules.HolderOf(O(ObservedKind.InTransport)), "a transport");
            T.Eq(HeldKind.Unknown, CustodyRules.HolderOf(O(ObservedKind.InCaravan)), "a caravan of unknown owner is Unknown, never guessed");
            T.Eq(MemberOutcome.HeldByPlayer, CustodyRules.OutcomeFor(HeldKind.PlayerCaravan), "a player caravan is held by the player");
            T.Eq(MemberOutcome.JoinedPlayer, CustodyRules.OutcomeFor(HeldKind.PlayerColonist), "a colonist joined the player");
            T.Eq(MemberOutcome.HeldByOther, CustodyRules.OutcomeFor(HeldKind.Unknown), "an unknown holder is someone else's custody");

            // Transitions of a held person, from positive evidence only.
            Expect(CustodyRules.Transition(CharacterStatus.Captured, HeldKind.PlayerPrisoner, O(ObservedKind.Dead)), CustodyTransitionKind.Reconcile, MemberOutcome.Killed, HeldKind.None, false, "death while held ⇒ reconcile Killed");
            Expect(CustodyRules.Transition(CharacterStatus.Captured, HeldKind.PlayerPrisoner, O(ObservedKind.Gone)), CustodyTransitionKind.Reconcile, MemberOutcome.Lost, HeldKind.None, false, "a discarded pawn ⇒ reconcile Lost");
            Expect(CustodyRules.Transition(CharacterStatus.Captured, HeldKind.PlayerPrisoner, O(ObservedKind.WorldFree, HeldKind.None, true)), CustodyTransitionKind.Reconcile, MemberOutcome.Returned, HeldKind.None, false, "a free, reserved world pawn that left ⇒ return");
            Expect(CustodyRules.Transition(CharacterStatus.Captured, HeldKind.PlayerPrisoner, O(ObservedKind.WorldFree)), CustodyTransitionKind.None, MemberOutcome.Pending, HeldKind.None, false, "a world pawn without exit evidence is not a return");
            Expect(CustodyRules.Transition(CharacterStatus.Captured, HeldKind.Kidnapped, O(ObservedKind.WorldFree, HeldKind.None, true, true)), CustodyTransitionKind.Holder, MemberOutcome.Pending, HeldKind.OtherFaction, false, "a free world pawn of another permanent faction stays held (OtherFaction)");
            Expect(CustodyRules.Transition(CharacterStatus.Defected, HeldKind.PlayerColonist, O(ObservedKind.WorldFree, HeldKind.None, true)), CustodyTransitionKind.Holder, MemberOutcome.Pending, HeldKind.Unaffiliated, false, "a recruited person who is free again is never returned (Unaffiliated)");
            Expect(CustodyRules.Transition(CharacterStatus.Captured, HeldKind.PlayerPrisoner, O(ObservedKind.HeldByPlayer, HeldKind.PlayerPrisoner)), CustodyTransitionKind.None, MemberOutcome.Pending, HeldKind.None, false, "unchanged custody ⇒ nothing");
            Expect(CustodyRules.Transition(CharacterStatus.Captured, HeldKind.PlayerPrisoner, O(ObservedKind.HeldByPlayer, HeldKind.PlayerSlave)), CustodyTransitionKind.Holder, MemberOutcome.Pending, HeldKind.PlayerSlave, false, "enslaved ⇒ the holder only");
            Expect(CustodyRules.Transition(CharacterStatus.Active, HeldKind.PlayerCaravan, O(ObservedKind.HeldByPlayer, HeldKind.PlayerPrisoner)), CustodyTransitionKind.Reconcile, MemberOutcome.HeldByPlayer, HeldKind.PlayerPrisoner, true, "someone carried, now arrested ⇒ a capture");
            Expect(CustodyRules.Transition(CharacterStatus.Captured, HeldKind.PlayerPrisoner, O(ObservedKind.JoinedPlayer)), CustodyTransitionKind.Reconcile, MemberOutcome.JoinedPlayer, HeldKind.PlayerColonist, false, "recruited ⇒ reconcile JoinedPlayer");
            Expect(CustodyRules.Transition(CharacterStatus.Defected, HeldKind.PlayerColonist, O(ObservedKind.JoinedPlayer)), CustodyTransitionKind.None, MemberOutcome.Pending, HeldKind.None, false, "a recruited colonist stays as recorded");
            Expect(CustodyRules.Transition(CharacterStatus.Defected, HeldKind.PlayerColonist, O(ObservedKind.HeldByPlayer, HeldKind.PlayerPrisoner)), CustodyTransitionKind.Holder, MemberOutcome.Pending, HeldKind.PlayerPrisoner, false, "a recruited person arrested again keeps the Defected story; the holder changes");
            Expect(CustodyRules.Transition(CharacterStatus.Captured, HeldKind.PlayerPrisoner, O(ObservedKind.InCaravan, HeldKind.PlayerCaravan)), CustodyTransitionKind.Holder, MemberOutcome.Pending, HeldKind.PlayerCaravan, false, "in a player caravan");
            Expect(CustodyRules.Transition(CharacterStatus.Captured, HeldKind.PlayerCaravan, O(ObservedKind.InTransport)), CustodyTransitionKind.Holder, MemberOutcome.Pending, HeldKind.Transport, false, "in a transport");
            Expect(CustodyRules.Transition(CharacterStatus.Captured, HeldKind.PlayerPrisoner, O(ObservedKind.Spawned)), CustodyTransitionKind.Holder, MemberOutcome.Pending, HeldKind.Unaffiliated, false, "spawned with no holder (walking out) ⇒ still held, Unaffiliated");
            Expect(CustodyRules.Transition(CharacterStatus.Captured, HeldKind.PlayerPrisoner, O(ObservedKind.Unknown)), CustodyTransitionKind.Holder, MemberOutcome.Pending, HeldKind.Unknown, false, "unclassifiable ⇒ held, Unknown (fail closed)");
            Expect(CustodyRules.Transition(CharacterStatus.Captured, HeldKind.Unknown, O(ObservedKind.None)), CustodyTransitionKind.None, MemberOutcome.Pending, HeldKind.None, false, "no observation, already Unknown ⇒ nothing");
            Expect(CustodyRules.Transition(CharacterStatus.Captured, HeldKind.Kidnapped, O(ObservedKind.WorldOther)), CustodyTransitionKind.None, MemberOutcome.Pending, HeldKind.None, false, "another quest's world pawn ⇒ unchanged");
            Expect(CustodyRules.Transition(CharacterStatus.Captured, HeldKind.PlayerPrisoner, O(ObservedKind.ReservationBroken, HeldKind.None, true)), CustodyTransitionKind.None, MemberOutcome.Pending, HeldKind.None, false, "a broken reservation is never a return");
            Expect(CustodyRules.Transition(CharacterStatus.Dead, HeldKind.PlayerPrisoner, O(ObservedKind.WorldFree, HeldKind.None, true)), CustodyTransitionKind.None, MemberOutcome.Pending, HeldKind.None, false, "the dead never transition again");
            // A Custody episode always closes, with the current holding when nothing has consequences any more.
            Expect(CustodyRules.EpisodeDecision(CharacterStatus.Captured, HeldKind.PlayerPrisoner, O(ObservedKind.WorldOther)), CustodyTransitionKind.Reconcile, MemberOutcome.HeldByPlayer, HeldKind.PlayerPrisoner, false, "a custody episode keeps the current holding");
            Expect(CustodyRules.EpisodeDecision(CharacterStatus.Captured, HeldKind.None, O(ObservedKind.None)), CustodyTransitionKind.Reconcile, MemberOutcome.HeldByOther, HeldKind.Unknown, false, "and an unknown one fails closed");
            Expect(CustodyRules.EpisodeDecision(CharacterStatus.Captured, HeldKind.PlayerPrisoner, O(ObservedKind.Dead)), CustodyTransitionKind.Reconcile, MemberOutcome.Killed, HeldKind.None, false, "a death is a death");
        }

        private static void MissionDecide()
        {
            bool unsup, captive;
            HeldKind h;
            T.Eq(MemberOutcome.HeldByPlayer, ReconciliationPlanner.Decide(O(ObservedKind.HeldByPlayer, HeldKind.PlayerPrisoner), true, out unsup, out h, out captive), "arrested ⇒ HeldByPlayer");
            T.Check(!unsup && captive && h == HeldKind.PlayerPrisoner, "a supported captive custody (" + h + ")");
            T.Eq(MemberOutcome.Pending, ReconciliationPlanner.Decide(O(ObservedKind.HeldByPlayer, HeldKind.PlayerPrisoner), false, out unsup, out h, out captive), "an ANONYMOUS member held is not decided");
            T.Check(unsup, "it is unsupported until 3.2B (promotion)");
            T.Eq(MemberOutcome.JoinedPlayer, ReconciliationPlanner.Decide(O(ObservedKind.JoinedPlayer, HeldKind.PlayerColonist), true, out unsup, out h, out captive), "joined");
            T.Check(!captive && h == HeldKind.PlayerColonist, "recruited is not a capture");
            T.Eq(MemberOutcome.Kidnapped, ReconciliationPlanner.Decide(O(ObservedKind.Kidnapped, HeldKind.Kidnapped), true, out unsup, out h, out captive), "kidnapped");
            T.Check(captive, "a kidnapping is a capture");
            T.Eq(MemberOutcome.HeldByOther, ReconciliationPlanner.Decide(O(ObservedKind.HeldByOther, HeldKind.OtherFaction), true, out unsup, out h, out captive), "held by another faction");
            T.Eq(MemberOutcome.HeldByPlayer, ReconciliationPlanner.Decide(O(ObservedKind.InCaravan, HeldKind.PlayerCaravan), true, out unsup, out h, out captive), "a player caravan holds them");
            T.Check(!captive && h == HeldKind.PlayerCaravan, "carried, not captured");
            T.Eq(MemberOutcome.HeldByOther, ReconciliationPlanner.Decide(O(ObservedKind.InCaravan, HeldKind.OtherFaction), true, out unsup, out h, out captive), "another faction's caravan");
            T.Eq(MemberOutcome.Pending, ReconciliationPlanner.Decide(O(ObservedKind.InTransport, HeldKind.Transport), true, out unsup, out h, out captive), "a pod in flight is transit: still Present (§ 12.3)");
            T.Eq(MemberOutcome.HeldByOther, ReconciliationPlanner.Decide(O(ObservedKind.WorldFree, HeldKind.None, true, true), true, out unsup, out h, out captive), "a world pawn of another permanent faction is never Returned");
            T.Check(h == HeldKind.OtherFaction && !captive, "held by OtherFaction, not a capture");
            T.Eq(MemberOutcome.Returned, ReconciliationPlanner.Decide(O(ObservedKind.WorldFree, HeldKind.None, true), true, out unsup, out h, out captive), "a free reserved world pawn that left ⇒ Returned (3.1 unchanged)");
            T.Eq(MemberOutcome.Pending, ReconciliationPlanner.Decide(O(ObservedKind.ReservationBroken, HeldKind.None, true), true, out unsup, out h, out captive), "a broken reservation is never Returned (3.1 unchanged)");
            T.Eq(MemberOutcome.Pending, ReconciliationPlanner.Decide(O(ObservedKind.Unknown), true, out unsup, out h, out captive), "unknown ⇒ Pending (the episode stays open, never home)");
            T.Eq(MemberOutcome.Pending, ReconciliationPlanner.Decide(O(ObservedKind.Spawned), true, out unsup, out h, out captive), "spawned ⇒ Pending");
        }

        // ================================================================== episode → vanilla-held

        private static void CaptureOnce()
        {
            TestNet n = new TestNet(9801);
            NetworkActor a = Solo(n, "capture");
            KnownCharacter c = Self(n, a);
            int commits0 = L(n).counters.commits;
            PhysicalEpisode e = Arrested(n, a, c);
            // Every wake-up kind, many times: signal, watch, load, dev.
            for (int i = 0; i < 3; i++) L(n).Reconcile(e, "signal");
            L(n).OnLoaded();
            n.Advance(5 * PhysicalLifecycleService.WatchPeriod);
            L(n).WakeAll("dev");
            T.Check(e.IsComplete && e.members[0].outcome == MemberOutcome.HeldByPlayer && e.members[0].observed == ObservedKind.HeldByPlayer, "the episode closed with HeldByPlayer (" + e + ")");
            T.Eq(commits0 + 1, L(n).counters.commits, "reconciled exactly once");
            T.Eq(1, n.recorder.Count(EventKeys.CharacterCapturedByPlayer), "KnownCharacter.CapturedByPlayer published once");
            T.Eq(1, n.recorder.Count(EventKeys.ContractorCasualties), "the group's casualty report once (one captured)");
            T.Eq(1, n.recorder.Count(EventKeys.EpisodeClosed), "Episode.Closed once");
            T.Eq(0, n.recorder.Count(EventKeys.ContractorCaptured), "a capture by the PLAYER is not reported as Contractor.Captured");
            T.Eq(CharacterStatus.Captured, c.status, "status Captured");
            T.Eq(1, L(n).counters.heldOutcomes, "one held outcome counted");
        }

        private static void EpisodeCompletesWhileHeld()
        {
            TestNet n = new TestNet(9802);
            NetworkActor a = Solo(n, "held");
            KnownCharacter c = Self(n, a);
            int tick = n.clock.Now;
            PhysicalEpisode e = Arrested(n, a, c);
            FakePhysicalWorldPortView t = new FakePhysicalWorldPortView(n, c);
            T.Check(e.IsComplete && e.releaseApplied && e.followUpApplied && e.PublishDone, "the mission episode is COMPLETE although the person is still held");
            T.Check(!c.episode.IsValid, "the episode link was cleared by RELEASE COMPLETE (the episode does not stay open for the captivity)");
            T.Check(c.custody == CustodyState.OutOfCustody && c.heldBy == HeldKind.PlayerPrisoner && c.heldSinceTick == tick, "OutOfCustody(PlayerPrisoner) since the capture tick (" + Rec(c) + ")");
            T.Eq(PersonAuthority.VanillaHeld, AuthorityGate.AuthorityOf(c), "authority: VanillaHeld");
            T.Check(!AuthorityGate.CanSimulateAbstractly(c), "CanSimulateAbstractly is false while held");
            T.Eq(0, t.token.normalized, "the held pawn is never normalized (not stored, not healed)");
            T.Eq(0, t.token.retainCalls, "no storage proof: it is not Stored");
            T.Eq(0, n.physical.passCalls, "no Network PassToWorld");
            T.Check(t.token.held && t.token.tag == EpisodeId.None && t.token.tagStrips == 1, "the pawn is untouched but for its episode routing tag");
            T.Eq(1, n.physical.creates, "one pawn, created once");
            T.Check(WatchExists(n), "the custody watch exists");
            T.Eq(1, L(n).HeldCount, "one person is held");
            ReleaseAction[] actions = ReleasePolicy.ActionsFor(e.members[0]);
            T.Check(actions.Length == 1 && actions[0] == ReleaseAction.StripTag, "a held member's RELEASE is the routing clean-up only");
        }

        /// <summary>A view on the fake port's token of a person (for readability).</summary>
        private sealed class FakePhysicalWorldPortView
        {
            public readonly TheNetwork.Diagnostics.RuntimeTests.FakePhysicalWorldPort.Token token;

            public FakePhysicalWorldPortView(TestNet n, KnownCharacter c)
            {
                token = n.physical.TokenOf(c.pawn);
            }
        }

        private static void NeverAdvancedAbstractly()
        {
            TestNet n = new TestNet(9803);
            NetworkActor a = Solo(n, "frozen");
            KnownCharacter c = Self(n, a);
            Arrested(n, a, c);
            SpatialState s = a.Get<ContractorSimulation>().spatial;
            string spatial = s.anchor?.tileId + "|" + s.status + "|" + s.lastUpdateTick + "|" + s.destination?.tileId;
            string before = Rec(c);
            int refused = AuthorityGate.refusedWrites;
            n.Advance(60 * Ticks.PerDay); // upkeep, recovery, spatial catch-up and relocation, careers, the custody watch: two months
            T.Eq(before, Rec(c), "two months later nothing abstract has advanced the held person (status, recovery, custody, binding, aging bookmark)");
            T.Eq(spatial, s.anchor?.tileId + "|" + s.status + "|" + s.lastUpdateTick + "|" + s.destination?.tileId, "the Solo's spatial entry stayed frozen");
            T.Eq(Availability.Unavailable, n.ctx.Contractors.AvailabilityOf(a), "unavailable for work (never 'busy', never home)");
            T.Check(!n.ctx.Operations.ContractorCanWork(a), "cannot work");
            T.Check(AuthorityGate.HasPhysicalPresence(n.ctx, a), "careers see the person as not home (no advancement)");
            T.Check(!n.ctx.Contractors.Checkout(a, new OperationId(n.ids.NextId()), 0.9f).characters.Contains(c.id), "never checked out for an operation");
            T.Check(AuthorityGate.refusedWrites >= refused, "abstract writers met the gate");
            // An organization's held member does not count toward its strength or a checkout either.
            NetworkActor org = PhysicalLifecycleTests.Make(n, ContractorForm.Company, "frozen-org");
            KnownCharacter lt = PhysicalLifecycleTests.Others(n, org)[0];
            float strength = n.ctx.Contractors.Strength(org);
            Arrested(n, org, lt);
            T.Check(n.ctx.Contractors.Strength(org) < strength, "the held member no longer counts toward the organization's strength");
            T.Check(!n.ctx.Contractors.Checkout(org, new OperationId(n.ids.NextId()), 0.9f).characters.Contains(lt.id), "and is never checked out");
        }

        private static void UnknownFailsClosed()
        {
            TestNet n = new TestNet(9804);
            NetworkActor a = Solo(n, "unknown");
            KnownCharacter c = Self(n, a);
            Arrested(n, a, c);
            int eps = L(n).counters.custodyEpisodes;
            n.physical.Script(c.pawn, O(ObservedKind.Unknown));
            RunWatch(n);
            T.Check(c.custody == CustodyState.OutOfCustody && c.heldBy == HeldKind.Unknown, "unclassifiable ⇒ held, Unknown (" + Rec(c) + ")");
            n.physical.Script(c.pawn, O(ObservedKind.Spawned));
            RunWatch(n);
            T.Eq(HeldKind.Unaffiliated, c.heldBy, "on a map with no holder ⇒ still held, Unaffiliated");
            n.physical.Script(c.pawn, O(ObservedKind.WorldOther));
            RunWatch(n);
            T.Eq(HeldKind.Unaffiliated, c.heldBy, "another quest's world pawn ⇒ unchanged");
            // An actual Free world pawn: the reservation failing. Never a return, never repaired here.
            n.physical.BreakReservation(c.pawn);
            n.physical.Free(c.pawn);
            RunWatch(n);
            T.Check(c.custody == CustodyState.OutOfCustody && L(n).counters.custodyBrokenReservations > 0, "a broken reservation keeps the person held and is reported");
            T.Eq(eps, L(n).counters.custodyEpisodes, "no Custody episode for any of it");
            T.Check(!AuthorityGate.CanSimulateAbstractly(c), "never abstract");
            // A mission member that cannot be classified keeps its episode open (never home).
            NetworkActor b = Solo(n, "unknown-mission");
            KnownCharacter d = Self(n, b);
            PhysicalEpisode e = PhysicalLifecycleTests.Begin(n, b, new[] { d });
            n.physical.Script(d.pawn, O(ObservedKind.Unknown));
            L(n).Reconcile(e, "watch");
            T.Check(e.state == EpisodeState.Open && !e.consequencesApplied && d.custody == CustodyState.Deployed, "an unknown mission member stays Present: nothing committed");
        }

        private static void SamePawnThroughout()
        {
            TestNet n = new TestNet(9805);
            NetworkActor a = Solo(n, "same");
            KnownCharacter c = Self(n, a);
            Arrested(n, a, c);
            PawnRef binding = c.pawn.Copy();
            n.Advance(10 * Ticks.PerDay);
            n.physical.Free(c.pawn);
            RunWatch(n);
            T.Check(c.custody == CustodyState.Stored && AuthorityGate.CanSimulateAbstractly(c), "back in the Network's custody (" + Rec(c) + ")");
            T.Check(c.pawn.SameBinding(binding), "the same binding");
            PhysicalEpisode again = PhysicalLifecycleTests.Begin(n, a, new[] { c });
            T.Check(again.state == EpisodeState.Open && again.members[0].pawn.SameBinding(binding), "rematerialized: the SAME pawn");
            T.Eq(1, n.physical.creates, "never regenerated");
            T.Eq(1, L(n).counters.rematerialized, "rematerialized once");
        }

        private static void ReturnOnce()
        {
            TestNet n = new TestNet(9806);
            NetworkActor a = Solo(n, "return");
            KnownCharacter c = Self(n, a);
            Arrested(n, a, c);
            int commits0 = L(n).counters.commits;
            n.physical.ThrowOn("normalize"); // RELEASE of the Custody episode is interrupted once
            n.physical.Free(c.pawn);
            RunWatch(n);
            List<PhysicalEpisode> ce = CustodyEpisodes(n, c);
            T.Eq(1, ce.Count, "one Custody episode");
            PhysicalEpisode r = ce[0];
            T.Check(r.state == EpisodeState.Closed && r.consequencesApplied && !r.releaseApplied, "committed (Returned), RELEASE pending after the injected fault");
            T.Check(c.custody == CustodyState.Stored && c.episode == r.id && !AuthorityGate.CanSimulateAbstractly(c), "Stored durable truth, yet NOT abstract until RELEASE completes (P3-INV-029)");
            n.Advance(2 * PhysicalLifecycleService.WatchPeriod);
            T.Check(r.IsComplete && AuthorityGate.CanSimulateAbstractly(c), "RELEASE resumed at its cursor and completed; only then abstract");
            T.Eq(MemberOutcome.Returned, r.members[0].outcome, "Returned");
            T.Eq(1, n.physical.TokenOf(c.pawn).normalized, "normalized once (store-time, S12)");
            T.Eq(1, n.physical.TokenOf(c.pawn).retainCalls, "the reservation proven once");
            T.Check(c.status == CharacterStatus.Active && c.heldBy == HeldKind.None && c.heldSinceTick < 0, "Captured resolved by the positive return; holder and heldSinceTick cleared (" + Rec(c) + ")");
            T.Eq(commits0 + 1, L(n).counters.commits, "one commit");
            T.Eq(1, n.recorder.Count(EventKeys.CharacterFreed), "KnownCharacter.Freed published once");
            T.Eq(0, n.recorder.Count("Contractor.Rescued"), "no causal claim (\"rescued\") is made about an observed release");
            // Idempotent: more watches, wake-ups and dev reconciles change nothing.
            for (int i = 0; i < 3; i++) RunWatch(n);
            L(n).ReconcileHeld(c, "dev");
            L(n).WakeHeld(c, "dev");
            L(n).WakeAll("dev");
            T.Eq(commits0 + 1, L(n).counters.commits, "still one commit");
            T.Eq(1, CustodyEpisodes(n, c).Count, "still one Custody episode");
            T.Eq(1, n.recorder.Count(EventKeys.CharacterFreed), "still one Freed event");
            T.Check(!WatchExists(n), "nobody is held: the custody watch is gone");
            T.Eq(0, n.physical.passCalls, "no Network PassToWorld");
        }

        private static void RecruitmentNeverStored()
        {
            TestNet n = new TestNet(9807);
            NetworkActor a = Solo(n, "recruit");
            KnownCharacter c = Self(n, a);
            Arrested(n, a, c);
            n.physical.Hold(c.pawn, ObservedKind.JoinedPlayer, HeldKind.PlayerColonist);
            RunWatch(n);
            T.Check(c.status == CharacterStatus.Defected && c.custody == CustodyState.OutOfCustody && c.heldBy == HeldKind.PlayerColonist, "recruited: Defected, OutOfCustody(PlayerColonist) (" + Rec(c) + ")");
            T.Eq(1, CustodyEpisodes(n, c).Count, "through one Custody episode");
            T.Eq(MemberOutcome.JoinedPlayer, CustodyEpisodes(n, c)[0].members[0].outcome, "JoinedPlayer");
            T.Eq(1, n.recorder.Count(EventKeys.CharacterDefected), "KnownCharacter.Defected once");
            T.Eq(1, n.recorder.Count(EventKeys.ContractorCasualties), "no second casualty report for a person already lost to the group");
            T.Check(a.IsActive, "the Solo actor is not ended or transformed by the Phase 3 bridge (O-20: permanent exit from NPC availability)");
            // Banished or released later: a free world pawn, but a recruited person is never stored back.
            n.physical.Free(c.pawn);
            RunWatch(n);
            T.Check(c.custody == CustodyState.OutOfCustody && c.heldBy == HeldKind.Unaffiliated && c.status == CharacterStatus.Defected, "never Stored (" + Rec(c) + ")");
            T.Eq(1, CustodyEpisodes(n, c).Count, "no second Custody episode");
            // VALIDATE refuses storing a Defected person, whatever asks.
            ReconciliationPlan p = new ReconciliationPlan { actor = a, now = n.clock.Now };
            p.Add(CommitOpKind.CharacterStored).character = c;
            string reason = null;
            try
            {
                ReconciliationPlanner.Validate(n.ctx, p);
            }
            catch (PlanInvalidException ex)
            {
                reason = ex.reasonKey;
            }
            T.Eq("DefectedTarget", reason, "VALIDATE refuses CharacterStored on a recruited person");
            // A recruitment observed during the mission itself ends the episode JoinedPlayer.
            NetworkActor b = Solo(n, "recruit-mission");
            KnownCharacter d = Self(n, b);
            PhysicalEpisode e = Capture(n, b, d, ObservedKind.JoinedPlayer, HeldKind.PlayerColonist);
            T.Check(e.IsComplete && e.members[0].outcome == MemberOutcome.JoinedPlayer && d.status == CharacterStatus.Defected && d.heldBy == HeldKind.PlayerColonist, "a mission JoinedPlayer: Defected, PlayerColonist");
        }

        private static void DeathWhileHeld()
        {
            TestNet n = new TestNet(9808);
            NetworkActor a = Solo(n, "dies");
            KnownCharacter c = Self(n, a);
            Arrested(n, a, c);
            int killed0 = n.recorder.Count(EventKeys.CharacterKilled);
            n.physical.Die(c.pawn);
            RunWatch(n);
            T.Check(c.status == CharacterStatus.Dead && c.custody == CustodyState.Released && c.diedTick >= 0, "death while held: Dead, Released (" + Rec(c) + ")");
            T.Check(c.heldBy == HeldKind.None && c.heldSinceTick == -1, "death clears current holder metadata");
            T.Check(!PhysicalLifecycleService.IsHeld(c), "a dead person is not in held custody");
            T.Check(!a.IsActive && a.endReasonKey == "Died", "the Solo ended with its person (" + a.status + ", " + a.endReasonKey + ")");
            T.Eq(killed0 + 1, n.recorder.Count(EventKeys.CharacterKilled), "KnownCharacter.Killed once");
            T.Eq(1, n.recorder.Count(EventKeys.ContractorEnded), "Contractor.Ended once");
            T.Check(!WatchExists(n) && L(n).HeldCount == 0, "nobody held any more: the custody watch is gone");
            string dead = Rec(c);
            // Physical death is final: a later "alive" observation (another mod's resurrection) never revives the record.
            n.physical.TokenOf(c.pawn).dead = false;
            n.physical.Script(c.pawn, O(ObservedKind.WorldFree, HeldKind.None, true));
            L(n).ReconcileHeld(c, "dev");
            for (int i = 0; i < 3; i++) RunWatch(n);
            T.Eq(dead, Rec(c), "monotonic: nothing changed after the death");
            PhysicalEpisode ignored;
            CommandResult plan = L(n).Plan(TheNetwork.Diagnostics.RuntimeTests.Suites.PhysicalRuntimeSuite.Request(a, new[] { c }), out ignored);
            T.Check(!plan.ok, "and no new episode can plan the dead person (" + plan + ")");
        }

        private static void SignalsOnlyWake()
        {
            TestNet n = new TestNet(9809);
            NetworkActor a = Solo(n, "signals");
            KnownCharacter c = Self(n, a);
            Arrested(n, a, c);
            string before = Rec(c);
            int commits0 = L(n).counters.commits, eps0 = L(n).counters.custodyEpisodes;
            for (int i = 0; i < 5; i++) L(n).WakeHeld(c, "Released");
            ScheduledJob job = n.scheduler.Find(PhysicalLifecycleService.CustodyWatchJob, PhysicalLifecycleService.CustodyWatchTarget);
            T.Check(job != null && job.dueTick == n.clock.Now + 1, "a wake-up only pulls the watch forward to the next tick");
            T.Eq(before, Rec(c), "a wake-up decides nothing");
            n.Advance(2);
            T.Check(Rec(c) == before && L(n).counters.commits == commits0 && L(n).counters.custodyEpisodes == eps0, "the observation found nothing new: nothing changed");
            // Dropped signals: vanilla frees the person and NO signal arrives. The bounded watch still converges.
            n.physical.Free(c.pawn);
            RunWatch(n);
            T.Check(c.custody == CustodyState.Stored, "converged without any signal (" + Rec(c) + ")");
            T.Eq(1, CustodyEpisodes(n, c).Count, "exactly once");
        }

        private static void WatchExistsIffHeld()
        {
            TestNet n = new TestNet(9810);
            NetworkActor a = Solo(n, "w1"), b = Solo(n, "w2");
            KnownCharacter c = Self(n, a), d = Self(n, b);
            L(n).OnLoaded();
            L(n).EnsureCustodyWatch();
            T.Check(!WatchExists(n), "nobody held: no custody watch (zero idle cost)");
            Arrested(n, a, c);
            Arrested(n, b, d);
            int jobs = 0;
            foreach (ScheduledJob j in n.scheduler.AllJobs) if (j.kind == PhysicalLifecycleService.CustodyWatchJob) jobs++;
            T.Eq(1, jobs, "two people held, ONE singleton watch");
            n.physical.Free(c.pawn);
            RunWatch(n);
            T.Check(WatchExists(n), "one still held: the watch stays");
            n.physical.Free(d.pawn);
            RunWatch(n);
            RunWatch(n);
            T.Check(!WatchExists(n) && L(n).HeldCount == 0, "nobody held: the watch removed itself");
            // A lost job is recreated from durable custody (load pass / validator), never a second one.
            NetworkActor w3 = Solo(n, "w3");
            Arrested(n, w3, Self(n, w3));
            n.scheduler.Cancel(PhysicalLifecycleService.CustodyWatchJob, PhysicalLifecycleService.CustodyWatchTarget);
            T.Check(!WatchExists(n), "the job was lost");
            L(n).OnLoaded();
            T.Check(WatchExists(n), "the load pass recreated it from durable custody");
        }

        private static void SaveLoadHeld()
        {
            TestNet n = new TestNet(9811);
            NetworkActor a = Solo(n, "saved");
            KnownCharacter c = Self(n, a);
            Arrested(n, a, c);
            string before = Rec(c);
            int creates = n.physical.creates;
            PhysicalLifecycleTests.SaveLoad(n);
            KnownCharacter lc = n.ctx.characters.Get(c.id);
            T.Eq(before, Rec(lc), "custody, holder, heldSinceTick, status and binding round-trip unchanged (no new persisted field)");
            T.Check(!AuthorityGate.CanSimulateAbstractly(lc), "still held after the load");
            L(n).OnLoaded();
            T.Eq(1, L(n).HeldCount, "the derived held index is rebuilt from durable custody");
            T.Check(WatchExists(n), "watched after the load");
            n.Advance(3 * PhysicalLifecycleService.CustodyWatchPeriod);
            T.Eq(before, Rec(lc), "the load generated, returned and decided nothing");
            n.physical.Free(lc.pawn);
            RunWatch(n);
            T.Check(lc.custody == CustodyState.Stored && AuthorityGate.CanSimulateAbstractly(lc), "and the same pawn returns later");
            T.Eq(creates, n.physical.creates, "nothing generated across the load");
        }

        private static void LostNeverRegenerated()
        {
            TestNet n = new TestNet(9812);
            NetworkActor a = Solo(n, "vanish");
            KnownCharacter c = Self(n, a);
            Arrested(n, a, c);
            n.physical.Vanish(c.pawn);
            RunWatch(n);
            T.Check(c.status == CharacterStatus.Lost && c.custody == CustodyState.Lost, "a held pawn discarded with no evidence ⇒ Lost (" + Rec(c) + ")");
            T.Check(c.heldBy == HeldKind.None && c.heldSinceTick == -1, "Lost clears current holder metadata");
            T.Check(!WatchExists(n) && L(n).HeldCount == 0 && !PhysicalLifecycleService.IsHeld(c), "Lost leaves the held index and watch");
            T.Eq(1, n.recorder.Count(EventKeys.CharacterVanished), "KnownCharacter.Vanished (the design's KnownCharacter.Lost) once");
            T.Eq(1, n.physical.creates, "never regenerated");
            T.Check(!a.IsActive && a.endReasonKey == "Lost", "the Solo ended as Lost");
        }

        private static void PlayerRecruitCannotRedeploy()
        {
            // Real, unspawned Pawn and player-faction shell; vanilla recruitment itself still needs RT-PHYX-021.
            // The fake port scripts ownership only. The production reconciliation and abstract checkout are real.
            foreach (ContractorForm form in new[] { ContractorForm.Solo, ContractorForm.Crew })
            {
                TestNet n = new TestNet(9830 + (int)form);
                NetworkActor a = PhysicalLifecycleTests.Make(n, form, "recruit-no-redeploy");
                OrganizationProfile org = a.Get<OrganizationProfile>();
                KnownCharacter c = org != null ? n.ctx.characters.Get(org.leader) : Self(n, a);
                ActorId originalOrg = c.org;
                CharacterId identity = c.id;
                PhysicalEpisode mission = PhysicalLifecycleTests.Begin(n, a, new[] { c });
                // Def constructors load Unity graphics; these minimal fixtures need only the pure category/player fields.
                ThingDef pawnDef = (ThingDef)FormatterServices.GetUninitializedObject(typeof(ThingDef));
                pawnDef.category = ThingCategory.Pawn;
                FactionDef playerDef = (FactionDef)FormatterServices.GetUninitializedObject(typeof(FactionDef));
                playerDef.isPlayer = true;
                Pawn pawn = new Pawn { thingIDNumber = c.pawn.thingIdNumber, def = pawnDef };
                Faction player = new Faction { def = playerDef };
                pawn.SetFactionDirect(player);
                c.pawn.pawn = pawn;
                mission.members[0].pawn.pawn = pawn;
                n.physical.Hold(c.pawn, ObservedKind.JoinedPlayer, HeldKind.PlayerColonist);
                L(n).Reconcile(mission, "watch");
                for (int i = 0; i < 3; i++) RunWatch(n);
                T.Check(mission.IsComplete && c.status == CharacterStatus.Defected && c.custody == CustodyState.OutOfCustody,
                    form + ": recruited, episode complete, vanilla authoritative");
                T.Check(ReferenceEquals(pawn, c.pawn.pawn) && ReferenceEquals(pawn.Faction, player) && pawn.Faction.IsPlayer,
                    form + ": the same real Pawn stays in the player faction");
                T.Check(c.id == identity && c.org == originalOrg, form + ": identity and previous affiliation kept, no Phase 4 transfer");
                T.Check(!AuthorityGate.CanSimulateAbstractly(c) && !c.IsAvailable, form + ": excluded from NPC person simulation and availability");
                if (form == ContractorForm.Solo)
                    T.Eq(Availability.Unavailable, n.ctx.Contractors.AvailabilityOf(a), "old Solo contractor unavailable");
                ForceCommitment next = n.ctx.Contractors.Checkout(a, new OperationId(n.ids.NextId()), 1f);
                T.Check(!next.characters.Contains(c.id), form + ": actual NPC checkout excludes the recruited person");
                PhysicalEpisode refused;
                CommandResult plan = L(n).Plan(TheNetwork.Diagnostics.RuntimeTests.Suites.PhysicalRuntimeSuite.Request(a, new[] { c }), out refused);
                T.Check(!plan.ok && refused == null, form + ": physical checkout also refuses the old NPC membership");
                T.Check(ReferenceEquals(c.pawn.pawn, pawn) && ReferenceEquals(pawn.Faction, player), form + ": all denied deployment attempts leave the player pawn untouched");
                T.Eq(1, n.physical.creates, form + ": no replacement pawn");
            }
        }

        private static void TerminalMetadataSaveLoad()
        {
            foreach (ObservedKind outcome in new[] { ObservedKind.Dead, ObservedKind.Gone, ObservedKind.WorldFree })
            {
                TestNet n = new TestNet(9840 + (int)outcome);
                NetworkActor a = Solo(n, "terminal-save-" + outcome);
                KnownCharacter c = Self(n, a);
                Arrested(n, a, c);
                if (outcome == ObservedKind.Dead) n.physical.Die(c.pawn);
                else if (outcome == ObservedKind.Gone) n.physical.Vanish(c.pawn);
                else n.physical.Free(c.pawn);
                RunWatch(n);
                CustodyState expected = outcome == ObservedKind.Dead ? CustodyState.Released : outcome == ObservedKind.Gone ? CustodyState.Lost : CustodyState.Stored;
                T.Eq(expected, c.custody, outcome + ": leaves held custody");
                T.Check(c.heldBy == HeldKind.None && c.heldSinceTick == -1, outcome + ": metadata cleared by the real transition");
                string record = Rec(c);
                PhysicalLifecycleTests.SaveLoad(n);
                KnownCharacter loaded = n.ctx.characters.Get(c.id);
                L(n).OnLoaded();
                T.Eq(record, Rec(loaded), outcome + ": corrected metadata round-trips through real Scribe");
                T.Check(loaded.heldBy == HeldKind.None && loaded.heldSinceTick == -1, outcome + ": save/load does not resurrect a holder");
                T.Check(!PhysicalLifecycleService.IsHeld(loaded) && L(n).HeldCount == 0 && !WatchExists(n), outcome + ": held index and watch remain empty after load");
            }
        }

        private static void RevertedClearsHolder()
        {
            foreach (bool bound in new[] { false, true })
            {
                TestNet n = new TestNet(bound ? 9851 : 9850);
                NetworkActor a = Solo(n, "never-placed");
                KnownCharacter c = Self(n, a);
                if (bound)
                {
                    PhysicalEpisode prior = PhysicalLifecycleTests.Begin(n, a, new[] { c });
                    n.physical.ExitNormally(c.pawn, 71);
                    L(n).Reconcile(prior, "watch");
                    T.Check(prior.IsComplete && c.custody == CustodyState.Stored, "a real earlier episode established the retained binding");
                }
                PhysicalEpisode e = PhysicalLifecycleTests.Begin(n, a, new[] { c }, materialize: false);
                // Stale metadata must not survive a never-placed episode's production revert.
                c.heldBy = HeldKind.OtherFaction;
                c.heldSinceTick = 12;
                L(n).Reconcile(e, "dev test-correction");
                T.Check(e.IsComplete && e.members[0].outcome == MemberOutcome.NeverPlaced, "never-placed episode reverted normally");
                T.Eq(bound ? CustodyState.Stored : CustodyState.Unmaterialized, c.custody, "revert preserves the existing bound/unbound distinction");
                T.Check(c.heldBy == HeldKind.None && c.heldSinceTick == -1, "revert clears holder metadata");
            }
        }

        private static void HolderChangesKeepSince()
        {
            TestNet n = new TestNet(9852);
            NetworkActor a = Solo(n, "continuous-held");
            KnownCharacter c = Self(n, a);
            Arrested(n, a, c);
            int since = c.heldSinceTick;
            n.physical.Hold(c.pawn, ObservedKind.HeldByPlayer, HeldKind.PlayerSlave);
            RunWatch(n);
            T.Eq(since, c.heldSinceTick, "prisoner to slave preserves the original heldSinceTick");
            T.Eq(HeldKind.PlayerSlave, c.heldBy, "holder updated to slave");
            n.physical.Hold(c.pawn, ObservedKind.JoinedPlayer, HeldKind.PlayerColonist);
            RunWatch(n); // CharacterHeld in a new Custody episode, rather than holder-only bookkeeping.
            T.Eq(since, c.heldSinceTick, "recruitment commit also preserves the continuous holding start");
            T.Check(c.status == CharacterStatus.Defected && c.heldBy == HeldKind.PlayerColonist, "now a player colonist");
            PhysicalLifecycleTests.SaveLoad(n);
            c = n.ctx.characters.Get(c.id);
            L(n).OnLoaded();
            T.Eq(since, c.heldSinceTick, "continuous start survives save/load");
            T.Eq(HeldKind.PlayerColonist, c.heldBy, "save/load keeps a genuinely live holder");
        }

        private static void NonHeldMetadataValidation()
        {
            foreach (CustodyState state in (CustodyState[])Enum.GetValues(typeof(CustodyState)))
            {
                if (state == CustodyState.OutOfCustody) continue;
                TestNet n = new TestNet(9860 + (int)state);
                NetworkActor a = Solo(n, "metadata-" + state);
                KnownCharacter c = Self(n, a);
                c.custody = state;
                c.heldBy = HeldKind.OtherFaction;
                c.heldSinceTick = 42;
                List<string> findings = new List<string>();
                EpisodeChecks.Report(n.ctx, findings);
                string report = string.Join("\n", findings.ToArray());
                T.Check(report.Contains(c.id + " is " + state + " but still records a vanilla holder"), state + ": stale holder diagnosed");
                T.Check(report.Contains(c.id + " is " + state + " but still records a live heldSinceTick"), state + ": stale timestamp diagnosed");
                T.Check(c.heldBy == HeldKind.OtherFaction && c.heldSinceTick == 42, "validation remains report-only");
                c.heldBy = HeldKind.None;
                findings.Clear();
                EpisodeChecks.Report(n.ctx, findings);
                T.Check(string.Join("\n", findings.ToArray()).Contains("still records a live heldSinceTick"), state + ": timestamp alone is diagnosed too");
            }
            // Old/default records do not require a migration or invent held history.
            KnownCharacter defaults = new KnownCharacter();
            T.Check(defaults.custody == CustodyState.Unmaterialized && defaults.heldBy == HeldKind.None && defaults.heldSinceTick == -1,
                "existing defaults already satisfy the non-held invariant");
        }

        private static void TerminalRollbackRestoresHolder()
        {
            foreach (bool died in new[] { true, false })
            {
                for (int k = 0; k <= 30; k++)
                {
                    TestNet n = new TestNet(9870 + k);
                    NetworkActor a = Solo(n, "terminal-rollback");
                    KnownCharacter c = Self(n, a);
                    Arrested(n, a, c);
                    string held = Rec(c).Replace("|ep 0|", "|ep ?|");
                    if (died) n.physical.Die(c.pawn); else n.physical.Vanish(c.pawn);
                    L(n).commitFaultAfter = k;
                    L(n).ReconcileHeld(c, "fault sweep");
                    PhysicalEpisode ce = CustodyEpisodes(n, c)[0];
                    if (ce.consequencesApplied) break;
                    T.Eq(held, Rec(c).Replace("|ep " + ce.id.Value + "|", "|ep ?|"), "k=" + k + ": fault restores status, custody and live holder exactly");
                    T.Check(c.custody == CustodyState.OutOfCustody && c.heldBy == HeldKind.PlayerPrisoner && c.heldSinceTick >= 0,
                        "k=" + k + ": rollback does not clear a genuinely held record");
                    L(n).Reconcile(ce, "retry");
                    T.Check(ce.IsComplete && c.heldBy == HeldKind.None && c.heldSinceTick == -1,
                        "k=" + k + ": retry commits terminal metadata cleanup exactly once");
                    T.Eq(1, n.recorder.Count(died ? EventKeys.CharacterKilled : EventKeys.CharacterVanished), "k=" + k + ": one terminal event");
                }
            }
        }

        private static void RemovalWithHeld()
        {
            TestNet n = new TestNet(9813);
            NetworkActor a = Solo(n, "held-before"), b = Solo(n, "held-open"), d = Solo(n, "present");
            KnownCharacter ca = Self(n, a), cb = Self(n, b), cd = Self(n, d);
            Arrested(n, a, ca);
            string heldBefore = Rec(ca);
            PhysicalEpisode eb = PhysicalLifecycleTests.Begin(n, b, new[] { cb });
            n.physical.Hold(cb.pawn, ObservedKind.Kidnapped, HeldKind.Kidnapped); // held, not yet observed by its episode
            PhysicalEpisode ed = PhysicalLifecycleTests.Begin(n, d, new[] { cd });
            int settled = L(n).SettleForRemoval();
            T.Eq(2, settled, "the two open episodes settled");
            T.Eq(heldBefore, Rec(ca), "a person already held is untouched by the settle (nothing invented)");
            T.Check(eb.state == EpisodeState.Closed && eb.members[0].outcome == MemberOutcome.Kidnapped && cb.heldBy == HeldKind.Kidnapped, "a held member is settled with its observed held outcome (" + eb.members[0].outcome + ")");
            T.Check(ed.members[0].outcome == MemberOutcome.Detached && cd.custody == CustodyState.OutOfCustody && cd.heldBy == HeldKind.Unknown, "a non-terminal member is Detached: OutOfCustody(Unknown)");
            T.Eq(3, L(n).HeldCount, "all three are held by vanilla; nobody is returned");
            T.Eq(0, n.physical.passCalls, "no Network PassToWorld");
            T.Eq(3, n.physical.creates, "nothing generated or deleted");
        }

        private static void ValidatorReports()
        {
            TestNet n = new TestNet(9814);
            NetworkActor a = Solo(n, "v1"), b = Solo(n, "v2"), d = Solo(n, "v3");
            KnownCharacter ca = Self(n, a), cb = Self(n, b), cd = Self(n, d);
            ca.custody = CustodyState.OutOfCustody;
            ca.heldBy = HeldKind.PlayerPrisoner;
            ca.heldSinceTick = -1; // held, with no binding and no since-tick
            cb.custody = CustodyState.Stored;
            cb.pawn = new PawnRef { thingIdNumber = 4242, defName = "Fake_Human", boundTick = 1 };
            cb.heldBy = HeldKind.Kidnapped; // Stored, yet a holder recorded
            cd.custody = CustodyState.Stored;
            cd.pawn = new PawnRef { thingIdNumber = 4343, defName = "Fake_Human", boundTick = 1 };
            cd.status = CharacterStatus.Defected; // a recruited person stored back
            PhysicalEpisode bad = new PhysicalEpisode { id = new EpisodeId(n.ids.NextId()), actor = a.id, purposeKey = CustodyRules.Purpose, state = EpisodeState.Open };
            bad.members.Add(new EpisodeMember { slot = 0 });
            bad.members.Add(new EpisodeMember { slot = 1 });
            n.ctx.episodes.Add(bad);
            string before = Rec(ca) + Rec(cb) + Rec(cd);
            List<string> findings = new List<string>();
            EpisodeChecks.Report(n.ctx, findings);
            string all = string.Join("\n", findings.ToArray());
            T.Check(all.Contains(ca.id + " is held by vanilla (OutOfCustody) with no pawn binding"), "held with no binding is reported");
            T.Check(all.Contains(ca.id + " is held by vanilla with no heldSinceTick"), "held with no since-tick is reported");
            T.Check(all.Contains(cb.id + " is Stored but still records a vanilla holder"), "a stored person with a holder is reported");
            T.Check(all.Contains(cd.id + " is Defected (recruited by the player) yet Stored"), "a recruited person stored back is reported");
            T.Check(all.Contains("Custody episode " + bad.id + " must hold exactly one named person"), "a malformed Custody episode is reported");
            T.Eq(before, Rec(ca) + Rec(cb) + Rec(cd), "the validator repaired nothing");
        }

        private static void KidnappedThenRecruited()
        {
            TestNet n = new TestNet(9815);
            NetworkActor a = Solo(n, "kidnap");
            KnownCharacter c = Self(n, a);
            PhysicalEpisode e = Capture(n, a, c, ObservedKind.Kidnapped, HeldKind.Kidnapped);
            T.Check(e.IsComplete && e.members[0].outcome == MemberOutcome.Kidnapped && c.status == CharacterStatus.Captured && c.heldBy == HeldKind.Kidnapped, "kidnapped: the episode closed once, Captured, OutOfCustody(Kidnapped)");
            T.Eq(1, n.recorder.Count(EventKeys.ContractorCaptured), "Contractor.Captured once (§ 15.3)");
            foreach (NetworkEvent ev in n.recorder.events)
            {
                ContractorEvent ce = ev as ContractorEvent;
                if (ce != null && ce.typeKey == EventKeys.ContractorCaptured) T.Check(!ce.contract.IsValid && ce.character == c.id && ce.reasonKey == "Kidnapped", "person-level, with the holder, never with a contract (no second contract letter)");
            }
            int eps = L(n).counters.custodyEpisodes;
            // Vanilla's ≈ 30-day "the captor recruits a kidnapped pawn": a free world pawn of the captor's permanent faction.
            n.physical.JoinOtherFaction(c.pawn);
            RunWatch(n);
            T.Check(c.custody == CustodyState.OutOfCustody && c.heldBy == HeldKind.OtherFaction && c.status == CharacterStatus.Captured, "held by OtherFaction, still Captured: never a free return (" + Rec(c) + ")");
            T.Eq(eps, L(n).counters.custodyEpisodes, "a change of holder is bookkeeping only");
            for (int i = 0; i < 3; i++) RunWatch(n);
            T.Check(c.custody == CustodyState.OutOfCustody, "never Stored, however long it is watched");
        }

        private static void CaravanAndTransport()
        {
            TestNet n = new TestNet(9816);
            NetworkActor a = Solo(n, "caravan");
            KnownCharacter c = Self(n, a);
            PhysicalEpisode e = Capture(n, a, c, ObservedKind.InCaravan, HeldKind.PlayerCaravan);
            T.Check(e.IsComplete && e.members[0].outcome == MemberOutcome.HeldByPlayer && c.heldBy == HeldKind.PlayerCaravan, "carried away in a player caravan: held by the player (" + Rec(c) + ")");
            T.Eq(CharacterStatus.Active, c.status, "carried is not captured: the story status is unchanged");
            T.Eq(0, n.recorder.Count(EventKeys.CharacterCapturedByPlayer), "no capture event for a carry");
            n.physical.Hold(c.pawn, ObservedKind.InTransport, HeldKind.Transport);
            RunWatch(n);
            T.Eq(HeldKind.Transport, c.heldBy, "then in a transport: the holder only");
            int casualties = n.recorder.Count(EventKeys.ContractorCasualties);
            n.physical.Hold(c.pawn, ObservedKind.HeldByPlayer, HeldKind.PlayerPrisoner);
            RunWatch(n);
            T.Check(c.status == CharacterStatus.Captured && c.heldBy == HeldKind.PlayerPrisoner, "then arrested: a capture, through a Custody episode (" + Rec(c) + ")");
            T.Eq(1, n.recorder.Count(EventKeys.CharacterCapturedByPlayer), "KnownCharacter.CapturedByPlayer once");
            T.Eq(casualties, n.recorder.Count(EventKeys.ContractorCasualties), "a Custody episode is a person-level fact: no group casualty report");
            n.physical.Free(c.pawn);
            RunWatch(n);
            T.Check(c.custody == CustodyState.Stored && c.status == CharacterStatus.Active, "freed: Stored, Active");
            // A pod in flight during a mission is transit: the member stays Present.
            NetworkActor b = Solo(n, "pod");
            KnownCharacter d = Self(n, b);
            PhysicalEpisode pod = PhysicalLifecycleTests.Begin(n, b, new[] { d });
            n.physical.Hold(d.pawn, ObservedKind.InTransport, HeldKind.Transport);
            L(n).Reconcile(pod, "watch");
            T.Check(pod.state == EpisodeState.Open && d.custody == CustodyState.Deployed, "a mission member in a travelling pod stays Present (§ 12.3)");
        }

        private static void CustodyCommitFault()
        {
            TestNet n = new TestNet(9817);
            NetworkActor a = Solo(n, "fault");
            KnownCharacter c = Self(n, a);
            Arrested(n, a, c);
            string held = Rec(c).Replace("|ep 0|", "|ep ?|");
            n.physical.Free(c.pawn);
            L(n).commitFaultAfter = 0;
            RunWatch(n);
            List<PhysicalEpisode> ce = CustodyEpisodes(n, c);
            T.Eq(1, ce.Count, "the Custody episode was opened");
            T.Check(ce[0].state == EpisodeState.Open && !ce[0].consequencesApplied && L(n).counters.commitFailures == 1, "its commit threw and was restored: nothing applied");
            T.Check(c.custody == CustodyState.OutOfCustody && c.episode == ce[0].id && !AuthorityGate.CanSimulateAbstractly(c), "still held, owned by its Custody episode, blocked");
            T.Eq(held, Rec(c).Replace("|ep " + ce[0].id.Value + "|", "|ep ?|"), "the person's record is exactly as before (but for the link)");
            n.Advance(2 * PhysicalLifecycleService.WatchPeriod);
            T.Check(ce[0].IsComplete && c.custody == CustodyState.Stored && AuthorityGate.CanSimulateAbstractly(c), "the episode watch retried: applied once, completed");
            T.Eq(1, CustodyEpisodes(n, c).Count, "no second Custody episode");
            T.Eq(1, n.recorder.Count(EventKeys.CharacterFreed), "one Freed event");
            // The fault sweep over a held commit: a throw after every Applier step restores the person exactly.
            for (int k = 0; k <= 30; k++)
            {
                TestNet m = new TestNet(9900 + k);
                NetworkActor s = Solo(m, "sweep" + k);
                KnownCharacter p = Self(m, s);
                PhysicalEpisode e = PhysicalLifecycleTests.Begin(m, s, new[] { p });
                string beforeCommit = Rec(p);
                m.physical.Hold(p.pawn, ObservedKind.HeldByPlayer, HeldKind.PlayerPrisoner);
                L(m).commitFaultAfter = k;
                L(m).Reconcile(e, "sweep");
                if (e.consequencesApplied) break; // k past the last step: the fault was never reached
                T.Check(Rec(p) == beforeCommit && !e.consequencesApplied && e.state == EpisodeState.Open, "k=" + k + ": a throw restores the person exactly (" + Rec(p) + ")");
                L(m).Reconcile(e, "retry");
                T.Check(e.IsComplete && p.custody == CustodyState.OutOfCustody && p.status == CharacterStatus.Captured, "k=" + k + ": the retry applies once");
                T.Eq(1, m.recorder.Count(EventKeys.CharacterCapturedByPlayer), "k=" + k + ": one capture event");
            }
        }

        private static void OrgLeaderHeldThenDies()
        {
            TestNet n = new TestNet(9818);
            NetworkActor org = PhysicalLifecycleTests.Make(n, ContractorForm.Company, "org-held");
            OrganizationProfile prof = org.Get<OrganizationProfile>();
            KnownCharacter leader = n.ctx.characters.Get(prof.leader), lt = PhysicalLifecycleTests.Others(n, org)[0];
            PhysicalEpisode e = PhysicalLifecycleTests.Begin(n, org, new[] { leader, lt });
            n.physical.Hold(leader.pawn, ObservedKind.HeldByPlayer, HeldKind.PlayerPrisoner);
            n.physical.ExitNormally(lt.pawn, 71);
            L(n).Reconcile(e, "watch");
            T.Check(e.IsComplete && leader.custody == CustodyState.OutOfCustody && leader.status == CharacterStatus.Captured, "the leader is held and Captured");
            T.Check(prof.leader != leader.id, "a captured leader is succeeded at the capture (the shared rule)");
            T.Eq(1, n.recorder.Count(EventKeys.LeaderSucceeded), "one succession");
            int casualties = n.recorder.Count(EventKeys.ContractorCasualties);
            ContractorSimulation sim = org.Get<ContractorSimulation>();
            string morale = sim.morale.descriptor + "|" + sim.morale.descriptorTick;
            n.physical.Die(leader.pawn);
            RunWatch(n);
            T.Check(leader.status == CharacterStatus.Dead && leader.custody == CustodyState.Released, "the old leader died while held");
            T.Eq(1, n.recorder.Count(EventKeys.LeaderSucceeded), "no second succession (no longer the leader)");
            T.Eq(casualties, n.recorder.Count(EventKeys.ContractorCasualties), "no second casualty report");
            T.Eq(morale, sim.morale.descriptor + "|" + sim.morale.descriptorTick, "no second morale shift");
            T.Check(org.IsActive, "the organization continues");
        }

        private static void BoundedWork()
        {
            TestNet n = new TestNet(9819);
            for (int i = 0; i < 40; i++) Solo(n, "bystander" + i);
            List<KnownCharacter> held = new List<KnownCharacter>();
            for (int i = 0; i < 3; i++)
            {
                NetworkActor a = Solo(n, "held" + i);
                KnownCharacter c = Self(n, a);
                Arrested(n, a, c);
                held.Add(c);
            }
            int observed = L(n).counters.custodyObserved, observes = n.physical.observes;
            Stopwatch sw = Stopwatch.StartNew();
            L(n).CustodyWatchRun(new ScheduledJob { kind = PhysicalLifecycleService.CustodyWatchJob, target = PhysicalLifecycleService.CustodyWatchTarget });
            sw.Stop();
            T.Eq(observed + 3, L(n).counters.custodyObserved, "one run observes exactly the 3 held people, not the " + n.ctx.characters.characters.Count + " characters");
            T.Eq(observes + 3, n.physical.observes, "three observations through the port, nothing else");
            Console.WriteLine("  custody watch: 3 held among " + n.ctx.characters.characters.Count + " characters: " + sw.Elapsed.TotalMilliseconds.ToString("0.000") + " ms per run");
        }

        private static void ScanCustody()
        {
            string rules = PhysicalLifecycleTests.Code(PhysicalLifecycleTests.Src("Domain/Physical/CustodyRules.cs"));
            foreach (string forbidden in new[] { "Verse", "RimWorld", "Find.", "ctx.", "scheduler", "bus", "Port", "Scribe", "NetLog", "Rand" })
                T.Check(!rules.Contains(forbidden), "the custody rules are pure: no " + forbidden);
            T.Eq(5, SaveMigrations.Current, "save format 5: no persisted field was added");
            string actor = PhysicalLifecycleTests.Code(PhysicalLifecycleTests.Src("Domain/Actors/NetworkActor.cs"));
            int looks = Regex.Matches(actor.Substring(actor.IndexOf("public sealed class KnownCharacter", StringComparison.Ordinal)), @"\b(Scribe_\w+|NetScribe)\.Look").Count;
            T.Eq(20, looks, "KnownCharacter persists exactly the fields it did in Phase 3.1");
            T.Eq(9, (int)HeldKind.Unknown, "HeldKind values unchanged");
            T.Eq(10, (int)MemberOutcome.Detached, "MemberOutcome values unchanged");
            T.Eq(13, (int)ObservedKind.ReservationBroken, "ObservedKind values unchanged");
            foreach (string f in PhysicalLifecycleTests.AllSources()) T.Check(!File.ReadAllText(f).Contains("HarmonyLib"), "no Harmony (" + Path.GetFileName(f) + ")");
            string lifecycle = PhysicalLifecycleTests.Code(PhysicalLifecycleTests.Src("Domain/Physical/PhysicalLifecycleService.cs"));
            string watch = lifecycle.Substring(lifecycle.IndexOf("public void CustodyWatchRun(", StringComparison.Ordinal));
            watch = watch.Substring(0, watch.IndexOf("public CustodyDecision ReconcileHeld(", StringComparison.Ordinal));
            T.Check(watch.Contains("HeldIds()") && !watch.Contains("characters.characters") && !watch.Contains("Find."), "the watch iterates the derived held index only (no store, pawn or world scan)");
        }

        private static string Doc(string rel)
        {
            string root = Environment.GetEnvironmentVariable("THENETWORK_REPO") ?? ".";
            return File.ReadAllText(Path.Combine(root, rel));
        }

        /// <summary>
        /// The 3.2A documentation states the honest status (IMPLEMENTED / HEADLESS VALIDATED, never owner runtime validated), records S11's FAIL
        /// and S21's PARTIAL, fixes the stale "PR #10 unmerged" text, and every new anchor it links resolves.
        /// </summary>
        private static void DocsStatus()
        {
            string readme = Doc("README.md"), phases = Doc("docs/IMPLEMENTATION_PHASES.md"), lifecycle = Doc("docs/PHYSICAL_LIFECYCLE.md"), testing = Doc("docs/RUNTIME_TESTING.md");
            string risks = Doc("docs/RISKS.md"), decisions = Doc("docs/DECISIONS.md"), spikes = Doc("docs/spikes/README.md");
            string s11 = Doc("docs/spikes/S11-rescue-site-holder.md"), s21 = Doc("docs/spikes/S21-observation-completeness.md");
            // The status, honestly.
            T.Check(lifecycle.Contains("**Phase 3.2A Held Custody — IMPLEMENTED / HEADLESS VALIDATED.**"), "PHYSICAL_LIFECYCLE states the 3.2A status in the agreed words");
            foreach (KeyValuePair<string, string> kv in new[] { new KeyValuePair<string, string>("README", readme), new KeyValuePair<string, string>("IMPLEMENTATION_PHASES", phases),
                new KeyValuePair<string, string>("PHYSICAL_LIFECYCLE", lifecycle), new KeyValuePair<string, string>("RUNTIME_TESTING", testing), new KeyValuePair<string, string>("DECISIONS", decisions) })
            {
                T.Check(kv.Value.Contains("IMPLEMENTED / HEADLESS VALIDATED"), kv.Key + " records 3.2A as IMPLEMENTED / HEADLESS VALIDATED");
                T.Check(!Regex.IsMatch(kv.Value, @"3\.2A[^\n]*IMPLEMENTED AND OWNER RUNTIME VALIDATED") && !Regex.IsMatch(kv.Value, @"(?i)3\.2A[^\n]{0,40}\bis owner runtime validated"), kv.Key + " never claims 3.2A is owner runtime validated");
                T.Check(!kv.Value.Contains("remains unmerged pending final review") && !kv.Value.Contains("remains open and unmerged pending final review"), kv.Key + " no longer says PR #10 is unmerged");
                T.Check(!Regex.IsMatch(kv.Value, @"(?i)Phase 3\.2 (is )?(implemented|started|begun|in progress)"), kv.Key + " does not claim all of Phase 3.2");
            }
            T.Check(lifecycle.Contains("**Phase 3.1 Controlled Physical Episode — IMPLEMENTED AND OWNER RUNTIME VALIDATED (PASS).**"), "the Phase 3.1 banner is unchanged");
            // S11 and S21, recorded.
            T.Check(s11.Contains("**Verdict: FAIL — SOURCE AUDIT**") && s11.Contains("rescue-site implementation is STOPPED") && s11.Contains("narrowest viable alternative"), "the S11 record: FAIL, stopped, with the alternative");
            foreach (string evidence in new[] { "GenStep_DownedRefugee.cs", "GenStep_PrisonerWillingToJoin.cs", "JobDriver_OfferHelp.cs", "SymbolResolver_Stockpile.cs", "ThingOwner.cs", "SitePart.cs", "Integration/SiteAdapter.cs" })
                T.Check(s11.Contains(evidence), "the S11 record cites " + evidence);
            T.Check(s11.Contains("NOT\nimplemented") || s11.Contains("NOT implemented") || s11.Contains("**not\nimplemented**") || s11.Contains("**not implemented**") || s11.Contains("(documented, NOT implemented)"), "the alternative is documented, not implemented");
            T.Check(s21.Contains("**Verdict: PARTIAL") && s21.Contains("NOT RUN by the owner"), "the S21 record: PARTIAL, not run by the owner");
            T.Check(spikes.Contains("(S11-rescue-site-holder.md)") && spikes.Contains("(S21-observation-completeness.md)") && spikes.Contains("**FAIL — SOURCE AUDIT**"), "the spikes README links both records and defines the verdict");
            // The decision, the invariants, the risks and the runtime ids.
            T.Check(decisions.Contains("### ADR-056 · Held custody: the custody watch, Custody episodes and M1 for held people (Phase 3.2A)"), "ADR-056 exists");
            foreach (string inv in new[] { "P3-INV-040", "P3-INV-041", "P3-INV-042", "P3-INV-043" }) T.Check(lifecycle.Contains("| **" + inv + "** |"), inv + " is in the invariant table");
            foreach (string r in new[] { "R-49", "R-50", "R-51" }) T.Check(risks.Contains("| " + r + " |") && risks.Contains("## " + r + " ·"), r + " is registered");
            foreach (string id in new[] { "RT-PHYX-020", "RT-PHYX-021", "RT-PHYX-022", "RT-PHYX-023", "RT-PHYX-024", "RT-PHYX-025", "RT-PHYS-031", "RT-PHYS-032", "RT-PHYS-033" })
                T.Check(Regex.IsMatch(testing, @"\| " + id + @" \|"), "RUNTIME_TESTING documents " + id);
            T.Check(testing.Contains("| RT-PHYX-009 | Unsupported custody: dev arrest quarantines — **RETIRED in 3.2A**"), "RT-PHYX-009 is documented as retired");
            // Anchors.
            T.Check(lifecycle.Contains("## Appendix L: Phase 3.2A as built (held custody)"), "Appendix L exists (anchor appendix-l-phase-32a-as-built-held-custody)");
            T.Check(testing.Contains("## 18. Phase 3.2A: held custody"), "RUNTIME_TESTING § 18 exists (anchor 18-phase-32a-held-custody)");
            T.Check(!Regex.IsMatch(testing, @"\(#18-(?!phase-32a-held-custody\))"), "RUNTIME_TESTING's own § 18 links use the real anchor");
            foreach (string doc in new[] { readme, phases, lifecycle, testing, risks, decisions, s21 })
            {
                if (doc.Contains("#appendix-l-")) T.Check(doc.Contains("#appendix-l-phase-32a-as-built-held-custody") && !Regex.IsMatch(doc, @"#appendix-l-(?!phase-32a-as-built-held-custody\))"), "every Appendix L link uses the real anchor");
                T.Check(!Regex.IsMatch(doc, @"RUNTIME_TESTING\.md#18-(?!phase-32a-held-custody\))"), "every link to RUNTIME_TESTING § 18 uses the real anchor");
                if (doc.Contains("#adr-056")) T.Check(!Regex.IsMatch(doc, @"#adr-056(?!--held-custody-the-custody-watch-custody-episodes-and-m1-for-held-people-phase-32a\))"), "every ADR-056 link uses the real anchor");
            }
            T.Eq(5, SaveMigrations.Current, "save format 5, as every document says");
        }

        // ================================================================== the rescue handoff (domain half) and FOLLOW-UP

        private sealed class RescueFixture
        {
            public TestNet n;
            public Contract contract;
            public Operation op;
            public NetworkActor actor;
            public PhysicalEpisode e;
            public List<KnownCharacter> people = new List<KnownCharacter>();
        }

        /// <summary>
        /// The first seed from <paramref name="from"/> whose Troubled operation leaves the contractor ACTIVE with a living named person (the
        /// abstract resolver may kill a Solo's only person, which ends the Solo before any rescue: not this fixture's case). Deterministic.
        /// </summary>
        private static int RescueSeed(int from, ContractorForm form)
        {
            for (int seed = from; seed < from + 40; seed++)
            {
                RescueFixture probe = Rescue(seed, form, false, false);
                if (probe.op != null && probe.op.status == OpStatus.Troubled && probe.actor.IsActive && probe.people.Count > 0) return seed;
            }
            return from;
        }

        /// <summary>A real awarded contract whose operation is Troubled (the abstract Phase 2 path), then handed to a Rescue episode.</summary>
        private static RescueFixture Rescue(int seed, ContractorForm form, bool plan = true, bool assert = true)
        {
            RescueFixture f = new RescueFixture { n = ProcurementTests.World(0, seed) };
            TestNet n = f.n;
            NetworkActor fixer = ProcurementTests.Fixer(n);
            f.actor = ProcurementTests.Reliable(n, form);
            ProcurementDevOverrides.forceBand = OutcomeBand.Failure;
            ProcurementDevOverrides.forceTroubled = SubStatus.Missing;
            ProcurementDevOverrides.forceSecured = 0;
            f.contract = ProcurementTests.Awarded(n, fixer, f.actor);
            f.op = ProcurementTests.Op(n, f.contract);
            ProcurementTests.RunUntil(n, () => f.op.status == OpStatus.Troubled || f.contract.IsTerminal);
            ProcurementDevOverrides.Clear();
            if (assert) T.Eq(OpStatus.Troubled, f.op.status, "the operation is Troubled");
            foreach (CharacterId id in f.op.characters)
            {
                KnownCharacter k = n.ctx.characters.Get(id);
                if (k != null && k.IsAlive) f.people.Add(k);
            }
            if (!plan) return f;
            EpisodeRequest r = new EpisodeRequest { actor = f.actor.id, purposeKey = "Rescue", where = new TileRef { tileId = 42 }, mapId = 7 };
            r.cause.operation = f.op.id;
            r.cause.contract = f.contract.id;
            foreach (KnownCharacter k in f.people) r.named.Add(k.id);
            CommandResult res = n.ctx.Lifecycle.Plan(r, out f.e);
            T.Check(res.ok, "the rescue episode is planned (" + res + ")");
            if (f.e != null) n.ctx.Lifecycle.Materialize(f.e);
            return f;
        }

        private static void HandoffSuspendsDeadline()
        {
            RescueFixture f = Rescue(9830, ContractorForm.Team);
            TestNet n = f.n;
            T.Check(f.people.Count > 0, "the operation has named people (" + f.people.Count + ")");
            T.Eq(OpStatus.Physical, f.op.status, "handed over: OpStatus.Physical");
            T.Check(f.op.physicalEpisode == f.e.id && f.op.physicalResolution == PhysicalResolution.None, "linked to the rescue episode");
            T.Check(!n.scheduler.Has(OperationService.TroubledJob, f.op.id.Value), "the Troubled deadline job is cancelled");
            n.AdvanceTo(f.op.troubledDeadlineTick + 2 * Ticks.PerDay);
            T.Eq(OpStatus.Physical, f.op.status, "past the old deadline: still Physical, nothing resolved it abstractly");
            T.Eq(ContractStatus.Troubled, f.contract.status, "the contract is still Troubled");
            n.ctx.Operations.Abort(f.op, "Test");
            T.Eq(OpStatus.Physical, f.op.status, "an abort is deferred to the episode");
            foreach (KnownCharacter k in f.people) n.physical.ExitNormally(k.pawn, 61);
            n.Advance(2 * PhysicalLifecycleService.WatchPeriod);
            T.Check(f.e.IsComplete && f.op.physicalResolution == PhysicalResolution.Found, "physical truth decided: Found");
            T.Check(f.op.status == OpStatus.Resolved && f.op.outcomeApplied, "resolved through FOLLOW-UP, forces returned");
            T.Check(f.contract.status != ContractStatus.Troubled && f.contract.causeKey != Causes.ContractorLost, "the contract left Troubled through the found branch, never as written off (" + f.contract.status + "/" + f.contract.causeKey + ")");
        }

        private static void NoDoubleResolution()
        {
            RescueFixture f = Rescue(9831, ContractorForm.Team);
            TestNet n = f.n;
            ScheduledJob stale = new ScheduledJob { kind = OperationService.TroubledJob, target = f.op.id.Value };
            n.ctx.Operations.TroubledDeadline(stale); // a stale deadline firing while the episode owns the operation
            T.Eq(OpStatus.Physical, f.op.status, "a stale Troubled deadline does nothing while the episode owns the operation");
            T.Check(!f.op.careerOutcomeApplied && !f.op.outcomeApplied, "and applied nothing");
            foreach (KnownCharacter k in f.people) n.physical.Die(k.pawn);
            n.Advance(2 * PhysicalLifecycleService.WatchPeriod);
            T.Check(f.e.IsComplete && f.op.physicalResolution == PhysicalResolution.WrittenOff && f.op.IsFinished, "physical truth: written off");
            long classified = f.actor.Get<ContractorSimulation>().career.Classified;
            int failed = n.recorder.Count(EventKeys.ContractFailed);
            n.ctx.Operations.TroubledDeadline(stale);
            n.ctx.Operations.DevAdvance(f.op);
            n.ctx.Operations.OnPhysicalResolved(f.op);
            n.ctx.Lifecycle.FinishPending(f.e);
            T.Eq(classified, f.actor.Get<ContractorSimulation>().career.Classified, "one career result: the abstract path cannot resolve it again");
            T.Eq(failed, n.recorder.Count(EventKeys.ContractFailed), "the contract failed once");
            T.Eq(1L, classified, "exactly one result on the record");
        }

        /// <summary>Everything the follow-up can change, as one comparable line (the control run and every faulted-then-retried run must agree).</summary>
        private static string Outcome(RescueFixture f)
        {
            TestNet n = f.n;
            Operation op = f.op;
            Contract c = f.contract;
            NetworkActor a = f.actor;
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            OrganizationProfile org = a.Get<OrganizationProfile>();
            List<string> parts = new List<string>
            {
                "op " + op.status + "/" + op.phase + " finished " + op.IsFinished + " forces " + op.outcomeApplied + " career " + op.careerOutcomeApplied + " " + op.physicalResolution + " steps " + op.physicalSteps,
                "contract " + c.status + "/" + c.subStatus + "/" + c.causeKey + " charged " + n.pay.charged + " refunded " + n.pay.refunded,
                "actor " + a.status + "/" + a.endReasonKey + " commitments " + sim.commitments.Count + " record " + sim.career.Classified + "/" + sim.career.failures + " score " + a.reputation.score,
                "org " + (org == null ? "-" : org.Healthy + "/" + org.Wounded + "/" + org.Committed),
                "spatial " + sim.spatial.status + "/" + sim.spatial.operation.Value + "/" + sim.spatial.anchor?.tileId,
                "episode complete " + f.e.IsComplete + " follow-up " + f.e.followUpApplied
            };
            foreach (KnownCharacter k in f.people) parts.Add(k.id + " " + k.status + "/" + k.custody);
            SortedDictionary<string, int> keys = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (NetworkEvent ev in n.recorder.events)
            {
                int x;
                keys.TryGetValue(ev.typeKey, out x);
                keys[ev.typeKey] = x + 1;
            }
            foreach (KeyValuePair<string, int> kv in keys) parts.Add(kv.Key + "×" + kv.Value);
            return string.Join("\n", parts.ToArray());
        }

        /// <summary>
        /// The FOLLOW-UP interruption matrix (§ 15.5, a 3.2 deliverable): for every k, a throw after k of the branch's sub-steps, then a retry. The
        /// interrupted state keeps OpStatus.Physical and followUpApplied false with exactly k sub-steps recorded; the retried end state equals the
        /// uninterrupted control in everything the follow-up touches (forces, recovery, career, finish, actor end, contract, money, spatial, events).
        /// </summary>
        private static void Matrix(int seed, ContractorForm form, Action<RescueFixture> physical, PhysicalResolution expected, int subSteps)
        {
            RescueFixture control = Rescue(seed, form);
            physical(control);
            control.n.ctx.Lifecycle.Reconcile(control.e, "control");
            T.Check(control.e.IsComplete && control.op.physicalResolution == expected, "control: completed with " + expected + " (" + control.op.physicalResolution + ")");
            string want = Outcome(control);
            for (int k = 0; k < subSteps; k++)
            {
                RescueFixture f = Rescue(seed, form);
                physical(f);
                f.n.ctx.Operations.physicalStepFaultAfter = k;
                f.n.ctx.Lifecycle.Reconcile(f.e, "faulted");
                int bits = 0;
                for (int b = f.op.physicalSteps; b != 0; b >>= 1) bits += b & 1;
                T.Check(f.e.consequencesApplied && f.e.releaseApplied && !f.e.followUpApplied, "k=" + k + ": FOLLOW-UP interrupted (its marker, not the operation, says so)");
                T.Eq(OpStatus.Physical, f.op.status, "k=" + k + ": OpStatus.Physical stays authoritative until the last step");
                T.Eq(k, bits, "k=" + k + ": exactly " + k + " sub-step(s) recorded");
                f.n.ctx.Operations.TroubledDeadline(new ScheduledJob { kind = OperationService.TroubledJob, target = f.op.id.Value });
                T.Eq(OpStatus.Physical, f.op.status, "k=" + k + ": the abstract deadline cannot take it meanwhile");
                f.n.ctx.Lifecycle.FinishPending(f.e);
                f.n.ctx.Lifecycle.FinishPending(f.e);
                T.Check(f.e.IsComplete, "k=" + k + ": completed on retry");
                string got = Outcome(f);
                T.Check(got == want, "k=" + k + ": the retried end state equals the uninterrupted one" + (got == want ? "" : "\n--- want\n" + want + "\n--- got\n" + got));
                int steps = f.op.physicalSteps;
                f.n.ctx.Operations.OnPhysicalResolved(f.op);
                T.Eq(steps, f.op.physicalSteps, "k=" + k + ": a re-run repeats nothing");
            }
        }

        private static void FollowUpMatrixFound()
        {
            Matrix(9840, ContractorForm.Team, f => { foreach (KnownCharacter k in f.people) f.n.physical.ExitNormally(k.pawn, 61); }, PhysicalResolution.Found, 3);
        }

        private static void FollowUpMatrixWrittenOff()
        {
            Matrix(9841, ContractorForm.Team, f => { foreach (KnownCharacter k in f.people) f.n.physical.Die(k.pawn); }, PhysicalResolution.WrittenOff, 5);
        }

        private static void FollowUpMatrixSoloHeld()
        {
            // A Solo whose person ends the rescue held by another faction: written off, and the existing rule ends a Solo that cannot work, once.
            int seed = RescueSeed(9842, ContractorForm.Solo);
            Matrix(seed, ContractorForm.Solo, f => { foreach (KnownCharacter k in f.people) f.n.physical.Hold(k.pawn, ObservedKind.HeldByOther, HeldKind.OtherFaction); }, PhysicalResolution.WrittenOff, 5);
            RescueFixture g = Rescue(seed, ContractorForm.Solo);
            foreach (KnownCharacter k in g.people) g.n.physical.Hold(k.pawn, ObservedKind.HeldByOther, HeldKind.OtherFaction);
            g.n.ctx.Lifecycle.Reconcile(g.e, "check");
            T.Check(!g.actor.IsActive && g.actor.endReasonKey == "LostContact", "the Solo ended LostContact once (" + g.actor.status + ", " + g.actor.endReasonKey + ")");
            T.Eq(1, g.n.recorder.Count(EventKeys.ContractorEnded), "one Contractor.Ended");
            KnownCharacter p = g.people[0];
            T.Check(p.custody == CustodyState.OutOfCustody && p.heldBy == HeldKind.OtherFaction, "the person stays held by vanilla, watched (" + Rec(p) + ")");
        }
    }
}
