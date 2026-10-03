using System;
using System.Collections.Generic;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Operations;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;

namespace TheNetwork.Domain.Physical
{
    /// <summary>
    /// DECIDE, PLAN and VALIDATE of the reconciliation (PHYSICAL_LIFECYCLE § 15.2 steps 2–4). Everything here READS state and
    /// returns data: no store, record, job, event, port or vanilla object is changed, and the only random stream used is the
    /// deterministic, seeded one the shared succession rule already uses (so a restored-and-retried plan decides the same thing).
    ///
    /// The per-person consequences are the SHARED fate rules (<see cref="FateRules"/>), the same ones the abstract casualty path
    /// calls; the per-operation bookkeeping is not part of a physical episode (§ 15.5). <see cref="PlanCasualties"/> runs an
    /// abstract <see cref="CasualtyReport"/> through this same planner and Applier: the parity proof of RT-PHYS-027.
    /// </summary>
    public static class ReconciliationPlanner
    {
        public const string CloseReconciled = "Reconciled";
        public const string CloseNeverPlaced = "NeverPlaced";
        public const string CloseDetached = "Detached";

        /// <summary>What a fate means for one person in a plan (the abstract vocabulary plus the physical Lost).</summary>
        private enum PlannedFate : byte
        {
            Killed = 0,
            Wounded = 1,
            Captured = 2,
            Missing = 3,
            Lost = 4
        }

        private struct FateEntry
        {
            public KnownCharacter character;
            public PlannedFate fate;
            public int woundDays;
            public string causeKey;
        }

        // ================================================================== DECIDE

        /// <summary>
        /// The bounded, monotone mapping from observed injury to abstract recovery (§ 10.5): ≥ 0.9 none; 0.7–0.9 ≈ 3 days;
        /// 0.4–0.7 ≈ 8; below ≈ 15. A downed pawn is at least moderately hurt. No per-hediff mirror, no health simulator.
        /// </summary>
        public static int WoundDaysFor(PhysicalObservation o)
        {
            if (o == null) return 0;
            float h = o.health;
            if (o.downed && h > 0.69f) h = 0.69f;
            if (h >= 0.9f) return 0;
            if (h >= 0.7f) return 3;
            if (h >= 0.4f) return 8;
            return 15;
        }

        /// <summary>
        /// DECIDE for one placed member (§ 15.3, § 9.3). Terminal only on POSITIVE evidence: Returned needs <c>WorldFree</c> AND
        /// the exit evidence ("not spawned" is never "returned"). Every held custody (player prisoner, colonist, kidnapped, held by
        /// another faction, a caravan) is not supported before 3.2 and is reported as <paramref name="unsupported"/>: the caller
        /// quarantines the episode, the pawn is untouched and the person stays blocked (§ 17). Anything else stays Pending.
        /// </summary>
        public static MemberOutcome Decide(PhysicalObservation o, out bool unsupported)
        {
            unsupported = false;
            if (o == null) return MemberOutcome.Pending;
            switch (o.kind)
            {
                case ObservedKind.Dead: return MemberOutcome.Killed;
                case ObservedKind.Gone: return MemberOutcome.Lost;
                case ObservedKind.WorldFree: return o.exitEvidence ? MemberOutcome.Returned : MemberOutcome.Pending;
                case ObservedKind.HeldByPlayer:
                case ObservedKind.JoinedPlayer:
                case ObservedKind.Kidnapped:
                case ObservedKind.HeldByOther:
                case ObservedKind.InCaravan:
                    unsupported = true;
                    return MemberOutcome.Pending;
                default:
                    // Spawned (here or elsewhere), InTransport, WorldOther, Unknown, None: the member stays Present.
                    return MemberOutcome.Pending;
            }
        }

        // ================================================================== PLAN

        /// <summary>
        /// PLAN for an episode whose every member has a terminal decision (§ 15.2 step 3). <paramref name="closeReasonKey"/> is
        /// Reconciled, NeverPlaced (nothing was placed) or Detached (prepare-for-removal settle).
        /// </summary>
        public static ReconciliationPlan PlanEpisode(DomainContext ctx, PhysicalEpisode e, List<MemberDecision> decisions, string closeReasonKey)
        {
            ReconciliationPlan p = new ReconciliationPlan
            {
                episode = e,
                actor = ctx.actors.Get(e.actor),
                now = ctx.Now,
                closeReasonKey = closeReasonKey
            };
            p.decisions.AddRange(decisions);
            if (p.actor == null) return p; // VALIDATE refuses it (ActorMissing): nothing is planned against a missing actor
            p.org = p.actor.Get<OrganizationProfile>();
            p.sim = p.actor.Get<ContractorSimulation>();
            if (e.cause.operation.IsValid) p.operation = ctx.operations?.Get(e.cause.operation);

            List<FateEntry> fates = new List<FateEntry>();
            Dictionary<int, MemberOutcome> planned = new Dictionary<int, MemberOutcome>();
            Dictionary<Tier, int> anonHealthyBack = new Dictionary<Tier, int>();
            int anonKilled = 0, anonWounded = 0, anonLost = 0;
            MemberDecision soloReturn = null;
            bool anyRelease = false;

            for (int i = 0; i < decisions.Count; i++)
            {
                MemberDecision d = decisions[i];
                EpisodeMember m = d.member;
                CommitOp done = p.Add(CommitOpKind.MemberDone);
                done.member = m;
                done.outcome = d.outcome;
                done.observed = d.observation != null ? d.observation.kind : ObservedKind.None;
                if (ReleasePolicy.ActionsFor(m.IsBound, m.IsNamed, d.outcome).Length > 0) anyRelease = true;

                if (m.IsNamed)
                {
                    KnownCharacter c = d.character;
                    p.Touch(c);
                    if (c == null) continue; // VALIDATE refuses it
                    planned[c.id.Value] = d.outcome;
                    switch (d.outcome)
                    {
                        case MemberOutcome.Killed:
                            fates.Add(new FateEntry { character = c, fate = PlannedFate.Killed, causeKey = "Physical" });
                            break;
                        case MemberOutcome.Lost:
                            fates.Add(new FateEntry { character = c, fate = PlannedFate.Lost });
                            p.lost++;
                            break;
                        case MemberOutcome.Returned:
                            p.returned++;
                            CommitOp stored = p.Add(CommitOpKind.CharacterStored);
                            stored.character = c;
                            stored.member = m;
                            // The positive return is authoritative for the person's story status too (a rescued Missing or Captured
                            // person is not left Missing forever): injured ⇒ the shared wound rule (which resolves Missing/Captured to
                            // Wounded), unhurt ⇒ the shared return rule (Missing/Captured ⇒ Active). Neither ever revives Dead or Lost.
                            if (d.woundDays > 0) fates.Add(new FateEntry { character = c, fate = PlannedFate.Wounded, woundDays = d.woundDays });
                            else if (c.status == CharacterStatus.Missing || c.status == CharacterStatus.Captured) p.Add(CommitOpKind.CharacterReturnedFree).character = c;
                            if (p.org == null && c.id == p.actor.bindings.embodies) soloReturn = d;
                            break;
                        case MemberOutcome.NeverPlaced:
                            p.neverPlaced++;
                            p.Add(CommitOpKind.CharacterReverted).character = c;
                            break;
                        case MemberOutcome.Detached:
                            p.Add(CommitOpKind.CharacterDetached).character = c;
                            break;
                    }
                }
                else
                {
                    switch (d.outcome)
                    {
                        case MemberOutcome.Returned:
                        case MemberOutcome.NeverPlaced:
                        case MemberOutcome.Detached:
                            if (d.outcome == MemberOutcome.Returned) p.returned++;
                            if (d.outcome == MemberOutcome.NeverPlaced) p.neverPlaced++;
                            CommitOp back = p.Add(CommitOpKind.AnonymousBack);
                            back.member = m;
                            back.tier = m.tier;
                            back.woundDays = d.outcome == MemberOutcome.Returned ? d.woundDays : 0;
                            if (back.woundDays > 0) anonWounded++;
                            else
                            {
                                int n;
                                anonHealthyBack.TryGetValue(m.tier, out n);
                                anonHealthyBack[m.tier] = n + 1;
                            }
                            break;
                        case MemberOutcome.Killed:
                        case MemberOutcome.Lost:
                            CommitOp gone = p.Add(CommitOpKind.AnonymousLost);
                            gone.member = m;
                            gone.tier = m.tier;
                            if (d.outcome == MemberOutcome.Killed) anonKilled++;
                            else
                            {
                                anonLost++;
                                p.lost++;
                            }
                            break;
                    }
                }
            }

            OrganizationProfile org = p.org;
            Func<KnownCharacter, bool> eligible = c =>
            {
                MemberOutcome o;
                if (planned.TryGetValue(c.id.Value, out o))
                {
                    // A member of THIS episode: eligible only if it comes back (or never left), alive and free.
                    return (o == MemberOutcome.Returned || o == MemberOutcome.NeverPlaced)
                        && c.IsAlive && c.status != CharacterStatus.Captured && c.status != CharacterStatus.Missing;
                }
                return c.IsAlive && c.status != CharacterStatus.Captured && c.status != CharacterStatus.Missing && AuthorityGate.CanSimulateAbstractly(c);
            };
            Func<Tier, int> healthyOf = t =>
            {
                int back;
                anonHealthyBack.TryGetValue(t, out back);
                return (org == null ? 0 : FateRules.PeekHealthy(org, t)) + back;
            };
            PlanFates(ctx, p, fates, anonKilled, anonWounded, 0, anonLost, e.cause.contract, e.cause.operation, eligible, healthyOf, true);

            // The returned Solo's hidden anchor, written once (§ 12.2). An organization's main body never moved, so it is left alone.
            if (soloReturn != null && p.sim != null && p.sim.spatial.IsInitialized)
            {
                TileRef tile = soloReturn.observation?.tile;
                if (tile == null || tile.tileId < 0) tile = e.whereTile;
                if (tile != null && tile.tileId >= 0) p.Add(CommitOpKind.SoloAnchor).tile = tile;
            }
            if (p.operation != null)
            {
                p.hasFollowUp = true;
                CommitOp marker = p.Add(CommitOpKind.OperationMarker);
                // A closed rescue resolves its operation (found / written off). An episode that never placed anyone, or was detached,
                // decided nothing about it: the operation goes back to its own abstract path (resolution None).
                marker.resolution = closeReasonKey != CloseReconciled ? PhysicalResolution.None
                    : (p.returned > 0 ? PhysicalResolution.Found : PhysicalResolution.WrittenOff);
            }

            PublicationSpec closed = new PublicationSpec
            {
                typeKey = EventKeys.EpisodeClosed,
                importance = Importance.Minor,
                actor = p.actor.id,
                actorName = p.actor.name.Display,
                episode = e.id,
                contract = e.cause.contract,
                operation = e.cause.operation,
                purposeKey = e.purposeKey,
                reasonKey = closeReasonKey,
                returned = p.returned,
                killed = p.killed,
                missing = p.lost,
                neverPlaced = p.neverPlaced
            };
            Publications.Subjects(closed, p.actor.id.Ref, e.id.Ref);
            p.Add(CommitOpKind.Publication).spec = closed;

            p.hasReleaseActions = anyRelease || p.actorEndKey != null;
            p.Add(CommitOpKind.EpisodeClosed).key = closeReasonKey;
            CommitOp markers = p.Add(CommitOpKind.StageMarkers);
            markers.flag = !p.hasReleaseActions;
            markers.followUpDone = !p.hasFollowUp;
            if (!p.hasReleaseActions)
            {
                // § 8.1 rule 5: nothing physical is left to release, so COMPLETE's assignments happen here.
                for (int i = 0; i < decisions.Count; i++)
                {
                    KnownCharacter c = decisions[i].character;
                    if (c != null && c.episode == e.id) p.Add(CommitOpKind.CharacterUnlink).character = c;
                }
            }
            if (p.sim != null) p.Add(CommitOpKind.SimDirty);
            return p;
        }

        /// <summary>
        /// The parity path (RT-PHYS-027): an abstract <see cref="CasualtyReport"/> planned through the SAME fate planning the
        /// physical commit uses, for a person set the abstract path would touch (the gate excludes the same people). The caller
        /// adds the per-operation bookkeeping that only an operation's own resolution does.
        /// </summary>
        public static ReconciliationPlan PlanCasualties(DomainContext ctx, NetworkActor a, CasualtyReport r, ContractId contract, OperationId op)
        {
            ReconciliationPlan p = new ReconciliationPlan { actor = a, now = ctx.Now, closeReasonKey = null };
            p.org = a.Get<OrganizationProfile>();
            p.sim = a.Get<ContractorSimulation>();
            List<FateEntry> fates = new List<FateEntry>();
            Dictionary<int, Fate> planned = new Dictionary<int, Fate>();
            for (int i = 0; i < r.fates.Count; i++)
            {
                CharacterFate f = r.fates[i];
                KnownCharacter c = ctx.characters.Get(f.character);
                if (c == null || f.fate == Fate.Unharmed || !AuthorityGate.CanSimulateAbstractly(c)) continue;
                PlannedFate pf = f.fate == Fate.Killed ? PlannedFate.Killed : f.fate == Fate.Wounded ? PlannedFate.Wounded : f.fate == Fate.Captured ? PlannedFate.Captured : PlannedFate.Missing;
                fates.Add(new FateEntry { character = c, fate = pf, woundDays = r.woundDays, causeKey = "Operation" });
                planned[c.id.Value] = f.fate;
                p.Touch(c);
            }
            OrganizationProfile org = p.org;
            Func<KnownCharacter, bool> eligible = c =>
            {
                Fate f;
                if (planned.TryGetValue(c.id.Value, out f) && f != Fate.Wounded) return false;
                return c.IsAlive && c.status != CharacterStatus.Captured && c.status != CharacterStatus.Missing && AuthorityGate.CanSimulateAbstractly(c);
            };
            Func<Tier, int> healthyOf = t => org == null ? 0 : FateRules.PeekHealthy(org, t);
            PlanFates(ctx, p, fates, CasualtyReport.Sum(r.killed), CasualtyReport.Sum(r.wounded), CasualtyReport.Sum(r.captured), CasualtyReport.Sum(r.missing), contract, op, eligible, healthyOf, false);
            p.hasReleaseActions = p.actorEndKey != null;
            if (p.sim != null) p.Add(CommitOpKind.SimDirty);
            return p;
        }

        /// <summary>
        /// The shared per-person and group consequences, in the order the abstract casualty path applies them: each fate (with its
        /// person-level publication), the group's casualty publication and morale shock, the morale descriptor, then the end of a
        /// Solo or the succession of a lost leader (decided here, from the plan's projected fates).
        /// </summary>
        private static void PlanFates(DomainContext ctx, ReconciliationPlan p, List<FateEntry> fates, int anonKilled, int anonWounded, int anonCaptured, int anonMissing,
            ContractId contract, OperationId op, Func<KnownCharacter, bool> eligible, Func<Tier, int> healthyOf, bool physical)
        {
            NetworkActor a = p.actor;
            OrganizationProfile org = p.org;
            int killed = anonKilled, wounded = anonWounded, captured = anonCaptured, missing = anonMissing;
            bool leaderLost = false;
            CharacterId oldLeader = org != null ? org.leader : CharacterId.None;
            KnownCharacter selfDiesAs = null;
            string selfEndKey = null;

            for (int i = 0; i < fates.Count; i++)
            {
                FateEntry f = fates[i];
                KnownCharacter c = f.character;
                bool isLeader = org != null ? c.id == org.leader : c.id == a.bindings.embodies;
                CommitOp op1;
                switch (f.fate)
                {
                    case PlannedFate.Killed:
                        killed++;
                        op1 = p.Add(CommitOpKind.CharacterKilled);
                        op1.character = c;
                        op1.key = f.causeKey;
                        op1.flag = physical;
                        p.Add(CommitOpKind.Publication).spec = Publications.Character(EventKeys.CharacterKilled, isLeader ? Importance.Major : Importance.Notable, a, c, contract, op, isLeader);
                        if (isLeader && org != null)
                        {
                            leaderLost = true;
                            p.Add(CommitOpKind.Publication).spec = Publications.Character(EventKeys.LeaderKilled, Importance.Major, a, c, contract, op, true);
                        }
                        if (org == null && c.id == a.bindings.embodies)
                        {
                            selfDiesAs = c;
                            selfEndKey = "Died";
                        }
                        break;
                    case PlannedFate.Wounded:
                        wounded++;
                        op1 = p.Add(CommitOpKind.CharacterWounded);
                        op1.character = c;
                        op1.woundDays = f.woundDays;
                        break;
                    case PlannedFate.Captured:
                        captured++;
                        p.Add(CommitOpKind.CharacterCaptured).character = c;
                        if (isLeader && org != null) leaderLost = true;
                        break;
                    case PlannedFate.Missing:
                        missing++;
                        p.Add(CommitOpKind.CharacterMissing).character = c;
                        if (isLeader && org != null) leaderLost = true;
                        break;
                    case PlannedFate.Lost:
                        missing++;
                        p.Add(CommitOpKind.CharacterLost).character = c;
                        p.Add(CommitOpKind.Publication).spec = Publications.Character(EventKeys.CharacterVanished, Importance.Notable, a, c, contract, op, isLeader);
                        if (isLeader && org != null) leaderLost = true;
                        if (org == null && c.id == a.bindings.embodies)
                        {
                            selfDiesAs = c;
                            selfEndKey = "Lost";
                        }
                        break;
                }
            }
            p.killed = killed;
            p.wounded = wounded;
            p.captured = captured;
            p.missing = missing;
            p.leaderLost = leaderLost;

            if (killed + wounded + captured + missing > 0)
            {
                PublicationSpec cas = Publications.Actor(EventKeys.ContractorCasualties, killed > 0 ? Importance.Notable : Importance.Minor, a, contract, op);
                cas.killed = killed;
                cas.wounded = wounded;
                cas.captured = captured;
                cas.missing = missing;
                p.Add(CommitOpKind.Publication).spec = cas;
                if (p.sim != null)
                {
                    CommitOp shock = p.Add(CommitOpKind.MoraleShock);
                    shock.killed = killed;
                    shock.captured = captured;
                    shock.missing = missing;
                    shock.flag = leaderLost;
                    p.Add(CommitOpKind.DescriptorCheck);
                }
            }

            if (a.status != ActorStatus.Active) return;
            if (org == null)
            {
                // A Solo is its person: the abstract rule ends it when that person is no longer alive.
                KnownCharacter self = ctx.characters.Get(a.bindings.embodies);
                if (self != null && (selfDiesAs != null || !self.IsAlive))
                {
                    p.actorEndKey = selfEndKey ?? "Died";
                    p.Add(CommitOpKind.ActorEnded).key = p.actorEndKey;
                }
                return;
            }
            if (!leaderLost) return;
            FateRules.SuccessionPlan s = FateRules.PlanSuccession(a, org, ctx.characters, oldLeader, eligible, healthyOf, ContractorService.Pools);
            p.succession = s;
            p.Touch(ctx.characters.Get(oldLeader));
            p.Touch(s.next);
            if (s.probedTiers.Count > 0) p.Add(CommitOpKind.SuccessionProbes);
            if (s.promote)
            {
                p.addsRecord = true;
                p.Add(CommitOpKind.Promotion);
            }
            p.Add(CommitOpKind.OldLeaderExit);
            if (s.Dissolves)
            {
                p.actorEndKey = "NoSuccessor";
                p.Add(CommitOpKind.ActorEnded).key = p.actorEndKey;
            }
            else
            {
                p.Add(CommitOpKind.NewLeader);
            }
        }

        // ================================================================== VALIDATE

        /// <summary>
        /// VALIDATE the COMPLETE plan before anything changes (§ 15.2 step 4). Throws <see cref="PlanInvalidException"/>; on a
        /// throw nothing has been touched.
        /// </summary>
        public static void Validate(DomainContext ctx, ReconciliationPlan p)
        {
            PhysicalEpisode e = p.episode;
            if (p.actor == null) throw new PlanInvalidException("ActorMissing", e != null ? e.actor.ToString() : null);
            if (e != null)
            {
                if (e.state == EpisodeState.Closed || e.consequencesApplied) throw new PlanInvalidException("AlreadyApplied", e.ToString());
                if (e.publications.Count != 0 || e.publishCursor != 0 || e.publishedTick >= 0) throw new PlanInvalidException("OutboxNotEmpty", e.ToString());
                if (p.decisions.Count != e.members.Count) throw new PlanInvalidException("MemberMissing", p.decisions.Count + " of " + e.members.Count);
                HashSet<EpisodeMember> seen = new HashSet<EpisodeMember>();
                HashSet<int> people = new HashSet<int>();
                HashSet<int> pawns = new HashSet<int>();
                Dictionary<Tier, int> anonymous = new Dictionary<Tier, int>();
                for (int i = 0; i < p.decisions.Count; i++)
                {
                    MemberDecision d = p.decisions[i];
                    EpisodeMember m = d.member;
                    if (m == null || !e.members.Contains(m)) throw new PlanInvalidException("ForeignMember", m?.ToString());
                    if (!seen.Add(m)) throw new PlanInvalidException("DuplicateMember", m.ToString());
                    if (m.state == MemberState.Done || m.outcome != MemberOutcome.Pending) throw new PlanInvalidException("MemberAlreadyDone", m.ToString());
                    if (d.outcome == MemberOutcome.Pending) throw new PlanInvalidException("NotTerminal", m.ToString());
                    if (m.IsBound && m.pawn.thingIdNumber > 0 && !pawns.Add(m.pawn.thingIdNumber)) throw new PlanInvalidException("SharedPawn", m.pawn.ToString());
                    if (m.IsNamed)
                    {
                        KnownCharacter c = d.character;
                        if (c == null || c.id != m.character) throw new PlanInvalidException("CharacterMissing", m.character.ToString());
                        if (!people.Add(c.id.Value)) throw new PlanInvalidException("DuplicatePerson", c.id.ToString());
                        if (c.episode != e.id) throw new PlanInvalidException("LinkMismatch", c.id + " → " + c.episode);
                        if (c.custody != CustodyState.Deployed) throw new PlanInvalidException("CustodyMismatch", c.id + " " + c.custody);
                        if (c.org != p.actor.id && p.actor.bindings.embodies != c.id) throw new PlanInvalidException("NotAMember", c.id.ToString());
                    }
                    else
                    {
                        int n;
                        anonymous.TryGetValue(m.tier, out n);
                        anonymous[m.tier] = n + 1;
                    }
                }
                foreach (KeyValuePair<Tier, int> kv in anonymous)
                {
                    // Per-tier conservation (§ 5.1): the checked-out headcount covers every anonymous member, so no count goes negative.
                    if (p.org == null || PeekCommitted(p.org, kv.Key) < kv.Value) throw new PlanInvalidException("Headcount", kv.Key + " " + kv.Value);
                }
                if (e.cause.operation.IsValid)
                {
                    Operation op = p.operation;
                    if (op == null || op.status != OpStatus.Physical || op.physicalEpisode != e.id) throw new PlanInvalidException("OperationLink", e.cause.operation.ToString());
                }
            }

            HashSet<int> statusTargets = new HashSet<int>();
            for (int i = 0; i < p.ops.Count; i++)
            {
                CommitOp op = p.ops[i];
                if (!Enum.IsDefined(typeof(CommitOpKind), op.kind)) throw new PlanInvalidException("UnknownOp", op.ToString());
                KnownCharacter c = op.character;
                switch (op.kind)
                {
                    case CommitOpKind.CharacterKilled:
                        if (c.status == CharacterStatus.Dead) throw new PlanInvalidException("DeathTwice", c.id.ToString());
                        if (!statusTargets.Add(c.id.Value)) throw new PlanInvalidException("DuplicateFate", c.id.ToString());
                        break;
                    case CommitOpKind.CharacterWounded:
                    case CommitOpKind.CharacterCaptured:
                    case CommitOpKind.CharacterMissing:
                    case CommitOpKind.CharacterLost:
                        // Death is monotonic (§ 10.1, P3-INV-004): nothing but a death targets a dead person.
                        if (c.status == CharacterStatus.Dead) throw new PlanInvalidException("DeadTarget", op.ToString());
                        if (!statusTargets.Add(c.id.Value)) throw new PlanInvalidException("DuplicateFate", c.id.ToString());
                        break;
                    case CommitOpKind.CharacterStored:
                    case CommitOpKind.CharacterReverted:
                    case CommitOpKind.CharacterDetached:
                        if (c.status == CharacterStatus.Dead) throw new PlanInvalidException("DeadTarget", op.ToString());
                        // A Lost person cannot be a member (planning requires the living); if a record says otherwise, no return
                        // story is written over it (a found-again person is not this task's content).
                        if (op.kind == CommitOpKind.CharacterStored && c.status == CharacterStatus.Lost) throw new PlanInvalidException("LostTarget", op.ToString());
                        break;
                    case CommitOpKind.CharacterReturnedFree:
                        // A return resolves only Missing or Captured; it is never a way back from Dead or Lost (P3-INV-004).
                        if (c.status != CharacterStatus.Missing && c.status != CharacterStatus.Captured) throw new PlanInvalidException("NotRecoverable", op.ToString());
                        if (!statusTargets.Add(c.id.Value)) throw new PlanInvalidException("DuplicateFate", c.id.ToString());
                        break;
                    case CommitOpKind.Promotion:
                        if (ctx.characters.Get(new CharacterId(ctx.ids.PeekNextId)) != null || ctx.actors.Get(new ActorId(ctx.ids.PeekNextId)) != null)
                        {
                            throw new PlanInvalidException("IdInUse", ctx.ids.PeekNextId.ToString());
                        }
                        break;
                    case CommitOpKind.SoloAnchor:
                        if (p.sim == null || op.tile == null || op.tile.tileId < 0) throw new PlanInvalidException("Anchor", op.ToString());
                        break;
                    case CommitOpKind.MoraleShock:
                    case CommitOpKind.DescriptorCheck:
                    case CommitOpKind.SimDirty:
                        if (p.sim == null) throw new PlanInvalidException("NoSimulation", op.ToString());
                        break;
                    case CommitOpKind.SuccessionProbes:
                    case CommitOpKind.OldLeaderExit:
                    case CommitOpKind.NewLeader:
                        if (p.succession == null || p.org == null) throw new PlanInvalidException("NoSuccession", op.ToString());
                        break;
                }
            }
            if (p.PlannedPublications > PhysicalEpisode.MaxPublications) throw new PlanInvalidException("OutboxBound", p.PlannedPublications.ToString());
        }

        /// <summary>A tier's checked-out headcount, read without creating its entry.</summary>
        public static int PeekCommitted(OrganizationProfile org, Tier t)
        {
            for (int i = 0; i < org.committed.Count; i++) if (org.committed[i].tier == t) return org.committed[i].healthy;
            return 0;
        }
    }
}
