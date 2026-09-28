using System;
using System.Collections.Generic;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;
using Verse;

namespace TheNetwork.Domain.Operations
{
    /// <summary>
    /// Abstract operations (STATE_MACHINES § 6): Preparing → Transit → Engaged → Returning → Delivering →
    /// Done, one scheduler job per checkpoint. Inputs are frozen when engagement begins; the resolver runs
    /// once and its outcome is committed; later checkpoints only apply it. No pawn, no map, no combat.
    /// </summary>
    public sealed class OperationService
    {
        public const string CheckpointJob = "operation.checkpoint";
        public const string TroubledJob = "operation.troubled";

        // Share of the ETA at which each checkpoint falls.
        private static readonly string[] Keys = { Checkpoint.Prep, Checkpoint.Arrive, Checkpoint.Resolve, Checkpoint.Return };
        private static readonly float[] At = { 0.2f, 0.5f, 0.7f, 1f };

        private readonly DomainContext ctx;

        public OperationService(DomainContext ctx)
        {
            this.ctx = ctx;
        }

        // ================================================================== inputs

        /// <summary>
        /// Resolver inputs as they would be if the contractor took the job now (willingness and pricing
        /// read this). Reads no randomness and changes nothing.
        /// </summary>
        public ResolverInputs Estimate(NetworkActor a, Contract c, ItemFacts f)
        {
            ResolverInputs i = Common(a, c, f);
            float strength = ctx.Contractors.Strength(a);
            i.forcePower = ContractorService.IsSolo(a) ? strength : strength * 0.6f;
            OrganizationProfile org = a.Get<OrganizationProfile>();
            i.leadership = org == null ? (ctx.Contractors.Embodied(a)?.notability ?? 0f) : 0.3f;
            return i;
        }

        private ResolverInputs Common(NetworkActor a, Contract c, ItemFacts f)
        {
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            int count = Math.Max(1, c.Quantity);
            float diff = Valuation.Difficulty(f, count);
            float goods = Math.Max(1f, Valuation.GoodsBasis(f, count));
            int sponsored = c.request.premiumContribution + c.Funding(Intel.MoneyPurpose.Renegotiation);
            return new ResolverInputs
            {
                threatPower = Valuation.MarketThreat(f, count),
                preparedness = ctx.Knowledge.Preparedness(a.id, Valuation.Topics(f)),
                intelQuality = 0.5f,
                logisticsPenalty = ContractorPricing.LogisticsPenalty(sim?.mobility, diff),
                moraleFactor = sim == null ? 1f : MoraleModel.Factor(sim.morale.descriptor),
                reckless = sim != null && sim.morale.descriptor == MoraleDescriptor.Reckless,
                caution = sim?.doctrine.caution ?? 0.5f,
                cruelty = sim?.doctrine.cruelty ?? 0f,
                professionalism = sim?.doctrine.professionalism ?? 0.5f,
                sponsorship = 0.3f * Math.Min(1f, sponsored / goods),
                specialization = Valuation.Specialization(a.Get<ContractorProfile>(), f),
                opposition = "market",
                difficulty = diff,
                market = true,
                frozenTick = ctx.Now
            };
        }

        /// <summary>The frozen inputs (SIMULATION § 3.2), from what was actually committed.</summary>
        public ResolverInputs Freeze(Operation op, NetworkActor a, Contract c, ItemFacts f)
        {
            ResolverInputs i = Common(a, c, f);
            i.forcePower = CommittedPower(a, op);
            i.headcount = op.Commitment().Headcount;
            i.leadership = Leadership(a, op);
            return i;
        }

        /// <summary>The strength of the people actually sent (Strength restricted to the commitment).</summary>
        public float CommittedPower(NetworkActor a, Operation op)
        {
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            if (sim == null) return 0f;
            float equip = (0.6f + 0.15f * sim.equipment.tier) * (0.6f + 0.4f * sim.equipment.condition);
            float people;
            OrganizationProfile org = a.Get<OrganizationProfile>();
            if (org == null)
            {
                KnownCharacter c = ctx.characters.Get(a.bindings.embodies);
                people = c != null && c.IsAlive ? 3.5f * (0.5f + sim.skill) : 0f;
            }
            else
            {
                people = 0f;
                for (int i = 0; i < op.forces.Count; i++) people += op.forces[i].healthy * ContractorService.TierWeight(op.forces[i].tier);
                for (int i = 0; i < op.characters.Count; i++)
                {
                    KnownCharacter c = ctx.characters.Get(op.characters[i]);
                    if (c != null && c.IsAlive) people += c.id == org.leader ? 4f : 3f;
                }
                people *= 0.7f + 0.6f * sim.skill;
            }
            return people * equip * MoraleModel.Factor(sim.morale.descriptor);
        }

        private float Leadership(NetworkActor a, Operation op)
        {
            OrganizationProfile org = a.Get<OrganizationProfile>();
            if (org == null) return ctx.Contractors.Embodied(a)?.notability ?? 0f;
            for (int i = 0; i < op.characters.Count; i++)
            {
                if (op.characters[i] == org.leader) return 0.5f + 0.5f * (ctx.characters.Get(org.leader)?.notability ?? 0f);
            }
            for (int i = 0; i < op.characters.Count; i++) if (org.lieutenants.Contains(op.characters[i])) return 0.3f;
            return 0f;
        }

        // ================================================================== lifecycle

        /// <summary>Starts the operation for an awarded contract: forces checked out, checkpoints scheduled.</summary>
        public Operation Start(Contract c, NetworkActor a, ItemFacts f)
        {
            Operation op = new Operation
            {
                id = new OperationId(ctx.ids.NextId()),
                contract = c.id,
                contractor = a.id,
                contractorName = a.name.Display,
                startedTick = ctx.Now,
                plannedTicks = Math.Max(Ticks.PerDay, c.terms?.etaTicks ?? Ticks.PerDay * 5)
            };
            op.seed = NetHash.Combine(NetHash.Combine(ctx.networkSeed, op.id.Value), "operation");
            op.danger = Resolver.Danger(Resolver.Edge(Estimate(a, c, f)));
            ForceCommitment forces = ctx.Contractors.Checkout(a, op.id, op.danger);
            op.forces = forces.forces;
            op.characters = forces.characters;
            for (int i = 0; i < Keys.Length; i++)
            {
                op.checkpoints.Add(new Checkpoint { key = Keys[i], dueTick = op.startedTick + (int)(op.plannedTicks * At[i]) });
            }
            ctx.operations.Add(op);
            c.operations.Add(op.id);
            ScheduleNext(op);
            // Hidden geography: where they start from, and the area where the work happens (WHERE only).
            ctx.Spatial?.BeginOperation(op, a, c, f);
            OperationEvent e = NewEvent(EventKeys.OperationStarted, Importance.Minor, op, c);
            ctx.bus.Publish(e);
            StateVersion.Bump();
            return op;
        }

        /// <summary>Schedules the first checkpoint not yet done (idempotent: the job kind is a singleton per operation).</summary>
        public void ScheduleNext(Operation op)
        {
            for (int i = 0; i < op.checkpoints.Count; i++)
            {
                Checkpoint cp = op.checkpoints[i];
                if (cp.done) continue;
                ctx.scheduler.Schedule(CheckpointJob, Math.Max(cp.dueTick, ctx.Now + 1), op.id.Value, i);
                return;
            }
        }

        public Checkpoint NextCheckpoint(Operation op)
        {
            for (int i = 0; i < op.checkpoints.Count; i++) if (!op.checkpoints[i].done) return op.checkpoints[i];
            return null;
        }

        /// <summary>operation.checkpoint. State-guarded: a stale job (done checkpoint, finished op) does nothing.</summary>
        public void RunCheckpoint(ScheduledJob job)
        {
            Operation op = ctx.operations.Get(new OperationId(job.target));
            if (op == null || op.quarantinedReason != null || op.IsFinished) return;
            if (job.arg < 0 || job.arg >= op.checkpoints.Count) return;
            Checkpoint cp = op.checkpoints[job.arg];
            if (cp.done || NextCheckpoint(op) != cp) return;
            if (op.status == OpStatus.Troubled) return;
            Contract c = ctx.contracts.Get(op.contract);
            if (c == null || c.IsTerminal)
            {
                Abort(op, "ContractClosed");
                return;
            }
            // A renegotiation pauses the operation; the decision resumes it.
            if (c.status == ContractStatus.Renegotiating) return;
            NetworkActor a = ctx.actors.Get(op.contractor);
            switch (cp.key)
            {
                case Checkpoint.Prep:
                case Checkpoint.Arrive:
                    if (!ContractorCanWork(a))
                    {
                        ctx.Procurement.OnContractorLostBeforeWork(c, op);
                        return;
                    }
                    cp.done = true;
                    if (cp.key == Checkpoint.Prep)
                    {
                        op.phase = OpPhase.Transit;
                        ctx.Spatial?.OnCheckpoint(op);
                        bool local = op.spatial != null && op.spatial.origin != null && op.spatial.workRegion != null && op.spatial.origin.tileId == op.spatial.workRegion.tileId;
                        ctx.FieldLog?.Note(c, local ? FieldLogKeys.WorkingNearby : FieldLogKeys.SetOut, op.contractorName);
                        // A chartered crossing earns one beat, never the hub, the landing or the route.
                        if (op.spatial != null && op.spatial.Charter) ctx.FieldLog?.NoteOnce(c, FieldLogKeys.TransportArranged, op.contractorName);
                        ScheduleNext(op);
                    }
                    else
                    {
                        ctx.Spatial?.ArriveAtWork(op);
                        ctx.FieldLog?.Note(c, FieldLogKeys.Arrived, op.contractorName);
                        Engage(op, c, a);
                    }
                    break;
                case Checkpoint.Resolve:
                    cp.done = true;
                    ctx.Spatial?.OnCheckpoint(op);
                    ResolveNow(op, c, a);
                    ctx.Spatial?.StartReturn(op);
                    break;
                case Checkpoint.Return:
                    cp.done = true;
                    ctx.Spatial?.ReturnFromWork(op);
                    ReturnHome(op, c, a);
                    break;
            }
            StateVersion.Bump();
        }

        /// <summary>Before engagement: is the contractor still there to do the work?</summary>
        public bool ContractorCanWork(NetworkActor a)
        {
            if (a == null || a.status != ActorStatus.Active || a.quarantinedReason != null) return false;
            if (ContractorService.IsSolo(a))
            {
                KnownCharacter self = ctx.Contractors.Embodied(a);
                return self != null && self.IsAlive && self.status != CharacterStatus.Captured && self.status != CharacterStatus.Missing;
            }
            return true;
        }

        /// <summary>Transit → Engaged: inputs frozen. A professional contractor reports a job that turned out much worse.</summary>
        private void Engage(Operation op, Contract c, NetworkActor a)
        {
            op.phase = OpPhase.Engaged;
            ItemFacts f = ctx.catalog.Facts(c.Acquire.DefName);
            if (f == null)
            {
                ctx.Procurement.Void(c, Causes.DefMissing);
                return;
            }
            op.frozenInputs = Freeze(op, a, c, f);
            float now = Resolver.Danger(Resolver.Edge(op.frozenInputs));
            Offer offer = ctx.contracts.Get(c.acceptedOffer);
            float quoted = offer?.basis.danger ?? op.danger;
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            bool reports = sim != null && sim.doctrine.professionalism >= 0.4f;
            bool forcedWorse = ProcurementDevOverrides.forceWorseThanExpected;
            ProcurementDevOverrides.forceWorseThanExpected = false;
            if ((forcedWorse || (reports && now - quoted > 0.25f)) && c.renegotiation == null)
            {
                ctx.Procurement.OnWorseThanExpected(c, op, now);
                return;
            }
            ScheduleNext(op);
        }

        /// <summary>After a renegotiation: inputs are refrozen (the client's choice legitimately changed them) and work resumes.</summary>
        public void Resume(Operation op, Contract c)
        {
            NetworkActor a = ctx.actors.Get(op.contractor);
            ItemFacts f = c.Acquire == null ? null : ctx.catalog.Facts(c.Acquire.DefName);
            if (a != null && f != null && op.phase == OpPhase.Engaged && op.outcome == null) op.frozenInputs = Freeze(op, a, c, f);
            ScheduleNext(op);
        }

        /// <summary>The resolve checkpoint: the resolver runs ONCE and the outcome is committed and published.</summary>
        private void ResolveNow(Operation op, Contract c, NetworkActor a)
        {
            if (op.outcome != null) return;
            ItemFacts f = ctx.catalog.Facts(c.Acquire.DefName);
            if (f == null)
            {
                ctx.Procurement.Void(c, Causes.DefMissing);
                return;
            }
            if (op.frozenInputs == null) op.frozenInputs = Freeze(op, a, c, f);
            NetRng rng = new NetRng(op.seed, "op.resolve", op.rerollNonce);
            OutcomeBand? forced = ProcurementDevOverrides.forceBand;
            ProcurementDevOverrides.forceBand = null;
            List<Participant> people = Participants(a, op);
            OperationOutcome o = Resolver.Resolve(op.frozenInputs, op.forces, people, c.Quantity, op.plannedTicks, rng, forced);
            if (ProcurementDevOverrides.forceSecured.HasValue)
            {
                o.secured = Math.Max(0, Math.Min(o.requested, ProcurementDevOverrides.forceSecured.Value));
                ProcurementDevOverrides.forceSecured = null;
            }
            if (ProcurementDevOverrides.forceDelayTicks.HasValue)
            {
                o.delayTicks = Math.Max(0, ProcurementDevOverrides.forceDelayTicks.Value);
                ProcurementDevOverrides.forceDelayTicks = null;
            }
            if (ProcurementDevOverrides.forceTroubled != null)
            {
                o.troubledKey = ProcurementDevOverrides.forceTroubled;
                ProcurementDevOverrides.forceTroubled = null;
            }
            if (ProcurementDevOverrides.forceNotTroubled)
            {
                o.troubledKey = null;
                ProcurementDevOverrides.forceNotTroubled = false;
            }
            o.committedTick = ctx.Now;
            // Committed with the outcome (never recomputed on load): empty when nobody came back to tell it.
            o.knowledgeGains = Resolver.GainsIfReported(o, op.forces, people.Count, Valuation.Topics(f));
            if (o.secured > 0) o.securedPayload.Add(CommitPayload(c, f, o, NetHash.Combine(op.seed, "payload")));
            op.outcome = o;
            op.status = OpStatus.Resolved;
            // Where trouble struck is fixed now, before any consequence reads it.
            ctx.Spatial?.RecordIncident(op);

            // Consequences for the contractor (records only), then the published result.
            ctx.Contractors.ApplyCasualties(a, o.ToReport(), c.id, op.id, Resolver.IsSuccess(o.band));
            ctx.Knowledge.Apply(a.id, o.knowledgeGains);
            OperationEvent e = NewEvent(EventKeys.OperationResolved, Importance.Minor, op, c);
            e.bandKey = o.band.ToString();
            e.secured = o.secured;
            e.requested = o.requested;
            e.killed = o.Killed;
            e.wounded = o.Wounded;
            e.captured = o.Captured;
            e.missing = o.Missing;
            e.flavorKey = o.flavorKey;
            ctx.bus.Publish(e);

            if (o.troubledKey != null)
            {
                op.status = OpStatus.Troubled;
                op.troubledDeadlineTick = ctx.Now + (int)((ContractKindRegistry.Get(c.kindKey)?.troubledDeadlineDays ?? 10f) * Ticks.PerDay);
                ctx.scheduler.Schedule(TroubledJob, op.troubledDeadlineTick, op.id.Value);
                ctx.Procurement.OnTroubled(c, op);
                return;
            }
            Checkpoint ret = op.Find(Checkpoint.Return);
            if (ret != null) ret.dueTick = Math.Max(ctx.Now + Ticks.PerHour, ret.dueTick + o.delayTicks);
            if (o.delayTicks > Ticks.PerDay / 2) ctx.Procurement.OnDelayed(c, op);
            ScheduleNext(op);
        }

        private List<Participant> Participants(NetworkActor a, Operation op)
        {
            List<Participant> list = new List<Participant>();
            OrganizationProfile org = a.Get<OrganizationProfile>();
            for (int i = 0; i < op.characters.Count; i++)
            {
                KnownCharacter c = ctx.characters.Get(op.characters[i]);
                if (c == null || !c.IsAlive) continue;
                bool leader = org == null || c.id == org.leader;
                // Exposure by role, never by importance: a leader in the same role faces the same odds.
                list.Add(new Participant { id = c.id, leader = leader, exposure = org == null ? 1f : (leader ? 0.9f : 1f) });
            }
            return list;
        }

        /// <summary>The exact goods secured: stuff and quality committed now, delivered as committed.</summary>
        private ItemPayload CommitPayload(Contract c, ItemFacts f, OperationOutcome o, int seed)
        {
            ItemPayload p = new ItemPayload { thing = c.Acquire.thing.Copy(), count = o.secured, role = PayloadRole.Target };
            if (f.madeFromStuff)
            {
                string stuff = ctx.world.PickStuff(f.defName, seed);
                if (stuff != null) p.stuff = new DefRef<ThingDef> { defName = stuff, label = ctx.catalog.Facts(stuff)?.label ?? stuff };
            }
            if (f.hasQuality)
            {
                NetRng q = new NetRng(seed, "quality");
                int b;
                switch (o.band)
                {
                    case OutcomeBand.Triumph: b = 3 + q.RangeInclusive(0, 1); break;
                    case OutcomeBand.Success: b = 2 + q.RangeInclusive(0, 1); break;
                    default: b = 1 + q.RangeInclusive(0, 1); break;
                }
                p.qualityBand = b;
            }
            return p;
        }

        /// <summary>The return checkpoint: survivors rejoin the roster; the contract decides what happens to the goods.</summary>
        private void ReturnHome(Operation op, Contract c, NetworkActor a)
        {
            op.phase = OpPhase.Returning;
            ReturnForces(op, a, false);
            op.phase = OpPhase.Delivering;
            op.status = OpStatus.Resolved;
            ctx.Procurement.OnOperationReturned(c, op);
        }

        private void ReturnForces(Operation op, NetworkActor a, bool missingFound)
        {
            if (op.outcomeApplied || a == null) return;
            CasualtyReport losses = op.outcome?.ToReport();
            if (losses != null && missingFound)
            {
                // Found alive: the missing come home wounded.
                List<TierCount> wounded = new List<TierCount>(losses.wounded);
                wounded.AddRange(losses.missing);
                losses = new CasualtyReport { killed = losses.killed, wounded = wounded, captured = losses.captured, missing = new List<TierCount>(), fates = losses.fates, woundDays = losses.woundDays };
            }
            ctx.Contractors.Return(a, op.id, op.Commitment(), losses);
            op.outcomeApplied = true;
        }

        /// <summary>The Delivering phase ends (goods dropped, handed over, or abandoned).</summary>
        public void Finish(Operation op)
        {
            if (op == null || op.IsFinished) return;
            op.phase = OpPhase.Done;
            op.endedTick = ctx.Now;
            ctx.scheduler.Cancel(CheckpointJob, op.id.Value);
            ctx.scheduler.Cancel(TroubledJob, op.id.Value);
            ctx.Spatial?.EndOperation(op);
            StateVersion.Bump();
        }

        /// <summary>
        /// Aborted: before the outcome, the checked-out forces return unharmed (STATE_MACHINES § 6). After
        /// the outcome, the committed results stand and survivors return.
        /// </summary>
        public void Abort(Operation op, string reasonKey)
        {
            if (op == null || op.IsFinished) return;
            NetworkActor a = ctx.actors.Get(op.contractor);
            if (op.outcome == null)
            {
                if (a != null) ctx.Contractors.Return(a, op.id, op.Commitment(), null);
                op.outcomeApplied = true;
            }
            else
            {
                ReturnForces(op, a, false);
            }
            op.status = OpStatus.Aborted;
            op.abortReasonKey = reasonKey;
            op.endedTick = ctx.Now;
            ctx.scheduler.Cancel(CheckpointJob, op.id.Value);
            ctx.scheduler.Cancel(TroubledJob, op.id.Value);
            ctx.Spatial?.EndOperation(op);
            StateVersion.Bump();
        }

        /// <summary>
        /// operation.troubled: the Troubled deadline. Phase 2 has no physical rescue, so it resolves abstractly from
        /// the operation's own stream: the group turns up (Returning) or is written off (Done).
        /// </summary>
        public void TroubledDeadline(ScheduledJob job)
        {
            Operation op = ctx.operations.Get(new OperationId(job.target));
            if (op == null || op.IsFinished || op.status != OpStatus.Troubled || op.outcome == null) return;
            Contract c = ctx.contracts.Get(op.contract);
            NetworkActor a = ctx.actors.Get(op.contractor);
            string key = op.outcome.troubledKey;
            float chance = key == "Stranded" ? 0.6f : (key == "Missing" ? 0.35f : 0.15f);
            bool found = new NetRng(op.seed, "op.troubled", op.rerollNonce).Chance(chance);
            if (ProcurementDevOverrides.forceTroubledFound.HasValue)
            {
                found = ProcurementDevOverrides.forceTroubledFound.Value;
                ProcurementDevOverrides.forceTroubledFound = null;
            }
            if (found)
            {
                // The missing come home wounded; captives stay captives (a Phase 3 rescue matter).
                for (int i = 0; i < op.outcome.fates.Count; i++)
                {
                    CharacterFate f = op.outcome.fates[i];
                    KnownCharacter kc = ctx.characters.Get(f.character);
                    if (f.fate == Fate.Missing && kc != null && kc.status == CharacterStatus.Missing)
                    {
                        kc.status = CharacterStatus.Wounded;
                        kc.statusTick = ctx.Now;
                        kc.woundedUntilTick = ctx.Now + 7 * Ticks.PerDay;
                    }
                }
                ReturnForces(op, a, true);
                op.status = OpStatus.Resolved;
                op.phase = OpPhase.Delivering;
                Checkpoint ret = op.Find(Checkpoint.Return);
                if (ret != null) ret.done = true;
                ctx.Spatial?.OnTroubledRecovered(op);
                if (c != null && !c.IsTerminal) ctx.Procurement.OnRecovered(c, op);
            }
            else
            {
                ReturnForces(op, a, false);
                op.status = OpStatus.Resolved;
                Finish(op);
                if (a != null && ContractorService.IsSolo(a) && !ContractorCanWork(a)) ctx.Contractors.EndActor(a, key == "Captured" ? "Captured" : "LostContact");
                if (c != null && !c.IsTerminal) ctx.Procurement.OnWrittenOff(c, op);
            }
            StateVersion.Bump();
        }

        /// <summary>Dev: runs the next checkpoint now (its due tick is moved to now; the same code path runs).</summary>
        public bool DevAdvance(Operation op)
        {
            if (op == null || op.IsFinished) return false;
            if (op.status == OpStatus.Troubled)
            {
                ctx.scheduler.Cancel(TroubledJob, op.id.Value);
                op.troubledDeadlineTick = ctx.Now;
                TroubledDeadline(new ScheduledJob { kind = TroubledJob, target = op.id.Value });
                return true;
            }
            for (int i = 0; i < op.checkpoints.Count; i++)
            {
                if (op.checkpoints[i].done) continue;
                op.checkpoints[i].dueTick = ctx.Now;
                ctx.scheduler.Cancel(CheckpointJob, op.id.Value);
                RunCheckpoint(new ScheduledJob { kind = CheckpointJob, target = op.id.Value, arg = i });
                return true;
            }
            return false;
        }

        // ================================================================== helpers

        private OperationEvent NewEvent(string key, Importance importance, Operation op, Contract c)
        {
            OperationEvent e = EventFactory.Make<OperationEvent>(key, importance, op.id.Ref, c.id.Ref, op.contractor.Ref);
            e.operation = op.id;
            e.contract = c.id;
            e.contractor = op.contractor;
            e.contractorName = op.contractorName;
            e.itemLabel = c.ItemLabel;
            e.requested = c.Quantity;
            return e;
        }

        public List<Operation> Running()
        {
            List<Operation> list = new List<Operation>();
            for (int i = 0; i < ctx.operations.operations.Count; i++) if (!ctx.operations.operations[i].IsFinished) list.Add(ctx.operations.operations[i]);
            return list;
        }
    }
}
