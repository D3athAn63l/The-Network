using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using TheNetwork.Diagnostics.RuntimeTests;
using TheNetwork.Diagnostics.RuntimeTests.Suites;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Physical;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Settings;

namespace TheNetwork.Tests
{
    /// <summary>
    /// The Phase 3.1 post-review correction pass (PR #10), proven headlessly over the PRODUCTION lifecycle and the scriptable fake port:
    ///
    /// <list type="bullet">
    /// <item>Fix 2, a failed placement never strands a bound person: after binding, a pawn that is spawned, dead, gone, held or unobservable is
    /// never reduced to NeverPlaced (PHYSICAL_LIFECYCLE § 7.3 rule 7).</item>
    /// <item>Fix 3, a retained named person observed as an actual Free world pawn is the M1 reservation failing: never Returned, quarantined, never
    /// quietly repaired (ADR-053).</item>
    /// <item>Fix 4, the first gender and age follow the PERSON (world seed and CharacterId), never the episode (§ 6.3).</item>
    /// <item>Fix 5, a truthful-aging step that threw is uncertain: nothing is replayed, falsified or placed (§ 6.4).</item>
    /// </list>
    ///
    /// The real adapter cannot run headlessly (it needs RimWorld objects): its decisions are the pure rules tested here, and the owner's
    /// physical tier (RT-PHYX-*) is the runtime proof, which this pass does NOT claim.
    /// </summary>
    public static class Phase31CorrectionTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("Phys31Fix.Fix2_PlacementRulesMatrix", PlacementMatrix));
            t.Add(new KeyValuePair<string, Action>("Phys31Fix.Fix2_SpawnedAfterNominalFailureIsPresent", SpawnedIsPresent));
            t.Add(new KeyValuePair<string, Action>("Phys31Fix.Fix2_DeadAfterFailedPlacementIsKilledOnce", DeadIsKilledOnce));
            t.Add(new KeyValuePair<string, Action>("Phys31Fix.Fix2_DiscardedBoundPawnIsLostNeverNeverPlaced", DiscardedIsLost));
            t.Add(new KeyValuePair<string, Action>("Phys31Fix.Fix2_UnspawnedUnheldUndiscardedIsStillNeverPlaced", StillNeverPlaced));
            t.Add(new KeyValuePair<string, Action>("Phys31Fix.Fix2_HeldOrUnobservableFailsClosed", HeldFailsClosed));
            t.Add(new KeyValuePair<string, Action>("Phys31Fix.Fix2_RematerializingADiscardedBindingIsLostNotNeverPlaced", RematerializeDiscarded));
            t.Add(new KeyValuePair<string, Action>("Phys31Fix.Fix2_NoPathCreatesASecondPawn", NoSecondPawn));
            t.Add(new KeyValuePair<string, Action>("Phys31Fix.Fix2_RemovalSettleClassifiesABoundMemberFromObservation", RemovalSettle));
            t.Add(new KeyValuePair<string, Action>("Phys31Fix.Fix3_WorldPawnRulesMatrix", WorldRulesMatrix));
            t.Add(new KeyValuePair<string, Action>("Phys31Fix.Fix3_OwnReservationWithExitEvidenceIsReturned", OwnReservationReturns));
            t.Add(new KeyValuePair<string, Action>("Phys31Fix.Fix3_ActualFreeOnARetainedPersonFailsClosed", ActualFreeFailsClosed));
            t.Add(new KeyValuePair<string, Action>("Phys31Fix.Fix3_OrdinaryFreeStillReturnsWhereNoReservationExists", OrdinaryFreeStillReturns));
            t.Add(new KeyValuePair<string, Action>("Phys31Fix.Fix3_ReleaseProvesTheReservationAndNeverRepairsIt", ReleaseNeverRepairs));
            t.Add(new KeyValuePair<string, Action>("Phys31Fix.Fix3_Scan_NoQuietHealAndNoDirectFreeMapping", ScanNoQuietHeal));
            t.Add(new KeyValuePair<string, Action>("Phys31Fix.Fix4_FirstIdentityIsPersonDeterministic", IdentityDeterministic));
            t.Add(new KeyValuePair<string, Action>("Phys31Fix.Fix4_IdentityIgnoresEpisodeMapSlotTimeAndCareer", IdentityIgnoresEpisode));
            t.Add(new KeyValuePair<string, Action>("Phys31Fix.Fix4_AdultAgeWindowIsDerivedFromTheRace", AdultWindow));
            t.Add(new KeyValuePair<string, Action>("Phys31Fix.Fix4_Scan_IdentityOnlyThroughVanillaRequestInputs", ScanIdentity));
            t.Add(new KeyValuePair<string, Action>("Phys31Fix.Fix5_AgingStepThatThrewIsUncertainAndNeverReplayed", AgingUncertain));
            t.Add(new KeyValuePair<string, Action>("Phys31Fix.Fix5_AgingFailureBeforeAnyChangeMayBeRetried", AgingRetryable));
            t.Add(new KeyValuePair<string, Action>("Phys31Fix.Fix5_UncertainAgeDetachesAtRemovalNeverNeverPlaced", AgingSettle));
            t.Add(new KeyValuePair<string, Action>("Phys31Fix.Fix5_Scan_CatchUpClaimsNothingItCannotProve", ScanAging));
            t.Add(new KeyValuePair<string, Action>("Phys31Fix.Docs_S31IsRecordedAsAcceptedAndNothingStaleRemains", DocsConsistent));
        }

        // ================================================================== helpers

        private static PhysicalLifecycleService L(TestNet n) { return PhysicalLifecycleTests.L(n); }

        private static NetworkActor Solo(TestNet n, string id) { return PhysicalLifecycleTests.Make(n, ContractorForm.Solo, id); }

        private static PhysicalEpisode Begin(TestNet n, NetworkActor a, KnownCharacter c) { return PhysicalLifecycleTests.Begin(n, a, new[] { c }); }

        private static bool Blocked(KnownCharacter c) { return !AuthorityGate.CanSimulateAbstractly(c); }

        private static PhysicalObservation Obs(ObservedKind k) { return PhysicalObservation.Of(k); }

        private static string Code(string rel) { return PhysicalLifecycleTests.Code(PhysicalLifecycleTests.Src(rel)); }

        /// <summary>The text of one method: from its signature up to the next marker (a source scan helper, not a parser).</summary>
        private static string Body(string code, string from, string to)
        {
            int a = code.IndexOf(from, StringComparison.Ordinal);
            T.Check(a >= 0, "found " + from);
            if (a < 0) return "";
            int b = code.IndexOf(to, a + from.Length, StringComparison.Ordinal);
            return b < 0 ? code.Substring(a) : code.Substring(a, b - a);
        }

        // ================================================================== Fix 2: a failed placement never strands a bound person

        private static void PlacementMatrix()
        {
            // The pure rule: only POSITIVE observation decides. (observation, § 7.5 check) → verdict.
            Action<ObservedKind, PassToWorldCheck, PlacementVerdictKind, string> row = (k, pass, want, what) =>
                T.Eq(want, PlacementRules.Classify(Obs(k), pass).kind, what + " (" + k + ", " + pass + ")");
            row(ObservedKind.Spawned, PassToWorldCheck.Spawned, PlacementVerdictKind.Present, "A: actually spawned is Present");
            row(ObservedKind.Spawned, PassToWorldCheck.Allowed, PlacementVerdictKind.Present, "A: spawned wins over a stale check");
            row(ObservedKind.Dead, PassToWorldCheck.Dead, PlacementVerdictKind.Present, "dead where it stood is Present (the ordinary path records the death)");
            row(ObservedKind.Gone, PassToWorldCheck.Unknown, PlacementVerdictKind.Lost, "B: discarded after binding is Lost");
            row(ObservedKind.Unknown, PassToWorldCheck.Allowed, PlacementVerdictKind.NeverPlaced, "C: alive, unspawned, undiscarded, held by nobody");
            row(ObservedKind.Unknown, PassToWorldCheck.AlreadyInWorldPawns, PlacementVerdictKind.NeverPlaced, "C: a stored world pawn that never left WorldPawns");
            row(ObservedKind.WorldFree, PassToWorldCheck.AlreadyInWorldPawns, PlacementVerdictKind.NeverPlaced, "C: a stored, reserved world pawn");
            ObservedKind[] held = { ObservedKind.HeldByPlayer, ObservedKind.JoinedPlayer, ObservedKind.Kidnapped, ObservedKind.HeldByOther, ObservedKind.InCaravan, ObservedKind.InTransport };
            foreach (ObservedKind k in held) row(k, PassToWorldCheck.Allowed, PlacementVerdictKind.FailClosed, "D: held is never NeverPlaced");
            row(ObservedKind.Unknown, PassToWorldCheck.Held, PlacementVerdictKind.FailClosed, "D: another vanilla owner holds it");
            row(ObservedKind.WorldOther, PassToWorldCheck.AlreadyInWorldPawns, PlacementVerdictKind.FailClosed, "D: a world pawn in another situation");
            row(ObservedKind.ReservationBroken, PassToWorldCheck.AlreadyInWorldPawns, PlacementVerdictKind.FailClosed, "D: a broken reservation is never NeverPlaced");
            row(ObservedKind.Unknown, PassToWorldCheck.Unknown, PlacementVerdictKind.FailClosed, "E: unknown is never NeverPlaced");
            row(ObservedKind.None, PassToWorldCheck.Unknown, PlacementVerdictKind.FailClosed, "E: nothing observed is never NeverPlaced");
            T.Eq(PlacementVerdictKind.FailClosed, PlacementRules.Classify(null, PassToWorldCheck.Allowed).kind, "E: no observation at all fails closed");
            T.Eq(PlacementVerdictKind.FailClosed, PlacementRules.Classify(Obs((ObservedKind)200), PassToWorldCheck.Allowed).kind, "E: an unrecognised kind fails closed");
            // Exhaustive guard: across every (kind, check) pair, NeverPlaced is returned ONLY for the C rows.
            foreach (ObservedKind k in Enum.GetValues(typeof(ObservedKind)))
            {
                foreach (PassToWorldCheck p in Enum.GetValues(typeof(PassToWorldCheck)))
                {
                    PlacementVerdict v = PlacementRules.Classify(Obs(k), p);
                    if (v.kind != PlacementVerdictKind.NeverPlaced) continue;
                    bool kindOk = k == ObservedKind.Unknown || k == ObservedKind.None || k == ObservedKind.WorldFree;
                    bool passOk = p == PassToWorldCheck.Allowed || p == PassToWorldCheck.AlreadyInWorldPawns;
                    T.Check(kindOk && passOk, "NeverPlaced only on positive evidence of an alive, unspawned, unheld pawn (" + k + ", " + p + ")");
                }
            }
        }

        private static void SpawnedIsPresent()
        {
            foreach (bool throws in new[] { false, true })
            {
                string s = throws ? "a placement that THREW" : "a placement that returned false";
                TestNet n = new TestNet(9600 + (throws ? 1 : 0));
                NetworkActor a = Solo(n, "f2spawn" + throws);
                KnownCharacter c = PhysicalLifecycleTests.Self(n, a);
                n.physical.failPlace = true;
                n.physical.failPlaceThrows = throws;
                n.physical.onPlaceFailed = t => { t.spawned = true; t.mapId = 7; }; // vanilla left the pawn spawned anyway
                PhysicalEpisode e = Begin(n, a, c);
                n.physical.failPlace = false;
                n.physical.failPlaceThrows = false;
                n.physical.onPlaceFailed = null;
                T.Eq(EpisodeState.Open, e.state, s + ": a spawned bound pawn opens the episode");
                T.Eq(MemberState.Present, e.members[0].state, s + ": the member is Present");
                T.Check(!e.consequencesApplied && e.closeReasonKey == null, s + ": nothing was closed as NeverPlaced");
                T.Eq(CustodyState.Deployed, c.custody, s + ": custody stays Deployed");
                T.Check(Blocked(c), s + ": the person stays physical");
                T.Eq(1, n.physical.creates, s + ": no second pawn");
                T.Eq(0, n.physical.passCalls + n.physical.passRejected, s + ": nothing was passed to the world");
                T.Eq(1, L(n).counters.placementRecovered, s + ": recorded as a recovered placement");
                if (throws) T.Check(e.lastError != null && e.lastError.Contains("Injected fault"), s + ": the failure is diagnosed (" + e.lastError + ")");
                // From there it is an ordinary episode: the pawn leaves normally and the episode completes as Returned, exactly once.
                n.physical.ExitNormally(c.pawn, 31);
                L(n).Reconcile(e, "test");
                T.Check(e.IsComplete && e.members[0].outcome == MemberOutcome.Returned, s + ": completes as Returned like any placed pawn");
                T.Eq(0, n.physical.passCalls, s + ": a returned pawn is never passed by the Network");
                T.Eq(1, n.physical.creates, s + ": still one pawn for life");
            }
        }

        private static void DeadIsKilledOnce()
        {
            TestNet n = new TestNet(9602);
            NetworkActor a = Solo(n, "f2dead");
            KnownCharacter c = PhysicalLifecycleTests.Self(n, a);
            n.physical.failPlace = true;
            n.physical.onPlaceFailed = t => t.dead = true; // SpawnSetup replaced a dead pawn with a corpse
            PhysicalEpisode e = Begin(n, a, c);
            n.physical.failPlace = false;
            n.physical.onPlaceFailed = null;
            T.Eq(EpisodeState.Open, e.state, "a dead bound pawn is positively physical: Present, never NeverPlaced");
            L(n).Reconcile(e, "test");
            T.Check(e.IsComplete && e.members[0].outcome == MemberOutcome.Killed, "the ordinary observation records the death (" + e.members[0] + ")");
            T.Eq(CharacterStatus.Dead, c.status, "Dead");
            T.Eq(CustodyState.Released, c.custody, "custody Released");
            T.Eq(1, n.physical.creates, "no second pawn");
            L(n).Reconcile(e, "again");
            T.Eq(CharacterStatus.Dead, c.status, "death is monotonic");
        }

        private static void DiscardedIsLost()
        {
            foreach (bool throws in new[] { false, true })
            {
                string s = throws ? "a placement that THREW" : "a placement that returned false";
                TestNet n = new TestNet(9610 + (throws ? 1 : 0));
                NetworkActor a = Solo(n, "f2gone" + throws);
                KnownCharacter c = PhysicalLifecycleTests.Self(n, a);
                n.physical.failPlace = true;
                n.physical.failPlaceThrows = throws;
                n.physical.onPlaceFailed = t => t.gone = true; // SpawnSetup discarded an invalid pawn
                PhysicalEpisode e = Begin(n, a, c);
                n.physical.failPlace = false;
                n.physical.failPlaceThrows = false;
                n.physical.onPlaceFailed = null;
                T.Eq(EpisodeState.Closed, e.state, s + ": the episode closed");
                T.Check(e.closeReasonKey != ReconciliationPlanner.CloseNeverPlaced, s + ": NOT as NeverPlaced (" + e.closeReasonKey + ")");
                T.Eq(MemberOutcome.Lost, e.members[0].outcome, s + ": the member is Lost");
                T.Eq(CharacterStatus.Lost, c.status, s + ": the person is Lost (the existing Lost semantics)");
                T.Eq(CustodyState.Lost, c.custody, s + ": custody Lost");
                T.Check(a.status != ActorStatus.Active, s + ": a Solo's end follows its person's loss");
                T.Check(c.pawn != null && c.pawn.IsBound, s + ": the write-once binding stays (one pawn for life)");
                T.Check(e.IsComplete && !c.episode.IsValid, s + ": the episode COMPLETED: no RELEASE is stranded waiting on a pawn that no longer exists");
                T.Eq(0, n.physical.passCalls + n.physical.passRejected, s + ": the Network never tried to pass a discarded pawn");
                T.Eq(0, L(n).counters.passRefused, s + ": so no RELEASE precondition was ever refused");
                T.Eq(1, n.physical.creates, s + ": nothing was regenerated");
                T.Eq(1, L(n).counters.placementLost, s + ": recorded");
                PhysicalEpisode again;
                CommandResult r = L(n).Plan(PhysicalRuntimeSuite.Request(a, new[] { c }), out again);
                T.Check(!r.ok, s + ": a lost person cannot be planned again (" + r + ")");
                T.Eq(1, n.physical.creates, s + ": and no pawn is made to replace the lost one");
            }
        }

        private static void StillNeverPlaced()
        {
            // The legitimate case: the placement failed and the bound pawn is positively alive, unspawned, undiscarded and held by nobody.
            TestNet n = new TestNet(9620);
            NetworkActor a = Solo(n, "f2never");
            KnownCharacter c = PhysicalLifecycleTests.Self(n, a);
            n.physical.failPlace = true;
            PhysicalEpisode e = Begin(n, a, c);
            n.physical.failPlace = false;
            T.Eq(MemberOutcome.NeverPlaced, e.members[0].outcome, "never placed");
            T.Eq(ReconciliationPlanner.CloseNeverPlaced, e.closeReasonKey, "closed NeverPlaced");
            T.Check(e.IsComplete, "released and complete");
            T.Eq(1, n.physical.TokenOf(c.pawn).passedToWorld, "the Network passes it to the world exactly once (§ 7.5, after the precondition held)");
            T.Eq(CustodyState.Stored, c.custody, "the bound person is Stored");
            T.Check(!Blocked(c), "and abstract again");
            T.Eq(0, L(n).counters.placementRecovered + L(n).counters.placementLost + L(n).counters.placementFailClosed, "no recovery, loss or fail-closed was involved");
            T.Eq(1, n.physical.creates, "one pawn");
        }

        private static void HeldFailsClosed()
        {
            // D: held by a vanilla owner after a failed placement.
            TestNet n = new TestNet(9630);
            NetworkActor a = Solo(n, "f2held");
            KnownCharacter c = PhysicalLifecycleTests.Self(n, a);
            n.physical.failPlace = true;
            n.physical.onPlaceFailed = t => t.held = true;
            PhysicalEpisode e = Begin(n, a, c);
            n.physical.failPlace = false;
            n.physical.onPlaceFailed = null;
            FakePhysicalWorldPort.Token tok = n.physical.TokenOf(c.pawn);
            T.Eq(EpisodeState.Quarantined, e.state, "a held bound pawn quarantines the episode");
            T.Check(e.quarantineKey != null && e.quarantineKey.StartsWith(PhysicalLifecycleService.QuarantinePlacement + ":", StringComparison.Ordinal), "with the placement diagnosis (" + e.quarantineKey + ")");
            T.Check(!e.consequencesApplied && e.closeReasonKey == null, "nothing was closed or committed");
            T.Eq(CustodyState.Deployed, c.custody, "custody stays Deployed");
            T.Eq(e.id, c.episode, "the episode link stays");
            T.Check(Blocked(c), "the person stays blocked from abstraction");
            T.Check(c.pawn.IsBound, "the binding is preserved");
            T.Eq(0, n.physical.passCalls + n.physical.passRejected, "the held pawn is never passed to the world");
            T.Eq(1, n.physical.creates, "no second pawn");
            // Watching, waking and time decide nothing while it is held.
            int commits = L(n).counters.commits;
            n.Advance(PhysicalLifecycleService.WatchPeriod * 4);
            L(n).WakeAll("test");
            T.Check(e.state == EpisodeState.Quarantined && L(n).counters.commits == commits && Blocked(c), "retries keep it quarantined");
            // A LATER positive observation (the holder let go: alive, unspawned, held by nobody) closes it through the ordinary commit.
            tok.held = false;
            L(n).Reconcile(e, "released");
            T.Check(e.IsComplete, "once positively observed passable the episode completes");
            T.Eq(ReconciliationPlanner.CloseNeverPlaced, e.closeReasonKey, "as NeverPlaced (nobody was ever placed)");
            T.Eq(1, tok.passedToWorld, "and the pawn is passed exactly once");
            T.Eq(1, n.physical.creates, "still one pawn");

            // E: the bound pawn cannot be observed at all.
            TestNet n2 = new TestNet(9631);
            NetworkActor b = Solo(n2, "f2unobs");
            KnownCharacter k = PhysicalLifecycleTests.Self(n2, b);
            n2.physical.failPlace = true;
            n2.physical.ThrowOn("observe", 1);
            PhysicalEpisode e2 = Begin(n2, b, k);
            n2.physical.failPlace = false;
            T.Eq(EpisodeState.Quarantined, e2.state, "an unobservable bound pawn fails closed");
            T.Check(e2.quarantineKey != null && e2.quarantineKey.Contains("could not be observed"), "diagnosed (" + e2.quarantineKey + ")");
            T.Check(Blocked(k) && k.custody == CustodyState.Deployed && n2.physical.creates == 1 && n2.physical.passCalls == 0, "blocked, Deployed, nothing created or passed");
        }

        private static void RematerializeDiscarded()
        {
            TestNet n = new TestNet(9640);
            NetworkActor a = Solo(n, "f2remat");
            KnownCharacter c = PhysicalLifecycleTests.Self(n, a);
            PhysicalEpisode e1 = Begin(n, a, c);
            n.physical.ExitNormally(c.pawn, 40);
            L(n).Reconcile(e1, "test");
            T.Check(e1.IsComplete && c.custody == CustodyState.Stored, "the first visit completed; the person is Stored");
            n.clock.Now += 5 * Ticks.PerDay;
            n.physical.Vanish(c.pawn); // the stored pawn was discarded by something else
            PhysicalEpisode e2;
            T.Check(L(n).Plan(PhysicalRuntimeSuite.Request(a, new[] { c }), out e2).ok, "planned");
            L(n).Materialize(e2);
            T.Eq(EpisodeState.Closed, e2.state, "closed");
            T.Eq(MemberOutcome.Lost, e2.members[0].outcome, "the stored person whose pawn is gone is Lost, never 'never placed'");
            T.Check(e2.closeReasonKey != ReconciliationPlanner.CloseNeverPlaced, "not NeverPlaced (" + e2.closeReasonKey + ")");
            T.Eq(CharacterStatus.Lost, c.status, "the person is Lost");
            T.Check(e2.IsComplete, "complete: nothing is stranded");
            T.Eq(1, L(n).counters.unresolvedBindings, "the unresolved binding was diagnosed");
            T.Eq(1, n.physical.creates, "and nothing was regenerated");
            T.Eq(0, n.physical.passCalls, "nothing was passed");
        }

        private static void NoSecondPawn()
        {
            string[] scenarios = { "spawned", "dead", "gone", "held", "never" };
            for (int i = 0; i < scenarios.Length; i++)
            {
                TestNet n = new TestNet(9650 + i);
                NetworkActor a = Solo(n, "f2none" + i);
                KnownCharacter c = PhysicalLifecycleTests.Self(n, a);
                n.physical.failPlace = true;
                n.physical.onPlaceFailed = t =>
                {
                    if (scenarios[i] == "spawned") t.spawned = true;
                    else if (scenarios[i] == "dead") t.dead = true;
                    else if (scenarios[i] == "gone") t.gone = true;
                    else if (scenarios[i] == "held") t.held = true;
                };
                PhysicalEpisode e = Begin(n, a, c);
                n.physical.failPlace = false;
                n.physical.onPlaceFailed = null;
                int thing = c.pawn.thingIdNumber;
                // Time, every kind of wake-up, a save and a load, and a new plan attempt: none of it can make a second pawn.
                n.Advance(PhysicalLifecycleService.WatchPeriod * 3);
                L(n).WakeAll("test");
                PhysicalLifecycleTests.SaveLoad(n);
                L(n).OnLoaded();
                n.Advance(PhysicalLifecycleService.WatchPeriod * 3);
                KnownCharacter lc = n.ctx.characters.Get(c.id);
                PhysicalEpisode ignored;
                L(n).Plan(PhysicalRuntimeSuite.Request(n.ctx.actors.Get(a.id), new[] { lc }), out ignored);
                T.Eq(1, n.physical.creates, scenarios[i] + ": exactly one pawn was ever created for the person");
                T.Eq(thing, lc.pawn.thingIdNumber, scenarios[i] + ": the binding is the same pawn");
                T.Eq(1, n.physical.tokens.Count, scenarios[i] + ": and the world holds one token");
            }
        }

        private static void RemovalSettle()
        {
            // Prepare-for-removal settles a Planned, Open or Quarantined episode through the same commit. A BOUND member that was never reported
            // placed is classified from observation there too, never assumed NeverPlaced.
            // (a) held after a failed placement ⇒ the episode is Quarantined; the settle detaches it (nothing invented), not NeverPlaced.
            TestNet n = new TestNet(9690);
            NetworkActor a = Solo(n, "f2settle-held");
            KnownCharacter c = PhysicalLifecycleTests.Self(n, a);
            n.physical.failPlace = true;
            n.physical.onPlaceFailed = t => t.held = true;
            PhysicalEpisode e = Begin(n, a, c);
            n.physical.failPlace = false;
            n.physical.onPlaceFailed = null;
            T.Eq(EpisodeState.Quarantined, e.state, "(a) quarantined");
            L(n).SettleForRemoval();
            T.Eq(MemberOutcome.Detached, e.members[0].outcome, "(a) a held bound member is Detached at removal, never NeverPlaced");
            T.Eq(CustodyState.OutOfCustody, c.custody, "(a) and nothing is invented about the person");
            T.Eq(0, n.physical.passCalls, "(a) and the held pawn is never passed");

            // (b) a stored person whose pawn vanished, planned again but not yet materialized ⇒ Lost, positively.
            TestNet n2 = new TestNet(9691);
            NetworkActor b = Solo(n2, "f2settle-gone");
            KnownCharacter k = PhysicalLifecycleTests.Self(n2, b);
            PhysicalEpisode first = Begin(n2, b, k);
            n2.physical.ExitNormally(k.pawn, 40);
            L(n2).Reconcile(first, "test");
            n2.clock.Now += 4 * Ticks.PerDay;
            PhysicalEpisode planned;
            T.Check(L(n2).Plan(PhysicalRuntimeSuite.Request(b, new[] { k }), out planned).ok, "(b) planned again");
            n2.physical.Vanish(k.pawn);
            L(n2).SettleForRemoval();
            T.Eq(MemberOutcome.Lost, planned.members[0].outcome, "(b) a bound member whose pawn is gone is Lost at removal, never NeverPlaced");
            T.Eq(CharacterStatus.Lost, k.status, "(b) the person is Lost");
            T.Eq(1, n2.physical.creates, "(b) nothing was regenerated");

            // (c) a stored person, planned again, pawn alive and unheld ⇒ NeverPlaced is correct (positive evidence).
            TestNet n3 = new TestNet(9692);
            NetworkActor d = Solo(n3, "f2settle-alive");
            KnownCharacter q = PhysicalLifecycleTests.Self(n3, d);
            PhysicalEpisode first3 = Begin(n3, d, q);
            n3.physical.ExitNormally(q.pawn, 41);
            L(n3).Reconcile(first3, "test");
            n3.clock.Now += 4 * Ticks.PerDay;
            PhysicalEpisode planned3;
            T.Check(L(n3).Plan(PhysicalRuntimeSuite.Request(d, new[] { q }), out planned3).ok, "(c) planned again");
            L(n3).SettleForRemoval();
            T.Eq(MemberOutcome.NeverPlaced, planned3.members[0].outcome, "(c) a bound member that is alive, unspawned and unheld is NeverPlaced");
            T.Eq(CustodyState.Stored, q.custody, "(c) the person is Stored again");
        }

        // ================================================================== Fix 3: an actual Free on a retained person is a bug, not a return

        private static void WorldRulesMatrix()
        {
            Func<bool, WorldSituation, bool, ObservedKind> kind = (retained, s, other) =>
                WorldPawnRules.KindOf(new WorldPawnFacts { retained = retained, situation = s, otherQuestReserves = other });
            // A retained named person: only the Network's OWN reservation (and no other quest) is a return.
            T.Eq(ObservedKind.WorldFree, kind(true, WorldSituation.ReservedByQuest, false), "retained + reserved by our registry ⇒ WorldFree (Returned is valid with exit evidence)");
            T.Eq(ObservedKind.ReservationBroken, kind(true, WorldSituation.Free, false), "retained + actual Free ⇒ ReservationBroken (M1 failed), NOT WorldFree");
            T.Eq(ObservedKind.ReservationBroken, kind(true, WorldSituation.Free, true), "retained + Free is broken whatever else is said");
            T.Eq(ObservedKind.WorldOther, kind(true, WorldSituation.ReservedByQuest, true), "retained + another quest also reserves it ⇒ WorldOther (never Returned)");
            T.Eq(ObservedKind.WorldOther, kind(true, WorldSituation.Other, false), "retained in another situation ⇒ WorldOther");
            T.Eq(ObservedKind.WorldOther, kind(true, WorldSituation.None, false), "retained but not a world pawn here ⇒ WorldOther");
            // A pawn the registry never covers keeps the ordinary reading.
            T.Eq(ObservedKind.WorldFree, kind(false, WorldSituation.Free, false), "not retained + Free ⇒ WorldFree (vanilla owns it: the ordinary Free semantics)");
            T.Eq(ObservedKind.WorldOther, kind(false, WorldSituation.ReservedByQuest, false), "not retained + reserved ⇒ WorldOther");
            T.Eq(ObservedKind.WorldOther, kind(false, WorldSituation.Other, false), "not retained + another situation ⇒ WorldOther");
            // The decision table is closed: the ONLY way to WorldFree for a retained person is our own reservation.
            foreach (WorldSituation s in Enum.GetValues(typeof(WorldSituation)))
            {
                foreach (bool other in new[] { false, true })
                {
                    bool free = kind(true, s, other) == ObservedKind.WorldFree;
                    T.Eq(s == WorldSituation.ReservedByQuest && !other, free, "retained: WorldFree only for our own reservation alone (" + s + ", other quest " + other + ")");
                }
            }
            // Returned requires exit evidence AND WorldFree: a broken reservation can never decide Returned.
            bool unsup;
            T.Eq(MemberOutcome.Pending, ReconciliationPlanner.Decide(new PhysicalObservation { kind = ObservedKind.ReservationBroken, exitEvidence = true }, out unsup), "even with exit evidence a broken reservation is Pending, never Returned");
            T.Eq(MemberOutcome.Returned, ReconciliationPlanner.Decide(new PhysicalObservation { kind = ObservedKind.WorldFree, exitEvidence = true }, out unsup), "own reservation + exit evidence ⇒ Returned");
        }

        private static void OwnReservationReturns()
        {
            TestNet n = new TestNet(9660);
            NetworkActor a = Solo(n, "f3ok");
            KnownCharacter c = PhysicalLifecycleTests.Self(n, a);
            PhysicalEpisode e = Begin(n, a, c);
            n.physical.ExitNormally(c.pawn, 44);
            L(n).Reconcile(e, "test");
            T.Check(e.IsComplete && e.members[0].outcome == MemberOutcome.Returned, "own ReservedByQuest + exit evidence ⇒ Returned");
            T.Eq(ObservedKind.WorldFree, e.members[0].observed, "decided from WorldFree");
            T.Eq(0, n.physical.passCalls, "no Network PassToWorld for a pawn vanilla already passed");
            T.Eq(CustodyState.Stored, c.custody, "Stored");
            T.Check(!Blocked(c), "abstract again after RELEASE");
        }

        private static void ActualFreeFailsClosed()
        {
            TestNet n = new TestNet(9661);
            NetworkActor a = Solo(n, "f3free");
            KnownCharacter c = PhysicalLifecycleTests.Self(n, a);
            PhysicalEpisode e = Begin(n, a, c);
            int commits = L(n).counters.commits, retains = n.physical.retains;
            n.physical.ExitFree(c.pawn, 44); // vanilla passed the pawn while the registry did not reserve it
            bool committed = L(n).Reconcile(e, "test");
            T.Check(!committed, "nothing was committed");
            T.Eq(EpisodeState.Quarantined, e.state, "the episode is quarantined: fail closed");
            T.Check(e.quarantineKey != null && e.quarantineKey.StartsWith(PhysicalLifecycleService.QuarantineReservation + ":", StringComparison.Ordinal), "with an explicit diagnosis (" + e.quarantineKey + ")");
            T.Check(e.lastError != null && e.lastError.Contains("Free") && e.lastError.Contains("M1"), "and a plain-words error naming the violation (" + e.lastError + ")");
            T.Check(!e.consequencesApplied && e.members[0].outcome == MemberOutcome.Pending, "NOT Returned: the member is undecided and the commit never ran");
            T.Eq(commits, L(n).counters.commits, "no commit");
            T.Eq(CustodyState.Deployed, c.custody, "custody is not changed to Stored");
            T.Eq(e.id, c.episode, "the episode link stays");
            T.Check(Blocked(c), "authority does not reopen: abstract simulation stays closed");
            T.Eq(1, L(n).counters.reservationBroken, "counted");
            // Nothing quietly repairs it: no registry is ensured, no reservation is proven, no pawn is passed, and time changes nothing.
            T.Eq(retains, n.physical.retains, "RELEASE never ran, so no reservation was 'ensured' or proven");
            T.Check(!n.physical.TokenOf(c.pawn).registryReserves, "the registry was not repaired by the Network");
            n.Advance(PhysicalLifecycleService.WatchPeriod * 6);
            L(n).WakeAll("again");
            T.Check(e.state == EpisodeState.Quarantined && L(n).counters.commits == commits && Blocked(c) && c.custody == CustodyState.Deployed, "repeated watches and wake-ups change nothing");
            T.Eq(0, n.physical.passCalls, "and pass nothing");
            PhysicalEpisode second;
            T.Check(!L(n).Plan(PhysicalRuntimeSuite.Request(a, new[] { c }), out second).ok, "the person cannot be planned into another episode");
            // An OWNER repairing the registry by hand is the only thing that can change the observation; the next ordinary observation then
            // decides from what it sees (the established quarantine rule: closed by a later successful reconcile, never by inference).
            n.physical.RepairReservation(c.pawn);
            L(n).Reconcile(e, "after the owner's repair");
            T.Check(e.IsComplete && e.members[0].outcome == MemberOutcome.Returned, "only a positive observation of the Network's own reservation can end it");
            T.Eq(1, n.physical.creates, "one pawn throughout");
        }

        private static void OrdinaryFreeStillReturns()
        {
            // No M1 retained pawn exists for an anonymous slot: ordinary Free semantics still apply and the existing logic still works.
            TestNet n = new TestNet(9662);
            NetworkActor org = PhysicalLifecycleTests.Make(n, ContractorForm.Company, "f3anon");
            PhysicalEpisode e = PhysicalLifecycleTests.Begin(n, org, null, 2);
            foreach (EpisodeMember m in e.members)
            {
                FakePhysicalWorldPort.Token t = n.physical.TokenOf(m.pawn);
                T.Check(!t.character.IsValid, "an anonymous token is not a retained person");
                n.physical.ExitNormally(m.pawn, 50);
            }
            L(n).Reconcile(e, "test");
            T.Check(e.IsComplete, "an anonymous slot that left as an ordinary Free world pawn still reconciles");
            foreach (EpisodeMember m in e.members) T.Eq(MemberOutcome.Returned, m.outcome, "Returned from ordinary WorldFree (" + m + ")");
            T.Eq(0, L(n).counters.reservationBroken, "no violation is reported for a pawn the registry never covered");
        }

        private static void ReleaseNeverRepairs()
        {
            TestNet n = new TestNet(9663);
            NetworkActor a = Solo(n, "f3release");
            KnownCharacter c = PhysicalLifecycleTests.Self(n, a);
            PhysicalEpisode e = Begin(n, a, c);
            FakePhysicalWorldPort.Token tok = n.physical.TokenOf(c.pawn);
            n.physical.ExitNormally(c.pawn, 45);
            // The reservation was in force at the observation and fails before RELEASE proves it (a vanished registry quest).
            L(n).commitBoundary = where => { if (where == "before") n.physical.BreakReservation(c.pawn); };
            L(n).Reconcile(e, "test");
            L(n).commitBoundary = null;
            EpisodeMember m = e.members[0];
            T.Check(e.state == EpisodeState.Closed && e.consequencesApplied, "the Returned commit happened");
            T.Check(!e.releaseApplied, "but RELEASE did not complete: it cannot turn the violation into a silent success");
            T.Eq((byte)1, m.releaseStep, "the cursor stays on the reservation proof (Normalize done, EnsureRetained refused)");
            T.Check(e.lastError != null && e.lastError.Contains("not proven"), "diagnosed (" + e.lastError + ")");
            T.Check(Blocked(c) && c.episode == e.id, "the gate stays closed and the link stays");
            T.Check(!tok.registryReserves && !tok.retained, "RELEASE proved nothing and REPAIRED nothing");
            T.Eq(0, n.physical.strips, "the routing tag was not stripped past the failed proof");
            L(n).FinishPending(e);
            n.Advance(PhysicalLifecycleService.WatchPeriod * 3);
            T.Check(!e.releaseApplied && !tok.registryReserves && Blocked(c), "retries keep refusing and still repair nothing");
            n.physical.RepairReservation(c.pawn); // an owner's repair
            L(n).FinishPending(e);
            T.Check(e.IsComplete && tok.retained, "once the reservation is genuinely in force RELEASE proves it and completes");
        }

        private static void ScanNoQuietHeal()
        {
            string adapter = Code("Integration/Physical/RimWorldPhysicalWorldPort.cs");
            string proof = Body(adapter, "public void EnsureRetained(PawnRef pawn)", "private Exception Refused");
            T.Check(!proof.Contains("EnsureQuest(") && !proof.Contains("Registry.Release") && !proof.Contains("Resume("), "EnsureRetained proves; it never creates or repairs the registry quest");
            T.Check(proof.Contains("FindQuest()") && proof.Contains("WorldPawnSituation.ReservedByQuest"), "it requires the quest to exist and vanilla to see ReservedByQuest");
            T.Check(!proof.Contains("WorldPawnSituation.Free ||"), "and no longer tolerates a pawn that is merely 'not Free'");
            foreach (string name in new[] { "public PhysicalObservation Observe(", "public PassToWorldCheck CheckPassToWorld(", "public void PassToWorld(", "public void Normalize(" })
            {
                string body = Body(adapter, name, "        public ");
                T.Check(!body.Contains("EnsureQuest("), name + " never creates the registry quest");
            }
            T.Eq(3, Regex.Matches(adapter, @"EnsureQuest\(").Count, "the adapter creates the registry quest in exactly three reviewed places (Create, PlacementRefusal, the load pass)");
            string observer = Code("Integration/Physical/PawnObserver.cs");
            T.Check(observer.Contains("WorldPawnRules.KindOf("), "the observer classifies world pawns through the pure rule");
            T.Check(!Regex.IsMatch(observer, @"==\s*WorldPawnSituation\.Free\s*\|\|") && !observer.Contains("o.kind = ObservedKind.WorldFree;"), "and never maps an actual Free straight to WorldFree");
            string lifecycle = Code("Domain/Physical/PhysicalLifecycleService.cs");
            T.Check(lifecycle.Contains("QuarantineReservation") && lifecycle.Contains("ObservedKind.ReservationBroken"), "the lifecycle quarantines a broken reservation");
            string tier = Code("Diagnostics/RuntimePhysicalTests/PhysicalScenarios.cs") + Code("Diagnostics/RuntimePhysicalTests/PhysicalTestRunner.cs");
            T.Check(tier.Contains("IsBrokenReservation(") && tier.Contains("PhysicalLifecycleService.QuarantineReservation") && tier.Contains("ACTUAL Free"), "the physical tier treats an actual Free as a FAIL, naming it");
            foreach (string id in new[] { "RT-PHYX-002", "RT-PHYX-005", "RT-PHYX-015", "RT-PHYX-016" }) T.Check(tier.Contains("CheckNoActualFree(\"" + id + "\""), id + " asserts that an actual Free never happened");
        }

        // ================================================================== Fix 4: first age and gender follow the PERSON

        private static void IdentityDeterministic()
        {
            T.Eq(1, PersonIdentity.Version, "version 1 is frozen");
            T.Eq("physical.identity.v1", PersonIdentity.Salt, "and its salt");
            T.Check(!PersonIdentity.For(1234, CharacterId.None).set, "no person, no identity (an anonymous slot states nothing)");
            int females = 0, males = 0;
            HashSet<int> ages = new HashSet<int>();
            for (int i = 1; i <= 400; i++)
            {
                FirstIdentity a = PersonIdentity.For(424242, new CharacterId(i));
                FirstIdentity b = PersonIdentity.For(424242, new CharacterId(i));
                T.Check(a.set && a.female == b.female && a.ageUnit == b.ageUnit, "person " + i + ": the same seed and person give the same identity every time");
                T.Check(a.ageUnit >= 0 && a.ageUnit < FirstIdentity.Resolution, "person " + i + ": the age position is in range");
                if (a.female) females++; else males++;
                ages.Add(a.ageUnit);
            }
            T.Check(females > 120 && males > 120, "different people differ: both genders occur (" + females + " female, " + males + " male)");
            T.Check(ages.Count > 380, "and so do ages (" + ages.Count + " distinct positions of 400)");
            int differs = 0;
            for (int i = 1; i <= 100; i++) if (PersonIdentity.For(1, new CharacterId(i)).ageUnit != PersonIdentity.For(2, new CharacterId(i)).ageUnit) differs++;
            T.Check(differs > 90, "another world's seed gives another identity for the same person id (" + differs + " of 100)");
            // The mapping onto a window is exact integer arithmetic and always inside it.
            foreach (int unit in new[] { 0, 1, 32767, 32768, FirstIdentity.Resolution - 1 })
            {
                FirstIdentity f = new FirstIdentity { set = true, ageUnit = unit };
                int y = f.PickYear(18, 49);
                T.Check(y >= 18 && y <= 49, "unit " + unit + " picks " + y + " inside [18, 49]");
            }
            T.Eq(18, new FirstIdentity { set = true, ageUnit = 0 }.PickYear(18, 49), "the lowest position is the window's first year");
            T.Eq(49, new FirstIdentity { set = true, ageUnit = FirstIdentity.Resolution - 1 }.PickYear(18, 49), "the highest is its last");
            T.Eq(30, new FirstIdentity { set = true, ageUnit = 12345 }.PickYear(30, 30), "a one-year window always picks that year");
        }

        private static void IdentityIgnoresEpisode()
        {
            // The same person of the same world, materialized FIRST by two different episodes: different episode ids and seeds, a different
            // slot, another map, ten years later, a different career. The identity input of the creation request is identical.
            TestNet a = new TestNet(9670);
            NetworkActor actorA = Solo(a, "f4id");
            KnownCharacter ca = PhysicalLifecycleTests.Self(a, actorA);
            PhysicalLifecycleTests.Begin(a, actorA, new[] { ca });
            ProjectionRequest ra = a.physical.requests[a.physical.requests.Count - 1];

            TestNet b = new TestNet(9670);
            NetworkActor actorB = Solo(b, "f4id");
            KnownCharacter cb = PhysicalLifecycleTests.Self(b, actorB);
            T.Eq(ca.id, cb.id, "the same person in the same world");
            for (int i = 0; i < 17; i++) b.ids.NextId(); // other things happened first: the next episode id is different
            b.clock.Now += 10 * Ticks.PerYear;
            ContractorSimulation sim = actorB.Get<ContractorSimulation>();
            sim.skill = 1f;
            sim.funds += 900000;
            sim.doctrine.caution = 0.9f;
            actorB.reputation.SetScore(actorB.reputation.score + 4000);
            FameBand[] bands = (FameBand[])Enum.GetValues(typeof(FameBand));
            actorB.reputation.SetBand(bands[bands.Length - 1]);
            EpisodeRequest request = PhysicalRuntimeSuite.Request(actorB, new[] { cb });
            request.mapId = 31;
            request.where = new TileRef { tileId = 777 };
            PhysicalEpisode eb;
            T.Check(L(b).Plan(request, out eb).ok, "planned");
            eb.members[0].slot = 5;
            L(b).Materialize(eb);
            ProjectionRequest rb = b.physical.requests[b.physical.requests.Count - 1];
            T.Check(ra.episode != rb.episode && ra.seed != rb.seed && ra.slot != rb.slot, "the episodes, seeds and slots really differ");
            T.Check(ra.identity.set && rb.identity.set, "both requests carry the person's identity");
            T.Eq(ra.identity.female, rb.identity.female, "the intended gender is identical");
            T.Eq(ra.identity.ageUnit, rb.identity.ageUnit, "and so is the age position");
            T.Eq(PersonIdentity.For(a.ctx.networkSeed, ca.id).ToString(), ra.identity.ToString(), "both are the pure function of (world seed, person)");

            // Property over many projections of one person: nothing the request builder is given can move the identity.
            PhysicalEpisode e = a.ctx.episodes.episodes[0];
            EpisodeMember m = e.members[0];
            string baseline = PersonIdentity.For(a.ctx.networkSeed, ca.id).ToString();
            ContractorSimulation simA = actorA.Get<ContractorSimulation>();
            foreach (FameBand band in Enum.GetValues(typeof(FameBand)))
            {
                actorA.reputation.SetBand(band);
                for (int k = 0; k < 6; k++)
                {
                    simA.skill = k / 5f;
                    simA.funds = k * 1000;
                    simA.doctrine.professionalism = k / 5f;
                    e.seed = k * 7919 + (int)band;
                    e.whereMapId = k;
                    m.slot = k;
                    a.clock.Now += k * Ticks.PerYear;
                    ProjectionRequest r = ProjectionPolicy.ForPerson(e, m, ca, actorA, a.ctx.networkSeed);
                    T.Eq(baseline, r.identity.ToString(), "fame " + band + ", experience, doctrine, funds, episode seed, map, slot and time #" + k + " change nothing");
                }
            }
            // A DIFFERENT person may differ.
            PhysicalEpisode other = new PhysicalEpisode { id = new EpisodeId(901), actor = actorA.id, seed = 5 };
            bool anyDifferent = false;
            for (int i = 1; i <= 30; i++)
            {
                KnownCharacter fake = new KnownCharacter { id = new CharacterId(1000 + i) };
                if (ProjectionPolicy.ForPerson(other, m, fake, actorA, a.ctx.networkSeed).identity.ToString() != baseline) anyDifferent = true;
            }
            T.Check(anyDifferent, "other people get other identities");
            // The request key (which the fame-invariance test compares) includes the identity, so it is covered by that property too.
            T.Check(ProjectionPolicy.Key(ra).Contains(ra.identity.ToString()), "the request key carries the identity");
        }

        private static void AdultWindow()
        {
            Func<float, bool, LifeStageFact> st = (age, adult) => new LifeStageFact { minAge = age, adult = adult };
            int lo, hi;
            // Vanilla humans: Baby 0, Child 3, PreTeen 9, Teen 13, Adult 18; life expectancy 80; the kind's range is the default.
            List<LifeStageFact> human = new List<LifeStageFact> { st(0, false), st(3, false), st(9, false), st(13, false), st(18, true) };
            T.Check(AdultAgeWindow.TryFor(human, 80f, 0, 999999, out lo, out hi) && lo == 18 && hi == 49, "humans: 18–49 (the flat part of their own age curve) (got " + lo + "–" + hi + ")");
            // A modded race scales by ITS numbers, not by human ones.
            List<LifeStageFact> odd = new List<LifeStageFact> { st(0, false), st(40, false), st(90, true) };
            T.Check(AdultAgeWindow.TryFor(odd, 400f, 0, 999999, out lo, out hi) && lo == 90 && hi == 245, "a long-lived race: 90–245 (got " + lo + "–" + hi + ")");
            // The kind's own generation range clamps both ends.
            T.Check(AdultAgeWindow.TryFor(human, 80f, 25, 40, out lo, out hi) && lo == 25 && hi == 40, "the kind's range clamps the window (got " + lo + "–" + hi + ")");
            T.Check(!AdultAgeWindow.TryFor(human, 80f, 60, 70, out lo, out hi), "a kind range above the window leaves no safe age: nothing is pinned");
            T.Check(!AdultAgeWindow.TryFor(human, 80f, 0, 10, out lo, out hi), "a kind range below adulthood leaves no safe age");
            // Only the contiguous Adult stages count.
            List<LifeStageFact> capped = new List<LifeStageFact> { st(0, false), st(18, true), st(60, false) };
            T.Check(AdultAgeWindow.TryFor(capped, 200f, 0, 999999, out lo, out hi) && lo == 18 && hi == 59, "a later non-adult stage ends the window (got " + lo + "–" + hi + ")");
            // Unsorted input, a fractional adult age.
            List<LifeStageFact> shuffled = new List<LifeStageFact> { st(17.5f, true), st(0, false), st(5, false) };
            T.Check(AdultAgeWindow.TryFor(shuffled, 100f, 0, 999999, out lo, out hi) && lo == 18, "unsorted stages and a fractional start are handled (got " + lo + "–" + hi + ")");
            // No safe window: nothing is guessed.
            T.Check(!AdultAgeWindow.TryFor(new List<LifeStageFact> { st(0, false), st(10, false) }, 80f, 0, 999999, out lo, out hi), "no Adult stage ⇒ no pin");
            T.Check(!AdultAgeWindow.TryFor(human, 18f, 0, 999999, out lo, out hi), "a life expectancy at adulthood ⇒ no pin");
            T.Check(!AdultAgeWindow.TryFor(human, 10f, 0, 999999, out lo, out hi), "a life expectancy below adulthood ⇒ no pin");
            T.Check(!AdultAgeWindow.TryFor(null, 80f, 0, 999999, out lo, out hi) && !AdultAgeWindow.TryFor(new List<LifeStageFact>(), 80f, 0, 999999, out lo, out hi), "no stages ⇒ no pin");
            // Whatever the numbers, a pinned age is a whole year inside the window.
            for (float life = 20f; life < 500f; life += 37f)
            {
                for (int min = 0; min < 100; min += 23)
                {
                    if (!AdultAgeWindow.TryFor(human, life, min, 999999, out lo, out hi)) continue;
                    T.Check(lo >= 18 && lo <= hi && lo >= min, "life " + life + ", kind min " + min + ": a sane window " + lo + "–" + hi);
                    for (int unit = 0; unit < FirstIdentity.Resolution; unit += 4093)
                    {
                        int y = new FirstIdentity { set = true, ageUnit = unit }.PickYear(lo, hi);
                        if (y < lo || y > hi) T.Check(false, "a picked year stays inside its window");
                    }
                }
            }
        }

        private static void ScanIdentity()
        {
            string projection = Code("Integration/Physical/PawnProjection.cs");
            T.Check(projection.Contains("fixedGender: pins.gender") && projection.Contains("fixedBiologicalAge: pins.biologicalAge") && projection.Contains("fixedChronologicalAge: pins.chronologicalAge"),
                "first gender and age go in through vanilla's own PawnGenerationRequest inputs");
            T.Check(projection.Contains("forceGenerateNewPawn: true") && projection.Contains("canGeneratePawnRelations: false") && projection.Contains("developmentalStages: DevelopmentalStage.Adult")
                && projection.Contains("MaxAttempts = 4"), "a NEW pawn, no relations, an adult, a bounded number of attempts: unchanged");
            T.Check(projection.Contains("race.forceGender == Gender.None") && projection.Contains("!kind.fixedGender.HasValue"), "a gender pin never contradicts the race's or the kind's own");
            T.Check(projection.Contains("kind.chronologicalAgeRange.HasValue"), "a kind with its own chronological range keeps it");
            foreach (string f in PhysicalLifecycleTests.AllSources())
            {
                string rel = PhysicalLifecycleTests.Rel(f);
                string code = PhysicalLifecycleTests.Code(File.ReadAllText(f));
                if (!rel.Contains("/Integration/Physical/") && !rel.Contains("/Domain/Physical/") && !rel.Contains("/Diagnostics/RuntimePhysicalTests/")) continue;
                T.Check(!Regex.IsMatch(code, @"\.AgeBiologicalTicks\s*=(?!=)") && !Regex.IsMatch(code, @"\.AgeChronologicalTicks\s*=(?!=)") && !Regex.IsMatch(code, @"\.BirthAbsTicks\s*=(?!=)"),
                    "no pawn's age is ever written after creation (" + rel + ")");
                T.Check(!Regex.IsMatch(code, @"\b(?:p|pawn|d|stored|x)\.gender\s*=(?!=)"), "no pawn's gender is ever written (" + rel + ")");
            }
            // The Network persists no duplicate of what only the pawn may say.
            foreach (Type type in new[] { typeof(KnownCharacter), typeof(PawnRef), typeof(EpisodeMember), typeof(PhysicalEpisode) })
            {
                foreach (System.Reflection.FieldInfo field in type.GetFields())
                {
                    string name = field.Name.ToLowerInvariant();
                    T.Check(name.IndexOf("gender", StringComparison.Ordinal) < 0 && name.IndexOf("birth", StringComparison.Ordinal) < 0 && name != "age" && name.IndexOf("identity", StringComparison.Ordinal) < 0,
                        type.Name + "." + field.Name + " is not a stored gender, age or identity");
                }
            }
            T.Check(Array.IndexOf(typeof(ProjectionRequest).GetInterfaces(), typeof(Verse.IExposable)) < 0, "the request (which carries the identity) is never persisted");
            string lifecycle = Code("Domain/Physical/PhysicalLifecycleService.cs");
            T.Check(lifecycle.Contains("ProjectionPolicy.ForPerson(e, m, c, ctx.actors.Get(e.actor), ctx.networkSeed)"), "the lifecycle derives it from the world seed, never from the episode");
        }

        // ================================================================== Fix 5: an aging step that threw is not provable

        private static void AgingUncertain()
        {
            TestNet n = new TestNet(9680);
            NetworkActor a = Solo(n, "f5age");
            KnownCharacter c = PhysicalLifecycleTests.Self(n, a);
            PhysicalEpisode e1 = Begin(n, a, c);
            n.physical.ExitNormally(c.pawn, 33);
            L(n).Reconcile(e1, "test");
            T.Check(e1.IsComplete, "the first visit completed");
            int bookmark = c.pawn.agedThroughTick;
            FakePhysicalWorldPort.Token tok = n.physical.TokenOf(c.pawn);
            n.clock.Now += 5 * Ticks.PerYear;
            int now = n.clock.Now;
            int places = n.physical.places;
            n.physical.ageUncertainAfter = 2L * Ticks.PerYear; // two game years complete, then the next step throws after advancing the pawn
            PhysicalEpisode e2 = Begin(n, a, c);
            T.Eq(EpisodeState.Quarantined, e2.state, "the episode is quarantined");
            T.Check(e2.quarantineKey != null && e2.quarantineKey.StartsWith(PhysicalLifecycleService.QuarantineAge + ":", StringComparison.Ordinal), "as an uncertain age (" + e2.quarantineKey + ")");
            T.Check(e2.quarantineKey.Contains("completed " + 2L * Ticks.PerYear) && e2.quarantineKey.Contains("uncertain " + (long)Ticks.PerYear), "with the evidence: what completed and what is uncertain (" + e2.quarantineKey + ")");
            T.Check(PhysicalLifecycleService.IsHardQuarantine(e2.quarantineKey), "a quarantine nothing resolves by looking at the world");
            T.Eq(bookmark + 2 * Ticks.PerYear, c.pawn.agedThroughTick, "agedThroughTick advanced by exactly the steps that RETURNED, no more");
            T.Check(c.pawn.agedThroughTick != now && c.pawn.agedThroughTick != bookmark, "it was never set to 'now' and never left to replay the whole interval");
            T.Eq(CustodyState.Deployed, c.custody, "the person stays Deployed");
            T.Eq(e2.id, c.episode, "the episode link stays");
            T.Check(Blocked(c), "the person is blocked from materialization and from abstraction");
            T.Eq(MemberState.Planned, e2.members[0].state, "nobody was placed");
            T.Eq(places, n.physical.places, "no pawn was placed");
            T.Eq(0, n.physical.passCalls, "nothing was passed to the world");
            T.Eq(1, n.physical.creates, "no second pawn");
            T.Check(e2.attempts >= PhysicalEpisode.MaxAttempts, "it is watched slowly");
            T.Eq(1, L(n).counters.agingUncertain, "counted");
            long pawnAged = tok.agedTicks;
            int catchUps = n.physical.catchUps;
            // Nothing replays the unknown interval: not a wake-up, not the watch, not a finish-pending pass, not a save and a load.
            L(n).Reconcile(e2, "again");
            L(n).WakeAll("test");
            L(n).FinishPending(e2);
            n.Advance(PhysicalLifecycleService.SlowWatchPeriod * 3);
            PhysicalLifecycleTests.SaveLoad(n);
            L(n).OnLoaded();
            n.Advance(PhysicalLifecycleService.SlowWatchPeriod * 3);
            PhysicalEpisode le = n.ctx.episodes.Get(e2.id);
            KnownCharacter lc = n.ctx.characters.Get(c.id);
            T.Eq(EpisodeState.Quarantined, le.state, "still quarantined after every wake-up, the watch and a save and load");
            T.Eq(catchUps, n.physical.catchUps, "the interval was never aged again");
            T.Eq(pawnAged, tok.agedTicks, "the pawn was not aged a second time");
            T.Eq(bookmark + 2 * Ticks.PerYear, lc.pawn.agedThroughTick, "the bookmark is unchanged");
            T.Check(!le.consequencesApplied && Blocked(lc) && lc.custody == CustodyState.Deployed, "nothing was committed and the person is still blocked");
            T.Eq(places, n.physical.places, "still nobody placed");
            PhysicalEpisode third;
            T.Check(!L(n).Plan(PhysicalRuntimeSuite.Request(n.ctx.actors.Get(a.id), new[] { lc }), out third).ok, "and the person cannot be planned into a new episode");
            T.Eq(1, n.physical.creates, "one pawn throughout");
        }

        private static void AgingRetryable()
        {
            // A failure BEFORE anything changed (the port's other exceptions) is not uncertain: the placement aborts NeverPlaced with a
            // diagnostic, the bookmark is untouched and the full interval is aged exactly once on the next materialization.
            TestNet n = new TestNet(9681);
            NetworkActor a = Solo(n, "f5retry");
            KnownCharacter c = PhysicalLifecycleTests.Self(n, a);
            PhysicalEpisode e1 = Begin(n, a, c);
            n.physical.ExitNormally(c.pawn, 33);
            L(n).Reconcile(e1, "test");
            int bookmark = c.pawn.agedThroughTick;
            n.clock.Now += 5 * Ticks.PerYear;
            n.physical.ThrowOn("age", 1);
            PhysicalEpisode e2 = Begin(n, a, c);
            T.Check(e2.IsComplete && e2.members[0].outcome == MemberOutcome.NeverPlaced, "an age catch-up that changed nothing aborts the placement NeverPlaced");
            T.Check(e2.lastError != null, "with a diagnostic (" + e2.lastError + ")");
            T.Eq(bookmark, c.pawn.agedThroughTick, "the bookmark is untouched");
            T.Eq(0, L(n).counters.agingUncertain, "nothing was uncertain");
            T.Check(!Blocked(c) && c.custody == CustodyState.Stored, "the person is Stored and available again");
            PhysicalEpisode e3 = Begin(n, a, c);
            T.Eq((long)5 * Ticks.PerYear, n.physical.lastCatchUp, "the next materialization ages the FULL interval, exactly once");
            T.Eq(EpisodeState.Open, e3.state, "and places the person");
            T.Eq(1, n.physical.creates, "one pawn");
        }

        private static void AgingSettle()
        {
            TestNet n = new TestNet(9682);
            NetworkActor a = Solo(n, "f5settle");
            KnownCharacter c = PhysicalLifecycleTests.Self(n, a);
            PhysicalEpisode e1 = Begin(n, a, c);
            n.physical.ExitNormally(c.pawn, 33);
            L(n).Reconcile(e1, "test");
            n.clock.Now += 3 * Ticks.PerYear;
            n.physical.ageUncertainAfter = 0;
            PhysicalEpisode e2 = Begin(n, a, c);
            T.Eq(EpisodeState.Quarantined, e2.state, "uncertain from the first step");
            L(n).SettleForRemoval();
            T.Eq(EpisodeState.Closed, e2.state, "prepare-for-removal settles it");
            T.Eq(MemberOutcome.Detached, e2.members[0].outcome, "Detached, never NeverPlaced (an uncertain age is not 'unplaced and fine')");
            T.Eq(CustodyState.OutOfCustody, c.custody, "nothing is invented about the person");
            T.Eq(1, n.physical.creates, "one pawn");
        }

        // ================================================================== Fix 1: the documentation says what the owner actually proved

        private static void DocsConsistent()
        {
            string docs = Path.GetFullPath(Path.Combine(PhysicalLifecycleTests.Root, "../../docs"));
            string readme = Path.GetFullPath(Path.Combine(PhysicalLifecycleTests.Root, "../../README.md"));
            List<string> files = new List<string>(Directory.GetFiles(docs, "*.md", SearchOption.AllDirectories));
            files.Add(readme);
            string decisions = File.ReadAllText(Path.Combine(docs, "DECISIONS.md"));
            int adr53 = decisions.IndexOf("### ADR-053 · Retained pawn exit reservation uses M1", StringComparison.Ordinal);
            int adr54 = decisions.IndexOf("### ADR-054 ·", StringComparison.Ordinal);
            T.Check(adr53 > 0 && adr54 > adr53, "ADR-053 exists, in numeric order before ADR-054");
            string adr = adr53 > 0 && adr54 > adr53 ? decisions.Substring(adr53, adr54 - adr53) : "";
            T.Check(adr.Contains("**Accepted — owner runtime validated.**"), "ADR-053 is Accepted, owner runtime validated");
            T.Check(adr.Contains("RELEASE merely proves the reservation; it does not create it") && adr.Contains("P3-INV-032"), "it records that RELEASE proves and never creates, and closes P3-INV-032");
            T.Check(adr.Contains("no Free-world-pawn window observed") && adr.Contains("no faction") && adr.Contains("no Network double `PassToWorld`"), "it records the owner's findings");
            T.Check(adr.Contains("Phase 3.1 as a complete controlled physical episode is not"), "and does not claim the whole of Phase 3.1");
            string spikes = File.ReadAllText(Path.Combine(docs, "spikes/README.md"));
            Match row = Regex.Match(spikes, @"\| \*\*S31\*\*[^\n]*");
            T.Check(row.Success && row.Value.Contains("PASS") && row.Value.Contains("M1 accepted") && row.Value.Contains("NOT YET RUN"), "the spikes README lists S31 as an owner-runtime PASS and the 3.1 suite as not yet run");
            string lifecycle = File.ReadAllText(Path.Combine(docs, "PHYSICAL_LIFECYCLE.md"));
            T.Check(lifecycle.Contains("### 7.6 The vanilla exit window: RESOLVED by M1 (spike S31, owner-validated)"), "§ 7.6 says the window is resolved");
            T.Check(lifecycle.Contains("## Appendix J: Phase 3.1 post-review correction pass (PR #10)"), "Appendix J records the correction pass");
            string[] stale =
            {
                "ADR-053 is left", "S31 has not been run", "S31 is NOT RUN", "candidate **M1** of", "updated in this branch", "an-open-mandatory-spike-s31", "is gated on a mandatory runtime spike",
                "left as it stands in this branch", "await the owner's confirmation", "S31 record, ADR-053", "AgingIncompleteException", "(3.0 does not need it); it is **NOT RUN**",
                "records exactly that much", "exact amount already applied"
            };
            foreach (string f in files)
            {
                string text = File.ReadAllText(f);
                string name = Path.GetFileName(f);
                foreach (string phrase in stale) T.Check(!text.Contains(phrase), name + " has no stale phrase: " + phrase);
                // Every link to the renamed or new anchors resolves to a real heading.
                if (text.Contains("#76-the-vanilla-exit-window-resolved-by-m1-spike-s31-owner-validated")) T.Check(lifecycle.Contains("### 7.6 The vanilla exit window: RESOLVED by M1"), name + ": the § 7.6 anchor resolves");
                if (text.Contains("#adr-053--retained-pawn-exit-reservation-uses-m1")) T.Check(adr53 > 0, name + ": the ADR-053 anchor resolves");
                if (text.Contains("#appendix-j-phase-31-post-review-correction-pass-pr-10")) T.Check(lifecycle.Contains("## Appendix J: Phase 3.1 post-review correction pass (PR #10)"), name + ": the Appendix J anchor resolves");
            }
            // Status wording never lets the S31 pass stand in for the 3.1 suite (and the 3.1 suite is not claimed as run).
            foreach (string name in new[] { "PHYSICAL_LIFECYCLE.md", "IMPLEMENTATION_PHASES.md", "RUNTIME_TESTING.md", "RISKS.md" })
            {
                string text = File.ReadAllText(Path.Combine(docs, name));
                T.Check(text.Contains("NOT YET RUN BY OWNER") || text.Contains("NOT YET RUN by the owner"), name + " states that the 3.1 physical suite has not yet been run by the owner");
            }
        }

        private static void ScanAging()
        {
            string adapter = Code("Integration/Physical/EncounterFactions.cs");
            string catchUp = Body(adapter, "public static long CatchUp(Pawn p, long elapsedTicks)", "public static class PawnNormalization");
            T.Check(catchUp.Contains("AgingUncertainException") && catchUp.Contains("age.AgeBiologicalTicks"), "each step's failure is reported as uncertain, with the measured biological ticks");
            T.Check(!adapter.Contains("AgingIncompleteException"), "the claim of an exact partial catch-up is gone from the adapter");
            string port = Code("Domain/Physical/PhysicalWorldPort.cs");
            T.Check(!port.Contains("AgingIncompleteException") && port.Contains("class AgingUncertainException"), "and from the port");
            string lifecycle = Code("Domain/Physical/PhysicalLifecycleService.cs");
            T.Check(lifecycle.Contains("ex.completedTicks") && !lifecycle.Contains("ex.appliedTicks"), "the bookmark advances by what completed only");
            MatchCollection caught = Regex.Matches(lifecycle, @"catch \(AgingUncertainException ex\)\s*\{[^}]*\}");
            bool advancesByCompleted = false, setsNow = false;
            foreach (Match m in caught)
            {
                if (m.Value.Contains("completedTicks")) advancesByCompleted = true;
                if (m.Value.Contains("= now")) setsNow = true;
            }
            T.Check(caught.Count == 2 && advancesByCompleted && !setsNow, "and neither failure path ever sets the bookmark to now to escape the failure (" + caught.Count + " handlers)");
            string doc = File.ReadAllText(Path.Combine(PhysicalLifecycleTests.Root, "../../docs/PHYSICAL_LIFECYCLE.md"));
            T.Check(!doc.Contains("records exactly that much") && !doc.Contains("exact amount already applied") && !doc.Contains("records exactly what was applied"), "the design text makes no atomicity claim vanilla does not provide");
        }
    }
}
