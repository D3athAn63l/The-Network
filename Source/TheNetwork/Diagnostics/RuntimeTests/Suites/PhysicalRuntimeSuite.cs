using System.Collections.Generic;
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

namespace TheNetwork.Diagnostics.RuntimeTests.Suites
{
    /// <summary>
    /// RT-PHYS (Phase 3.0, PHYSICAL_LIFECYCLE § 21.1, Tier S): the ABSTRACT half of the lifecycle, inside the sandbox, over the
    /// scriptable <see cref="FakePhysicalWorldPort"/>. Every case runs the production <see cref="PhysicalLifecycleService"/>,
    /// planner, Applier and stages; the fake only hands out tokens and answers scripted observations. Nothing here can reach a
    /// pawn, a thing, a map, a faction, WorldPawns, silver, a letter or the live Network: the sandbox owns every byte it writes.
    /// The cases marked headless-only in § 21.1 (007, 011, 015, 027, 028) are in the headless suite.
    /// </summary>
    public static class PhysicalRuntimeSuite
    {
        public const string Suite = "PHYS";

        public static IEnumerable<RuntimeTestCase> Cases(IRuntimeTestHost host)
        {
            yield return RuntimeTestCase.Immediate(Suite, "RT-PHYS-001", "One person materializes exactly once (Deployed, one membership, one binding)", MaterializeOnce, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PHYS-002", "The same person cannot materialize twice (P3-INV-001)", NoSecondEpisode, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PHYS-003", "A normal exit reconciles exactly once under duplicate wake-ups", ExitOnce, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PHYS-004", "Rematerialization reuses the same binding, never a new pawn", SameBinding, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PHYS-005", "A physical wound becomes the abstract recovery truth once", WoundOnce, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PHYS-006", "Physical death is final: no abstract availability or resurrection", DeathMonotonic, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PHYS-008", "Held, unknown or absent is never Returned", NeverReturnedByAbsence, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PHYS-009", "A group member's death changes that member only; tiers are conserved", GroupDeath, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PHYS-010", "Map removal cannot silently erase a person", MapRemoval, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PHYS-012", "An unsupported custody quarantines; the pawn is untouched, the person blocked", UnsupportedCustody, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PHYS-013", "A reconcile that throws restores the exact durable state; the retry applies once", ThrowRestores, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PHYS-014", "Publication resumes at its cursor; nothing accepted is published twice", PublicationCursor, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PHYS-016", "A person is never owned by an episode and an operation at once", OperationExclusivity, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PHYS-017", "The spatial anchor is frozen while physical and written once at close", SpatialFrozen, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PHYS-018", "Prepare-for-removal settles every open episode; nothing is deleted", RemovalSettle, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PHYS-019", "The validator reports episode contradictions and repairs none", ValidatorReports, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PHYS-020", "Role verdict: incapability rejected; only one role skill's base level is ever raised", RoleVerdictAndCorrection, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PHYS-021", "Established truth beats randomness: the request carries only durable statements", EstablishedTruth, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PHYS-022", "Fame invariance: fame, score and visibility change no projection request or role", FameInvariance, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PHYS-025", "Truthful aging asks for the full, uncapped interval", TruthfulAging, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PHYS-030", "Time-independent identity: the same origin facts give the same role in year 1 and year 10", TimeIndependentIdentity, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PHYS-026", "Commit fault sweep: every injected throw restores the fingerprint", FaultSweep, true);
            yield return RuntimeTestCase.Immediate(Suite, "RT-PHYS-029", "Release interruption keeps the gate closed until COMPLETE; no second PassToWorld", ReleaseInterruption, true);
        }

        // ================================================================== helpers

        private static PhysicalLifecycleService L(RuntimeTestSandbox sb) { return sb.Ctx.Lifecycle; }

        private static LiveFingerprint Print(RuntimeTestSandbox sb) { return LiveFingerprint.Of(sb.Ctx, sb.Ids, sb.Scheduler, sb.Journal); }

        public static NetworkActor Contractor(RuntimeTestContext ctx, ContractorForm form)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor a = sb.AddContractor(form);
            ctx.Track(a);
            sb.Ctx.Spatial.EnsureInitialized(a);
            return a;
        }

        public static KnownCharacter Self(RuntimeTestSandbox sb, NetworkActor a)
        {
            return sb.Ctx.characters.Get(a.bindings.embodies);
        }

        /// <summary>The organization's named people other than the leader (lieutenants first).</summary>
        public static List<KnownCharacter> Others(RuntimeTestSandbox sb, NetworkActor a)
        {
            List<KnownCharacter> l = new List<KnownCharacter>();
            OrganizationProfile org = a.Get<OrganizationProfile>();
            for (int i = 0; i < org.knownMembers.Count; i++)
            {
                KnownCharacter c = sb.Ctx.characters.Get(org.knownMembers[i]);
                if (c != null && c.id != org.leader && c.IsAvailable) l.Add(c);
            }
            return l;
        }

        public static KnownCharacter Leader(RuntimeTestSandbox sb, NetworkActor a)
        {
            return sb.Ctx.characters.Get(a.Get<OrganizationProfile>().leader);
        }

        public static EpisodeRequest Request(NetworkActor a, IEnumerable<KnownCharacter> people, int anonymousRegulars = 0)
        {
            EpisodeRequest r = new EpisodeRequest { actor = a.id, purposeKey = "Visit", cause = new EpisodeCause { devKey = "RT-PHYS" }, where = new TileRef { tileId = 42 }, mapId = 7 };
            if (people != null) foreach (KnownCharacter c in people) r.named.Add(c.id);
            if (anonymousRegulars > 0) r.anonymous.Add(new TierCount(Tier.Regular, anonymousRegulars));
            return r;
        }

        public static PhysicalEpisode Begin(RuntimeTestContext ctx, NetworkActor a, IEnumerable<KnownCharacter> people, int anonymousRegulars = 0)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            PhysicalEpisode e;
            CommandResult r = L(sb).Plan(Request(a, people, anonymousRegulars), out e);
            ctx.Assert.True(r.ok, "the episode is planned (" + r + ")");
            L(sb).Materialize(e);
            return e;
        }

        private static List<EpisodeMember> Anonymous(PhysicalEpisode e)
        {
            List<EpisodeMember> l = new List<EpisodeMember>();
            for (int i = 0; i < e.members.Count; i++) if (!e.members[i].IsNamed) l.Add(e.members[i]);
            return l;
        }

        private static int Count(RuntimeTestSandbox sb, string key, EpisodeId episode)
        {
            int n = 0;
            for (int i = 0; i < sb.Events.events.Count; i++)
            {
                EpisodeEvent ee = sb.Events.events[i] as EpisodeEvent;
                if (sb.Events.events[i].typeKey == key && (ee == null || ee.episode == episode)) n++;
            }
            return n;
        }

        private static void Same(RuntimeTestContext ctx, LiveFingerprint before, LiveFingerprint after, string what)
        {
            List<string> diff = before.Diff(after);
            ctx.Assert.True(diff.Count == 0, what + (diff.Count > 0 ? ": " + string.Join("; ", diff.ToArray()) : ""));
        }

        // ================================================================== RT-PHYS-001 .. 006

        private static void MaterializeOnce(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor a = Contractor(ctx, ContractorForm.Solo);
            KnownCharacter c = Self(sb, a);
            ctx.Assert.True(AuthorityGate.CanSimulateAbstractly(c), "an abstract person starts abstract");
            PhysicalEpisode e = Begin(ctx, a, new[] { c });
            ctx.Assert.Equal(EpisodeState.Open, e.state, "≥ 1 member placed ⇒ Open");
            ctx.Assert.Equal(CustodyState.Deployed, c.custody, "custody Deployed");
            ctx.Assert.Equal(e.id, c.episode, "one exclusive membership");
            ctx.Assert.True(c.pawn != null && c.pawn.IsBound, "one binding");
            ctx.Assert.Equal(1, sb.Physical.creates, "exactly one creation");
            ctx.Assert.Equal(1, sb.Physical.places, "exactly one placement");
            ctx.Assert.Equal(MemberState.Present, e.members[0].state, "the member is Present");
            ctx.Assert.Equal(c.pawn.thingIdNumber, e.members[0].pawn.thingIdNumber, "the member mirrors the person's binding");
            ctx.Assert.False(AuthorityGate.CanSimulateAbstractly(c), "the gate is closed while physical");
            ctx.Assert.Equal(PersonAuthority.Physical, AuthorityGate.AuthorityOf(c), "one authority: Physical");
            ctx.Assert.True(AuthorityGate.HasPhysicalPresence(sb.Ctx, a), "the actor has physical presence (an episode counts as a job)");
            ctx.Assert.True(sb.Scheduler.Has(PhysicalLifecycleService.WatchJob, e.id.Value), "the episode is watched");
        }

        private static void NoSecondEpisode(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            ctx.ExpectLog("step failed"); // the injected release fault is the scenario
            NetworkActor a = Contractor(ctx, ContractorForm.Solo);
            KnownCharacter c = Self(sb, a);
            Begin(ctx, a, new[] { c });
            LiveFingerprint before = Print(sb);
            PhysicalEpisode second;
            CommandResult r = L(sb).Plan(Request(a, new[] { c }), out second);
            ctx.Assert.False(r.ok, "a second plan for the same person is refused");
            ctx.Assert.Equal("AlreadyPhysical", r.reasonKey, "as AlreadyPhysical");
            ctx.Assert.Null(second, "no episode was made");
            Same(ctx, before, Print(sb), "a refused plan changes nothing");

            // Closed with RELEASE still pending: Stored, yet still not plannable (Stored alone is not enough).
            NetworkActor b = Contractor(ctx, ContractorForm.Solo);
            KnownCharacter d = Self(sb, b);
            PhysicalEpisode e = Begin(ctx, b, new[] { d });
            sb.Physical.ExitNormally(d.pawn, 50);
            sb.Physical.ThrowOn("strip", 99);
            L(sb).Reconcile(e, "test");
            ctx.Assert.True(e.consequencesApplied && !e.releaseApplied, "committed, release pending");
            ctx.Assert.Equal(CustodyState.Stored, d.custody, "custody already Stored");
            ctx.Assert.False(L(sb).Plan(Request(b, new[] { d }), out second).ok, "still refused while the release is pending");
            sb.Physical.ClearFaults();
        }

        private static void ExitOnce(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor a = Contractor(ctx, ContractorForm.Solo);
            KnownCharacter c = Self(sb, a);
            PhysicalEpisode e = Begin(ctx, a, new[] { c });
            sb.Physical.ExitNormally(c.pawn, 55);
            int commits = L(sb).counters.commits;
            L(sb).Reconcile(e, "signal");
            L(sb).Reconcile(e, "watch");
            L(sb).OnLoaded();
            sb.Clock.Now += PhysicalLifecycleService.WatchPeriod + 1;
            sb.Scheduler.RunDue();
            L(sb).Reconcile(e, "dev");
            L(sb).WakeAll("dev");
            ctx.Assert.Equal(commits + 1, L(sb).counters.commits, "four kinds of wake-up, one commit");
            ctx.Assert.True(e.IsComplete, "closed with every stage complete");
            ctx.Assert.Equal(MemberOutcome.Returned, e.members[0].outcome, "Returned");
            ctx.Assert.Equal(1, Count(sb, EventKeys.EpisodeClosed, e.id), "one Episode.Closed event");
            ctx.Assert.Equal(CustodyState.Stored, c.custody, "custody Stored");
            ctx.Assert.False(c.episode.IsValid, "the link was cleared at RELEASE COMPLETE");
            ctx.Assert.True(AuthorityGate.CanSimulateAbstractly(c), "abstract again");
            ctx.Assert.Zero(sb.Physical.passCalls, "a WorldFree member is never passed to the world (P3-INV-031)");
            ctx.Assert.False(sb.Scheduler.Has(PhysicalLifecycleService.WatchJob, e.id.Value), "no watch job is left behind");
        }

        private static void SameBinding(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor a = Contractor(ctx, ContractorForm.Solo);
            KnownCharacter c = Self(sb, a);
            PhysicalEpisode e1 = Begin(ctx, a, new[] { c });
            int thing = c.pawn.thingIdNumber;
            sb.Physical.ExitNormally(c.pawn, 56);
            L(sb).Reconcile(e1, "test");
            ctx.Assert.True(e1.IsComplete, "the first episode completed");
            sb.Clock.Now += 10 * Ticks.PerDay;
            PhysicalEpisode e2 = Begin(ctx, a, new[] { c });
            ctx.Assert.Equal(thing, c.pawn.thingIdNumber, "the same binding (token)");
            ctx.Assert.Equal(thing, e2.members[0].pawn.thingIdNumber, "the member carries it");
            ctx.Assert.Equal(1, sb.Physical.creates, "no second creation (P3-INV-006)");
            ctx.Assert.Equal(1, sb.Physical.catchUps, "one truthful aging catch-up");
            ctx.Assert.Equal((long)(10 * Ticks.PerDay), sb.Physical.lastCatchUp, "for exactly the stored interval");
            ctx.Assert.Equal(EpisodeState.Open, e2.state, "and it is placed again");
        }

        private static void WoundOnce(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor a = Contractor(ctx, ContractorForm.Company);
            OrganizationProfile org = a.Get<OrganizationProfile>();
            KnownCharacter lt = Others(sb, a)[0];
            int woundedBefore = org.Wounded, healthyBefore = org.Healthy;
            PhysicalEpisode e = Begin(ctx, a, new[] { lt }, 2);
            List<EpisodeMember> anon = Anonymous(e);
            sb.Physical.ExitNormally(lt.pawn, 60, 0.5f);
            sb.Physical.ExitNormally(anon[0].pawn, 60, 1f);
            sb.Physical.ExitNormally(anon[1].pawn, 60, 0.75f);
            L(sb).Reconcile(e, "test");
            ctx.Assert.True(e.IsComplete, "reconciled");
            ctx.Assert.Equal(CharacterStatus.Wounded, lt.status, "the named member comes back Wounded");
            ctx.Assert.Equal(e.closedTick + 8 * Ticks.PerDay, lt.woundedUntilTick, "with the bounded recovery for health 0.5 (8 days)");
            ctx.Assert.Equal(woundedBefore + 1, org.Wounded, "one anonymous wounded bucket");
            ctx.Assert.Equal(healthyBefore - 1, org.Healthy, "the other anonymous member is healthy again (one is recovering)");
            LiveFingerprint before = Print(sb);
            L(sb).Reconcile(e, "again");
            L(sb).WakeAll("again");
            Same(ctx, before, Print(sb), "a second wake-up applies nothing");
        }

        private static void DeathMonotonic(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor a = Contractor(ctx, ContractorForm.Solo);
            KnownCharacter c = Self(sb, a);
            PhysicalEpisode e = Begin(ctx, a, new[] { c });
            sb.Physical.Die(c.pawn);
            L(sb).Reconcile(e, "test");
            ctx.Assert.Equal(CharacterStatus.Dead, c.status, "Dead");
            ctx.Assert.Equal(CustodyState.Released, c.custody, "custody Released");
            ctx.Assert.Equal("Physical", c.deathCauseKey, "a physical death");
            ctx.Assert.Equal(ActorStatus.Dissolved, a.status, "the Solo ends");
            ctx.Assert.True(e.IsComplete, "and the episode completes");
            int diedTick = c.diedTick;
            // Abstract jobs keep running: none of them can bring the person back.
            sb.Clock.Now += 20 * Ticks.PerDay;
            sb.Ctx.Upkeep.UpkeepJob(new ScheduledJob { kind = ContractorService.UpkeepJob, target = a.id.Value });
            CasualtyReport r = new CasualtyReport();
            r.fates.Add(new CharacterFate { character = c.id, fate = Fate.Wounded });
            int gateRefused = AuthorityGate.refusedWrites;
            sb.Ctx.Contractors.ApplyCasualties(a, r, ContractId.None, OperationId.None, false);
            ctx.Assert.Equal(CharacterStatus.Dead, c.status, "an abstract wound cannot overwrite death");
            ctx.Assert.True(AuthorityGate.refusedWrites > gateRefused, "the authority gate refused it (custody Released is never abstract)");
            // Even a writer that reached the shared rule directly could not: death is monotonic in the rule itself (P3-INV-004).
            int deadRefused = FateRules.refusedDeadWrites;
            FateRules.Wounded(c, sb.Clock.Now, 5);
            FateRules.Captured(c, sb.Clock.Now);
            ctx.Assert.Equal(CharacterStatus.Dead, c.status, "the fate rule keeps the person dead");
            ctx.Assert.Equal(deadRefused + 2, FateRules.refusedDeadWrites, "both writes were refused");
            ctx.Assert.Equal(diedTick, c.diedTick, "the death tick is unchanged");
            ctx.Assert.Equal(Availability.Ended, sb.Ctx.Contractors.AvailabilityOf(a), "never available again");
        }

        // ================================================================== RT-PHYS-008 .. 012

        private static void NeverReturnedByAbsence(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            ctx.ExpectLog("quarantined (UnsupportedCustody");
            NetworkActor a = Contractor(ctx, ContractorForm.Solo);
            KnownCharacter c = Self(sb, a);
            PhysicalEpisode e = Begin(ctx, a, new[] { c });
            ObservedKind[] stay = { ObservedKind.Unknown, ObservedKind.WorldOther, ObservedKind.InTransport, ObservedKind.Spawned };
            for (int i = 0; i < stay.Length; i++)
            {
                sb.Physical.Script(c.pawn, new PhysicalObservation { kind = stay[i], mapId = 99 });
                L(sb).Reconcile(e, "test");
                ctx.Assert.Equal(EpisodeState.Open, e.state, stay[i] + " keeps the member Present");
            }
            sb.Physical.Script(c.pawn, new PhysicalObservation { kind = ObservedKind.WorldFree, exitEvidence = false });
            L(sb).Reconcile(e, "test");
            ctx.Assert.Equal(EpisodeState.Open, e.state, "a world pawn without exit evidence is not Returned");
            ctx.Assert.False(AuthorityGate.CanSimulateAbstractly(c), "the person stays blocked");
            sb.Physical.Hold(c.pawn, ObservedKind.Kidnapped);
            L(sb).Reconcile(e, "test");
            ctx.Assert.Equal(EpisodeState.Quarantined, e.state, "a held person is never abstracted (quarantined until 3.2)");
            ctx.Assert.Equal(CustodyState.Deployed, c.custody, "no custody is invented");
            ctx.Assert.False(e.consequencesApplied, "and nothing was committed");
        }

        private static void GroupDeath(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor a = Contractor(ctx, ContractorForm.Company);
            NetworkActor bystander = Contractor(ctx, ContractorForm.Team);
            OrganizationProfile org = a.Get<OrganizationProfile>();
            List<KnownCharacter> others = Others(sb, a);
            KnownCharacter dies = others[0], lives = others[1], leader = Leader(sb, a);
            int regularHealthy = FateRules.PeekHealthy(org, Tier.Regular);
            CharacterStatus leaderStatus = leader.status;
            CharacterRole leaderRole = leader.role;
            List<string> bystanderBefore = new List<string>();
            foreach (KnownCharacter k in sb.Ctx.characters.characters) if (k.org == bystander.id) bystanderBefore.Add(k.id + ":" + k.status + ":" + k.custody);
            PhysicalEpisode e = Begin(ctx, a, new[] { dies, lives }, 2);
            List<EpisodeMember> anon = Anonymous(e);
            sb.Physical.Die(dies.pawn);
            sb.Physical.ExitNormally(lives.pawn, 61);
            sb.Physical.Die(anon[0].pawn);
            sb.Physical.ExitNormally(anon[1].pawn, 61);
            L(sb).Reconcile(e, "test");
            ctx.Assert.True(e.IsComplete, "reconciled");
            ctx.Assert.Equal(CharacterStatus.Dead, dies.status, "the intended member died");
            ctx.Assert.Equal(CharacterStatus.Active, lives.status, "the other came back unhurt");
            ctx.Assert.Equal(CustodyState.Stored, lives.custody, "and is Stored");
            ctx.Assert.Equal(leaderStatus, leader.status, "the leader's status is untouched");
            ctx.Assert.Equal(leaderRole, leader.role, "and the leader's role");
            ctx.Assert.Equal(leader.id, org.leader, "no succession (the leader was not lost)");
            ctx.Assert.Equal(regularHealthy - 1, FateRules.PeekHealthy(org, Tier.Regular), "one Regular lost, one returned (tier conservation)");
            ctx.Assert.Zero(ReconciliationPlanner.PeekCommitted(org, Tier.Regular), "nothing is left checked out");
            List<string> bystanderAfter = new List<string>();
            foreach (KnownCharacter k in sb.Ctx.characters.characters) if (k.org == bystander.id) bystanderAfter.Add(k.id + ":" + k.status + ":" + k.custody);
            ctx.Assert.Equal(string.Join(",", bystanderBefore.ToArray()), string.Join(",", bystanderAfter.ToArray()), "nobody else's truth changed");
        }

        private static void MapRemoval(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor a = Contractor(ctx, ContractorForm.Company);
            KnownCharacter lt = Others(sb, a)[0];
            PhysicalEpisode e = Begin(ctx, a, new[] { lt }, 2);
            List<EpisodeMember> anon = Anonymous(e);
            sb.Physical.Vanish(anon[0].pawn);
            int passed = sb.Physical.RemoveMap(7, 63);
            ctx.Assert.Equal(2, passed, "vanilla passed the two pawns still on the map");
            L(sb).Reconcile(e, "map removed");
            ctx.Assert.Equal(EpisodeState.Closed, e.state, "closed from observed state");
            for (int i = 0; i < e.members.Count; i++) ctx.Assert.NotEqual(MemberOutcome.Pending, e.members[i].outcome, "every member has a terminal outcome (" + e.members[i] + ")");
            ctx.Assert.Equal(MemberOutcome.Returned, e.MemberFor(lt.id).outcome, "the named member Returned");
            ctx.Assert.Equal(MemberOutcome.Lost, anon[0].outcome, "the discarded one is Lost (never 'home')");
            ctx.Assert.Equal(MemberOutcome.Returned, anon[1].outcome, "the other Returned");
            ctx.Assert.Zero(sb.Physical.passCalls, "the Network passed nobody to the world again");
        }

        private static void UnsupportedCustody(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            ctx.ExpectLog("quarantined (UnsupportedCustody");
            NetworkActor a = Contractor(ctx, ContractorForm.Solo);
            KnownCharacter c = Self(sb, a);
            PhysicalEpisode e = Begin(ctx, a, new[] { c });
            int actions = sb.Physical.actions.Count;
            sb.Physical.Hold(c.pawn, ObservedKind.HeldByPlayer);
            L(sb).Reconcile(e, "arrested");
            ctx.Assert.Equal(EpisodeState.Quarantined, e.state, "Quarantined");
            ctx.Assert.True(e.quarantineKey != null && e.quarantineKey.StartsWith("UnsupportedCustody"), "as an unsupported custody (" + e.quarantineKey + ")");
            ctx.Assert.Equal(actions, sb.Physical.actions.Count, "the pawn was not touched (no port action)");
            ctx.Assert.False(AuthorityGate.CanSimulateAbstractly(c), "the person stays blocked");
            ctx.Assert.Equal(CharacterStatus.Active, c.status, "no capture is faked");
            // Released later and observed leaving: the quarantined episode closes through the normal commit.
            sb.Physical.TokenOf(c.pawn).held = false;
            sb.Physical.ExitNormally(c.pawn, 64);
            L(sb).Reconcile(e, "released");
            ctx.Assert.True(e.IsComplete, "Quarantined → Closed by a later successful reconcile");
            ctx.Assert.True(AuthorityGate.CanSimulateAbstractly(c), "and only then abstract");
        }

        // ================================================================== RT-PHYS-013 .. 019

        private static void ThrowRestores(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            ctx.ExpectLog("step failed"); // the injected commit fault is the scenario
            NetworkActor a = Contractor(ctx, ContractorForm.Company);
            KnownCharacter leader = Leader(sb, a);
            PhysicalEpisode e = Begin(ctx, a, new[] { leader }, 1);
            sb.Physical.Die(leader.pawn);
            sb.Physical.ExitNormally(Anonymous(e)[0].pawn, 65);
            LiveFingerprint before = null, restored = null;
            L(sb).commitBoundary = s => { if (s == "before") before = Print(sb); else restored = Print(sb); };
            int events = sb.Events.events.Count, strips = sb.Physical.strips;
            L(sb).commitFaultAfter = 3;
            bool committed = L(sb).Reconcile(e, "test");
            L(sb).commitBoundary = null;
            ctx.Assert.False(committed, "the faulted commit did not apply");
            ctx.Assert.NotNull(before, "the commit started");
            ctx.Assert.NotNull(restored, "and was restored");
            Same(ctx, before, restored, "the durable fingerprint is exactly the pre-commit one");
            ctx.Assert.False(e.consequencesApplied, "the flag was never set");
            ctx.Assert.Equal(events, sb.Events.events.Count, "nothing was published");
            ctx.Assert.Equal(strips, sb.Physical.strips, "no release action ran");
            ctx.Assert.Equal(CharacterStatus.Active, leader.status, "the leader is not half-dead");
            ctx.Assert.Equal(1, e.attempts, "one attempt recorded");
            ctx.Assert.True(L(sb).Reconcile(e, "retry"), "the retry commits");
            ctx.Assert.Equal(CharacterStatus.Dead, leader.status, "once");
            ctx.Assert.NotEqual(leader.id, a.Get<OrganizationProfile>().leader, "a successor leads");
            int successions = a.Get<OrganizationProfile>().succession.successions;
            ctx.Assert.False(L(sb).Reconcile(e, "again"), "a later wake-up is a no-op");
            ctx.Assert.Equal(successions, a.Get<OrganizationProfile>().succession.successions, "no second succession");
        }

        private sealed class ThrowingConsumer : IEventConsumer
        {
            public int calls;
            public string Name => "RT-PHYS throwing consumer";
            public void Handle(NetworkEvent evt) { calls++; throw new System.InvalidOperationException("consumer failure (test)"); }
        }

        private static void PublicationCursor(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            ctx.ExpectLog("step failed"); // the injected publication interruption
            ctx.ExpectLog("RT-PHYS throwing consumer"); // the consumer failure the bus must contain
            NetworkActor a = Contractor(ctx, ContractorForm.Company);
            KnownCharacter leader = Leader(sb, a), lt = Others(sb, a)[0];
            PhysicalEpisode e = Begin(ctx, a, new[] { leader, lt });
            sb.Physical.Die(leader.pawn);
            sb.Physical.Die(lt.pawn);
            ThrowingConsumer thrower = new ThrowingConsumer();
            sb.Bus.Register(ConsumerOrder.Presentation, thrower, EventKeys.EpisodeClosed);
            L(sb).publishInterruptAfter = 1;
            L(sb).Reconcile(e, "test");
            int total = e.publications.Count;
            List<string> keys = new List<string>();
            for (int i = 0; i < e.publications.Count; i++) keys.Add(e.publications[i].typeKey);
            ctx.Assert.AtLeast(3, total, "the outbox holds at least three specs (" + string.Join(", ", keys.ToArray()) + ")");
            ctx.Assert.True(e.consequencesApplied && e.releaseApplied && e.followUpApplied, "commit, release and follow-up are done");
            ctx.Assert.Equal(1, e.publishCursor, "interrupted after the first accepted spec");
            ctx.Assert.Equal(-1, e.publishedTick, "PUBLISH is not complete");
            ctx.Assert.Equal(1, Published(sb, a, e, keys[0]), "the first spec was published");
            int successions = a.Get<OrganizationProfile>().succession.successions;
            L(sb).publishInterruptAfter = 1;
            L(sb).FinishPending(e);
            ctx.Assert.Equal(2, e.publishCursor, "the resume submitted only the next spec");
            int failures = sb.Diag.failedConsumers.Count;
            L(sb).FinishPending(e);
            ctx.Assert.True(e.PublishDone, "PUBLISH completes");
            ctx.Assert.Zero(e.publications.Count, "and the outbox is cleared");
            Dictionary<string, int> expected = new Dictionary<string, int>();
            foreach (string k in keys)
            {
                int n;
                expected.TryGetValue(k, out n);
                expected[k] = n + 1;
            }
            foreach (KeyValuePair<string, int> kv in expected) ctx.Assert.Equal(kv.Value, Published(sb, a, e, kv.Key), kv.Key + " was accepted exactly as often as the outbox holds it");
            ctx.Assert.Equal(1, thrower.calls, "a throwing consumer ran once");
            ctx.Assert.Equal(failures + 1, sb.Diag.failedConsumers.Count, "its failure was contained by the bus");
            L(sb).FinishPending(e);
            L(sb).Reconcile(e, "again");
            ctx.Assert.Equal(1, thrower.calls, "and was never redispatched");
            ctx.Assert.Equal(successions, a.Get<OrganizationProfile>().succession.successions, "publication replayed no consequence");
            HashSet<long> seqs = new HashSet<long>();
            for (int i = 0; i < sb.Events.events.Count; i++) ctx.Assert.True(seqs.Add(sb.Events.events[i].seq), "no duplicate sequence number");
        }

        /// <summary>Events of this episode's actor (or the episode itself) with this key, as the recorder saw them.</summary>
        private static int Published(RuntimeTestSandbox sb, NetworkActor a, PhysicalEpisode e, string key)
        {
            int n = 0;
            for (int i = 0; i < sb.Events.events.Count; i++)
            {
                NetworkEvent evt = sb.Events.events[i];
                if (evt.typeKey != key) continue;
                ContractorEvent ce = evt as ContractorEvent;
                EpisodeEvent ee = evt as EpisodeEvent;
                if ((ce != null && ce.actor == a.id) || (ee != null && ee.episode == e.id)) n++;
            }
            return n;
        }

        private static void OperationExclusivity(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            // Episode ⇒ no checkout.
            NetworkActor a = Contractor(ctx, ContractorForm.Company);
            KnownCharacter lt = Others(sb, a)[0];
            Begin(ctx, a, new[] { lt });
            OperationId fake = new OperationId(sb.Ids.NextId());
            ForceCommitment f = sb.Ctx.Contractors.Checkout(a, fake, 0.9f);
            ctx.Assert.False(f.characters.Contains(lt.id), "a person in an episode is never checked out");
            sb.Ctx.Contractors.Return(a, fake, f, null);
            // Operation ⇒ no episode: people checked out to a live operation of their organization.
            NetworkActor b = Contractor(ctx, ContractorForm.Company);
            Operation op = LiveOperation(sb, b);
            ctx.Assert.True(op.characters.Count > 0, "the operation took named people");
            PhysicalEpisode e;
            CommandResult r = L(sb).Plan(Request(b, new[] { sb.Ctx.characters.Get(op.characters[0]) }), out e);
            ctx.Assert.Equal("OnOperation", r.reasonKey, "a person on an operation cannot join an episode");
        }

        /// <summary>A live operation record holding a checkout (sandbox data only; no contract, no money, no job).</summary>
        public static Operation LiveOperation(RuntimeTestSandbox sb, NetworkActor a)
        {
            OperationId id = new OperationId(sb.Ids.NextId());
            ForceCommitment f = sb.Ctx.Contractors.Checkout(a, id, 0.9f);
            Operation op = new Operation { id = id, contractor = a.id, contractorName = a.name.Display, status = OpStatus.Troubled, startedTick = sb.Clock.Now };
            op.characters.AddRange(f.characters);
            op.forces.AddRange(f.forces);
            sb.Ctx.operations.Add(op);
            return op;
        }

        private static void SpatialFrozen(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor a = Contractor(ctx, ContractorForm.Solo);
            KnownCharacter c = Self(sb, a);
            SpatialState s = a.Get<ContractorSimulation>().spatial;
            ctx.Assert.True(s.IsInitialized, "the Solo has a hidden location");
            s.nextAmbientTick = sb.Clock.Now;
            PhysicalEpisode e = Begin(ctx, a, new[] { c });
            string before = s.status + "|" + s.anchor.tileId + "|" + s.lastUpdateTick + "|" + s.nextAmbientTick + "|" + (s.destination?.tileId ?? -1);
            int frozen = sb.Ctx.Spatial.counters.frozen;
            sb.Clock.Now += 5 * Ticks.PerDay;
            sb.Ctx.Spatial.Upkeep(a);
            sb.Ctx.Spatial.CatchUp(a);
            sb.Ctx.Spatial.MaybeRelocate(a);
            string during = s.status + "|" + s.anchor.tileId + "|" + s.lastUpdateTick + "|" + s.nextAmbientTick + "|" + (s.destination?.tileId ?? -1);
            ctx.Assert.Equal(before, during, "frozen while physical: no catch-up, no relocation, no write");
            ctx.Assert.AtLeast(frozen + 3, sb.Ctx.Spatial.counters.frozen, "each spatial writer was refused");
            sb.Physical.ExitNormally(c.pawn, 77);
            L(sb).Reconcile(e, "test");
            ctx.Assert.Equal(77, s.anchor.tileId, "the anchor is written at close, where the person left");
            ctx.Assert.Equal(sb.Clock.Now, s.lastUpdateTick, "from now (no invented travel)");
            ctx.Assert.Equal(SpatialStatus.Idle, s.status, "Idle");
            L(sb).Reconcile(e, "again");
            ctx.Assert.Equal(77, s.anchor.tileId, "written once");
        }

        private static void RemovalSettle(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor a = Contractor(ctx, ContractorForm.Company);
            KnownCharacter lt = Others(sb, a)[0];
            PhysicalEpisode open = Begin(ctx, a, new[] { lt }, 1);
            NetworkActor solo = Contractor(ctx, ContractorForm.Solo);
            PhysicalEpisode planned;
            ctx.Assert.True(L(sb).Plan(Request(solo, new[] { Self(sb, solo) }), out planned).ok, "a second episode is only Planned");
            int people = sb.Ctx.characters.Count, tokens = sb.Physical.tokens.Count;
            int settled = L(sb).SettleForRemoval();
            ctx.Assert.Equal(2, settled, "both settled");
            foreach (PhysicalEpisode e in sb.Ctx.episodes.episodes) ctx.Assert.Equal(EpisodeState.Closed, e.state, "no episode is left Planned or Open (" + e + ")");
            ctx.Assert.Equal(MemberOutcome.Detached, open.MemberFor(lt.id).outcome, "a present member is Detached, nothing invented");
            ctx.Assert.Equal(CustodyState.OutOfCustody, lt.custody, "and is OutOfCustody(Unknown)");
            ctx.Assert.Equal(HeldKind.Unknown, lt.heldBy, "held by an unknown holder");
            ctx.Assert.Equal(MemberOutcome.NeverPlaced, planned.members[0].outcome, "a Planned member was never placed");
            foreach (FakePhysicalWorldPort.Token t in sb.Physical.tokens.Values) ctx.Assert.False(t.tag.IsValid, "no episode tag is left on a pawn");
            ctx.Assert.Zero(sb.Physical.passCalls, "no pawn was passed to the world");
            ctx.Assert.Equal(people, sb.Ctx.characters.Count, "no person was deleted");
            ctx.Assert.Equal(tokens, sb.Physical.tokens.Count, "no pawn was deleted");
        }

        private static void ValidatorReports(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor a = Contractor(ctx, ContractorForm.Company);
            List<KnownCharacter> others = Others(sb, a);
            KnownCharacter x = others[0], y = Leader(sb, a);
            x.custody = CustodyState.Deployed;              // Deployed with no episode
            y.episode = new EpisodeId(987654);              // a link to an episode that does not exist
            PhysicalEpisode bad = new PhysicalEpisode { id = new EpisodeId(sb.Ids.NextId()), actor = a.id, purposeKey = "Visit", state = EpisodeState.Open, publishCursor = 5 };
            sb.Ctx.episodes.Add(bad);
            LiveFingerprint before = Print(sb);
            List<string> findings = new List<string>();
            int n = EpisodeChecks.Report(sb.Ctx, findings);
            string all = string.Join(" | ", findings.ToArray());
            ctx.Assert.AtLeast(3, n, "the contradictions are reported (" + all + ")");
            ctx.Assert.True(all.Contains(x.id + " is Deployed"), "a Deployed person with no episode");
            ctx.Assert.True(all.Contains(y.id + " points to episode"), "a link to a missing episode");
            ctx.Assert.True(all.Contains("publish cursor 5"), "a publish cursor out of bounds");
            Same(ctx, before, Print(sb), "nothing was repaired");
            ctx.Assert.False(AuthorityGate.CanSimulateAbstractly(x) || AuthorityGate.CanSimulateAbstractly(y), "both stay blocked");
        }

        // ================================================================== RT-PHYS-020, 021, 022, 030 (Phase 3.1: projection truth)

        private static readonly string[] SkillNames = { "Shooting", "Melee", "Construction", "Mining", "Cooking", "Plants", "Animals", "Crafting", "Artistic", "Medicine", "Social", "Intellectual" };

        /// <summary>A plain candidate: every skill at the given base level, a passion on Shooting and Medicine, some identity facts.</summary>
        public static RoleCandidate Candidate(int level, int aptitude = 0)
        {
            RoleCandidate c = new RoleCandidate { name = "Ada 'Ace' Vale", gender = "Female", childhood = "UrbworldUrchin", adulthood = "Mercenary", xenotype = "Baseliner", bioAgeTicks = 25L * Ticks.PerYear, genes = 3, hediffs = 1 };
            for (int i = 0; i < SkillNames.Length; i++) c.skills[SkillNames[i]] = new SkillFacts { levelBase = level, aptitude = aptitude, passion = SkillNames[i] == "Shooting" ? 2 : SkillNames[i] == "Medicine" ? 1 : 0 };
            c.traits.Add("Tough");
            return c;
        }

        private static void RoleVerdictAndCorrection(RuntimeTestContext ctx)
        {
            // An incapability is identity: rejected, never corrected.
            RoleSpec rifle = RoleRules.SpecFor(OperationalRole.Rifleman, ExperienceBand.Veteran);
            RoleCandidate pacifist = Candidate(12);
            pacifist.disabledWorkTags.Add("Violent");
            RoleVerdict v = RoleRules.Verify(rifle, pacifist);
            ctx.Assert.True(!v.holds && !v.Correctable, "a candidate incapable of violence is rejected as a Rifleman: " + v);
            RoleCandidate deaf = Candidate(12);
            deaf.skills["Shooting"].totallyDisabled = true;
            ctx.Assert.True(!RoleRules.Verify(rifle, deaf).Correctable, "a totally disabled role skill is rejected, never corrected");
            RoleCandidate brawler = Candidate(15);
            brawler.traits.Add("Brawler");
            ctx.Assert.True(!RoleRules.Verify(RoleRules.SpecFor(OperationalRole.Marksman, ExperienceBand.Elite), brawler).holds, "a Brawler is never a Marksman");
            RoleCandidate noCare = Candidate(15);
            noCare.disabledWorkTags.Add("Caring");
            ctx.Assert.True(!RoleRules.Verify(RoleRules.SpecFor(OperationalRole.Medic, ExperienceBand.Elite), noCare).holds, "a candidate who cannot care is never a Medic");

            // A role-skill shortfall: ONE skill's base level is raised just enough (aptitudes included), and nothing else moves.
            foreach (OperationalRole role in System.Enum.GetValues(typeof(OperationalRole)))
            {
                foreach (ExperienceBand band in System.Enum.GetValues(typeof(ExperienceBand)))
                {
                    RoleSpec spec = RoleRules.SpecFor(role, band);
                    RoleCandidate low = Candidate(1, 2);
                    RoleCandidate before = low.Copy();
                    RoleVerdict verdict = RoleRules.Verify(spec, low);
                    if (verdict.holds) continue;
                    ctx.Assert.True(verdict.Correctable, role + "/" + band + ": a plain shortfall is correctable (" + verdict + ")");
                    ctx.Assert.True(RoleRules.ApplyCorrection(low, verdict), role + "/" + band + ": the correction applies");
                    ctx.Assert.True(RoleRules.Verify(spec, low).holds, role + "/" + band + ": re-verified, it holds");
                    ctx.Assert.Equal(before.IdentityKey(), low.IdentityKey(), role + "/" + band + ": passion, aptitudes, traits, backstory, genes, hediffs, age, gender and name are unchanged");
                    int raised = 0;
                    foreach (string sk in SkillNames)
                    {
                        int was = before.skills[sk].levelBase, now = low.skills[sk].levelBase;
                        ctx.Assert.True(now >= was, role + "/" + band + ": " + sk + " was not lowered");
                        if (now != was)
                        {
                            raised++;
                            ctx.Assert.Equal(verdict.correctSkill, sk, role + "/" + band + ": only the verdict's skill moved");
                            ctx.Assert.Equal(spec.floor - 2, now, role + "/" + band + ": raised exactly to the floor minus the aptitude");
                        }
                    }
                    ctx.Assert.True(raised == 1, role + "/" + band + ": exactly one skill raised (" + raised + ")");
                }
            }
            ctx.Assert.True(!RoleRules.ApplyCorrection(Candidate(1), RoleVerdict.Holds), "a holding verdict corrects nothing");
            ctx.Assert.True(RoleRules.FloorFor(ExperienceBand.Green) < RoleRules.FloorFor(ExperienceBand.Experienced) && RoleRules.FloorFor(ExperienceBand.Elite) < RoleRules.FloorFor(ExperienceBand.Legendary), "the floor rises with competence");
        }

        private static void EstablishedTruth(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor a = Contractor(ctx, ContractorForm.Solo);
            KnownCharacter c = Self(sb, a);
            int before = sb.Physical.requests.Count;
            PhysicalEpisode e = Begin(ctx, a, new[] { c });
            ctx.Assert.Equal(before + 1, sb.Physical.requests.Count, "one creation request");
            ProjectionRequest r = sb.Physical.requests[sb.Physical.requests.Count - 1];
            ctx.Assert.Equal(c.id, r.character, "the request names the person");
            ctx.Assert.True(ReferenceEquals(c.name, r.name), "the established name snapshot itself is the request's name");
            ctx.Assert.True(c.opRole != OperationalRole.Unset && r.role == c.opRole, "the stored operational role (" + c.opRole + ") is the request's role");
            ctx.Assert.Equal(RoleDerivation.ForSolo(a), c.opRole, "and it is the one derived from the actor's origin facts");
            ctx.Assert.Equal(ContractorService.Experience(a), r.capability, "competence is the current experience band");
            ctx.Assert.Equal(a.Get<ContractorSimulation>().equipment.tier, r.equipmentTier, "the abstract equipment tier");
            ctx.Assert.Equal(NetHash.Combine(e.seed, e.members[0].slot), r.seed, "the seed is the episode's");
            ctx.Assert.True(r.faction != null && r.faction.loadId == e.faction.loadId, "the episode's encounter faction");
            // Nothing the Network never established is in the request: there is no field for it. (The person's first gender and age position
            // are ESTABLISHED, by a pure function of the world seed and the person: § 6.3; the pawn is the truth once it exists.)
            HashSet<string> allowed = new HashSet<string> { "episode", "actor", "character", "slot", "tier", "name", "role", "capability", "equipmentTier", "seed", "faction", "identity" };
            ctx.Assert.Equal(PersonIdentity.For(sb.Ctx.networkSeed, c.id).ToString(), r.identity.ToString(), "the request's first identity is the person's, from the world seed and the person only");
            foreach (System.Reflection.FieldInfo f in typeof(ProjectionRequest).GetFields()) ctx.Assert.True(allowed.Contains(f.Name), "the request has no field for an unestablished fact (" + f.Name + ")");
            NamePins pins = NamePins.From(new NameSnapshot { first = "Ada", last = "Vale" });
            ctx.Assert.True(pins.first == "Ada" && pins.last == "Vale" && pins.nick == null, "stated first and last names are pinned; an unstated nickname is not");
            NamePins split = NamePins.From(new NameSnapshot { display = "Rook Calder" });
            ctx.Assert.True(split.first == "Rook" && split.last == "Calder", "a display-only record pins its two parts");
            ctx.Assert.True(!NamePins.From(new NameSnapshot()).Any, "a nameless record pins nothing (vanilla's name stands)");
        }

        private static void FameInvariance(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor a = Contractor(ctx, ContractorForm.Solo);
            KnownCharacter c = Self(sb, a);
            PhysicalEpisode e = Begin(ctx, a, new[] { c });
            EpisodeMember m = e.members[0];
            string key = ProjectionPolicy.Key(ProjectionPolicy.ForPerson(e, m, c, a, sb.Ctx.networkSeed));
            OperationalRole role = RoleDerivation.ForSolo(a);
            CareerRecord career = a.Get<ContractorSimulation>().career;
            foreach (FameBand band in System.Enum.GetValues(typeof(FameBand)))
            {
                a.reputation.SetBand(band);
                ctx.Assert.Equal(key, ProjectionPolicy.Key(ProjectionPolicy.ForPerson(e, m, c, a, sb.Ctx.networkSeed)), "fame " + band + " changes no projection request");
                ctx.Assert.Equal(role, RoleDerivation.ForSolo(a), "fame " + band + " changes no role");
            }
            int[] scores = { 0, 1, 250, 5000, int.MaxValue };
            for (int i = 0; i < scores.Length; i++)
            {
                a.reputation.SetScore(scores[i]);
                career.reputationEarned = scores[i] / 2;
                career.triumphs = i * 7;
                ctx.Assert.Equal(key, ProjectionPolicy.Key(ProjectionPolicy.ForPerson(e, m, c, a, sb.Ctx.networkSeed)), "reputation score " + scores[i] + " changes no projection request");
            }
            ctx.Assert.Equal(role, RoleDerivation.ForSolo(a), "nor the role");
        }

        private static void TimeIndependentIdentity(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor a = Contractor(ctx, ContractorForm.Solo);
            KnownCharacter c = Self(sb, a);
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            ContractorProfile profile = a.Get<ContractorProfile>();
            OperationalRole year1 = RoleDerivation.SoloRole(a.seed, profile.specialties);
            ExperienceBand band1 = ContractorService.Experience(a);
            ctx.Assert.Equal(year1, c.opRole, "the role stored at Instantiate is the year-1 derivation");
            // Ten years of a career: experience, doctrine, fame and funds all move.
            for (int y = 0; y < 10; y++)
            {
                sb.Clock.Now += Ticks.PerYear;
                sim.skill = System.Math.Min(1f, sim.skill + 0.08f);
                sim.doctrine.caution = (y % 3) / 3f;
                sim.doctrine.professionalism = 1f - y / 20f;
                sim.funds += 5000;
                a.reputation.SetScore(a.reputation.score + 400);
            }
            OperationalRole year10 = RoleDerivation.SoloRole(a.seed, profile.specialties);
            ctx.Assert.Equal(year1, year10, "the same origin facts give the identical role ten years later");
            ctx.Assert.Equal(year1, RoleDerivation.ForSolo(a), "ForSolo agrees");
            ctx.Assert.Equal(year1, c.opRole, "the stored role was never rewritten");
            ctx.Assert.True(ContractorService.Experience(a) >= band1, "while competence may change (" + band1 + " → " + ContractorService.Experience(a) + ")");
            // Determinism across inputs: the same (seed, specialties) always gives the same role; the version is pinned.
            for (int s = 1; s < 200; s++) ctx.Assert.Equal(RoleDerivation.SoloRole(s * 7919, profile.specialties), RoleDerivation.SoloRole(s * 7919, new List<string>(profile.specialties)), "seed " + s + " is deterministic");
            ctx.Assert.Equal(1, RoleDerivation.Version, "derivation version 1 is frozen");
        }

        // ================================================================== RT-PHYS-025, 026, 029

        private static void TruthfulAging(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            NetworkActor rare = Contractor(ctx, ContractorForm.Solo), often = Contractor(ctx, ContractorForm.Solo);
            KnownCharacter r = Self(sb, rare), o = Self(sb, often);
            Visit(ctx, rare, r);
            Visit(ctx, often, o);
            int[] years = { 1, 10, 70 };
            for (int i = 0; i < years.Length; i++)
            {
                int stored = r.pawn.agedThroughTick;
                for (int y = 0; y < years[i]; y++)
                {
                    sb.Clock.Now += Ticks.PerYear;
                    Visit(ctx, often, o); // met every year
                }
                long elapsed = Visit(ctx, rare, r); // met once, at the end
                ctx.Assert.Equal((long)years[i] * Ticks.PerYear, elapsed, years[i] + " game-years: the catch-up asked for is the full interval, uncapped");
                ctx.Assert.Equal((long)years[i] * Ticks.PerYear, (long)r.pawn.agedThroughTick - stored, "measured from the stored bookmark");
            }
            FakePhysicalWorldPort.Token tr = sb.Physical.TokenOf(r.pawn), to = sb.Physical.TokenOf(o.pawn);
            ctx.Assert.Equal(tr.agedTicks, to.agedTicks, "a rarely met person ages exactly as a frequently met one");
            ctx.Assert.Equal(81L * Ticks.PerYear, tr.agedTicks, "81 game-years in all");
        }

        /// <summary>One visit: materialize, leave normally, reconcile. Returns the aging catch-up the visit asked for (0 on a first creation).</summary>
        private static long Visit(RuntimeTestContext ctx, NetworkActor a, KnownCharacter c)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            int catchUps = sb.Physical.catchUps;
            PhysicalEpisode e = Begin(ctx, a, new[] { c });
            long elapsed = sb.Physical.catchUps > catchUps ? sb.Physical.lastCatchUp : 0L;
            sb.Physical.ExitNormally(c.pawn, 80);
            L(sb).Reconcile(e, "visit over");
            ctx.Assert.True(e.IsComplete, "the visit completed");
            return elapsed;
        }

        private static void FaultSweep(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            ctx.ExpectLog("step failed");
            NetworkActor a = Contractor(ctx, ContractorForm.Company);
            OrganizationProfile org = a.Get<OrganizationProfile>();
            KnownCharacter leader = Leader(sb, a);
            List<KnownCharacter> others = Others(sb, a);
            KnownCharacter lt = others[0];
            PhysicalEpisode e = Begin(ctx, a, new[] { leader, lt }, 2);
            // Nobody else may lead: the succession must PROMOTE (a new record, an id drawn), so the sweep covers the allocator too.
            for (int i = 1; i < others.Count; i++) others[i].status = CharacterStatus.Captured;
            List<EpisodeMember> anon = Anonymous(e);
            sb.Physical.Die(leader.pawn);
            sb.Physical.Die(lt.pawn);
            sb.Physical.Die(anon[0].pawn);
            sb.Physical.ExitNormally(anon[1].pawn, 70, 0.6f);
            List<MemberDecision> decisions = new List<MemberDecision>();
            for (int i = 0; i < e.members.Count; i++)
            {
                EpisodeMember m = e.members[i];
                PhysicalObservation obs = sb.Physical.Observe(m.pawn, e.id);
                bool unsup;
                MemberOutcome outcome = ReconciliationPlanner.Decide(obs, out unsup);
                decisions.Add(new MemberDecision { member = m, character = m.IsNamed ? sb.Ctx.characters.Get(m.character) : null, outcome = outcome, observation = obs, woundDays = outcome == MemberOutcome.Returned ? ReconciliationPlanner.WoundDaysFor(obs) : 0 });
            }
            ReconciliationPlan plan = ReconciliationPlanner.PlanEpisode(sb.Ctx, e, decisions, ReconciliationPlanner.CloseReconciled);
            ReconciliationPlanner.Validate(sb.Ctx, plan);
            ctx.Assert.True(plan.addsRecord, "the plan promotes a successor (it adds a record)");
            int n = plan.ops.Count;
            ctx.Assert.AtLeast(15, n, "a commit of many steps (" + n + ")");

            // Coverage proof (§ 15.7): every declared write is inside the touched set.
            CommitTarget probe = new CommitTarget { now = sb.Clock.Now, ids = sb.Ids, characters = sb.Ctx.characters, outbox = e.publications };
            DurableSnapshot touched = ReconciliationApplier.TouchedSet(plan, probe);
            for (int i = 0; i < n; i++)
            {
                foreach (object w in ReconciliationApplier.Writes(plan, plan.ops[i], probe))
                {
                    ctx.Assert.True(w == ReconciliationApplier.CharacterMembership || touched.Covers(w), "op " + i + " (" + plan.ops[i] + ") writes only inside the touched set");
                }
            }

            int events = sb.Events.events.Count, strips = sb.Physical.strips, chars = sb.Ctx.characters.Count;
            for (int k = 0; k <= n; k++)
            {
                LiveFingerprint before = Print(sb);
                CommitTarget t = new CommitTarget { now = sb.Clock.Now, ids = sb.Ids, characters = sb.Ctx.characters, outbox = e.publications };
                bool threw = false;
                try
                {
                    ReconciliationApplier.Commit(plan, t, k);
                }
                catch (InjectedFaultException)
                {
                    threw = true;
                }
                ctx.Assert.True(threw, "fault " + k + " was injected");
                Same(ctx, before, Print(sb), "a throw after " + k + " of " + n + " steps leaves the durable state exactly as before");
                ctx.Assert.False(e.consequencesApplied, "the flag stays false (fault " + k + ")");
                ctx.Assert.Equal(chars, sb.Ctx.characters.Count, "no promoted record survives (fault " + k + ")");
            }
            ctx.Assert.Equal(events, sb.Events.events.Count, "no event was published by any attempt");
            ctx.Assert.Equal(strips, sb.Physical.strips, "no release action ran");

            // Through the service: one faulted attempt, then the fault-free run applies once, then a re-run is a no-op.
            L(sb).commitFaultAfter = n / 2;
            ctx.Assert.False(L(sb).Reconcile(e, "faulted"), "a mid-commit fault applies nothing");
            ctx.Assert.True(L(sb).Reconcile(e, "retry"), "the fault-free run commits");
            ctx.Assert.Equal(chars + 1, sb.Ctx.characters.Count, "exactly one promoted record");
            ctx.Assert.Equal(CharacterStatus.Dead, leader.status, "the leader died once");
            ctx.Assert.Equal(1, org.succession.successions, "one succession");
            LiveFingerprint after = Print(sb);
            ctx.Assert.False(L(sb).Reconcile(e, "again"), "a re-run is a no-op");
            Same(ctx, after, Print(sb), "and changes nothing");
        }

        private static void ReleaseInterruption(RuntimeTestContext ctx)
        {
            RuntimeTestSandbox sb = ctx.RequireSandbox();
            ctx.ExpectLog("step failed"); // the injected release faults
            string[] actions = { "normalize", "retain", "strip" };
            for (int i = 0; i < actions.Length; i++)
            {
                NetworkActor a = Contractor(ctx, ContractorForm.Solo);
                KnownCharacter c = Self(sb, a);
                PhysicalEpisode e = Begin(ctx, a, new[] { c });
                FakePhysicalWorldPort.Token tok = sb.Physical.TokenOf(c.pawn);
                sb.Physical.ExitNormally(c.pawn, 90, 0.5f);
                sb.Physical.ThrowOn(actions[i]);
                L(sb).Reconcile(e, "test");
                ctx.Assert.True(e.consequencesApplied, actions[i] + ": the durable reconcile succeeded");
                ctx.Assert.False(e.releaseApplied, actions[i] + ": RELEASE is not complete");
                ctx.Assert.Equal((byte)i, e.members[0].releaseStep, actions[i] + ": the cursor holds exactly the actions that succeeded");
                ctx.Assert.Equal(CustodyState.Stored, c.custody, actions[i] + ": custody is already Stored");
                ctx.Assert.False(AuthorityGate.CanSimulateAbstractly(c), actions[i] + ": yet the gate stays closed (Stored alone is not enough)");
                ctx.Assert.Equal(Availability.Unavailable, sb.Ctx.Contractors.AvailabilityOf(a), actions[i] + ": procurement cannot select them");
                int until = c.woundedUntilTick;
                sb.Clock.Now = until + 1;
                sb.Ctx.Upkeep.UpkeepJob(new ScheduledJob { kind = ContractorService.UpkeepJob, target = a.id.Value });
                ctx.Assert.Equal(CharacterStatus.Wounded, c.status, actions[i] + ": abstract recovery does not run");
                L(sb).FinishPending(e);
                ctx.Assert.True(e.releaseApplied, actions[i] + ": the resume completes RELEASE");
                ctx.Assert.Equal(1, tok.normalized, actions[i] + ": normalized exactly once");
                ctx.Assert.Equal(1, tok.retainCalls, actions[i] + ": reserved exactly once");
                ctx.Assert.Equal(1, tok.tagStrips, actions[i] + ": tag stripped exactly once");
                ctx.Assert.True(AuthorityGate.CanSimulateAbstractly(c), actions[i] + ": only now abstract");
            }
            ctx.Assert.Zero(sb.Physical.passCalls, "a WorldFree member produced ZERO PassToWorld requests");

            // The fake enforces § 7.5 itself: a request for an already-passed pawn is rejected and recorded.
            NetworkActor any = Contractor(ctx, ContractorForm.Solo);
            KnownCharacter p = Self(sb, any);
            PhysicalEpisode pe = Begin(ctx, any, new[] { p });
            sb.Physical.ExitNormally(p.pawn, 91);
            bool rejected = false;
            try
            {
                sb.Physical.PassToWorld(p.pawn);
            }
            catch (PhysicalPreconditionException)
            {
                rejected = true;
            }
            ctx.Assert.True(rejected, "an invalid PassToWorld request is rejected");
            ctx.Assert.Equal(1, sb.Physical.passRejected, "and recorded");
            L(sb).Reconcile(pe, "test");

            // A bound pawn that was never placed is the one case the Network passes, after the precondition held.
            NetworkActor np = Contractor(ctx, ContractorForm.Solo);
            KnownCharacter q = Self(sb, np);
            sb.Physical.failPlace = true;
            PhysicalEpisode never = Begin(ctx, np, new[] { q });
            sb.Physical.failPlace = false;
            ctx.Assert.Equal(MemberOutcome.NeverPlaced, never.members[0].outcome, "never placed");
            ctx.Assert.True(never.IsComplete, "closed and released");
            ctx.Assert.Equal(1, sb.Physical.TokenOf(q.pawn).passedToWorld, "passed to the world exactly once (Decide)");
            ctx.Assert.Equal(CustodyState.Stored, q.custody, "the bound person is Stored");
        }
    }
}
