using System.Collections.Generic;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;

namespace TheNetwork.Domain.Physical
{
    /// <summary>
    /// The ATOMIC DURABLE COMMIT (PHYSICAL_LIFECYCLE § 15.2 step 5, § 15.6, § 15.7): the ONE bounded mutation layer of a
    /// reconciliation. It runs a validated plan's flat, ordered list of total assignments inside a snapshot of EXACTLY the
    /// touched set (never the whole Network); if any assignment throws, every captured object and collection is put back, a
    /// promoted record is removed again, and the exception propagates, so <c>consequencesApplied</c> was never set. On success
    /// <c>consequencesApplied = true</c> is the LAST statement.
    ///
    /// This file is held to the commit's purity by a source scan (RT-PHYS-027): it references no event bus, no scheduler, no
    /// physical port, no logging, no vanilla API and no random source. Publications are written to the outbox as DATA.
    /// </summary>
    public static class ReconciliationApplier
    {
        /// <summary>Stands for "the characters store's membership" in the declared writes (restored by truncation, not by the snapshot).</summary>
        public static readonly object CharacterMembership = new object();

        /// <summary>
        /// The touched set of a plan (§ 15.6 "computed by ONE function from the plan"): the episode, the touched characters, the
        /// actor (its header and its components: profile, simulation with its spatial state), at most one operation, the parity
        /// outbox, and the id allocator when the plan adds a record. The characters store's membership is restored separately.
        /// </summary>
        public static DurableSnapshot TouchedSet(ReconciliationPlan p, CommitTarget t)
        {
            DurableSnapshot s = new DurableSnapshot();
            s.Capture(p.episode);
            for (int i = 0; i < p.touchedCharacters.Count; i++) s.Capture(p.touchedCharacters[i]);
            s.Capture(p.actor);
            s.Capture(p.operation);
            if (p.episode == null) s.Capture(t.outbox);
            if (p.addsRecord) s.Capture(t.ids);
            return s;
        }

        /// <summary>
        /// Runs the commit. <paramref name="faultAfter"/> is the deterministic test-only fault point (§ 15.7): −1 none; k ∈ [0, N]
        /// throws after exactly k of the N assignments (0 = before the first, N = immediately before the flag).
        /// </summary>
        public static void Commit(ReconciliationPlan p, CommitTarget t, int faultAfter)
        {
            DurableSnapshot snapshot = TouchedSet(p, t);
            int membership = t.characters.Count;
            try
            {
                for (int i = 0; i < p.ops.Count; i++)
                {
                    if (faultAfter == i) throw new InjectedFaultException("after " + i + " of " + p.ops.Count + " commit steps");
                    Apply(p, p.ops[i], t);
                }
                if (faultAfter >= p.ops.Count) throw new InjectedFaultException("immediately before the consequencesApplied flag");
                if (p.episode != null) p.episode.consequencesApplied = true; // the LAST statement of the guarded commit
            }
            catch
            {
                snapshot.Restore();
                t.characters.TruncateTo(membership);
                t.promoted = null;
                throw;
            }
        }

        private static void Apply(ReconciliationPlan p, CommitOp op, CommitTarget t)
        {
            int now = t.now;
            KnownCharacter c = op.character;
            OrganizationProfile org = p.org;
            ContractorSimulation sim = p.sim;
            PhysicalEpisode e = p.episode;
            switch (op.kind)
            {
                case CommitOpKind.MemberDone:
                    op.member.state = MemberState.Done;
                    op.member.outcome = op.outcome;
                    op.member.observed = op.observed;
                    op.member.observedTick = now;
                    break;
                case CommitOpKind.CharacterKilled:
                    FateRules.Killed(c, now, op.key);
                    if (op.flag) c.custody = CustodyState.Released;
                    break;
                case CommitOpKind.CharacterWounded:
                    FateRules.Wounded(c, now, op.woundDays);
                    break;
                case CommitOpKind.CharacterCaptured:
                    FateRules.Captured(c, now);
                    break;
                case CommitOpKind.CharacterMissing:
                    FateRules.Missing(c, now);
                    break;
                case CommitOpKind.CharacterLost:
                    FateRules.Lost(c, now);
                    c.custody = CustodyState.Lost;
                    break;
                case CommitOpKind.CharacterStored:
                    c.custody = CustodyState.Stored;
                    c.heldBy = HeldKind.None;
                    c.heldSinceTick = -1;
                    int aged = op.agedThrough >= 0 ? op.agedThrough : now;
                    if (c.pawn != null && aged > c.pawn.agedThroughTick) c.pawn.agedThroughTick = aged;
                    if (op.member?.pawn != null && aged > op.member.pawn.agedThroughTick) op.member.pawn.agedThroughTick = aged;
                    break;
                case CommitOpKind.CharacterReturnedFree:
                    FateRules.ReturnedFree(c, now);
                    break;
                case CommitOpKind.CharacterHeld:
                    {
                        // § 8.2: vanilla holds the person. heldSinceTick is when vanilla began holding them; a change of holder keeps it.
                        bool alreadyHeld = c.custody == CustodyState.OutOfCustody && c.heldSinceTick >= 0;
                        c.custody = CustodyState.OutOfCustody;
                        c.heldBy = op.held;
                        if (!alreadyHeld) c.heldSinceTick = now;
                        break;
                    }
                case CommitOpKind.CharacterDefected:
                    FateRules.Defected(c, now);
                    break;
                case CommitOpKind.CharacterReverted:
                    c.custody = c.pawn != null && c.pawn.IsBound ? CustodyState.Stored : CustodyState.Unmaterialized;
                    break;
                case CommitOpKind.CharacterDetached:
                    c.custody = CustodyState.OutOfCustody;
                    c.heldBy = HeldKind.Unknown;
                    c.heldSinceTick = now;
                    break;
                case CommitOpKind.CharacterUnlink:
                    c.episode = EpisodeId.None;
                    break;
                case CommitOpKind.AnonymousBack:
                    org.TierOf(op.tier, true).healthy--;
                    if (op.woundDays > 0) ContractorService.AddWounded(org, op.tier, 1, now + System.Math.Max(1, op.woundDays) * Ticks.PerDay);
                    else org.TierOf(op.tier).healthy++;
                    org.committed.RemoveAll(x => x.healthy <= 0);
                    break;
                case CommitOpKind.AnonymousLost:
                    org.TierOf(op.tier, true).healthy--;
                    org.committed.RemoveAll(x => x.healthy <= 0);
                    break;
                case CommitOpKind.MoraleShock:
                    MoraleModel.Shock(sim, FateRules.LossShare(org, op.killed, op.captured, op.missing), op.flag, now);
                    break;
                case CommitOpKind.DescriptorCheck:
                    {
                        MoraleDescriptor before = sim.morale.descriptor;
                        MoraleDescriptor after = FateRules.NextDescriptor(sim);
                        if (after == before) break;
                        FateRules.SetDescriptor(sim, after, now);
                        PublicationSpec s = Publications.Actor(EventKeys.MoraleShifted, Importance.Minor, p.actor, ContractId.None, OperationId.None);
                        s.descriptorKey = after.ToString();
                        s.reasonKey = before.ToString();
                        t.outbox.Add(s);
                        break;
                    }
                case CommitOpKind.SuccessionProbes:
                    FateRules.ApplyProbes(org, p.succession);
                    break;
                case CommitOpKind.Promotion:
                    t.promoted = FateRules.ApplyPromotion(p.actor, org, p.succession, t.ids, t.characters, now);
                    t.outbox.Add(Publications.Character(EventKeys.CharacterPromoted, Importance.Notable, p.actor, t.promoted, ContractId.None, OperationId.None, false));
                    break;
                case CommitOpKind.OldLeaderExit:
                    FateRules.ApplyOldLeaderExit(org, t.characters, p.succession.oldLeader);
                    break;
                case CommitOpKind.NewLeader:
                    {
                        KnownCharacter next = p.succession.next ?? t.promoted;
                        KnownCharacter old = t.characters.Get(p.succession.oldLeader);
                        FateRules.ApplyNewLeader(p.actor, org, t.characters, p.succession, next, now);
                        PublicationSpec s = new PublicationSpec
                        {
                            typeKey = EventKeys.LeaderSucceeded,
                            importance = Importance.Notable,
                            actor = p.actor.id,
                            actorName = p.actor.name.Display,
                            character = p.succession.oldLeader,
                            characterName = old?.name.Display,
                            successor = next.id,
                            successorName = next.name.Display
                        };
                        Publications.Subjects(s, p.actor.id.Ref, next.id.Ref);
                        t.outbox.Add(s);
                        break;
                    }
                case CommitOpKind.ActorEnded:
                    {
                        FateRules.ActorEnded(p.actor, op.key, now);
                        PublicationSpec s = new PublicationSpec { typeKey = EventKeys.ContractorEnded, importance = Importance.Major, actor = p.actor.id, actorName = p.actor.name.Display, reasonKey = op.key };
                        Publications.Subjects(s, p.actor.id.Ref);
                        t.outbox.Add(s);
                        break;
                    }
                case CommitOpKind.SoloAnchor:
                    {
                        // § 12.2: the anchor, status and update tick, by direct assignment. No route, no travel time, no job.
                        SpatialState s = sim.spatial;
                        s.anchor = op.tile.Copy();
                        s.destination = null;
                        s.journeyOrigin = null;
                        s.bridgeFrom = null;
                        s.bridgeTo = null;
                        s.bridged = false;
                        s.purpose = SpatialPurpose.None;
                        s.operation = OperationId.None;
                        s.journeyStartTick = -1;
                        s.arrivalTick = -1;
                        s.blockedReason = null;
                        s.status = SpatialStatus.Idle;
                        s.lastUpdateTick = now;
                        break;
                    }
                case CommitOpKind.OperationMarker:
                    p.operation.physicalResolution = op.resolution;
                    break;
                case CommitOpKind.Publication:
                    t.outbox.Add(op.spec);
                    break;
                case CommitOpKind.EpisodeClosed:
                    e.state = EpisodeState.Closed;
                    e.closedTick = now;
                    e.closeReasonKey = op.key;
                    e.committedTick = now;
                    e.quarantineKey = null;
                    break;
                case CommitOpKind.StageMarkers:
                    e.publishCursor = 0;
                    e.publishedTick = -1;
                    e.releaseApplied = op.flag;
                    e.releasedTick = op.flag ? now : -1;
                    e.followUpApplied = op.followUpDone;
                    break;
                case CommitOpKind.SimDirty:
                    sim.MarkDirty();
                    break;
                default:
                    throw new PlanInvalidException("UnknownOp", op.ToString());
            }
        }

        /// <summary>
        /// What each operation DECLARES it writes (§ 15.7 coverage proof): every object listed must be inside
        /// <see cref="TouchedSet"/>, or be the characters store's membership (<see cref="CharacterMembership"/>), which the
        /// commit restores by truncation.
        /// </summary>
        public static List<object> Writes(ReconciliationPlan p, CommitOp op, CommitTarget t)
        {
            List<object> w = new List<object>();
            OrganizationProfile org = p.org;
            ContractorSimulation sim = p.sim;
            switch (op.kind)
            {
                case CommitOpKind.MemberDone:
                    w.Add(op.member);
                    break;
                case CommitOpKind.CharacterKilled:
                case CommitOpKind.CharacterWounded:
                case CommitOpKind.CharacterCaptured:
                case CommitOpKind.CharacterMissing:
                case CommitOpKind.CharacterLost:
                case CommitOpKind.CharacterReverted:
                case CommitOpKind.CharacterDetached:
                case CommitOpKind.CharacterUnlink:
                case CommitOpKind.CharacterReturnedFree:
                case CommitOpKind.CharacterHeld:
                case CommitOpKind.CharacterDefected:
                    w.Add(op.character);
                    break;
                case CommitOpKind.CharacterStored:
                    w.Add(op.character);
                    if (op.character.pawn != null) w.Add(op.character.pawn);
                    if (op.member?.pawn != null) w.Add(op.member.pawn);
                    break;
                case CommitOpKind.AnonymousBack:
                    w.Add(org);
                    w.Add(org.committed);
                    w.Add(org.tiers);
                    w.Add(org.woundedRecovery);
                    break;
                case CommitOpKind.AnonymousLost:
                    w.Add(org);
                    w.Add(org.committed);
                    break;
                case CommitOpKind.MoraleShock:
                    w.Add(sim.morale);
                    break;
                case CommitOpKind.DescriptorCheck:
                    w.Add(sim);
                    w.Add(sim.morale);
                    w.Add(t.outbox);
                    break;
                case CommitOpKind.SuccessionProbes:
                    w.Add(org.tiers);
                    break;
                case CommitOpKind.Promotion:
                    w.Add(org.tiers);
                    w.Add(org.knownMembers);
                    w.Add(t.ids);
                    w.Add(CharacterMembership);
                    w.Add(t.outbox);
                    break;
                case CommitOpKind.OldLeaderExit:
                    w.Add(org.lieutenants);
                    w.Add(org.knownMembers);
                    break;
                case CommitOpKind.NewLeader:
                    w.Add(org);
                    w.Add(org.lieutenants);
                    w.Add(org.knownMembers);
                    w.Add(org.succession);
                    w.Add(p.succession.next ?? (object)CharacterMembership);
                    KnownCharacter old = t.characters.Get(p.succession.oldLeader);
                    if (old != null) w.Add(old);
                    if (sim != null)
                    {
                        w.Add(sim);
                        w.Add(sim.morale);
                        w.Add(sim.doctrine);
                    }
                    w.Add(t.outbox);
                    break;
                case CommitOpKind.ActorEnded:
                    w.Add(p.actor);
                    ContractorProfile profile = p.actor.Get<ContractorProfile>();
                    if (profile != null) w.Add(profile);
                    w.Add(t.outbox);
                    break;
                case CommitOpKind.SoloAnchor:
                    w.Add(sim.spatial);
                    break;
                case CommitOpKind.OperationMarker:
                    w.Add(p.operation);
                    break;
                case CommitOpKind.Publication:
                    w.Add(t.outbox);
                    break;
                case CommitOpKind.EpisodeClosed:
                case CommitOpKind.StageMarkers:
                    w.Add(p.episode);
                    break;
                case CommitOpKind.SimDirty:
                    w.Add(sim);
                    break;
            }
            return w;
        }
    }
}
