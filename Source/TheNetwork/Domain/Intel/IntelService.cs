using System.Collections.Generic;
using RimWorld;
using Verse;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;

namespace TheNetwork.Domain.Intel
{
    /// <summary>
    /// Who the player is asking: an existing source actor, or a live faction that has no proxy yet
    /// (its proxy is created lazily on submission).
    /// </summary>
    public struct SourceKey
    {
        public ActorId actor;
        public int factionLoadId;

        public static SourceKey ForActor(ActorId id) { return new SourceKey { actor = id, factionLoadId = -1 }; }
        public static SourceKey ForFaction(int loadId) { return new SourceKey { actor = ActorId.None, factionLoadId = loadId }; }

        public bool IsFaction => !actor.IsValid && factionLoadId >= 0;

        public override string ToString()
        {
            return actor.IsValid ? actor.ToString() : "F" + factionLoadId;
        }
    }

    /// <summary>
    /// The Intel state machine (STATE_MACHINES § 1). Every transition happens here. Commands take a
    /// source and an item topic — never a quantity — and are gated by the Comms Console. Rounds run from
    /// scheduler jobs and draw from per-round NetRng streams, so a reload before a round resolves gives
    /// the same round.
    /// </summary>
    public sealed class IntelService
    {
        private readonly DomainContext ctx;

        public IntelService(DomainContext ctx)
        {
            this.ctx = ctx;
        }

        // ================================================================== guards

        public bool CommsOk(out string reasonKey)
        {
            if (IntelDevOverrides.commsGateOverride)
            {
                reasonKey = null;
                return true;
            }
            return ctx.comms.CanContact(out reasonKey);
        }

        private NetworkActor ResolveSource(SourceKey key, bool createProxy, out string reasonKey)
        {
            reasonKey = null;
            if (key.actor.IsValid)
            {
                NetworkActor a = ctx.actors.Get(key.actor);
                if (a != null && a.kind == ActorKind.FactionProxy && a.Get<IntelSourceProfile>()?.derived == true && createProxy)
                {
                    // Refresh the derived profile from the faction's current signals before freezing terms.
                    FactionFacts ff = a.bindings.faction == null ? null : ctx.Actors.FindFaction(a.bindings.faction.loadId);
                    if (ff != null) ctx.Actors.GetOrCreateFactionProxy(ff, true);
                }
                return ctx.Actors.IsUsableSource(a, out reasonKey) ? a : null;
            }
            if (key.IsFaction)
            {
                FactionFacts f = ctx.Actors.FindFaction(key.factionLoadId);
                if (!SourcePolicies.FactionCanBeContact(f, out reasonKey)) return null;
                NetworkActor existing = ctx.actors.ProxyForFaction(f.loadId);
                if (existing != null && existing.Has<IntelSourceProfile>() && !createProxy) return existing;
                return createProxy ? ctx.Actors.GetOrCreateFactionProxy(f, true) : null;
            }
            reasonKey = "SourceMissing";
            return null;
        }

        /// <summary>
        /// Preview of the terms a source would freeze for this topic (fee, continuation), without any
        /// side effect. For a faction without a proxy the profile is derived on the fly.
        /// </summary>
        public SearchTerms PreviewTerms(SourceKey key, string defName)
        {
            ItemFacts facts = ctx.catalog.Facts(defName);
            if (facts == null) return null;
            IntelSourceProfile profile;
            string name;
            string kindKey;
            if (key.actor.IsValid)
            {
                NetworkActor a = ctx.actors.Get(key.actor);
                profile = a?.Get<IntelSourceProfile>();
                if (profile == null) return null;
                if (profile.derived && a.bindings.faction != null)
                {
                    FactionFacts ff = ctx.Actors.FindFaction(a.bindings.faction.loadId);
                    if (ff != null) profile = SourcePolicies.ForFaction(ff);
                }
                name = a.name.Display;
                kindKey = a.kind.ToString();
            }
            else
            {
                FactionFacts f = ctx.Actors.FindFaction(key.factionLoadId);
                if (f == null) return null;
                profile = SourcePolicies.ForFaction(f);
                name = f.name;
                kindKey = ActorKind.FactionProxy.ToString();
            }
            return SourcePolicies.FreezeTerms(profile, facts, name, kindKey);
        }

        public CommandResult CanSubmit(SourceKey source, string defName)
        {
            string reason;
            if (!CommsOk(out reason)) return CommandResult.Fail(reason);
            if (ctx.diagnostics.IsDegraded("intel")) return CommandResult.Fail("SubsystemDegraded");
            if (!ServiceToggles.IntelEnabled) return CommandResult.Fail("IntelDisabled");
            if (string.IsNullOrEmpty(defName)) return CommandResult.Fail("NoTopic");
            if (!ctx.catalog.IsRequestable(defName, out reason)) return CommandResult.Fail(reason ?? "NotRequestable");
            if (ResolveSource(source, false, out reason) == null && !(source.IsFaction && reason == null)) return CommandResult.Fail(reason ?? "SourceMissing");
            SearchTerms preview = PreviewTerms(source, defName);
            if (preview == null) return CommandResult.Fail("SourceMissing");
            if (preview.initialFee > 0 && !IntelDevOverrides.waiveFees && !ctx.payment.CanCharge(preview.initialFee, out reason)) return CommandResult.Fail(reason ?? "CannotAfford");
            return CommandResult.Ok;
        }

        // ================================================================== commands

        /// <summary>SubmitIntel(topic, source). There is no quantity parameter, by design.</summary>
        public CommandResult Submit(SourceKey sourceKey, string defName)
        {
            CommandResult can = CanSubmit(sourceKey, defName);
            if (!can.ok) return can;

            string reason;
            NetworkActor source = ResolveSource(sourceKey, true, out reason);
            if (source == null) return CommandResult.Fail(reason ?? "SourceMissing");
            ItemFacts facts = ctx.catalog.Facts(defName);
            if (facts == null) return CommandResult.Fail("TopicMissing");
            IntelSourceProfile profile = source.Get<IntelSourceProfile>();
            SearchTerms terms = SourcePolicies.FreezeTerms(profile, facts, source.name.Display, source.kind.ToString());

            bool waived = IntelDevOverrides.waiveFees;
            if (terms.initialFee > 0 && !waived && !ctx.payment.TryCharge(terms.initialFee, out reason))
            {
                return CommandResult.Fail(reason ?? "CannotAfford");
            }

            NetworkActor player = ctx.actors.PlayerProxy;
            IntelRequest r = new IntelRequest
            {
                id = new IntelRequestId(ctx.ids.NextId()),
                requester = player?.id ?? ActorId.None,
                source = source.id,
                topic = new IntelTopic
                {
                    kind = TopicKind.Item,
                    thing = new DefRef<ThingDef> { defName = facts.defName, label = facts.label, packageId = facts.packageId, modName = facts.modName }
                },
                terms = terms,
                state = IntelState.Submitted,
                submittedTick = ctx.Now
            };
            r.seed = NetHash.Combine(NetHash.Combine(ctx.networkSeed, r.id.Value), "intel");
            if (terms.initialFee > 0)
            {
                r.fees.Add(new MoneyRecord { tick = ctx.Now, silver = waived ? 0 : terms.initialFee, direction = MoneyDirection.PlayerPaid, noteKey = waived ? "fee.devWaived" : "fee.initial", round = 1 });
            }
            ctx.intel.Add(r);

            IntelEvent e = NewEvent(EventKeys.IntelRequested, Importance.Minor, r);
            e.silver = terms.initialFee;
            ctx.bus.Publish(e);

            // Submitted → Searching in the same tick (round 1 scheduled).
            r.state = IntelState.Searching;
            r.round = 1;
            r.segmentStartRound = 1;
            StartRound(r, facts);
            NetLog.Trace(LogCategory.Intel, "Submitted " + r + " to " + source + " fee " + terms.initialFee);
            return CommandResult.Ok;
        }

        public CommandResult CanContinue(IntelRequestId id)
        {
            IntelRequest r = ctx.intel.Get(id);
            if (r == null) return CommandResult.Fail("RequestMissing");
            if (r.state != IntelState.AwaitingDecision) return CommandResult.Fail("NotAwaitingDecision");
            string reason;
            if (!CommsOk(out reason)) return CommandResult.Fail(reason);
            if (!ctx.Actors.SourceStillExists(r.source)) return CommandResult.Fail("SourceEnded");
            if (r.round >= r.terms.maxRounds) return CommandResult.Fail("RoundLimit");
            int fee = r.terms.continuationFee;
            if (fee > 0 && !IntelDevOverrides.waiveFees && !ctx.payment.CanCharge(fee, out reason)) return CommandResult.Fail(reason ?? "CannotAfford");
            return CommandResult.Ok;
        }

        /// <summary>AwaitingDecision → Searching, charging whatever the frozen continuation policy charges.</summary>
        public CommandResult Continue(IntelRequestId id)
        {
            CommandResult can = CanContinue(id);
            if (!can.ok) return can;
            IntelRequest r = ctx.intel.Get(id);
            ItemFacts facts = ctx.catalog.Facts(r.topic.DefName);
            if (facts == null)
            {
                Invalidate(r, "TopicMissing", false);
                return CommandResult.Fail("TopicMissing");
            }
            int fee = IntelDevOverrides.waiveFees ? 0 : r.terms.continuationFee;
            string reason;
            if (fee > 0 && !ctx.payment.TryCharge(fee, out reason)) return CommandResult.Fail(reason ?? "CannotAfford");
            r.round++;
            r.segmentStartRound = r.round;
            if (fee > 0) r.fees.Add(new MoneyRecord { tick = ctx.Now, silver = fee, direction = MoneyDirection.PlayerPaid, noteKey = "fee.continuation", round = r.round });
            r.state = IntelState.Searching;
            StartRound(r, facts);
            IntelEvent e = NewEvent(EventKeys.IntelSearchContinued, Importance.Minor, r);
            e.silver = fee;
            ctx.bus.Publish(e);
            return CommandResult.Ok;
        }

        public CommandResult CanEnd(IntelRequestId id)
        {
            IntelRequest r = ctx.intel.Get(id);
            if (r == null) return CommandResult.Fail("RequestMissing");
            if (r.state != IntelState.AwaitingDecision) return CommandResult.Fail("NotAwaitingDecision");
            string reason;
            if (!CommsOk(out reason)) return CommandResult.Fail(reason);
            return CommandResult.Ok;
        }

        /// <summary>AwaitingDecision → Concluded. Every lead already delivered is kept.</summary>
        public CommandResult End(IntelRequestId id)
        {
            CommandResult can = CanEnd(id);
            if (!can.ok) return can;
            Conclude(ctx.intel.Get(id), "PlayerEnded");
            return CommandResult.Ok;
        }

        public CommandResult CanCancel(IntelRequestId id)
        {
            IntelRequest r = ctx.intel.Get(id);
            if (r == null) return CommandResult.Fail("RequestMissing");
            if (r.state != IntelState.Searching) return CommandResult.Fail("NotSearching");
            string reason;
            if (!CommsOk(out reason)) return CommandResult.Fail(reason);
            return CommandResult.Ok;
        }

        /// <summary>Searching → Cancelled. Early in a round, the source's policy refunds part of that round's fee.</summary>
        public CommandResult Cancel(IntelRequestId id)
        {
            CommandResult can = CanCancel(id);
            if (!can.ok) return can;
            IntelRequest r = ctx.intel.Get(id);
            ctx.scheduler.Cancel(JobKinds.IntelRound, r.id.Value);
            int refund = CancelRefund(r, ctx.Now);
            r.state = IntelState.Cancelled;
            r.endedTick = ctx.Now;
            r.endReasonKey = "PlayerCancelled";
            r.nextRoundDueTick = -1;
            if (refund > 0) Refund(r, refund, "refund.cancel");
            IntelEvent e = NewEvent(EventKeys.IntelCancelled, Importance.Minor, r);
            e.silver = refund;
            ctx.bus.Publish(e);
            TryScheduleClose(r);
            return CommandResult.Ok;
        }

        /// <summary>The share of the running segment's fee refunded on cancellation (source policy).</summary>
        public static int CancelRefund(IntelRequest r, int now)
        {
            int segmentFee = 0;
            for (int i = 0; i < r.fees.Count; i++)
            {
                MoneyRecord m = r.fees[i];
                if (m.direction == MoneyDirection.PlayerPaid && m.round == r.segmentStartRound) segmentFee += m.silver;
            }
            if (segmentFee <= 0 || r.roundStartedTick < 0 || r.nextRoundDueTick <= r.roundStartedTick) return 0;
            // Only the first round of a paid segment, and only in its first half.
            if (r.round != r.segmentStartRound) return 0;
            int half = (r.nextRoundDueTick - r.roundStartedTick) / 2;
            if (now - r.roundStartedTick >= half) return 0;
            return segmentFee * r.terms.cancelRefundPercent / 100;
        }

        // ================================================================== rounds

        private void StartRound(IntelRequest r, ItemFacts facts)
        {
            r.roundStartedTick = ctx.Now;
            r.roundThreatBasis = ctx.world.BaseThreatPoints();
            int duration = SourcePolicies.RoundDurationTicks(r.terms, facts, r.seed, r.round);
            r.nextRoundDueTick = ctx.Now + duration;
            ctx.scheduler.Schedule(JobKinds.IntelRound, r.nextRoundDueTick, r.id.Value, r.round);
            StateVersion.Bump();
        }

        /// <summary>
        /// The intel.round job. State-guarded: a stale job (wrong round, wrong state) does nothing, so
        /// the kind is safe to retry.
        /// </summary>
        public void RunRound(ScheduledJob job)
        {
            IntelRequest r = ctx.intel.Get(new IntelRequestId(job.target));
            if (r == null || r.quarantinedReason != null) return;
            if (r.state != IntelState.Searching || job.arg != r.round) return;
            ResolveRound(r);
        }

        /// <summary>Dev: resolve the running round now (DEBUGGING § 3). Same seeded outcome as waiting.</summary>
        public bool ForceRoundNow(IntelRequestId id)
        {
            IntelRequest r = ctx.intel.Get(id);
            if (r == null || r.state != IntelState.Searching) return false;
            ctx.scheduler.Cancel(JobKinds.IntelRound, r.id.Value);
            ResolveRound(r);
            return true;
        }

        private void ResolveRound(IntelRequest r)
        {
            ItemFacts facts = ctx.catalog.Facts(r.topic.DefName);
            if (facts == null)
            {
                Invalidate(r, "TopicMissing", false);
                return;
            }
            if (!ctx.Actors.SourceStillExists(r.source))
            {
                Invalidate(r, "SourceGone", false);
                return;
            }

            NetRng rng = new NetRng(r.seed, "intel.round." + r.rerollNonce, r.round);
            float chance = SourcePolicies.LeadChance(r.terms, facts, r.round);
            bool leadRoll = rng.Chance(chance);
            LeadDivergence divergence = SourcePolicies.DrawDivergence(rng, SourcePolicies.Reliability(r.terms.reliabilityBand));
            if (IntelDevOverrides.forceLead) leadRoll = true;
            if (IntelDevOverrides.forceNoLead) leadRoll = false;
            if (IntelDevOverrides.forceDivergence.HasValue) divergence = IntelDevOverrides.forceDivergence.Value;
            SourceKind? forcedKind = IntelDevOverrides.forceSourceKind;
            IntelDevOverrides.Clear();

            Lead lead = null;
            string noLeadReason = "NothingThisRound";
            if (leadRoll)
            {
                int oppSeed = NetHash.Combine(NetHash.Combine(r.seed, r.round), "opp." + r.rerollNonce);
                IntelRoundResult result = ctx.Opportunities.GenerateForIntel(r, facts, divergence, oppSeed, forcedKind);
                if (result.thingCreationFailed)
                {
                    ctx.catalog.MarkUnusable(r.topic.DefName, result.reason);
                    Invalidate(r, "ItemCannotBeProduced", true);
                    return;
                }
                lead = result.lead;
                if (lead == null) noLeadReason = result.reason ?? "NoCredibleSource";
            }

            if (lead != null)
            {
                r.leads.Add(lead.id);
                IntelEvent e = NewEvent(EventKeys.IntelLeadDelivered, Importance.Notable, r);
                e.lead = lead.id;
                e.opportunity = lead.opportunity;
                e.subjects.Add(lead.id.Ref);
                e.subjects.Add(lead.opportunity.Ref);
                e.place = lead.reported.location?.Copy();
                ctx.bus.Publish(e);
            }
            else
            {
                IntelEvent e = NewEvent(EventKeys.IntelNoLead, Importance.Minor, r);
                e.reasonKey = noLeadReason;
                ctx.bus.Publish(e);
            }

            ApplyFollowUp(r, facts, SourcePolicies.AfterRound(r.terms, r, lead != null));
        }

        private void ApplyFollowUp(IntelRequest r, ItemFacts facts, RoundFollowUp next)
        {
            switch (next)
            {
                case RoundFollowUp.NextRound:
                    r.round++;
                    StartRound(r, facts);
                    break;
                case RoundFollowUp.NextRoundNewSegment:
                    r.round++;
                    r.segmentStartRound = r.round;
                    StartRound(r, facts);
                    break;
                case RoundFollowUp.AskPlayer:
                    r.state = IntelState.AwaitingDecision;
                    r.nextRoundDueTick = -1;
                    StateVersion.Bump();
                    break;
                case RoundFollowUp.ConcludeAfterLead:
                    Conclude(r, "SourceDone");
                    break;
                case RoundFollowUp.ConcludeRoundLimit:
                    Conclude(r, "RoundLimit");
                    break;
                case RoundFollowUp.ConcludeNothingCredible:
                    Conclude(r, "NothingCredible");
                    break;
                default:
                    Conclude(r, "SearchExhausted");
                    break;
            }
        }

        private void Conclude(IntelRequest r, string reasonKey)
        {
            if (r.IsTerminal) return;
            ctx.scheduler.Cancel(JobKinds.IntelRound, r.id.Value);
            r.state = IntelState.Concluded;
            r.endedTick = ctx.Now;
            r.endReasonKey = reasonKey;
            r.nextRoundDueTick = -1;
            // A search that ends with no lead at all is a meaningful outcome (Notable); fees are kept.
            IntelEvent e = NewEvent(EventKeys.IntelConcluded, r.leads.Count == 0 ? Importance.Notable : Importance.Minor, r);
            e.reasonKey = reasonKey;
            ctx.bus.Publish(e);
            TryScheduleClose(r);
        }

        // ================================================================== invalidation and money

        /// <summary>
        /// Any non-terminal state → Invalidated (a required reference is gone, or the item cannot be
        /// produced). The player did nothing wrong: the running round's fee is refunded in full, or
        /// every fee when <paramref name="fullRefund"/> (a technical failure of the item itself).
        /// </summary>
        public void Invalidate(IntelRequest r, string reasonKey, bool fullRefund)
        {
            if (r == null || r.IsTerminal) return;
            ctx.scheduler.Cancel(JobKinds.IntelRound, r.id.Value);
            int refund = fullRefund ? r.TotalPaid() - r.TotalRefunded() : RunningRoundFee(r);
            r.state = IntelState.Invalidated;
            r.endedTick = ctx.Now;
            r.endReasonKey = reasonKey;
            r.nextRoundDueTick = -1;
            if (refund > 0) Refund(r, refund, fullRefund ? "refund.full" : "refund.invalidated");
            IntelEvent e = NewEvent(EventKeys.IntelInvalidated, Importance.Minor, r);
            e.reasonKey = reasonKey;
            e.silver = refund;
            ctx.bus.Publish(e);
            TryScheduleClose(r);
            NetLog.Info(LogCategory.Intel, "Intel " + r.id + " (" + r.topic.Label + ") invalidated: " + reasonKey + (refund > 0 ? ", refunded " + refund + " silver" : "") + ".");
        }

        /// <summary>The fee that funds the running segment, when a round is actually running.</summary>
        public static int RunningRoundFee(IntelRequest r)
        {
            if (r.state != IntelState.Searching && r.state != IntelState.Submitted) return 0;
            int fee = 0;
            for (int i = 0; i < r.fees.Count; i++)
            {
                MoneyRecord m = r.fees[i];
                if (m.direction == MoneyDirection.PlayerPaid && m.round == r.segmentStartRound) fee += m.silver;
            }
            return fee;
        }

        private void Refund(IntelRequest r, int silver, string noteKey)
        {
            string reason;
            bool delivered = ctx.payment.TryRefund(silver, out reason);
            r.fees.Add(new MoneyRecord { tick = ctx.Now, silver = silver, direction = MoneyDirection.PlayerRefunded, noteKey = noteKey, round = r.round, pending = !delivered });
            if (!delivered)
            {
                ctx.scheduler.Schedule(JobKinds.RefundRetry, ctx.Now + Ticks.PerDay, r.id.Value);
                NetLog.Info(LogCategory.Payment, "Refund of " + silver + " silver for " + r.id + " is pending (" + reason + "); retrying daily.");
            }
        }

        /// <summary>payment.refund: retries undelivered refunds.</summary>
        public void RetryRefunds(ScheduledJob job)
        {
            IntelRequest r = ctx.intel.Get(new IntelRequestId(job.target));
            if (r == null) return;
            bool stillPending = false;
            for (int i = 0; i < r.fees.Count; i++)
            {
                MoneyRecord m = r.fees[i];
                if (!m.pending) continue;
                string reason;
                if (ctx.payment.TryRefund(m.silver, out reason)) m.pending = false;
                else stillPending = true;
            }
            if (stillPending) ctx.scheduler.Schedule(JobKinds.RefundRetry, ctx.Now + Ticks.PerDay, r.id.Value);
            StateVersion.Bump();
        }

        public bool HasPendingRefund(IntelRequest r)
        {
            for (int i = 0; i < r.fees.Count; i++) if (r.fees[i].pending) return true;
            return false;
        }

        // ================================================================== closing

        /// <summary>Terminal and every lead Closed → a 1-day archive job closes the request.</summary>
        public void TryScheduleClose(IntelRequest r)
        {
            if (r == null || !r.IsTerminal || r.state == IntelState.Closed) return;
            for (int i = 0; i < r.leads.Count; i++)
            {
                Lead l = ctx.intel.Get(r.leads[i]);
                if (l != null && l.state != LeadState.Closed) return;
            }
            r.closeDueTick = ctx.Now + Ticks.PerDay;
            ctx.scheduler.Schedule(JobKinds.IntelClose, r.closeDueTick, r.id.Value);
            StateVersion.Bump();
        }

        public void CloseJob(ScheduledJob job)
        {
            IntelRequest r = ctx.intel.Get(new IntelRequestId(job.target));
            if (r == null || !r.IsTerminal || r.state == IntelState.Closed) return;
            for (int i = 0; i < r.leads.Count; i++)
            {
                Lead l = ctx.intel.Get(r.leads[i]);
                if (l != null && l.state != LeadState.Closed) return; // a lead reopened the wait
            }
            r.state = IntelState.Closed;
            r.closeDueTick = -1;
            StateVersion.Bump();
        }

        /// <summary>Called when a lead closes: its request may now close.</summary>
        public void OnLeadClosed(Lead lead)
        {
            IntelRequest r = ctx.intel.Get(lead.intel);
            if (r != null) TryScheduleClose(r);
        }

        // ================================================================== helpers

        private IntelEvent NewEvent(string key, Importance importance, IntelRequest r)
        {
            IntelEvent e = EventFactory.Make<IntelEvent>(key, importance, r.id.Ref, r.source.Ref, r.requester.Ref);
            e.request = r.id;
            e.source = r.source;
            e.requester = r.requester;
            e.round = r.round;
            e.leadsSoFar = r.leads.Count;
            e.topicDefName = r.topic.DefName;
            e.topicLabel = r.topic.Label;
            e.sourceName = r.terms.sourceName ?? ctx.actors.NameOf(r.source);
            return e;
        }

        public List<IntelRequest> ActiveRequests()
        {
            List<IntelRequest> list = new List<IntelRequest>();
            for (int i = 0; i < ctx.intel.requests.Count; i++) if (ctx.intel.requests[i].IsActive) list.Add(ctx.intel.requests[i]);
            return list;
        }
    }
}
