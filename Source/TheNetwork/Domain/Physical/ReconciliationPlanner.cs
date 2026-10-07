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
            Lost = 4,

            /// <summary>Phase 3.2A: recruited by the player (§ 8.2: JoinedPlayer ⇒ status Defected). Physical only; the abstract path never produces it.</summary>
            Defected = 5
        }

        private struct FateEntry
        {
            public KnownCharacter character;
            public PlannedFate fate;
            public int woundDays;
            public string causeKey;

            /// <summary>Phase 3.2A, physical only: the person-level event a capture publishes (null: none, as on the abstract path).</summary>
            public string publicationKey;
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

        /// <summary>DECIDE for one placed NAMED member (see the full overload).</summary>
        public static MemberOutcome Decide(PhysicalObservation o, out bool unsupported)
        {
            HeldKind heldBy;
            bool captive;
            return Decide(o, true, out unsupported, out heldBy, out captive);
        }

        /// <summary>
        /// DECIDE for one placed member (§ 15.3, § 9.3). Terminal only on POSITIVE evidence: Returned needs <c>WorldFree</c> AND the exit
        /// evidence ("not spawned" is never "returned") AND no allegiance to a permanent faction. Phase 3.2A: a held custody (the player's
        /// prisoner or slave, recruited, kidnapped, held by another faction, a caravan, or a world pawn another faction recruited) is a
        /// TERMINAL held outcome of a NAMED member: the episode commits once and the person's continuing captivity is their own custody record
        /// (<see cref="CustodyRules"/>). A travelling transporter is transit, not custody: the member stays Present (§ 12.3). An ANONYMOUS
        /// member that is held would first have to be promoted to a Known Character, which is Phase 3.2B: it is reported as
        /// <paramref name="unsupported"/>, the caller quarantines the episode, the pawn is untouched (§ 17). Anything else stays Pending.
        /// </summary>
        public static MemberOutcome Decide(PhysicalObservation o, bool named, out bool unsupported, out HeldKind heldBy, out bool captive)
        {
            return Decide(o, named, false, out unsupported, out heldBy, out captive);
        }

        /// <summary>Structured group slots may reach a terminal held decision; identity still waits for the entire atomic batch.</summary>
        public static MemberOutcome Decide(PhysicalObservation o, bool named, bool anonymousPromotionSupported,
            out bool unsupported, out HeldKind heldBy, out bool captive)
        {
            unsupported = false;
            heldBy = HeldKind.None;
            captive = false;
            if (o == null) return MemberOutcome.Pending;
            bool held;
            switch (o.kind)
            {
                case ObservedKind.Dead: return MemberOutcome.Killed;
                case ObservedKind.Gone: return MemberOutcome.Lost;
                case ObservedKind.WorldFree:
                    if (!o.exitEvidence) return MemberOutcome.Pending;
                    if (!o.otherAllegiance) return MemberOutcome.Returned;
                    held = true; // another faction made the pawn a member: never a free return
                    break;
                case ObservedKind.InTransport:
                    // § 12.3 / § 15.3: a pawn in a travelling transporter is in transit, not held: the member stays Present until it lands
                    // (spawned), or ends up held or a world pawn. (For a person vanilla ALREADY holds, the custody watch records the transport.)
                    return MemberOutcome.Pending;
                default:
                    // Spawned (here or elsewhere), WorldOther, ReservationBroken, Unknown, None: the member stays Present.
                    held = CustodyRules.IsHeldKind(o.kind);
                    break;
            }
            if (!held) return MemberOutcome.Pending;
            if (!named && !anonymousPromotionSupported)
            {
                unsupported = true;
                return MemberOutcome.Pending;
            }
            return CustodyRules.MissionHeld(o, out heldBy, out captive);
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
            for (int i = 0; i < decisions.Count; i++)
            {
                MemberDecision d = decisions[i];
                p.decisions.Add(new MemberDecision { member = d.member, character = d.character, outcome = d.outcome,
                    observation = d.observation, woundDays = d.woundDays, holder = d.holder, captive = d.captive, promotionFacts = d.promotionFacts });
            }
            if (p.actor == null) return p; // VALIDATE refuses it (ActorMissing): nothing is planned against a missing actor
            p.org = p.actor.Get<OrganizationProfile>();
            p.sim = p.actor.Get<ContractorSimulation>();
            if (e.cause.operation.IsValid) p.operation = ctx.operations?.Get(e.cause.operation);
            // A Custody episode (Phase 3.2A) reconciles a transition of someone vanilla ALREADY holds: the group took the loss when the person was
            // first captured, so a later death, recruitment or return is a person-level fact (no second casualty report, no second morale shock).
            bool custody = CustodyRules.IsCustodyEpisode(e);

            List<FateEntry> fates = new List<FateEntry>();
            Dictionary<int, MemberDecision> planned = new Dictionary<int, MemberDecision>();
            Dictionary<Tier, int> anonHealthyBack = new Dictionary<Tier, int>();
            int anonKilled = 0, anonWounded = 0, anonLost = 0;
            MemberDecision soloReturn = null;
            bool anyRelease = false;

            int discretionary = DiscretionaryBudget(ctx, p);
            for (int i = 0; i < p.decisions.Count; i++)
            {
                MemberDecision d = p.decisions[i];
                EpisodeMember m = d.member;
                if (m != null && !m.IsNamed && CanPromoteSlot(m, d))
                {
                    bool strong = HasStrongEvidence(d);
                    if (strong || (HasPresence(m, p.now) && discretionary > 0))
                    {
                        d.character = PrepareSlotCharacter(ctx, p, d);
                        if (!strong) discretionary--;
                        p.promotedCharacters.Add(d.character);
                        p.Touch(d.character);
                        p.addsRecord = true;
                        CommitOp promote = p.Add(CommitOpKind.SlotPromotion);
                        promote.member = m;
                        promote.character = d.character;
                        promote.tier = m.tier;
                    }
                }
                CommitOp done = p.Add(CommitOpKind.MemberDone);
                done.member = m;
                done.outcome = d.outcome;
                done.observed = d.observation != null ? d.observation.kind : ObservedKind.None;
                if (ReleasePolicy.ActionsFor(m.IsBound, m.IsNamed || d.character != null, d.outcome).Length > 0) anyRelease = true;

                if (m.IsNamed || d.character != null)
                {
                    KnownCharacter c = d.character;
                    p.Touch(c);
                    if (c == null) continue; // VALIDATE refuses it
                    planned[c.id.Value] = d;
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
                            // The stored pawn stops ticking when vanilla passes it (it is reserved, so suspended): its aging is brought
                            // current from THAT tick at the next materialization. Unknown exit ⇒ the commit tick (§ 6.4).
                            int exit = d.observation != null ? d.observation.exitTick : -1;
                            stored.agedThrough = exit >= 0 && exit <= p.now ? exit : -1;
                            // The positive return is authoritative for the person's story status too (a rescued Missing or Captured
                            // person is not left Missing forever): injured ⇒ the shared wound rule (which resolves Missing/Captured to
                            // Wounded), unhurt ⇒ the shared return rule (Missing/Captured ⇒ Active). Neither ever revives Dead or Lost.
                            if (d.woundDays > 0) fates.Add(new FateEntry { character = c, fate = PlannedFate.Wounded, woundDays = d.woundDays });
                            else if (c.status == CharacterStatus.Missing || c.status == CharacterStatus.Captured) p.Add(CommitOpKind.CharacterReturnedFree).character = c;
                            if (p.org == null && c.id == p.actor.bindings.embodies) soloReturn = d;
                            if (custody)
                            {
                                // 3.2A: a person vanilla held is free again (released, escaped or rescued: the observation does not say which).
                                bool lead = p.org != null ? c.id == p.org.leader : c.id == p.actor.bindings.embodies;
                                p.Add(CommitOpKind.Publication).spec = Publications.Character(EventKeys.CharacterFreed, Importance.Notable, p.actor, c, ContractId.None, OperationId.None, lead);
                            }
                            break;
                        case MemberOutcome.NeverPlaced:
                            p.neverPlaced++;
                            p.Add(CommitOpKind.CharacterReverted).character = c;
                            break;
                        case MemberOutcome.Detached:
                            p.Add(CommitOpKind.CharacterDetached).character = c;
                            break;
                        case MemberOutcome.HeldByPlayer:
                        case MemberOutcome.JoinedPlayer:
                        case MemberOutcome.Kidnapped:
                        case MemberOutcome.HeldByOther:
                            // Phase 3.2A (§ 8.2, § 15.3): vanilla holds the SAME pawn. The episode ends here; the continuing captivity is the
                            // person's own custody record (OutOfCustody, heldBy, heldSinceTick), watched by the custody watch. The pawn is
                            // untouched and nothing is normalized, stored, healed or abstractly advanced.
                            p.held++;
                            if (d.outcome == MemberOutcome.JoinedPlayer)
                            {
                                if (c.status != CharacterStatus.Defected) fates.Add(new FateEntry { character = c, fate = PlannedFate.Defected, publicationKey = EventKeys.CharacterDefected });
                            }
                            else if (d.captive && c.status != CharacterStatus.Captured && c.status != CharacterStatus.Defected)
                            {
                                fates.Add(new FateEntry
                                {
                                    character = c,
                                    fate = PlannedFate.Captured,
                                    publicationKey = d.outcome == MemberOutcome.HeldByPlayer ? EventKeys.CharacterCapturedByPlayer : EventKeys.ContractorCaptured,
                                    causeKey = d.holder.ToString()
                                });
                            }
                            CommitOp heldOp = p.Add(CommitOpKind.CharacterHeld);
                            heldOp.character = c;
                            heldOp.held = d.holder == HeldKind.None ? HeldKind.Unknown : d.holder;
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
                // A member of THIS episode is judged by the person this plan will leave behind (projected truth), never by a status
                // the same plan resolves; anyone else by the ordinary abstract rule.
                MemberDecision d;
                if (planned.TryGetValue(c.id.Value, out d)) return EligibleAfterPlan(c, d.outcome, d.woundDays);
                return FateRules.MayLead(c.status) && AuthorityGate.CanSimulateAbstractly(c);
            };
            Func<Tier, int> healthyOf = t =>
            {
                int back;
                anonHealthyBack.TryGetValue(t, out back);
                return (org == null ? 0 : FateRules.PeekHealthy(org, t)) + back;
            };
            PlanFates(ctx, p, fates, anonKilled, anonWounded, 0, anonLost, e.cause.contract, e.cause.operation, eligible, healthyOf, true, !custody);

            // The returned Solo's hidden anchor, written once (§ 12.2). An organization's main body never moved, so it is left alone.
            if (soloReturn != null && p.sim != null && p.sim.spatial.IsInitialized)
            {
                TileRef tile = soloReturn.observation?.tile;
                if (tile == null || tile.tileId < 0) tile = e.whereTile;
                // A Custody return (Phase 3.2A): a world pawn has no tile, and no location is fabricated. The person's last recorded anchor is
                // kept; the write only ends any journey frozen since before the episode and restarts the spatial clock now, so the captivity
                // is never simulated afterwards as ordinary travel or downtime.
                if (custody && (tile == null || tile.tileId < 0)) tile = p.sim.spatial.anchor;
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

            p.hasReleaseActions = anyRelease || p.actorEndKey != null || (e.faction != null && e.faction.IsValid);
            p.Add(CommitOpKind.EpisodeClosed).key = closeReasonKey;
            CommitOp markers = p.Add(CommitOpKind.StageMarkers);
            markers.flag = !p.hasReleaseActions;
            markers.followUpDone = !p.hasFollowUp;
            if (!p.hasReleaseActions)
            {
                // § 8.1 rule 5: nothing physical is left to release, so COMPLETE's assignments happen here.
                for (int i = 0; i < p.decisions.Count; i++)
                {
                    KnownCharacter c = p.decisions[i].character;
                    if (c != null && c.episode == e.id) p.Add(CommitOpKind.CharacterUnlink).character = c;
                }
            }
            if (p.sim != null) p.Add(CommitOpKind.SimDirty);
            return p;
        }

        private const ConcretizationEvidence StrongMask = ConcretizationEvidence.DeliberateIdentification
            | ConcretizationEvidence.PlayerCombat | ConcretizationEvidence.PlayerRelation | ConcretizationEvidence.FormerColonist;

        /// <summary>Material custody is independent of optional evidence; a caravan or transport by itself identifies nobody.</summary>
        public static bool HasStrongEvidence(MemberDecision d)
        {
            if (d == null) return false;
            if (d.promotionFacts != null && (d.promotionFacts.evidence & StrongMask) != ConcretizationEvidence.None) return true;
            if (!CustodyRules.IsHeldOutcome(d.outcome)) return false;
            if (d.captive || d.outcome == MemberOutcome.JoinedPlayer || d.outcome == MemberOutcome.Kidnapped) return true;
            PhysicalObservation o = d.observation;
            return d.outcome == MemberOutcome.HeldByOther && o != null
                && (o.kind == ObservedKind.HeldByOther || CustodyRules.JoinedAnotherFaction(o));
        }

        private static bool HasPresence(EpisodeMember m, int now)
        { return m.p0Eligible && m.playerVisibleTick >= 0 && m.playerVisibleTick <= now; }

        private static bool CanPromoteSlot(EpisodeMember m, MemberDecision d)
        {
            return m.IsBound && OrganizationCompositionV1.IsRole(m.seatRole)
                && (d.outcome == MemberOutcome.Returned || d.outcome == MemberOutcome.Killed || d.outcome == MemberOutcome.Lost || CustodyRules.IsHeldOutcome(d.outcome));
        }

        private static bool LeavesCurrentPin(MemberDecision d)
        { return d.outcome != MemberOutcome.Killed && d.outcome != MemberOutcome.Lost && d.outcome != MemberOutcome.JoinedPlayer; }

        private static int DiscretionaryBudget(DomainContext ctx, ReconciliationPlan p)
        {
            if (p.org == null) return 0;
            int current = 0;
            HashSet<int> seen = new HashSet<int>();
            for (int i = 0; i < p.org.knownMembers.Count; i++)
            {
                CharacterId id = p.org.knownMembers[i];
                if (seen.Add(id.Value) && OrganizationSeatPolicy.IsCurrentMember(ctx.characters.Get(id), p.actor.id)) current++;
            }
            for (int i = 0; i < p.decisions.Count; i++)
            {
                MemberDecision d = p.decisions[i];
                if (d.member != null && !d.member.IsNamed && CanPromoteSlot(d.member, d) && HasStrongEvidence(d) && LeavesCurrentPin(d)) current++;
            }
            return Math.Max(0, OrganizationProfile.MaxKnownMembers - current);
        }

        private static KnownCharacter PrepareSlotCharacter(DomainContext ctx, ReconciliationPlan p, MemberDecision d)
        {
            long next = (long)ctx.ids.PeekNextId + p.promotedCharacters.Count;
            // NextId must remain representable too; no allocation occurs while planning.
            if (next <= 0 || next >= int.MaxValue) throw new PlanInvalidException("IdExhausted", next.ToString());
            EpisodeMember m = d.member;
            NameSnapshot name = d.promotionFacts?.name;
            if (name == null || string.IsNullOrWhiteSpace(name.Display)) throw new PlanInvalidException("PromotionNameUnknown", m.ToString());
            return new KnownCharacter
            {
                id = new CharacterId((int)next), name = name.Copy(), role = CharacterRole.Member, org = p.actor.id,
                opRole = m.seatRole, pawn = m.pawn.Copy(), episode = p.episode.id, custody = CustodyState.Deployed,
                createdTick = p.now, statusTick = p.now,
                firstEncounterTick = m.playerVisibleTick >= 0 && m.playerVisibleTick <= p.now ? m.playerVisibleTick : -1
            };
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
            PlanFates(ctx, p, fates, CasualtyReport.Sum(r.killed), CasualtyReport.Sum(r.wounded), CasualtyReport.Sum(r.captured), CasualtyReport.Sum(r.missing), contract, op, eligible, healthyOf, false, true);
            p.hasReleaseActions = p.actorEndKey != null;
            if (p.sim != null) p.Add(CommitOpKind.SimDirty);
            return p;
        }

        /// <summary>
        /// The story status a named member will have once THIS plan commits, computed without touching the person (projected
        /// truth): a positive <see cref="MemberOutcome.Returned"/> that is injured becomes Wounded (the shared
        /// <see cref="FateRules.Wounded"/>), and one that is unhurt resolves Missing or Captured to Active (the shared
        /// <see cref="FateRules.ReturnedFree"/>). Every other outcome leaves the pre-plan status: NeverPlaced is no return and
        /// resolves nothing. Dead and Lost never change (P3-INV-004; VALIDATE also refuses such a member).
        /// </summary>
        public static CharacterStatus ProjectedStatus(KnownCharacter c, MemberOutcome outcome, int woundDays)
        {
            CharacterStatus s = c.status;
            if (outcome != MemberOutcome.Returned || s == CharacterStatus.Dead || s == CharacterStatus.Lost) return s;
            if (woundDays > 0) return CharacterStatus.Wounded;
            return s == CharacterStatus.Missing || s == CharacterStatus.Captured ? CharacterStatus.Active : s;
        }

        /// <summary>
        /// "If this reconciliation plan commits, is this member a valid living succession candidate?" Only a member who came back
        /// (Returned) or never left (NeverPlaced) may lead; Killed, Lost and Detached may not. The status judged is the
        /// <see cref="ProjectedStatus"/>, by the same rule the abstract path uses (<see cref="FateRules.MayLead"/>), so a Missing
        /// or Captured person this plan returns is not excluded by the status it resolves, a wounded return is judged as the living
        /// Wounded person the commit writes, and a NeverPlaced person keeps an unresolved status. The episode's own link is not
        /// held against its member: it is the link this plan's RELEASE clears.
        /// </summary>
        public static bool EligibleAfterPlan(KnownCharacter c, MemberOutcome outcome, int woundDays)
        {
            if (c == null || (outcome != MemberOutcome.Returned && outcome != MemberOutcome.NeverPlaced)) return false;
            return FateRules.MayLead(ProjectedStatus(c, outcome, woundDays));
        }

        /// <summary>
        /// The shared per-person and group consequences, in the order the abstract casualty path applies them: each fate (with its
        /// person-level publication), the group's casualty publication and morale shock, the morale descriptor, then the end of a
        /// Solo or the succession of a lost leader (decided here, from the plan's projected fates).
        /// </summary>
        /// <remarks>
        /// <paramref name="groupLoss"/> is false only for a Phase 3.2A Custody episode: the person was already counted as the group's loss when
        /// first captured, so a later death, recruitment or return of that held person writes the person-level fate and publication (and an
        /// actor end or succession when it follows) but no second casualty report and no second morale shock.
        /// </remarks>
        private static void PlanFates(DomainContext ctx, ReconciliationPlan p, List<FateEntry> fates, int anonKilled, int anonWounded, int anonCaptured, int anonMissing,
            ContractId contract, OperationId op, Func<KnownCharacter, bool> eligible, Func<Tier, int> healthyOf, bool physical, bool groupLoss)
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
                        // Physical only (3.2A): who holds them is a person-level fact the abstract path never states. Never with the contract:
                        // the contract's own Troubled story already told the player (no second contract letter).
                        if (f.publicationKey != null)
                        {
                            PublicationSpec held = Publications.Character(f.publicationKey, isLeader ? Importance.Major : Importance.Notable, a, c, ContractId.None, op, isLeader);
                            held.reasonKey = f.causeKey;
                            p.Add(CommitOpKind.Publication).spec = held;
                        }
                        if (isLeader && org != null) leaderLost = true;
                        break;
                    case PlannedFate.Defected:
                        // Recruited by the player (§ 8.2). Counted with the captured for the group's loss (the person is lost to it the same way).
                        captured++;
                        p.Add(CommitOpKind.CharacterDefected).character = c;
                        p.Add(CommitOpKind.Publication).spec = Publications.Character(f.publicationKey ?? EventKeys.CharacterDefected, isLeader ? Importance.Major : Importance.Notable, a, c, ContractId.None, op, isLeader);
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

            if (groupLoss && killed + wounded + captured + missing > 0)
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
            if (p.promotedCharacters.Count != 0) throw new PlanInvalidException("MixedPromotionSuccession", "new slot identities and leader loss require Phase 3.2C");
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
                if (e.members == null || e.members.Count == 0 || e.members.Count > PhysicalLifecycleService.MaxMembers)
                    throw new PlanInvalidException("EpisodeMemberCount", e.id.ToString());
                if (e.state == EpisodeState.Closed || e.consequencesApplied) throw new PlanInvalidException("AlreadyApplied", e.ToString());
                if (e.publications.Count != 0 || e.publishCursor != 0 || e.publishedTick >= 0) throw new PlanInvalidException("OutboxNotEmpty", e.ToString());
                if (p.decisions.Count != e.members.Count) throw new PlanInvalidException("MemberMissing", p.decisions.Count + " of " + e.members.Count);
                HashSet<EpisodeMember> seen = new HashSet<EpisodeMember>();
                HashSet<int> people = new HashSet<int>();
                HashSet<int> pawns = new HashSet<int>();
                Dictionary<Tier, int> anonymous = new Dictionary<Tier, int>();
                HashSet<int> slots = new HashSet<int>();
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
                        // A mission member is Deployed; the member of a Custody episode (3.2A) is someone vanilla already holds.
                        CustodyState expected = CustodyRules.IsCustodyEpisode(e) ? CustodyState.OutOfCustody : CustodyState.Deployed;
                        if (c.custody != expected) throw new PlanInvalidException("CustodyMismatch", c.id + " " + c.custody);
                        if (c.org != p.actor.id && p.actor.bindings.embodies != c.id) throw new PlanInvalidException("NotAMember", c.id.ToString());
                    }
                    else
                    {
                        if (d.character != null)
                        {
                            if (!p.promotedCharacters.Contains(d.character)) throw new PlanInvalidException("ForeignPromotion", m.ToString());
                            if (!slots.Add(m.slot)) throw new PlanInvalidException("DuplicateSlot", m.slot.ToString());
                            ValidateSlotCharacter(ctx, p, d);
                            if (!people.Add(d.character.id.Value)) throw new PlanInvalidException("DuplicatePerson", d.character.id.ToString());
                        }
                        else if (CustodyRules.IsHeldOutcome(d.outcome)) throw new PlanInvalidException("AnonymousHeldWithoutIdentity", m.ToString());
                        int debits = 0;
                        for (int j = 0; j < p.ops.Count; j++)
                        {
                            CommitOp debit = p.ops[j];
                            if (ReferenceEquals(debit.member, m) && (debit.kind == CommitOpKind.SlotPromotion
                                || debit.kind == CommitOpKind.AnonymousBack || debit.kind == CommitOpKind.AnonymousLost)) debits++;
                        }
                        if (debits != 1) throw new PlanInvalidException("AnonymousDebit", m + " " + debits);
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

            if (p.promotedCharacters.Count > OrganizationSeatPolicy.MaxMissionMembers) throw new PlanInvalidException("PromotionBound", p.promotedCharacters.Count.ToString());
            int promotionOrdinal = 0;
            HashSet<KnownCharacter> promotionPayloads = new HashSet<KnownCharacter>();
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
                        // O-20: a player recruit permanently exits old NPC availability; Phase 4 uses the real colony through PlayerProxy.
                        if (op.kind == CommitOpKind.CharacterStored && c.status == CharacterStatus.Defected) throw new PlanInvalidException("DefectedTarget", op.ToString());
                        break;
                    case CommitOpKind.CharacterHeld:
                        // Death is monotonic: a dead person is never "held" (the death path releases them).
                        if (c.status == CharacterStatus.Dead) throw new PlanInvalidException("DeadTarget", op.ToString());
                        if (op.held == HeldKind.None) throw new PlanInvalidException("NoHolder", op.ToString());
                        break;
                    case CommitOpKind.CharacterDefected:
                        if (c.status == CharacterStatus.Dead) throw new PlanInvalidException("DeadTarget", op.ToString());
                        if (!statusTargets.Add(c.id.Value)) throw new PlanInvalidException("DuplicateFate", c.id.ToString());
                        break;
                    case CommitOpKind.CharacterReturnedFree:
                        // A return resolves only Missing or Captured; it is never a way back from Dead or Lost (P3-INV-004).
                        if (c.status != CharacterStatus.Missing && c.status != CharacterStatus.Captured) throw new PlanInvalidException("NotRecoverable", op.ToString());
                        if (!statusTargets.Add(c.id.Value)) throw new PlanInvalidException("DuplicateFate", c.id.ToString());
                        break;
                    case CommitOpKind.SlotPromotion:
                        if (op.member == null || op.member.IsNamed || op.tier != op.member.tier || c == null
                            || !p.promotedCharacters.Contains(c) || !p.touchedCharacters.Contains(c) || !promotionPayloads.Add(c)
                            || !p.decisions.Exists(d => ReferenceEquals(d.member, op.member) && ReferenceEquals(d.character, c))) throw new PlanInvalidException("PromotionPayload", op.ToString());
                        long expected = (long)ctx.ids.PeekNextId + promotionOrdinal++;
                        if (expected <= 0 || expected >= int.MaxValue || c.id.Value != expected) throw new PlanInvalidException("PromotionId", op.ToString());
                        if (ctx.characters.Get(c.id) != null || ctx.actors.Get(new ActorId(c.id.Value)) != null) throw new PlanInvalidException("IdInUse", c.id.ToString());
                        break;
                    case CommitOpKind.Promotion:
                        long abstractNext = (long)ctx.ids.PeekNextId + promotionOrdinal++;
                        if (abstractNext <= 0 || abstractNext >= int.MaxValue) throw new PlanInvalidException("IdExhausted", abstractNext.ToString());
                        if (ctx.characters.Get(new CharacterId((int)abstractNext)) != null || ctx.actors.Get(new ActorId((int)abstractNext)) != null)
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
            if (promotionPayloads.Count != p.promotedCharacters.Count) throw new PlanInvalidException("PromotionMissing", p.promotedCharacters.Count.ToString());
            if (promotionOrdinal > 0 && !p.addsRecord) throw new PlanInvalidException("PromotionSnapshot", null);
            if (p.PlannedPublications > PhysicalEpisode.MaxPublications) throw new PlanInvalidException("OutboxBound", p.PlannedPublications.ToString());
        }

        private static void ValidateSlotCharacter(DomainContext ctx, ReconciliationPlan p, MemberDecision d)
        {
            EpisodeMember m = d.member;
            KnownCharacter c = d.character;
            if (p.org == null || CustodyRules.IsCustodyEpisode(p.episode) || !CanPromoteSlot(m, d)) throw new PlanInvalidException("UnsupportedSlotPromotion", m.ToString());
            if (!HasStrongEvidence(d) && !HasPresence(m, p.now)) throw new PlanInvalidException("PromotionEvidence", m.ToString());
            if (c.org != p.actor.id || c.role != CharacterRole.Member || c.opRole != m.seatRole || c.episode != p.episode.id
                || c.custody != CustodyState.Deployed || c.status != CharacterStatus.Active
                || c.createdTick != p.now || c.statusTick != p.now
                || c.firstEncounterTick != (m.playerVisibleTick >= 0 && m.playerVisibleTick <= p.now ? m.playerVisibleTick : -1)
                || c.pawn == null || !c.pawn.SameBinding(m.pawn) || c.pawn.thingIdNumber != m.pawn.thingIdNumber
                || c.pawn.defName != m.pawn.defName || c.pawn.boundTick != m.pawn.boundTick || c.pawn.agedThroughTick != m.pawn.agedThroughTick) throw new PlanInvalidException("PromotionProvenance", m.ToString());
            if (d.promotionFacts?.name == null || string.IsNullOrWhiteSpace(d.promotionFacts.name.Display)
                || c.name == null || c.name.first != d.promotionFacts.name.first || c.name.nick != d.promotionFacts.name.nick
                || c.name.last != d.promotionFacts.name.last || c.name.display != d.promotionFacts.name.display) throw new PlanInvalidException("PromotionNameUnknown", m.ToString());
            for (int i = 0; i < ctx.characters.characters.Count; i++)
            {
                KnownCharacter existing = ctx.characters.characters[i];
                if (existing?.pawn != null && existing.pawn.IsBound && existing.pawn.SameBinding(m.pawn)) throw new PlanInvalidException("PawnAlreadyNamed", existing.id.ToString());
            }
            for (int i = 0; i < p.episode.members.Count; i++)
            {
                EpisodeMember other = p.episode.members[i];
                if (!ReferenceEquals(m, other) && other?.pawn != null && other.pawn.IsBound && other.pawn.SameBinding(m.pawn)) throw new PlanInvalidException("SharedPawn", m.ToString());
            }
            if (ctx.episodes != null)
            {
                for (int i = 0; i < ctx.episodes.episodes.Count; i++)
                {
                    PhysicalEpisode otherEpisode = ctx.episodes.episodes[i];
                    if (otherEpisode == null || ReferenceEquals(otherEpisode, p.episode) || otherEpisode.releaseApplied) continue;
                    for (int j = 0; j < otherEpisode.members.Count; j++)
                    {
                        EpisodeMember other = otherEpisode.members[j];
                        if (other?.pawn != null && other.pawn.IsBound && other.pawn.SameBinding(m.pawn))
                            throw new PlanInvalidException("PawnHasAnotherEpisodeOwner", otherEpisode.id.ToString());
                    }
                }
            }
            int discretionary = 0;
            for (int i = 0; i < p.decisions.Count; i++)
            {
                MemberDecision other = p.decisions[i];
                if (other.member != null && !other.member.IsNamed && other.character != null && !HasStrongEvidence(other)) discretionary++;
            }
            if (discretionary > DiscretionaryBudget(ctx, p)) throw new PlanInvalidException("DiscretionaryTarget", discretionary.ToString());
        }

        /// <summary>A tier's checked-out headcount, read without creating its entry.</summary>
        public static int PeekCommitted(OrganizationProfile org, Tier t)
        {
            for (int i = 0; i < org.committed.Count; i++) if (org.committed[i].tier == t) return org.committed[i].healthy;
            return 0;
        }
    }
}
