using System;
using System.Collections.Generic;
using System.Linq;
using TheNetwork.Diagnostics.RuntimeTests;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Physical;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Integration.Physical;
using Verse;

namespace TheNetwork.Tests
{
    public static class Phase32bPromotionTests
    {
        public static void Register(List<KeyValuePair<string, Action>> tests)
        {
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Promotion_PlanIsPureAndActualIdentityCommitsOnce", PurePlan));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Promotion_CaptureAboveSixConservesSamePawn", CaptureOverflow));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Promotion_StrongFirstPreservesDiscretionaryTarget", StrongFirst));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Promotion_LargePresenceAndUnknownEvidenceStayEphemeral", Ephemeral));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Promotion_AcceptedStrongSignalsOverflow", StrongSignals));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Promotion_CaravanAloneDoesNotIdentify", Caravan));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Promotion_RecruitLeavesOldNpcLivingMembership", Recruit));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Promotion_DeathRemovesExactlyOneHuman", Death));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Promotion_MultipleRecordsFullFaultSweepAndCoverage", FaultSweep));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Promotion_ValidationRejectsBadFactsAndDuplicateBindings", Validation));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Promotion_CrossEpisodeOwnerRejectsBeforeFirstIdentity", CrossEpisodeOwner));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Promotion_ActualAdapterReadsExactExistingPawnName", ActualAdapterFacts));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Promotion_PendingPeersCannotCommitIdentity", PendingPeers));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Promotion_LeaderLossGuardLeavesOrdinarySuccession", SuccessionBoundary));
            tests.Add(new KeyValuePair<string, Action>("Phys32b.Promotion_AbstractSuccessionPreservesOverflowMembership", AbstractOverflow));
        }

        private sealed class Fixture
        {
            public TestNet net = new TestNet();
            public NetworkActor actor;
            public OrganizationProfile org;
            public PhysicalEpisode episode;
            public List<MemberDecision> decisions = new List<MemberDecision>();

            public Fixture(int existing, int healthy = 0)
            {
                actor = new NetworkActor { id = new ActorId(net.ids.NextId()), kind = ActorKind.Organization, name = NameSnapshot.Org("Crew"), seed = 123, foundedTick = 1 };
                org = new OrganizationProfile { capacity = 7 }; actor.Add(org);
                actor.Add(new ContractorProfile());
                actor.Add(new ContractorSimulation());
                net.ctx.actors.Add(actor);
                org.TierOf(Tier.Regular).healthy = healthy;
                for (int i = 0; i < existing; i++)
                {
                    KnownCharacter c = new KnownCharacter { id = new CharacterId(net.ids.NextId()), name = NameSnapshot.Person("Old" + i, null, "Crew"), org = actor.id,
                        role = i == 0 ? CharacterRole.Leader : CharacterRole.Member, opRole = i == 0 ? OperationalRole.Leader : OperationalRole.Rifleman };
                    net.ctx.characters.Add(c); org.knownMembers.Add(c.id);
                    if (i == 0) org.leader = c.id;
                }
                episode = new PhysicalEpisode { id = new EpisodeId(net.ids.NextId()), actor = actor.id, state = EpisodeState.Open, createdTick = net.clock.Now - 10, purposeKey = "GroupVisit" };
                net.ctx.episodes.Add(episode);
            }

            public MemberDecision Slot(OperationalRole role = OperationalRole.Medic, bool p0 = false,
                MemberOutcome outcome = MemberOutcome.Returned, ConcretizationEvidence evidence = ConcretizationEvidence.None)
            {
                int index = episode.members.Count;
                Pawn pawn = new Pawn { thingIDNumber = 90000 + index, Name = new NameTriple("Actual" + index, "Nick" + index, "Surname" + index) };
                EpisodeMember m = new EpisodeMember { slot = index, tier = Tier.Regular, seatRole = role, state = MemberState.Present,
                    pawn = new PawnRef { pawn = pawn, thingIdNumber = pawn.thingIDNumber, defName = "Human", boundTick = episode.createdTick, agedThroughTick = episode.createdTick },
                    playerVisibleTick = net.clock.Now - 5, p0Eligible = p0 };
                episode.members.Add(m); org.TierOf(Tier.Regular, true).healthy++;
                PhysicalObservation observation = new PhysicalObservation { kind = outcome == MemberOutcome.Returned ? ObservedKind.WorldFree
                    : outcome == MemberOutcome.Killed ? ObservedKind.Dead : outcome == MemberOutcome.JoinedPlayer ? ObservedKind.JoinedPlayer : ObservedKind.HeldByPlayer,
                    exitEvidence = outcome == MemberOutcome.Returned, exitTick = net.clock.Now - 2, health = 1f };
                MemberDecision d = new MemberDecision { member = m, outcome = outcome, observation = observation,
                    captive = outcome == MemberOutcome.HeldByPlayer, holder = outcome == MemberOutcome.HeldByPlayer ? HeldKind.PlayerPrisoner : outcome == MemberOutcome.JoinedPlayer ? HeldKind.PlayerColonist : HeldKind.None,
                    promotionFacts = new PhysicalPromotionFacts { name = NameSnapshot.Person("Actual" + index, "Nick" + index, "Surname" + index), evidence = evidence } };
                decisions.Add(d); return d;
            }

            public ReconciliationPlan Plan()
            { ReconciliationPlan p = ReconciliationPlanner.PlanEpisode(net.ctx, episode, decisions, ReconciliationPlanner.CloseReconciled); ReconciliationPlanner.Validate(net.ctx, p); return p; }
            public CommitTarget Target()
            { return new CommitTarget { now = net.clock.Now, ids = net.ids, characters = net.ctx.characters, outbox = episode.publications }; }
            public int Living()
            { return org.Healthy + org.Wounded + org.Committed + org.knownMembers.Distinct().Count(id => OrganizationSeatPolicy.IsCurrentMember(net.ctx.characters.Get(id), actor.id)); }
            public LiveFingerprint Print()
            { return LiveFingerprint.Of(net.ctx, net.ids, net.scheduler, net.journal); }
        }

        private static void Same(Fixture f, LiveFingerprint before, string what)
        { T.Check(before.Diff(f.Print()).Count == 0, what + ": " + string.Join(";", before.Diff(f.Print()).ToArray())); }

        private static void Reason(string expected, Action action)
        {
            try { action(); T.Check(false, "expected refusal " + expected); }
            catch (PlanInvalidException ex) { T.Eq(expected, ex.reasonKey, "specific fail-closed refusal"); }
        }

        private static void PurePlan()
        {
            Fixture f = new Fixture(1); MemberDecision d = f.Slot(p0: true); LiveFingerprint before = f.Print(); int id = f.net.ids.PeekNextId;
            ReconciliationPlan p = f.Plan(); Same(f, before, "PLAN and VALIDATE changed no durable state");
            T.Check(!d.member.IsNamed && d.character == null, "caller decision and Episode member stay anonymous until commit");
            T.Eq(1, p.promotedCharacters.Count, "one prospective identity"); T.Eq(id, p.promotedCharacters[0].id.Value, "id is previewed without allocation");
            int living = f.Living(); CommitTarget t = f.Target(); ReconciliationApplier.Commit(p, t, -1);
            KnownCharacter c = f.net.ctx.characters.Get(d.member.character);
            T.Check(c != null && ReferenceEquals(c.pawn.pawn, d.member.pawn.pawn), "same actual pawn is bound");
            T.Eq(d.promotionFacts.name.Display, c.name.Display, "pawn actual full name preserved"); T.Eq(OperationalRole.Medic, c.opRole, "operational role preserved");
            T.Eq(CharacterRole.Member, c.role, "organization membership rank"); T.Eq(f.actor.id, c.org, "organization provenance");
            T.Eq(d.member.playerVisibleTick, c.firstEncounterTick, "first encounter derives from successful placement");
            T.Eq(CustodyState.Stored, c.custody, "returned promoted person is stored"); T.Eq(f.episode.id, c.episode, "membership remains through release");
            T.Eq(living, f.Living(), "one committed anonymous became exactly one living identity"); T.Eq(0, f.org.Committed, "anonymous checkout removed"); T.Eq(0, f.org.Healthy, "anonymous healthy copy was not restored");
            T.Eq(1, t.slotPromotions.Count, "bounded result collection"); T.Check(f.episode.consequencesApplied && !f.episode.releaseApplied, "consequences flag precedes required named release");
            T.Eq(ReleaseAction.Normalize, ReleasePolicy.ActionsFor(d.member)[0], "release now follows named return policy");
            Reason("AlreadyApplied", () => ReconciliationPlanner.Validate(f.net.ctx, p));
        }

        private static void CaptureOverflow()
        {
            Fixture f = new Fixture(6); MemberDecision held = f.Slot(outcome: MemberOutcome.HeldByPlayer); f.Slot(role: OperationalRole.Rifleman);
            int living = f.Living(); ReconciliationPlan p = f.Plan(); ReconciliationApplier.Commit(p, f.Target(), -1);
            KnownCharacter c = f.net.ctx.characters.Get(held.member.character);
            T.Eq(7, f.org.knownMembers.Count, "strong identity overflows normal six"); T.Eq(living, f.Living(), "held person not duplicated or dropped");
            T.Eq(CharacterStatus.Captured, c.status, "capture status applied through shared fate rule"); T.Eq(CustodyState.OutOfCustody, c.custody, "actual held custody");
            T.Eq(HeldKind.PlayerPrisoner, c.heldBy, "holder preserved"); T.Eq(f.net.clock.Now, c.heldSinceTick, "holding origin");
            T.Check(ReferenceEquals(c.pawn.pawn, held.member.pawn.pawn), "same captured pawn"); T.Eq(1, f.org.Healthy, "only ordinary return restores one anonymous human");
            T.Eq(0, f.org.Committed, "all checkouts consumed once"); T.Eq(1, p.captured, "capture counted once");
        }

        private static void StrongFirst()
        {
            Fixture f = new Fixture(5); MemberDecision p0 = f.Slot(p0: true); MemberDecision strong = f.Slot(outcome: MemberOutcome.HeldByPlayer);
            ReconciliationPlan p = f.Plan(); ReconciliationApplier.Commit(p, f.Target(), -1);
            T.Check(!p0.member.IsNamed && strong.member.IsNamed, "mandatory later-listed custody takes remaining normal seat before optional P0");
            T.Eq(6, f.org.knownMembers.Count, "discretionary never pushes living count above six"); T.Eq(1, f.org.Healthy, "unpromoted optional member returned as anonymous once");
            Fixture g = new Fixture(6); g.Slot(p0: true); g.Slot(outcome: MemberOutcome.HeldByPlayer); ReconciliationPlan q = g.Plan();
            T.Eq(1, q.promotedCharacters.Count, "strong overflow does not make discretionary overflow");
        }

        private static void Ephemeral()
        {
            Fixture f = new Fixture(2, 20); MemberDecision ordinary = f.Slot(); MemberDecision unknown = f.Slot(evidence: (ConcretizationEvidence)128);
            int living = f.Living(); ReconciliationPlan p = f.Plan(); ReconciliationApplier.Commit(p, f.Target(), -1);
            T.Eq(0, p.promotedCharacters.Count, "large presence or unknown signal creates no identities");
            T.Check(!ordinary.member.IsNamed && !unknown.member.IsNamed, "ordinary large detachment stays ephemeral"); T.Eq(living, f.Living(), "anonymous return conserves humans");
            Fixture g = new Fixture(1); MemberDecision missing = g.Slot(p0: true); missing.member.playerVisibleTick = -1;
            T.Eq(0, g.Plan().promotedCharacters.Count, "old absent placement never invents encounter");
            Fixture h = new Fixture(1); MemberDecision future = h.Slot(p0: true); future.member.playerVisibleTick = h.net.clock.Now + 1;
            T.Eq(0, h.Plan().promotedCharacters.Count, "future placement never invents encounter");
        }

        private static void StrongSignals()
        {
            foreach (ConcretizationEvidence signal in new[] { ConcretizationEvidence.DeliberateIdentification, ConcretizationEvidence.PlayerCombat, ConcretizationEvidence.PlayerRelation, ConcretizationEvidence.FormerColonist })
            {
                Fixture f = new Fixture(6); MemberDecision d = f.Slot(evidence: signal); d.member.playerVisibleTick = -1;
                ReconciliationPlan p = f.Plan(); ReconciliationApplier.Commit(p, f.Target(), -1);
                T.Check(d.member.IsNamed, "accepted strong signal promotes: " + signal); T.Eq(7, f.org.knownMembers.Count, "accepted strong overflow: " + signal);
                T.Eq(-1, f.net.ctx.characters.Get(d.member.character).firstEncounterTick, "unknown encounter tick remains unknown");
            }
        }

        private static void Caravan()
        {
            Fixture f = new Fixture(1); MemberDecision d = f.Slot(outcome: MemberOutcome.HeldByPlayer); d.captive = false; d.holder = HeldKind.PlayerCaravan; d.observation.kind = ObservedKind.InCaravan;
            T.Check(!ReconciliationPlanner.HasStrongEvidence(d), "ordinary caravan is no material identity evidence");
            Reason("AnonymousHeldWithoutIdentity", () => f.Plan());
            T.Check(!d.member.IsNamed && !f.episode.consequencesApplied && f.org.Committed == 1, "refusal retains Episode-owned anonymous person");
            bool unsupported, captive; HeldKind held; MemberOutcome result = ReconciliationPlanner.Decide(new PhysicalObservation { kind = ObservedKind.HeldByPlayer }, false, true, out unsupported, out held, out captive);
            T.Eq(MemberOutcome.HeldByPlayer, result, "structured group can decide positive held outcome before identity"); T.Check(!unsupported && captive, "supported group capture facts");
            T.Eq(MemberOutcome.Pending, ReconciliationPlanner.Decide(new PhysicalObservation { kind = ObservedKind.HeldByPlayer }, false, out unsupported, out held, out captive), "legacy anonymous custody still refuses");
            T.Check(unsupported, "unsupported legacy held flag preserved");
        }

        private static void Recruit()
        {
            Fixture f = new Fixture(6); MemberDecision d = f.Slot(outcome: MemberOutcome.JoinedPlayer); int living = f.Living(); ReconciliationPlan p = f.Plan(); ReconciliationApplier.Commit(p, f.Target(), -1);
            KnownCharacter c = f.net.ctx.characters.Get(d.member.character);
            T.Eq(7, f.org.knownMembers.Count, "recruit identity remains historical even above six"); T.Eq(CharacterStatus.Defected, c.status, "existing recruited status semantics");
            T.Eq(CustodyState.OutOfCustody, c.custody, "player still owns real pawn"); T.Eq(HeldKind.PlayerColonist, c.heldBy, "colonist holder");
            T.Eq(living - 1, f.Living(), "recruit removes exactly one old NPC human"); T.Check(!OrganizationSeatPolicy.IsCurrentMember(c, f.actor.id), "defected identity is not current old-NPC membership");
            T.Check(!AuthorityGate.CanSimulateAbstractly(c), "recruit cannot run old NPC simulation"); T.Check(ReferenceEquals(c.pawn.pawn, d.member.pawn.pawn), "actual recruited pawn retained");
        }

        private static void Death()
        {
            Fixture f = new Fixture(1); MemberDecision d = f.Slot(p0: true, outcome: MemberOutcome.Killed); int living = f.Living(); ReconciliationPlan p = f.Plan(); ReconciliationApplier.Commit(p, f.Target(), -1);
            KnownCharacter c = f.net.ctx.characters.Get(d.member.character);
            T.Eq(CharacterStatus.Dead, c.status, "physically met identity records actual death"); T.Eq(CustodyState.Released, c.custody, "corpse left to vanilla");
            T.Eq(living - 1, f.Living(), "one dead human removed, never two"); T.Eq(0, f.org.Healthy + f.org.Committed, "dead slot has no anonymous copy"); T.Eq(1, p.killed, "one death report");
        }

        private static void FaultSweep()
        {
            Fixture f = new Fixture(1); f.Slot(p0: true); f.Slot(outcome: MemberOutcome.HeldByPlayer); f.Slot(evidence: ConcretizationEvidence.PlayerCombat);
            int living = f.Living(), chars = f.net.ctx.characters.Count, next = f.net.ids.PeekNextId; ReconciliationPlan p = f.Plan();
            T.Eq(3, p.promotedCharacters.Count, "bounded multi-record plan"); T.Eq(next, p.promotedCharacters[0].id.Value, "first preview id"); T.Eq(next + 2, p.promotedCharacters[2].id.Value, "distinct sequential last preview id");
            CommitTarget probe = f.Target(); DurableSnapshot touched = ReconciliationApplier.TouchedSet(p, probe);
            foreach (CommitOp op in p.ops)
                foreach (object write in ReconciliationApplier.Writes(p, op, probe))
                    T.Check(ReferenceEquals(write, ReconciliationApplier.CharacterMembership) || touched.Covers(write), "declared write covered: " + op.kind);
            for (int k = 0; k <= p.ops.Count; k++)
            {
                LiveFingerprint before = f.Print(); CommitTarget t = f.Target(); int point = k;
                T.Throws(() => ReconciliationApplier.Commit(p, t, point), "fault before/after every assignment " + k);
                Same(f, before, "fault " + k + " restores all durable state"); T.Eq(chars, f.net.ctx.characters.Count, "record membership rollback " + k);
                T.Eq(next, f.net.ids.PeekNextId, "allocator rollback " + k); T.Eq(living, f.Living(), "human rollback " + k);
                T.Eq(0, t.slotPromotions.Count, "runtime result rollback " + k); T.Check(!f.episode.consequencesApplied && f.episode.publications.Count == 0, "flag/outbox rollback " + k);
                foreach (KnownCharacter c in p.promotedCharacters) { T.Check(f.net.ctx.characters.Get(c.id) == null, "rollback repairs store index " + k); T.Eq(CustodyState.Deployed, c.custody, "same immutable prospective payload retry " + k); }
                T.Check(f.episode.members.All(m => !m.IsNamed && m.state == MemberState.Present), "anonymous bindings and member state rollback " + k);
            }
            CommitTarget success = f.Target(); ReconciliationApplier.Commit(p, success, -1);
            T.Eq(chars + 3, f.net.ctx.characters.Count, "retry adds each identity once"); T.Eq(next + 3, f.net.ids.PeekNextId, "retry draws each id once"); T.Eq(living, f.Living(), "retry preserves human count");
            T.Eq(3, success.slotPromotions.Count, "all records returned"); T.Check(f.episode.publications.Count <= PhysicalEpisode.MaxPublications, "multi-promotion respects existing bounded outbox");
        }

        private static void Validation()
        {
            Fixture f = new Fixture(1); MemberDecision d = f.Slot(outcome: MemberOutcome.HeldByPlayer); d.promotionFacts = null; LiveFingerprint before = f.Print();
            Reason("PromotionNameUnknown", () => f.Plan()); Same(f, before, "unknown actual name cannot half-promote");
            Fixture g = new Fixture(1); g.Slot(p0: true); ReconciliationPlan p = g.Plan(); p.promotedCharacters[0].opRole = OperationalRole.Heavy;
            Reason("PromotionProvenance", () => ReconciliationPlanner.Validate(g.net.ctx, p));
            Fixture h = new Fixture(1); MemberDecision first = h.Slot(p0: true), second = h.Slot(p0: true); second.member.pawn = first.member.pawn.Copy();
            Reason("SharedPawn", () => h.Plan());
            Fixture j = new Fixture(1); MemberDecision named = j.Slot(p0: true); KnownCharacter existing = j.net.ctx.characters.Get(j.org.leader); existing.pawn = named.member.pawn.Copy();
            Reason("PawnAlreadyNamed", () => j.Plan());
            Fixture k = new Fixture(1); k.Slot(p0: true); ReconciliationPlan q = k.Plan(); k.net.ids.NextId();
            Reason("PromotionId", () => ReconciliationPlanner.Validate(k.net.ctx, q));
            Fixture crossed = new Fixture(1); crossed.Slot(p0: true); crossed.Slot(p0: true); ReconciliationPlan cross = crossed.Plan();
            CommitOp[] promotions = cross.ops.Where(op => op.kind == CommitOpKind.SlotPromotion).ToArray();
            EpisodeMember swap = promotions[0].member; promotions[0].member = promotions[1].member; promotions[1].member = swap;
            Reason("PromotionPayload", () => ReconciliationPlanner.Validate(crossed.net.ctx, cross));
        }

        private static void CrossEpisodeOwner()
        {
            Fixture f = new Fixture(1); MemberDecision d = f.Slot(p0: true);
            NetworkActor secondActor = new NetworkActor { id = new ActorId(f.net.ids.NextId()), kind = ActorKind.Organization };
            OrganizationProfile secondOrg = new OrganizationProfile { capacity = 7 };
            secondOrg.TierOf(Tier.Regular, true).healthy = 1;
            secondActor.Add(secondOrg); f.net.ctx.actors.Add(secondActor);
            EpisodeMember alias = new EpisodeMember { slot = 0, seatRole = OperationalRole.Heavy, tier = Tier.Regular,
                state = MemberState.Present, pawn = d.member.pawn.Copy() };
            PhysicalEpisode other = new PhysicalEpisode { id = new EpisodeId(f.net.ids.NextId()), actor = secondActor.id, state = EpisodeState.Open };
            other.members.Add(alias); f.net.ctx.episodes.Add(other);
            int chars = f.net.ctx.characters.Count, next = f.net.ids.PeekNextId;
            PawnRef original = d.member.pawn, otherBinding = alias.pawn;
            LiveFingerprint before = f.Print();
            Reason("PawnHasAnotherEpisodeOwner", () => f.Plan());
            Same(f, before, "cross-Episode owner refusal precedes every durable assignment");
            T.Eq(chars, f.net.ctx.characters.Count, "no first identity can choose one of two org provenances");
            T.Eq(next, f.net.ids.PeekNextId, "cross-Episode refusal allocates no identity");
            T.Eq(1, f.org.Committed, "original checkout remains protected");
            T.Eq(1, secondOrg.Committed, "other owner's checkout is untouched");
            T.Check(!d.member.IsNamed && !alias.IsNamed, "both owners remain anonymous after refusal");
            T.Check(ReferenceEquals(original, d.member.pawn) && ReferenceEquals(otherBinding, alias.pawn), "same binding objects remain owned by their Episodes");
            other.releaseApplied = true;
            ReconciliationPlan allowed = f.Plan();
            T.Eq(1, allowed.promotedCharacters.Count, "released historical owner does not block current identity");
            T.Check(!d.member.IsNamed && !alias.IsNamed, "allowed PLAN still creates no durable identity");
        }

        private sealed class UnknownName : Name
        {
            public override string ToStringFull => "Unsupported existing name";
            public override string ToStringShort => ToStringFull;
            public override bool IsValid => true;
            public override bool Numerical => false;
            public override bool ConfusinglySimilarTo(Name other) { return false; }
            public override void ExposeData() { }
        }

        private static void ActualAdapterFacts()
        {
            Fixture f = new Fixture(1); MemberDecision d = f.Slot(p0: true);
            Pawn pawn = d.member.pawn.pawn;
            PawnRef binding = d.member.pawn;
            Name actual = pawn.Name;
            LiveFingerprint before = f.Print();
            RimWorldPhysicalWorldPort port = new RimWorldPhysicalWorldPort(f.net.ctx);
            Game previousGame = Current.Game;
            try
            {
                // The real collector has no optional BattleLog/TickManager source in this headless fixture.
                Current.Game = null;
                PhysicalPromotionFacts triple = port.ReadPromotionFacts(f.episode, d.member);
                T.Check(triple != null && triple.name != null, "actual adapter reads the exact durable Episode's bound Pawn");
                T.Eq(((NameTriple)actual).First, triple.name.first, "actual NameTriple first field");
                T.Eq(((NameTriple)actual).Nick, triple.name.nick, "actual NameTriple nickname field");
                T.Eq(((NameTriple)actual).Last, triple.name.last, "actual NameTriple last field");
                T.Eq(actual.ToStringFull, triple.name.display, "actual vanilla full-name display");
                T.Eq(ConcretizationEvidence.None, triple.evidence, "unavailable optional logs create no positive evidence");
                T.Check(port.lastEvidenceScan != null && (port.lastEvidenceScan.collectionFailed || port.lastEvidenceScan.invalidWindow), "optional history was unavailable without erasing the name");
                T.Check(ReferenceEquals(actual, pawn.Name) && ReferenceEquals(binding, d.member.pawn), "name reading replaces neither Pawn name nor binding");
                Same(f, before, "actual fact reading creates no identity and writes no durable Network state");

                NameSingle single = new NameSingle("Existing Single Name"); pawn.Name = single;
                PhysicalPromotionFacts one = port.ReadPromotionFacts(f.episode, d.member);
                T.Eq(single.ToStringFull, one.name.display, "actual NameSingle full display preserved");
                T.Check(one.name.first == null && one.name.nick == null && one.name.last == null, "single name is never split into fabricated person fields");
                T.Check(ReferenceEquals(single, pawn.Name) && ReferenceEquals(pawn, d.member.pawn.pawn), "actual single-name Pawn is untouched");
                T.Eq(ConcretizationEvidence.None, one.evidence, "missing optional log preserves single name without evidence");

                d.member.pawn.thingIdNumber++;
                T.Check(port.ReadPromotionFacts(f.episode, d.member) == null, "mismatched durable thing ID cannot substitute a Pawn");
                d.member.pawn.thingIdNumber--;
                d.member.pawn.pawn = null;
                T.Check(port.ReadPromotionFacts(f.episode, d.member) == null, "unresolved same-Pawn reference has no name facts");
                d.member.pawn.pawn = pawn;
                T.Check(port.ReadPromotionFacts(new PhysicalEpisode { id = f.episode.id }, d.member) == null, "matching Episode ID without the exact durable Episode is refused");
                T.Check(port.ReadPromotionFacts(f.episode, new EpisodeMember { pawn = binding }) == null, "foreign member cannot obtain name facts through another binding");

                pawn.Name = null;
                T.Check(port.ReadPromotionFacts(f.episode, d.member).name == null, "missing actual name is never generated by fact collection");
                pawn.Name = new UnknownName();
                T.Check(port.ReadPromotionFacts(f.episode, d.member).name == null, "unknown name shape fails closed without parsing its display");
                T.Check(!d.member.IsNamed && d.character == null && f.net.ctx.characters.Count == 1, "all adapter queries leave identity for atomic reconciliation");
            }
            finally
            {
                pawn.Name = actual;
                d.member.pawn.pawn = pawn;
                d.member.pawn.thingIdNumber = pawn.thingIDNumber;
                Current.Game = previousGame;
            }
        }

        private static void PendingPeers()
        {
            Fixture f = new Fixture(1); MemberDecision held = f.Slot(outcome: MemberOutcome.HeldByPlayer); MemberDecision peer = f.Slot(); peer.outcome = MemberOutcome.Pending;
            LiveFingerprint before = f.Print(); Reason("NotTerminal", () => f.Plan()); Same(f, before, "held identity waits for pending peers");
            T.Check(!held.member.IsNamed && f.net.ctx.characters.Count == 1 && f.org.Committed == 2, "temporary slot ownership survives uncommitted held decision");
        }

        private static void SuccessionBoundary()
        {
            Fixture f = new Fixture(1, 1); f.Slot(p0: true); KnownCharacter leader = f.net.ctx.characters.Get(f.org.leader);
            leader.episode = f.episode.id; leader.custody = CustodyState.Deployed;
            EpisodeMember m = new EpisodeMember { character = leader.id, seatRole = OperationalRole.Leader, state = MemberState.Present };
            f.episode.members.Add(m); f.decisions.Add(new MemberDecision { member = m, character = leader, outcome = MemberOutcome.Killed, observation = new PhysicalObservation { kind = ObservedKind.Dead } });
            LiveFingerprint before = f.Print(); Reason("MixedPromotionSuccession", () => f.Plan()); Same(f, before, "unsupported virtual-roster succession changes nothing");
            f.decisions[0].member.p0Eligible = false; ReconciliationPlan p = f.Plan(); ReconciliationApplier.Commit(p, f.Target(), -1);
            T.Check(f.org.leader != leader.id && f.net.ctx.characters.Get(f.org.leader) != null, "existing ordinary anonymous-return succession remains supported");
        }

        private static void AbstractOverflow()
        {
            Fixture f = new Fixture(6, 1); FateRules.SuccessionPlan s = new FateRules.SuccessionPlan { oldLeader = f.org.leader, promote = true, promoteFrom = Tier.Regular, promotedName = NameSnapshot.Person("Successor", null, "Crew") };
            KnownCharacter c = FateRules.ApplyPromotion(f.actor, f.org, s, f.net.ids, f.net.ctx.characters, f.net.clock.Now);
            T.Eq(7, f.org.knownMembers.Count, "necessary abstract successor is listed above six"); T.Check(f.org.knownMembers.Contains(c.id), "overflow successor membership exists");
            FateRules.ApplyNewLeader(f.actor, f.org, f.net.ctx.characters, s, c, f.net.clock.Now); T.Eq(7, f.org.knownMembers.Count, "leader attachment does not duplicate member");
        }
    }
}
