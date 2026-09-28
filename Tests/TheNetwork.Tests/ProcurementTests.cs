using System;
using System.Collections.Generic;
using System.Reflection;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Operations;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Domain.Ports;
using TheNetwork.Domain.Relations;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;
using TheNetwork.Settings;

namespace TheNetwork.Tests
{
    /// <summary>Phase 2 procurement: willingness, bidding, quotes, money, operations, outcomes, delivery, consequences.</summary>
    public static class ProcurementTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("Procurement.PostExactQuantityOpensBidding", PostOpens));
            t.Add(new KeyValuePair<string, Action>("Bidding.OpenTrickleCapAndRefusals", OpenBidding));
            t.Add(new KeyValuePair<string, Action>("Bidding.DirectStillMayRefuse", DirectRefuses));
            t.Add(new KeyValuePair<string, Action>("Bidding.NewcomerBidderIsPersistent", Newcomer));
            t.Add(new KeyValuePair<string, Action>("Willingness.ReasonKeys", WillingnessReasons));
            t.Add(new KeyValuePair<string, Action>("Fixer.ReachLimitsCandidatePool", ReachLimits));
            t.Add(new KeyValuePair<string, Action>("Quote.FrozenWithSeparateContributions", QuoteFrozen));
            t.Add(new KeyValuePair<string, Action>("Fixer.DepositInsuranceReplacementPolicies", FixerPolicyShapes));
            t.Add(new KeyValuePair<string, Action>("Economics.SanityValuationAndMarketFloor", SanityValuation));
            t.Add(new KeyValuePair<string, Action>("Economics.DepositChargedOrRefused", DepositCharged));
            t.Add(new KeyValuePair<string, Action>("Operations.ResolverBandsCasualtiesDeterminism", ResolverBands));
            t.Add(new KeyValuePair<string, Action>("Operations.FrozenInputsSameOutcome", FrozenSameOutcome));
            t.Add(new KeyValuePair<string, Action>("Procurement.FullSuccessDeliversCommittedPayload", FullSuccess));
            t.Add(new KeyValuePair<string, Action>("Procurement.PartialDecisionAndContinuationLineage", PartialContinuation));
            t.Add(new KeyValuePair<string, Action>("Procurement.PartialGraceDefault", PartialGrace));
            t.Add(new KeyValuePair<string, Action>("Procurement.FailureLosesDepositInsurancePartial", FailureDeposit));
            t.Add(new KeyValuePair<string, Action>("Procurement.CatastropheLastKnownLocation", Catastrophe));
            t.Add(new KeyValuePair<string, Action>("Procurement.TroubledResolvesByDeadline", Troubled));
            t.Add(new KeyValuePair<string, Action>("Procurement.VoidRefundsEverything", VoidRefund));
            t.Add(new KeyValuePair<string, Action>("Procurement.CancellationPolicy", Cancellation));
            t.Add(new KeyValuePair<string, Action>("Procurement.ContractorLostBeforeWork", LostBeforeWork));
            t.Add(new KeyValuePair<string, Action>("Procurement.WorseThanExpectedRenegotiation", WorseThanExpected));
            t.Add(new KeyValuePair<string, Action>("Delivery.HoldRerouteAndLimit", DeliveryHold));
            t.Add(new KeyValuePair<string, Action>("Payment.DefaultHoldsOrHandsOver", PaymentDefault));
            t.Add(new KeyValuePair<string, Action>("Relations.HistoryChangesWillingnessAndPrice", RelationsMatter));
            t.Add(new KeyValuePair<string, Action>("Procurement.TerminalNeverReopens", TerminalNeverReopens));
            t.Add(new KeyValuePair<string, Action>("Persist.ContractsAndOperationsRoundTrip", PersistRoundTrip));
            t.Add(new KeyValuePair<string, Action>("Validator.RebuildsJobsAndVoidsMissingDefs", ValidatorRepairs));
            t.Add(new KeyValuePair<string, Action>("Compaction.TerminalContractsArchived", Compaction));
            t.Add(new KeyValuePair<string, Action>("Soak.ThreeGameYearsHundredContractors", Soak));
            t.Add(new KeyValuePair<string, Action>("Soak.EighteenGameYearsRetentionAndSaveSize", SoakYears));
            t.Add(new KeyValuePair<string, Action>("Operations.CapturedMissingAreRecords", CapturedMissingRecords));
            t.Add(new KeyValuePair<string, Action>("Removal.OriginFactionGoneContractorSurvives", OriginGone));
            t.Add(new KeyValuePair<string, Action>("Intel.KnownCapableContractorsBecomeContacts", ContractorContacts));
            t.Add(new KeyValuePair<string, Action>("Persist.EveryContractStateRoundTrips", PersistEveryState));
            t.Add(new KeyValuePair<string, Action>("NoPhaseCreep.Phase2Boundaries", NoPhaseCreep));
        }

        // ================================================================== helpers

        public static TestNet World(int contractors = 30, int seed = 424242, string brokerage = "Standard", string insurance = "None", ReachBand reach = ReachBand.Vast)
        {
            TestNet n = ContractorTests.WorldWithCast(contractors, seed);
            n.pay.silver = 1000000;
            ProcurementDevOverrides.Clear();
            return n;
        }

        public static NetworkActor Fixer(TestNet n, string brokerage = "Standard", string insurance = "None", ReachBand reach = ReachBand.Vast)
        {
            return n.AddFixer("Thorough", Band.Medium, Band.Medium, Band.Medium, brokerage, insurance, reach);
        }

        /// <summary>A capable, steady, professional team that will take ordinary work.</summary>
        public static NetworkActor Reliable(TestNet n, ContractorForm form = ContractorForm.Team)
        {
            NetworkActor a = ContractorTests.Make(n, ContractorTests.Template(form, ExperienceBand.Veteran, FameBand.Local));
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            sim.doctrine.caution = 0.4f;
            sim.doctrine.cruelty = 0.3f;
            sim.doctrine.professionalism = 0.8f;
            sim.doctrine.loyalty = 0.6f;
            sim.doctrine.greed = 0.3f;
            sim.doctrine.discretion = 0.6f;
            sim.equipment.tier = 4;
            sim.morale.descriptor = MoraleDescriptor.Steady;
            sim.mobility.rangeBand = Band.High;
            sim.origin = null;
            sim.MarkDirty();
            return a;
        }

        public static Contract Post(TestNet n, NetworkActor fixer, string def, int count, NetworkActor direct = null, int premium = 0)
        {
            CommandResult r = n.ctx.Procurement.Post(new ProcurementRequest
            {
                defName = def, count = count, broker = fixer.id, premiumContribution = premium,
                mode = direct != null ? ProcurementMode.Direct : ProcurementMode.Open, invited = direct?.id ?? ActorId.None
            });
            T.Check(r.ok, "posted (" + r + ")");
            return n.ctx.contracts.Get(n.ctx.Procurement.lastPosted);
        }

        public static Offer Bid(TestNet n, Contract c)
        {
            n.AdvanceTo(c.windowCloseTick);
            List<Offer> open = n.ctx.Procurement.OpenOffers(c);
            return open.Count > 0 ? open[0] : null;
        }

        /// <summary>Posts to the reliable contractor, collects its quote and accepts it.</summary>
        public static Contract Awarded(TestNet n, NetworkActor fixer, NetworkActor contractor, string def = "TestSteel", int count = 150, bool insure = false)
        {
            Contract c = Post(n, fixer, def, count, contractor);
            Offer o = Bid(n, c);
            T.Check(o != null, "the reliable contractor bids (" + Refusals(c) + ")");
            if (o == null) return c;
            CommandResult r = n.ctx.Procurement.Accept(o.id, insure);
            T.Check(r.ok, "accepted (" + r + ")");
            return c;
        }

        public static string Refusals(Contract c)
        {
            List<string> s = new List<string>();
            foreach (Refusal r in c.refusals) s.Add(r.actorName + ":" + string.Join("+", r.reasonKeys.ToArray()));
            return string.Join(", ", s.ToArray());
        }

        /// <summary>Advances time in quarter days until the condition holds (or the limit passes).</summary>
        public static void RunUntil(TestNet n, Func<bool> done, int maxDays = 80)
        {
            int end = n.clock.Now + maxDays * Ticks.PerDay;
            while (!done() && n.clock.Now < end) n.Advance(Ticks.PerDay / 4);
        }

        public static Operation Op(TestNet n, Contract c)
        {
            return n.ctx.Procurement.CurrentOperation(c);
        }

        /// <summary>The soak invariants: each is checked every simulated day and must stay 0.</summary>
        public static void SoakInvariants(TheNetwork.Diagnostics.SoakHarness.Result r, string label)
        {
            T.Eq(0, r.overCapacity, label + ": no commitments above job capacity");
            T.Eq(0, r.doubleBooked, label + ": no named person on two live operations");
            T.Eq(0, r.moneyViolations, label + ": no negative, empty or magical money");
            T.Eq(0, r.duplicateRefunds, label + ": no duplicate refund or payout");
            T.Eq(0, r.lklAboveSecured, label + ": no Last Known Location holds more than was secured");
            T.Eq(0L, r.moneyDrift, label + ": charges − refunds at the port = charges − refunds in the ledgers");
            T.Eq(0L, r.transferDrift, label + ": transfers sum to zero");
            // Phase 2.5 spatial continuity.
            T.Eq(0, r.spatialInvalid, label + ": every active contractor always has a valid hidden anchor");
            T.Eq(0, r.teleports, label + ": no contractor teleports (longest daily move " + r.maxDailyMove + " tiles)");
            T.Eq(0, r.fieldLogDuplicates, label + ": no duplicated Field Log entry");
            T.Eq(0, r.fieldLogLeaks, label + ": no Field Log on another issuer's or a closed contract");
            T.Check(r.spatial != null && r.spatial.ambientJourneys > 0 && r.spatial.operationPlans > 0, label + ": contractors travelled on their own and for operations");
            T.Eq(0, r.spatial.initFailed, label + ": no failed initialization");
            T.Eq(0, r.spatial.faults, label + ": no spatial faults");
        }

        /// <summary>Two ledgers hold the same records, in order: direction, purpose, amount, counterpart, pending.</summary>
        public static bool SameLedger(Contract a, Contract b)
        {
            if (a.ledger.Count != b.ledger.Count) return false;
            for (int i = 0; i < a.ledger.Count; i++)
            {
                MoneyRecord x = a.ledger[i], y = b.ledger[i];
                if (x.direction != y.direction || x.purpose != y.purpose || x.silver != y.silver || x.linkedContract != y.linkedContract || x.pending != y.pending || x.tick != y.tick || x.noteKey != y.noteKey) return false;
            }
            return true;
        }

        // ================================================================== posting and bidding

        private static void PostOpens()
        {
            TestNet n = World();
            NetworkActor fixer = Fixer(n);
            Contract c = Post(n, fixer, "TestSteel", 137);
            T.Eq(ContractStatus.Bidding, c.status, "Posted → Bidding in the same tick");
            T.Eq(137, c.Acquire.count, "the EXACT quantity is on the contract");
            T.Eq("TestSteel", c.Acquire.DefName, "the exact ThingDef");
            T.Check(c.parties.issuer == n.ctx.actors.PlayerProxy.id && c.parties.broker == fixer.id && !c.parties.contractor.IsValid, "issuer = player, broker = Fixer, no contractor yet");
            T.Eq(c.id, c.lineage.root, "a new contract is its own root");
            T.Check(c.Deliver != null && c.Deliver.preferredMapId == 1, "delivery target: the default home map");
            T.Eq(1, n.recorder.Count(EventKeys.ContractPosted), "Contract.Posted");
            T.Check(!n.ctx.Procurement.Post(new ProcurementRequest { defName = "TestSteel", count = 0, broker = fixer.id }).ok, "a quantity is required");
            T.Check(!n.ctx.Procurement.Post(new ProcurementRequest { defName = "TestSteel", count = 5, broker = n.ctx.actors.PlayerProxy.id }).ok, "the broker must be a Fixer");
            n.comms.usable = false;
            T.Eq("NoUsableCommsConsole", n.ctx.Procurement.Post(new ProcurementRequest { defName = "TestSteel", count = 5, broker = fixer.id }).reasonKey, "gated by the Comms Console");
        }

        private static void OpenBidding()
        {
            TestNet n = World(60);
            NetworkActor fixer = Fixer(n);
            Contract c = Post(n, fixer, "TestRifle", 3);
            T.Check(c.candidates.Count > 0 && c.candidates.Count <= 12, "a bounded candidate pool (" + c.candidates.Count + ")");
            n.AdvanceTo(c.windowOpenTick + (c.windowCloseTick - c.windowOpenTick) / 10 + 1);
            int afterFirst = c.evaluated.Count;
            n.AdvanceTo(c.windowCloseTick);
            T.Check(c.evaluated.Count >= afterFirst && afterFirst > 0, "evaluations trickle in over the window (" + afterFirst + " then " + c.evaluated.Count + ")");
            List<Offer> open = n.ctx.Procurement.OpenOffers(c);
            T.Check(open.Count <= 3, "at most three client-facing offers (" + open.Count + ")");
            T.Check(open.Count + c.refusals.Count >= 1, "every evaluation is an offer or a refusal");
            foreach (Refusal r in c.refusals) T.Check(r.reasonKeys.Count > 0, "a refusal carries reason keys");
            if (open.Count >= 2)
            {
                CommandResult ok = n.ctx.Procurement.Accept(open[1].id, false);
                T.Check(ok.ok, "accepting one offer");
                T.Eq(OfferState.Accepted, open[1].state, "accepted");
                T.Eq(OfferState.Superseded, open[0].state, "the others are superseded");
            }
            else
            {
                T.Check(c.status == ContractStatus.Bidding || c.status == ContractStatus.Unfilled, "a window with no offers ends Unfilled");
            }
        }

        private static void DirectRefuses()
        {
            TestNet n = World(10);
            NetworkActor fixer = Fixer(n);
            NetworkActor tired = Reliable(n);
            tired.Get<ContractorSimulation>().morale.descriptor = MoraleDescriptor.Exhausted;
            Contract c = Post(n, fixer, "TestSteel", 150, tired);
            T.Eq(1, c.candidates.Count, "Direct: only the invited contractor is evaluated");
            Offer o = Bid(n, c);
            T.Check(o == null, "Direct is not guaranteed acceptance");
            T.Check(c.refusals.Count == 1 && c.refusals[0].reasonKeys.Contains(RefusalReasons.Exhausted), "refused with Exhausted (" + Refusals(c) + ")");
            T.Eq(ContractStatus.Unfilled, c.status, "no offer at close → Unfilled");
            tired.Get<ContractorSimulation>().morale.descriptor = MoraleDescriptor.Steady;
            T.Check(n.ctx.Procurement.Repost(c.id, false).ok, "repost");
            T.Eq(2, c.biddingRound, "a new bidding round");
            Offer again = Bid(n, c);
            T.Check(again != null && again.round == 2, "the rested contractor bids in the new round (" + Refusals(c) + ")");
            n.AdvanceTo(n.clock.Now + 20 * Ticks.PerDay);
            T.Check(c.status == ContractStatus.Unfilled || c.status == ContractStatus.Expired, "an unaccepted quote expires; an unfilled contract expires later (" + c.status + ")");
        }

        private static void Newcomer()
        {
            TestNet n = World(3);
            NetworkActor fixer = Fixer(n);
            int before = ContractorTests.Contractors(n).Count;
            foreach (NetworkActor a in ContractorTests.Contractors(n)) a.Get<ContractorSimulation>().morale.descriptor = MoraleDescriptor.Exhausted;
            Contract c = Post(n, fixer, "TestSteel", 150);
            int after = ContractorTests.Contractors(n).Count;
            T.Eq(before + 1, after, "a thin pool brings a new persistent contractor into the world");
            NetworkActor fresh = n.ctx.actors.Get(c.candidates[0]);
            T.Check(fresh != null && fresh.provenance.source == ProvenanceSource.WorldGenerated, "the newcomer is world-generated and a candidate");
            T.Eq(1, n.recorder.Count(EventKeys.ContractorCreated), "Contractor.Created (a world event)");
            Contract c2 = Post(n, fixer, "TestSteel", 150);
            T.Eq(after, ContractorTests.Contractors(n).Count, "not on every contract (cooldown)");
            T.Check(n.ctx.actors.Get(fresh.id) != null, "the newcomer persists after its contract");
        }

        // ================================================================== willingness

        private static void WillingnessReasons()
        {
            TestNet n = World(0);
            NetworkActor fixer = Fixer(n);
            ItemFacts steel = n.cat.Facts("TestSteel");
            Contract c = Post(n, fixer, "TestSteel", 150, Reliable(n));

            NetworkActor ok = Reliable(n);
            WillingnessDecision d = Willingness.Evaluate(n.ctx, ok, c, steel);
            T.Check(d.accept, "a steady capable team accepts ordinary work (" + string.Join(",", d.reasonKeys.ToArray()) + ")");

            NetworkActor grudge = Reliable(n);
            n.ctx.Relations.Apply(grudge.id, c.parties.issuer, new RelationDelta { standing = -60f, trust = -0.3f, defaults = 1 }, HistoryRecordId.None);
            T.Check(Willingness.Evaluate(n.ctx, grudge, c, steel).reasonKeys.Contains(RefusalReasons.BadBlood), "bad relationship → BadBlood");

            NetworkActor busy = Reliable(n, ContractorForm.Crew);
            ContractorSimulation bs = busy.Get<ContractorSimulation>();
            for (int i = 0; i < ContractorService.JobCapacity(busy); i++) bs.commitments.Add(new OperationId(900 + i));
            T.Check(Willingness.Evaluate(n.ctx, busy, c, steel).reasonKeys.Contains(RefusalReasons.Overcommitted), "workload → Overcommitted");

            NetworkActor tired = Reliable(n);
            tired.Get<ContractorSimulation>().morale.descriptor = MoraleDescriptor.Exhausted;
            T.Check(Willingness.Evaluate(n.ctx, tired, c, steel).reasonKeys.Contains(RefusalReasons.Exhausted), "morale → Exhausted");

            NetworkActor soft = Reliable(n);
            soft.Get<ContractorSimulation>().doctrine.cruelty = 0.05f;
            soft.Get<ContractorSimulation>().doctrine.professionalism = 0.3f;
            Contract guns = Post(n, fixer, "TestRifle", 2, Reliable(n));
            T.Check(Willingness.Evaluate(n.ctx, soft, guns, n.cat.Facts("TestRifle")).reasonKeys.Contains(RefusalReasons.AgainstDoctrine), "doctrine mismatch → AgainstDoctrine");

            NetworkActor solo = ContractorTests.Make(n, ContractorTests.Template(ContractorForm.Solo, ExperienceBand.Green, FameBand.Unknown, false, "Cautious"));
            solo.Get<ContractorSimulation>().morale.descriptor = MoraleDescriptor.Steady;
            solo.Get<ContractorSimulation>().equipment.tier = 5;
            solo.Get<ContractorSimulation>().mobility.rangeBand = Band.High;
            Contract relic = Post(n, fixer, "ModX_Relic", 1, Reliable(n));
            WillingnessDecision danger = Willingness.Evaluate(n.ctx, solo, relic, n.cat.Facts("ModX_Relic"));
            T.Check(danger.reasonKeys.Contains(RefusalReasons.TooDangerous), "a green Solo after a unique relic → TooDangerous (danger " + danger.danger + ")");

            NetworkActor company = Reliable(n, ContractorForm.Company);
            Contract tiny = Post(n, fixer, "TestSteel", 5, Reliable(n));
            T.Check(Willingness.Evaluate(n.ctx, company, tiny, steel).reasonKeys.Contains(RefusalReasons.PayTooLow), "a company asked for a handful of steel → PayTooLow");

            NetworkActor bound = Reliable(n);
            n.world.factions.Add(FakeWorld.Faction(77, "Red Fangs", "ludeon.rimworld", 4, true, true, true));
            bound.Get<ContractorSimulation>().origin = new FactionRef { loadId = 77, name = "Red Fangs", defName = "RedFangs" };
            bound.Get<ContractorSimulation>().doctrine.loyalty = 0.9f;
            T.Check(Willingness.Evaluate(n.ctx, bound, c, steel).reasonKeys.Contains(RefusalReasons.PoliticalRisk), "loyal to a faction hostile to the client → PoliticalRisk");

            NetworkActor weak = Reliable(n);
            weak.Get<ContractorSimulation>().equipment.tier = 1;
            T.Check(Willingness.Evaluate(n.ctx, weak, relic, n.cat.Facts("ModX_Relic")).reasonKeys.Contains(RefusalReasons.NoCapability), "equipment below the goods' tech → NoCapability");

            WillingnessDecision a1 = Willingness.Evaluate(n.ctx, ok, c, steel);
            WillingnessDecision a2 = Willingness.Evaluate(n.ctx, ok, c, steel);
            T.Check(a1.accept == a2.accept && a1.danger == a2.danger, "deterministic: the same question gets the same answer");
        }

        private static void ReachLimits()
        {
            TestNet n = World(80);
            NetworkActor narrow = Fixer(n, "Standard", "None", ReachBand.Minimal);
            NetworkActor wide = Fixer(n, "Standard", "None", ReachBand.Vast);
            ProcurementDevOverrides.forceNewcomer = false;
            n.ctx.actors.lastNewcomerBidderTick = n.clock.Now;
            Contract a = Post(n, narrow, "TestSteel", 150);
            Contract b = Post(n, wide, "TestSteel", 150);
            T.Check(a.candidates.Count <= 4, "a Minimal-reach Fixer reaches at most 4 (" + a.candidates.Count + ")");
            T.Check(b.candidates.Count > a.candidates.Count && b.candidates.Count <= 12, "a Vast-reach Fixer reaches more, capped at 12 (" + b.candidates.Count + ")");
        }

        // ================================================================== quotes and money

        private static void QuoteFrozen()
        {
            TestNet n = World(0);
            NetworkActor fixer = Fixer(n);
            NetworkActor team = Reliable(n);
            Contract c = Post(n, fixer, "TestSteel", 150, team);
            Offer o = Bid(n, c);
            T.Check(o != null && o.quote != null, "an offer with its quote");
            if (o == null) return;
            ProcurementQuote q = o.quote;
            int price = q.finalPrice;
            bool contractorPart = false, brokerPart = false;
            foreach (QuoteComponent k in q.components)
            {
                if (k.IsContractor) { contractorPart = true; T.Eq(team.id, k.contributedBy, "contractor components carry the contractor"); }
                else { brokerPart = true; T.Eq(fixer.id, k.contributedBy, "Fixer components carry the Fixer"); }
            }
            T.Check(contractorPart && brokerPart, "contractor and Fixer contributions are kept apart");
            T.Eq(o.contractorQuote, q.ContractorShare(), "the offer's own bid is the contractor share");
            T.Eq(q.finalPrice, q.ContractorShare() + q.BrokerShare(), "final price = contractor + Fixer components");
            T.Check(q.finalPrice >= q.marketFloor, "never below the market-purchase floor");
            T.Eq(q.finalPrice, q.deposit + q.balance, "deposit + balance = price");
            T.Check(q.validUntilTick > n.clock.Now && q.etaTicks >= 2 * Ticks.PerDay, "validity and ETA frozen");
            // Changing the world does not change a frozen quote.
            team.Get<ContractorSimulation>().doctrine.greed = 0.99f;
            n.ctx.Relations.Apply(team.id, c.parties.issuer, new RelationDelta { standing = 50f, trust = 0.4f }, HistoryRecordId.None);
            T.Eq(price, n.ctx.contracts.Get(o.id).quote.finalPrice, "reading the quote again never recomputes it");
        }

        private static void FixerPolicyShapes()
        {
            T.Check(FixerPolicies.DepositShare("Lean", 0.3f, 0.5f) > FixerPolicies.DepositShare("Premium", 0.3f, 0.5f), "Lean brokers ask a larger deposit than Premium ones");
            T.Check(FixerPolicies.DepositShare("Standard", 0.8f, 0.5f) > FixerPolicies.DepositShare("Standard", 0.1f, 0.5f), "dangerous work raises the deposit");
            float s = FixerPolicies.DepositShare("Standard", 0.3f, 0.5f);
            T.Check(s > 0.45f && s < 0.6f, "about half as a starting point, not a law (" + s + ")");
            T.Check(FixerPolicies.CoordinationShare("Premium") > FixerPolicies.CoordinationShare("Lean"), "brokerage style changes the coordination component");
            FixerProfile none = new FixerProfile { insurancePolicyKey = null };
            T.Check(FixerPolicies.InsuranceOffer(none, 1000, 0.5f) == null, "no insurance style → no insurance offer");
            Insurance basic = FixerPolicies.InsuranceOffer(new FixerProfile { insurancePolicyKey = "fixer.insurance.Basic" }, 1000, 0.5f);
            Insurance generous = FixerPolicies.InsuranceOffer(new FixerProfile { insurancePolicyKey = "fixer.insurance.Generous" }, 1000, 0.5f);
            T.Check(basic.coverage < 1f && generous.coverage < 1f && generous.coverage > basic.coverage, "coverage below 1; Generous covers more");
            T.Check(generous.premium > basic.premium && basic.premium > 0, "and costs more");
            T.Check(!basic.Covers(Causes.PreWorkLoss) && generous.Covers(Causes.PreWorkLoss), "only listed failure classes are covered");
            bool tr; float share;
            FixerPolicies.Replacement("fixer.replacement.Standard", "Premium", out tr, out share);
            T.Check(tr && share == 1f, "Premium: replacement, else full refund");
            FixerPolicies.Replacement("fixer.replacement.Standard", "Lean", out tr, out share);
            T.Check(!tr && share < 0.5f, "Lean: no replacement, a small refund");
            T.Check(FixerPolicies.CancelRefundShare("refund.Standard", true) > FixerPolicies.CancelRefundShare("refund.Standard", false), "cancellation refunds less once the work is under way");
        }

        private static void SanityValuation()
        {
            ItemFacts broken = new ItemFacts { defName = "X", marketValue = 0f, categoryMedian = 80f, stackLimit = 10, tradeable = true };
            T.Eq(80f, Valuation.UnitValue(broken), "no market value → category median");
            ItemFacts cheap = new ItemFacts { defName = "Y", marketValue = 5f, recipeInputValue = 200f, stackLimit = 1, tradeable = true, craftable = true };
            T.Check(Valuation.UnitValue(cheap) >= 220f, "worth less than its inputs → raised to the recipe input value");
            ItemFacts normal = new ItemFacts { defName = "Z", marketValue = 100f, stackLimit = 10, tradeable = true, craftable = true, techLevel = 4 };
            ItemFacts valuable = new ItemFacts { defName = "V", marketValue = 1500f, stackLimit = 1, tradeable = true, craftable = true, techLevel = 4 };
            ItemFacts valuableUnique = new ItemFacts { defName = "VU", marketValue = 1500f, stackLimit = 1, unique = true, techLevel = 6 };
            T.Eq(100f, Valuation.UnitValue(normal), "a sane value is used as is");
            T.Check(Valuation.MarketFloor(normal, 10) >= 1400, "the floor is above buying from a trader");
            ItemFacts unique = new ItemFacts { defName = "U", marketValue = 100f, stackLimit = 1, unique = true, techLevel = 6 };
            T.Check(Valuation.Difficulty(unique, 1) > Valuation.Difficulty(normal, 1) + 0.5f, "unique goods are much harder to source");
            T.Check(Valuation.MarketThreat(unique, 1) > 4f * Valuation.MarketThreat(normal, 1), "and far more dangerous to get");
            ItemFacts absurd = new ItemFacts { defName = "W", marketValue = float.PositiveInfinity, stackLimit = 1 };
            T.Check(Valuation.GoodsBasis(absurd, 5) > 0 && Valuation.GoodsBasis(absurd, 5) <= Valuation.MaxPrice, "absurd values are capped, never overflow");
            // Price premium for unique work, through the real bid path.
            TestNet n = World(0);
            NetworkActor team = Reliable(n, ContractorForm.Company);
            team.Get<ContractorSimulation>().equipment.tier = 5;
            int eta;
            List<QuoteComponent> plain = ContractorPricing.Bid(team, valuable, 1, 0.3f, "Unknown", out eta, new NetRng(1, "x"));
            List<QuoteComponent> rare = ContractorPricing.Bid(team, valuableUnique, 1, 0.3f, "Unknown", out eta, new NetRng(1, "x"));
            T.Check(ContractorPricing.Sum(rare) > 1.5f * ContractorPricing.Sum(plain), "severe premium for unique items (" + ContractorPricing.Sum(rare) + " vs " + ContractorPricing.Sum(plain) + ")");
        }

        private static void DepositCharged()
        {
            TestNet n = World(0);
            NetworkActor fixer = Fixer(n, "Standard", "Basic");
            NetworkActor team = Reliable(n);
            Contract c = Post(n, fixer, "TestSteel", 150, team, 40);
            Offer o = Bid(n, c);
            T.Check(o != null && o.quote.insuranceOffer != null, "an insured quote");
            if (o == null) return;
            n.pay.silver = 10;
            CommandResult poor = n.ctx.Procurement.Accept(o.id, true);
            T.Eq("CannotAffordDeposit", poor.reasonKey, "cannot pay → CannotAffordDeposit");
            T.Eq(ContractStatus.Bidding, c.status, "nothing changed");
            n.pay.silver = 1000000;
            int before = n.pay.charged;
            T.Check(n.ctx.Procurement.Accept(o.id, true).ok, "accepted with insurance");
            int due = o.quote.deposit + 40 + o.quote.insuranceOffer.premium;
            T.Eq(due, n.pay.charged - before, "deposit + premium contribution + insurance premium charged at award");
            T.Eq(ContractStatus.Active, c.status, "Awarded → Active (operation started)");
            T.Eq(team.id, c.parties.contractor, "contractor = accepted bidder");
            T.Check(c.terms != null && c.terms.price == o.quote.finalPrice && c.terms.insurance != null, "terms copied from the frozen quote");
            T.Eq(1, c.operations.Count, "one operation");
            Operation op = Op(n, c);
            T.Check(op.phase == OpPhase.Preparing && op.Commitment().Headcount > 0, "forces checked out");
            T.Check(team.Get<ContractorSimulation>().commitments.Contains(op.id), "the contractor is committed");
        }

        // ================================================================== resolver and operations

        private static ResolverInputs Inputs(float force, float threat)
        {
            return new ResolverInputs { forcePower = force, threatPower = threat, preparedness = 0.5f, intelQuality = 0.5f, moraleFactor = 1f, caution = 0.4f, professionalism = 0.5f, difficulty = 0.5f, market = true };
        }

        private static void ResolverBands()
        {
            HashSet<OutcomeBand> seen = new HashSet<OutcomeBand>();
            int killedGood = 0, killedBad = 0, lost = 0;
            List<TierCount> forces = new List<TierCount> { new TierCount(Tier.Regular, 8), new TierCount(Tier.Veteran, 3) };
            for (int i = 0; i < 400; i++)
            {
                float force = 0.5f + (i % 40);
                OperationOutcome o = Resolver.Resolve(Inputs(force, 6f), forces, new List<Participant>(), 50, 10 * Ticks.PerDay, new NetRng(i, "t"));
                seen.Add(o.band);
                T.Check(o.secured >= 0 && o.secured <= 50, "secured within the request");
                if (o.band == OutcomeBand.Triumph) T.Eq(50, o.secured, "Triumph secures everything");
                if (o.band == OutcomeBand.Disaster) T.Eq(0, o.secured, "Disaster secures nothing");
                if (o.band <= OutcomeBand.Success) killedGood += o.Killed; else if (o.band >= OutcomeBand.Failure) killedBad += o.Killed;
                lost += o.Captured + o.Missing;
                int committed = 11;
                T.Check(o.Killed + o.Captured + o.Missing <= committed, "no more casualties than people sent");
            }
            T.Eq(6, seen.Count, "all six bands occur (" + string.Join(",", new List<OutcomeBand>(seen).ConvertAll(x => x.ToString()).ToArray()) + ")");
            T.Check(killedBad > killedGood, "bad bands kill more (" + killedBad + " vs " + killedGood + ")");
            T.Check(lost > 0, "captured and missing occur");
            OperationOutcome a = Resolver.Resolve(Inputs(5f, 6f), forces, new List<Participant>(), 50, Ticks.PerDay * 10, new NetRng(99, "op.resolve"));
            OperationOutcome b = Resolver.Resolve(Inputs(5f, 6f), forces, new List<Participant>(), 50, Ticks.PerDay * 10, new NetRng(99, "op.resolve"));
            T.Check(a.band == b.band && a.secured == b.secured && a.Killed == b.Killed && a.Wounded == b.Wounded && a.delayTicks == b.delayTicks, "same inputs and stream → same outcome");
            OperationOutcome solo = Resolver.Resolve(Inputs(1f, 30f), new List<TierCount>(), new List<Participant> { new Participant { id = new CharacterId(5), leader = true, exposure = 1f } }, 1, Ticks.PerDay * 5, new NetRng(3, "t"), OutcomeBand.Disaster);
            T.Eq(1, solo.fates.Count, "a Solo gets exactly one fate draw");
        }

        private static void FrozenSameOutcome()
        {
            Func<TestNet> make = () =>
            {
                TestNet n = World(0, 99);
                NetworkActor fixer = Fixer(n);
                NetworkActor team = Reliable(n);
                Awarded(n, fixer, team);
                return n;
            };
            TestNet x = make(), y = make();
            Contract cx = x.ctx.contracts.contracts[0], cy = y.ctx.contracts.contracts[0];
            Operation ox = Op(x, cx), oy = Op(y, cy);
            RunUntil(x, () => ox.frozenInputs != null);
            T.Check(ox.frozenInputs != null && ox.phase >= OpPhase.Engaged, "inputs frozen when engagement begins");
            // A reload between engagement and resolution: the operation round-trips through Scribe.
            Operation copy = RoundTrip(ox);
            T.Eq(ox.frozenInputs.forcePower, copy.frozenInputs.forcePower, "frozen force survives a reload");
            T.Eq(ox.seed, copy.seed, "seed persisted");
            float force = ox.frozenInputs.forcePower;
            // World changes after engagement do not change the outcome.
            NetworkActor tx = x.ctx.actors.Get(cx.parties.contractor);
            tx.Get<ContractorSimulation>().equipment.tier = 1;
            tx.Get<ContractorSimulation>().MarkDirty();
            RunUntil(x, () => ox.outcome != null);
            RunUntil(y, () => oy.outcome != null);
            T.Eq(force, ox.frozenInputs.forcePower, "inputs stay frozen");
            T.Check(ox.outcome != null && oy.outcome != null, "both resolved");
            if (ox.outcome == null || oy.outcome == null) return;
            T.Eq(oy.outcome.band, ox.outcome.band, "same band despite the later change");
            T.Eq(oy.outcome.secured, ox.outcome.secured, "same cargo secured");
            T.Eq(oy.outcome.Killed, ox.outcome.Killed, "same casualties");
            T.Eq(oy.outcome.delayTicks, ox.outcome.delayTicks, "same delay");
        }

        public static Operation RoundTrip(Operation op)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "thenetwork-op-" + Guid.NewGuid().ToString("N") + ".xml");
            Verse.Scribe.saver.InitSaving(path, "op");
            try { op.ExposeData(); } finally { Verse.Scribe.saver.FinalizeSaving(); }
            Operation copy = new Operation();
            Verse.Scribe.loader.InitLoading(path);
            try { copy.ExposeData(); } finally { Verse.Scribe.loader.FinalizeLoading(); }
            System.IO.File.Delete(path);
            return copy;
        }

        // ================================================================== outcomes

        private static void FullSuccess()
        {
            TestNet n = World(0);
            NetworkActor fixer = Fixer(n);
            NetworkActor team = Reliable(n);
            Contract c = Awarded(n, fixer, team, "TestRifle", 4);
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            int charged = n.pay.charged;
            RunUntil(n, () => c.IsTerminal);
            T.Eq(ContractStatus.Fulfilled, c.status, "Fulfilled");
            T.Eq(4, c.Acquire.delivered, "the exact quantity delivered");
            T.Eq(1, n.delivery.deliveries, "one drop-pod delivery");
            T.Check(n.delivery.lastPayload.Count == 1 && n.delivery.lastPayload[0].thing.defName == "TestRifle" && n.delivery.lastPayload[0].count == 4, "the committed payload: exact def and count");
            Operation op = Op(n, c);
            T.Check(n.delivery.lastPayload[0].qualityBand == op.outcome.securedPayload[0].qualityBand && op.outcome.securedPayload[0].qualityBand >= 0, "quality committed at resolution, delivered as committed");
            T.Eq(c.terms.balance, n.pay.charged - charged, "balance charged on delivery");
            T.Eq(OpPhase.Done, op.phase, "operation Done");
            T.Check(!team.Get<ContractorSimulation>().commitments.Contains(op.id), "the contractor is free again");
            T.Eq(1, n.recorder.Count(EventKeys.ContractCompleted), "Contract.Completed");
            T.Check(n.ctx.Knowledge.Proficiency(team.id, TheNetwork.Domain.Knowledge.Topics.Thing("TestRifle")) > 0f, "knowledge gained from the work");
            T.Check(n.ctx.Relations.Get(team.id, c.parties.issuer).successes == 1, "relationship recorded the success");
        }

        private static void PartialContinuation()
        {
            TestNet n = World(0);
            NetworkActor fixer = Fixer(n);
            NetworkActor team = Reliable(n);
            Contract c = Awarded(n, fixer, team, "TestSteel", 200);
            ProcurementDevOverrides.forceBand = OutcomeBand.Partial;
            RunUntil(n, () => c.status == ContractStatus.Renegotiating || c.IsTerminal);
            T.Eq(ContractStatus.Renegotiating, c.status, "a partial result is not silently success");
            T.Eq(SubStatus.PartialResult, c.subStatus, "the client decides");
            int secured = c.Acquire.secured;
            T.Check(secured > 0 && secured < 200, "part of the goods secured (" + secured + ")");
            T.Check(n.ctx.Procurement.RespondPartial(c.id, PartialChoice.AcceptAndContinue).ok, "accept and continue");
            T.Eq(ContractStatus.PartiallyFulfilled, c.status, "the parent is PartiallyFulfilled");
            T.Eq(secured, c.Acquire.delivered, "the secured part was delivered");
            T.Eq(1, c.lineage.children.Count, "a NEW linked contract");
            Contract child = n.ctx.contracts.Get(c.lineage.children[0]);
            T.Check(child != null && child.lineage.parent == c.id && child.lineage.root == c.id && child.lineage.relationKey == ContractLineage.Continuation, "continuation lineage: parent and root");
            T.Eq(200 - secured, child.Quantity, "for the remainder");
            T.Eq(ContractStatus.Bidding, child.status, "the continuation looks for a contractor");
            int proRated = (int)Math.Round(c.terms.price * secured / 200f);
            T.Check(c.ExternalCharged() <= Math.Max(proRated, c.terms.deposit) + 1, "paid a pro-rated price at most");
        }

        private static void PartialGrace()
        {
            TestNet n = World(0);
            NetworkActor fixer = Fixer(n);
            Contract c = Awarded(n, fixer, Reliable(n), "TestSteel", 200);
            ProcurementDevOverrides.forceBand = OutcomeBand.Partial;
            RunUntil(n, () => c.status == ContractStatus.Renegotiating || c.IsTerminal);
            T.Eq(SubStatus.PartialResult, c.subStatus, "waiting for the client");
            RunUntil(n, () => c.IsTerminal, 10);
            T.Eq(ContractStatus.PartiallyFulfilled, c.status, "the grace default accepts the partial result");
            T.Eq(0, c.lineage.children.Count, "no continuation unless asked");
        }

        private static void FailureDeposit()
        {
            TestNet n = World(0);
            NetworkActor fixer = Fixer(n, "Standard", "Basic");
            Contract c = Awarded(n, fixer, Reliable(n), "TestSteel", 150, true);
            int refundedBefore = n.pay.refunded;
            ProcurementDevOverrides.forceBand = OutcomeBand.Failure;
            ProcurementDevOverrides.forceSecured = 0;
            RunUntil(n, () => c.IsTerminal);
            T.Eq(ContractStatus.Failed, c.status, "Failed");
            T.Eq(Causes.OperationFailed, c.outcome.causeKey, "cause recorded");
            int payout = n.pay.refunded - refundedBefore;
            int deposit = c.Funding(MoneyPurpose.Deposit);
            T.Check(payout > 0 && payout < deposit, "insurance recovers only part of the deposit (" + payout + " of " + deposit + ")");
            T.Eq((int)Math.Round(deposit * c.terms.insurance.coverage), payout, "exactly the coverage share");
            TestNet m = World(0);
            NetworkActor f2 = Fixer(m);
            Contract c2 = Awarded(m, f2, Reliable(m), "TestSteel", 150);
            int r0 = m.pay.refunded;
            ProcurementDevOverrides.forceBand = OutcomeBand.Failure;
            ProcurementDevOverrides.forceSecured = 0;
            RunUntil(m, () => c2.IsTerminal);
            T.Eq(0, m.pay.refunded - r0, "uninsured: the deposit is lost");
            T.Eq(0, c2.Acquire.delivered, "nothing delivered");
        }

        private static void Catastrophe()
        {
            TestNet n = World(0);
            n.world.factions.Add(FakeWorld.Faction(21, "Blood Hawks", "ludeon.rimworld", 4, true, true, true));
            NetworkActor fixer = Fixer(n);
            NetworkActor team = Reliable(n);
            Contract c = Awarded(n, fixer, team, "TestSteel", 150);
            int refunded = n.pay.refunded;
            ProcurementDevOverrides.forceBand = OutcomeBand.Disaster;
            ProcurementDevOverrides.forceFollowUp = true;
            RunUntil(n, () => c.IsTerminal && n.ctx.consequences.pending.Count == 0);
            T.Check(c.status == ContractStatus.Failed || c.status == ContractStatus.Troubled, "catastrophic loss (" + c.status + ")");
            if (c.status == ContractStatus.Failed) T.Eq(Causes.CatastrophicLoss, c.outcome.causeKey, "cause CatastrophicLoss");
            Opportunity lkl = null;
            foreach (Opportunity o in n.ctx.opportunities.opportunities) if (o.origin == OpportunityOrigin.ConsequenceRule) lkl = o;
            T.Check(lkl != null, "a Last Known Location was created");
            if (lkl == null) return;
            T.Eq(c.id.Ref, lkl.originRef, "linked back to the contract");
            T.Eq(1, lkl.lineageDepth, "lineage depth");
            T.Eq(OpportunityState.Materialized, lkl.state, "through the Phase 1 site machinery");
            Opportunity again = lkl;
            int secured = Op(n, c).outcome.secured;
            T.Check(again.TargetCount >= 0 && again.TargetCount <= secured, "cargo bounded by what was secured (" + again.TargetCount + " of " + secured + ")");
            T.Eq(refunded, n.pay.refunded, "the follow-up does not refund the deposit");
            T.Eq(1, n.recorder.Count(EventKeys.OpportunityFollowUpCreated), "Opportunity.FollowUpCreated (letter)");
        }

        private static void Troubled()
        {
            TestNet n = World(0);
            NetworkActor fixer = Fixer(n);
            NetworkActor team = Reliable(n);
            Contract c = Awarded(n, fixer, team, "TestSteel", 150);
            ProcurementDevOverrides.forceBand = OutcomeBand.Failure;
            ProcurementDevOverrides.forceTroubled = SubStatus.Missing;
            RunUntil(n, () => c.status == ContractStatus.Troubled || c.IsTerminal);
            T.Eq(ContractStatus.Troubled, c.status, "missing → Troubled");
            T.Check(!c.IsTerminal, "Troubled is not terminal");
            T.Eq(1, n.recorder.Count(EventKeys.ContractorMissing), "Contractor.Missing (Major)");
            RunUntil(n, () => c.IsTerminal, 30);
            T.Check(c.IsTerminal, "resolved by the deadline, never stuck (" + c.status + ")");
            Operation op = Op(n, c);
            T.Check(op.IsFinished && op.outcomeApplied, "the operation finished and its people accounted for");
            T.Check(!team.Get<ContractorSimulation>().commitments.Contains(op.id), "no commitment left behind");
        }

        private static void VoidRefund()
        {
            TestNet n = World(0);
            NetworkActor fixer = Fixer(n, "Standard", "Basic");
            NetworkActor team = Reliable(n);
            Contract c = Awarded(n, fixer, team, "TestSteel", 150, true);
            OrganizationProfile org = team.Get<OrganizationProfile>();
            int committed = org.Committed;
            T.Check(committed > 0, "people committed");
            int paid = c.ExternalCharged();
            int refunded = n.pay.refunded;
            n.ctx.Procurement.Void(c, Causes.DefMissing);
            T.Eq(ContractStatus.Voided, c.status, "Voided");
            T.Eq(paid, n.pay.refunded - refunded, "technical invalidation refunds every silver paid (deposit, premium, insurance)");
            T.Eq(0, org.Committed, "the aborted operation returned its people unharmed");
            T.Eq(OpStatus.Aborted, Op(n, c).status, "operation aborted");
            n.pay.canRefund = false;
            Contract c2 = Awarded(n, fixer, Reliable(n), "TestSteel", 150);
            n.ctx.Procurement.Void(c2, Causes.DefMissing);
            T.Check(c2.HasPendingRefund(), "no destination: the refund is pending");
            n.pay.canRefund = true;
            n.Advance(Ticks.PerDay + 1);
            T.Check(!c2.HasPendingRefund(), "retried and delivered");
        }

        private static void Cancellation()
        {
            TestNet n = World(0);
            NetworkActor fixer = Fixer(n);
            Contract open = Post(n, fixer, "TestSteel", 150, Reliable(n));
            int charged = n.pay.charged;
            T.Check(n.ctx.Procurement.Cancel(open.id).ok, "cancel before award");
            T.Eq(ContractStatus.Cancelled, open.status, "Cancelled");
            T.Eq(charged, n.pay.charged, "free before award");

            Contract c = Awarded(n, fixer, Reliable(n));
            int deposit = c.Funding(MoneyPurpose.Deposit);
            int refunded = n.pay.refunded;
            T.Check(n.ctx.Procurement.Cancel(c.id).ok, "cancel while Preparing");
            T.Eq((int)Math.Round(deposit * 0.25f), n.pay.refunded - refunded, "the Standard refund policy returns a quarter");

            Contract late = Awarded(n, fixer, Reliable(n));
            Operation op = Op(n, late);
            RunUntil(n, () => op.phase >= OpPhase.Engaged || late.IsTerminal);
            T.Eq("TooLateToCancel", n.ctx.Procurement.Cancel(late.id).reasonKey, "not a player command once the work is engaged");
        }

        private static void LostBeforeWork()
        {
            TestNet n = World(12);
            NetworkActor fixer = Fixer(n, "Premium");
            NetworkActor team = Reliable(n);
            Contract c = Awarded(n, fixer, team);
            foreach (NetworkActor a in ContractorTests.Contractors(n)) if (a != team) { Reliable(n); break; }
            Reliable(n);
            n.ctx.Contractors.EndActor(team, "Test");
            RunUntil(n, () => c.IsTerminal, 20);
            T.Eq(ContractStatus.Failed, c.status, "the original contract ends");
            T.Eq(Causes.PreWorkLoss, c.outcome.causeKey, "cause PreWorkLoss");
            T.Eq(1, c.lineage.children.Count, "a Premium broker finds a replacement");
            Contract child = n.ctx.contracts.Get(c.lineage.children[0]);
            T.Check(child.lineage.inheritedFrom == c.id && child.lineage.parent == c.id && child.lineage.relationKey == ContractLineage.Replacement, "replacement lineage: inheritedFrom + parent");
            T.Check(child.IsUnderway && child.parties.contractor != team.id, "the replacement is under way with another contractor");
            T.Eq(c.terms.price, child.terms.price, "the Fixer honours the quoted price");
            T.Eq(0, child.ExternalCharged(), "no second deposit charged");
            T.Eq(c.terms.deposit, child.Funding(MoneyPurpose.Deposit), "the paid deposit is carried over as a transfer");

            TestNet m = World(0);
            NetworkActor lean = Fixer(m, "Lean");
            NetworkActor solo = Reliable(m);
            Contract c2 = Awarded(m, lean, solo);
            int deposit = c2.Funding(MoneyPurpose.Deposit);
            int r0 = m.pay.refunded;
            m.ctx.Contractors.EndActor(solo, "Test");
            RunUntil(m, () => c2.IsTerminal, 20);
            T.Eq(Causes.PreWorkLoss, c2.outcome.causeKey, "PreWorkLoss");
            T.Eq(0, c2.lineage.children.Count, "a Lean broker does not replace");
            T.Eq((int)Math.Round(deposit * 0.25f), m.pay.refunded - r0, "a quarter refunded by policy");
        }

        private static void WorseThanExpected()
        {
            TestNet n = World(0);
            NetworkActor fixer = Fixer(n);
            Contract c = Awarded(n, fixer, Reliable(n), "TestSteel", 200);
            ProcurementDevOverrides.forceWorseThanExpected = true;
            RunUntil(n, () => c.status == ContractStatus.Renegotiating || c.IsTerminal);
            T.Eq(SubStatus.WorseThanExpected, c.subStatus, "the contractor reports worse than expected");
            T.Check(c.renegotiation != null && c.renegotiation.extraSilver > 0 && c.renegotiation.reducedCount < 200, "it asks for more money or less scope");
            int extra = c.renegotiation.extraSilver;
            int charged = n.pay.charged;
            T.Check(n.ctx.Procurement.RespondWorse(c.id, WorseChoice.PayMore).ok, "the client pays more");
            T.Eq(extra, n.pay.charged - charged, "the extra is charged");
            T.Eq(ContractStatus.Active, c.status, "back to Active");
            RunUntil(n, () => c.IsTerminal);
            T.Check(c.IsTerminal, "and the contract runs to its end (" + c.status + ")");

            TestNet m = World(0);
            NetworkActor f2 = Fixer(m);
            Contract c2 = Awarded(m, f2, Reliable(m), "TestSteel", 200);
            ProcurementDevOverrides.forceWorseThanExpected = true;
            RunUntil(m, () => c2.status == ContractStatus.Renegotiating || c2.IsTerminal);
            int reduced = c2.renegotiation.reducedCount;
            RunUntil(m, () => c2.status != ContractStatus.Renegotiating, 5);
            T.Eq(reduced, c2.Quantity, "no answer: the grace default accepts the reduced scope");
        }

        private static void DeliveryHold()
        {
            TestNet n = World(0);
            NetworkActor fixer = Fixer(n);
            Contract c = Awarded(n, fixer, Reliable(n), "TestSteel", 100);
            n.delivery.homes.Clear();
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            int charged = n.pay.charged;
            RunUntil(n, () => c.subStatus == SubStatus.Hold || c.IsTerminal);
            T.Eq(SubStatus.Hold, c.subStatus, "no home map → Hold");
            T.Eq(charged, n.pay.charged, "no balance charged while nothing can land");
            n.delivery.homes.Add(7);
            RunUntil(n, () => c.IsTerminal, 3);
            T.Eq(ContractStatus.Fulfilled, c.status, "delivered once a home exists again");
            T.Eq(7, c.Deliver.deliveredMapId, "rerouted to another home map");

            TestNet m = World(0);
            NetworkActor f2 = Fixer(m);
            Contract c2 = Awarded(m, f2, Reliable(m), "TestSteel", 100);
            m.delivery.noSpot.Add(1);
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            RunUntil(m, () => c2.subStatus == SubStatus.Hold || c2.IsTerminal);
            T.Check(c2.Deliver.attempts >= 3, "no drop spot: retried before Hold (" + c2.Deliver.attempts + ")");
            RunUntil(m, () => c2.IsTerminal, 20);
            T.Eq(ContractStatus.Failed, c2.status, "a Hold that outlasts its limit ends; nothing stays stuck");
            T.Eq(Causes.UndeliverableNoHome, c2.outcome.causeKey, "UndeliverableNoHome");
        }

        private static void PaymentDefault()
        {
            TestNet n = World(0);
            NetworkActor fixer = Fixer(n);
            NetworkActor greedy = Reliable(n);
            ContractorSimulation gs = greedy.Get<ContractorSimulation>();
            gs.doctrine.greed = 0.9f;
            gs.doctrine.professionalism = 0.3f;
            gs.doctrine.loyalty = 0.2f;
            Contract c = Awarded(n, fixer, greedy, "TestSteel", 100);
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            RunUntil(n, () => Op(n, c).outcome != null || c.IsTerminal);
            n.pay.silver = 0;
            RunUntil(n, () => c.subStatus == SubStatus.AwaitingPayment || c.IsTerminal, 30);
            T.Eq(SubStatus.AwaitingPayment, c.subStatus, "a greedy contractor holds the goods");
            T.Eq(1, n.recorder.Count(EventKeys.PaymentDefaulted), "Payment.Defaulted");
            T.Check(n.ctx.Relations.Get(greedy.id, c.parties.issuer).defaults == 1, "the contractor remembers");
            n.pay.silver = 1000000;
            T.Check(n.ctx.Procurement.PayBalance(c.id).ok, "the client pays later");
            T.Eq(ContractStatus.Fulfilled, c.status, "and receives the goods");

            TestNet m = World(0);
            NetworkActor f2 = Fixer(m);
            NetworkActor holdout = Reliable(m);
            holdout.Get<ContractorSimulation>().doctrine.greed = 0.9f;
            holdout.Get<ContractorSimulation>().doctrine.professionalism = 0.3f;
            holdout.Get<ContractorSimulation>().doctrine.loyalty = 0.2f;
            Contract c2 = Awarded(m, f2, holdout, "TestSteel", 100);
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            RunUntil(m, () => Op(m, c2).outcome != null || c2.IsTerminal);
            m.pay.silver = 0;
            RunUntil(m, () => c2.IsTerminal, 15);
            T.Eq(ContractStatus.PartiallyFulfilled, c2.status, "no payment by the grace deadline → partial handover");
            T.Check(c2.Acquire.delivered > 0 && c2.Acquire.delivered < 100, "only the goods the deposit covered (" + c2.Acquire.delivered + ")");
            T.Eq(Causes.PartialHandover, c2.outcome.causeKey, "PartialHandover");
        }

        private static void RelationsMatter()
        {
            TestNet n = World(0);
            NetworkActor fixer = Fixer(n);
            NetworkActor team = Reliable(n);
            ItemFacts steel = n.cat.Facts("TestSteel");
            int eta;
            int neutral = ContractorPricing.Sum(ContractorPricing.Bid(team, steel, 150, 0.3f, "Unknown", out eta, new NetRng(1, "q")));
            int trusted = ContractorPricing.Sum(ContractorPricing.Bid(team, steel, 150, 0.3f, "Trusted", out eta, new NetRng(1, "q")));
            int rival = ContractorPricing.Sum(ContractorPricing.Bid(team, steel, 150, 0.3f, "Rival", out eta, new NetRng(1, "q")));
            T.Check(trusted <= neutral && rival > neutral, "the relationship changes the price (" + trusted + " / " + neutral + " / " + rival + ")");
            Contract c = Post(n, fixer, "TestSteel", 150, team);
            T.Check(Willingness.Evaluate(n.ctx, team, c, steel).accept, "willing before");
            ContractEvent e = EventFactory.Make<ContractEvent>(EventKeys.PaymentDefaulted, Importance.Notable);
            e.contractor = team.id;
            e.issuer = c.parties.issuer;
            n.bus.Publish(e);
            n.bus.Publish(e);
            T.Check(Willingness.Evaluate(n.ctx, team, c, steel).reasonKeys.Contains(RefusalReasons.BadBlood), "after defaults: BadBlood");
        }

        private static void TerminalNeverReopens()
        {
            TestNet n = World(0);
            NetworkActor fixer = Fixer(n);
            Contract c = Post(n, fixer, "TestSteel", 150, Reliable(n));
            n.ctx.Procurement.Cancel(c.id);
            T.Eq(ContractStatus.Cancelled, c.status, "terminal");
            T.Check(!n.ctx.Procurement.Repost(c.id, true).ok, "a terminal contract cannot be reposted");
            T.Check(!n.ctx.Procurement.Cancel(c.id).ok, "or cancelled again");
            n.ctx.Procurement.Void(c, Causes.DefMissing);
            T.Eq(ContractStatus.Cancelled, c.status, "or voided");
        }

        private static void PersistRoundTrip()
        {
            TestNet n = World(0);
            NetworkActor fixer = Fixer(n, "Standard", "Basic");
            Contract bidding = Post(n, fixer, "TestRifle", 2, Reliable(n));
            n.AdvanceTo(bidding.windowCloseTick);
            Contract active = Awarded(n, fixer, Reliable(n), "TestSteel", 150, true);
            RunUntil(n, () => Op(n, active).frozenInputs != null);
            TheNetwork.Core.NetworkState state = new TheNetwork.Core.NetworkState { contracts = n.ctx.contracts, operations = n.ctx.operations, consequences = n.ctx.consequences };
            string path = PersistenceTests.SaveState(state, 1);
            TheNetwork.Core.NetworkState loaded = new TheNetwork.Core.NetworkState();
            Verse.Scribe.loader.InitLoading(path);
            try
            {
                List<string> failures = new List<string>();
                loaded.ExposeStores(failures);
                T.Eq(0, failures.Count, "no store failed (" + string.Join("; ", failures.ToArray()) + ")");
            }
            finally
            {
                Verse.Scribe.loader.FinalizeLoading();
            }
            loaded.RebuildIndexes();
            System.IO.File.Delete(path);
            Contract b2 = loaded.contracts.Get(bidding.id), a2 = loaded.contracts.Get(active.id);
            T.Check(b2 != null && a2 != null, "contracts reloaded");
            if (b2 == null || a2 == null) return;
            T.Eq(bidding.status, b2.status, "status");
            T.Eq(bidding.offers.Count, b2.offers.Count, "offers");
            T.Check(a2.terms != null && a2.terms.price == active.terms.price && a2.terms.insurance != null && a2.terms.insurance.coverage == active.terms.insurance.coverage, "terms and insurance");
            T.Check(a2.Acquire.count == 150 && a2.Deliver != null, "polymorphic objectives");
            T.Eq(active.ledger.Count, a2.ledger.Count, "money ledger");
            Offer o1 = n.ctx.contracts.Get(active.acceptedOffer), o2 = loaded.contracts.Get(active.acceptedOffer);
            T.Check(o2 != null && o2.quote.finalPrice == o1.quote.finalPrice && o2.quote.components.Count == o1.quote.components.Count, "the frozen quote and its components");
            Operation p1 = Op(n, active), p2 = loaded.operations.Get(p1.id);
            T.Check(p2 != null && p2.phase == p1.phase && p2.seed == p1.seed && p2.checkpoints.Count == p1.checkpoints.Count, "operation phase, seed, checkpoints");
            T.Check(p2.frozenInputs != null && p2.frozenInputs.forcePower == p1.frozenInputs.forcePower, "frozen inputs");
            T.Check(p2.quarantinedReason == null && a2.quarantinedReason == null, "nothing quarantined");
        }

        private static void ValidatorRepairs()
        {
            TestNet n = World(0);
            NetworkActor fixer = Fixer(n);
            Contract bidding = Post(n, fixer, "TestSteel", 150, Reliable(n));
            Contract active = Awarded(n, fixer, Reliable(n));
            Operation op = Op(n, active);
            T.Check(n.ctx.Procurement.OpenOffers(bidding).Count == 1 && n.clock.Now >= bidding.windowCloseTick, "the first contract waits on its quote");
            n.scheduler.Cancel(ProcurementService.OffersJob, bidding.id.Value);
            n.scheduler.Cancel(OperationService.CheckpointJob, op.id.Value);
            List<string> findings = new List<string>();
            int repaired = n.ctx.Procurement.EnsureJobs(findings);
            T.Eq(2, repaired, "both missing jobs recreated (" + string.Join("; ", findings.ToArray()) + ")");
            T.Check(n.scheduler.Has(ProcurementService.OffersJob, bidding.id.Value) && n.scheduler.Has(OperationService.CheckpointJob, op.id.Value), "jobs present again");
            T.Eq(0, n.ctx.Procurement.EnsureJobs(null), "idempotent");
            int paid = active.ExternalCharged();
            int refunded = n.pay.refunded;
            int voided = n.ctx.Procurement.InvalidateMissing(def => def != "TestSteel", findings);
            T.Eq(2, voided, "every live contract for a removed def is voided");
            T.Check(bidding.status == ContractStatus.Voided && active.status == ContractStatus.Voided, "Voided(DefMissing)");
            T.Eq(Causes.DefMissing, active.outcome.causeKey, "cause DefMissing");
            T.Eq(paid, n.pay.refunded - refunded, "full refund");
            T.Eq("test steel", active.ItemLabel, "the label snapshot survives for history");
            Contract orphanKind = Post(n, fixer, "TestRifle", 1, Reliable(n));
            orphanKind.kindKey = "RemovedKind";
            n.ctx.Procurement.InvalidateMissing(def => true, null);
            T.Eq(Causes.KindMissing, orphanKind.outcome?.causeKey, "an unknown kind is voided too");
        }

        private static void Compaction()
        {
            TestNet n = World(0);
            NetworkActor fixer = Fixer(n);
            Contract c = Awarded(n, fixer, Reliable(n), "TestSteel", 100);
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            RunUntil(n, () => c.IsTerminal);
            T.Eq(ContractStatus.Fulfilled, c.status, "fulfilled");
            TheNetwork.Core.NetworkState state = new TheNetwork.Core.NetworkState { contracts = n.ctx.contracts, operations = n.ctx.operations, intel = n.ctx.intel, opportunities = n.ctx.opportunities };
            TheNetwork.Core.CompactionService comp = new TheNetwork.Core.CompactionService(state, n.scheduler, n.clock, n.ctx);
            bool finished;
            comp.Run(n.clock.Now + Ticks.PerDay * 30, out finished);
            T.Check(n.ctx.contracts.Get(c.id) != null, "kept for a year");
            int removed = comp.Run(n.clock.Now + Ticks.PerYear + Ticks.PerDay, out finished);
            T.Check(removed >= 3 && n.ctx.contracts.Get(c.id) == null && n.ctx.operations.Count == 0 && n.ctx.contracts.offers.Count == 0, "then archived with its offers and operation (" + removed + ")");
        }

        private static void Soak()
        {
            TheNetwork.Diagnostics.SoakHarness.Result r = TheNetwork.Diagnostics.SoakHarness.Run(100, 14, 180, 2468);
            Console.WriteLine(r.text);
            T.Check(r.posted > 300, "a steady stream of contracts (" + r.posted + ")");
            T.Eq(0, r.stuck, "no contract is left stuck");
            SoakInvariants(r, "180 days");
            T.Eq(0, r.npcIssued, "no NPC-issued contracts");
            T.Check(r.contractorsEnd >= 90, "the population stays near its target (" + r.contractorsEnd + ")");
            T.Check(r.Count(ContractStatus.Fulfilled) > 0 && r.Count(ContractStatus.Failed) + r.Count(ContractStatus.PartiallyFulfilled) > 0, "a mix of outcomes");
            T.Check(r.relations <= RelationService.GlobalCap && r.jobs < 5000 && r.journal <= EventJournal.HardCap, "bounded stores");
            T.Check(r.avgDayMs < 50.0, "cheap per simulated day (" + r.avgDayMs.ToString("0.00") + " ms)");
        }

        private static void SoakYears()
        {
            TheNetwork.Diagnostics.SoakHarness.Result r = TheNetwork.Diagnostics.SoakHarness.Run(100, 14, 3 * 360, 1357);
            Console.WriteLine(r.text);
            T.Eq(0, r.stuck, "eighteen in-game years (1,080 days): nothing stuck");
            SoakInvariants(r, "1,080 days");
            T.Check(r.posted > 1800, "thousands of contracts over the years (" + r.posted + ")");
            T.Check(r.contractsKept < r.posted * 0.6, "contracts closed over a year ago are archived (" + r.contractsKept + " of " + r.posted + " kept)");
            T.Check(r.relations <= RelationService.GlobalCap && r.knowledgeBooks <= r.contractorsEnd + r.contractorsEnded + 20, "relations and knowledge bounded (" + r.relations + ", " + r.knowledgeBooks + ")");
            T.Check(r.contractorsEnd >= 90, "population held over years (" + r.contractorsEnd + ")");
            string path = PersistenceTests.SaveState(r.state, 2);
            long bytes = new System.IO.FileInfo(path).Length;
            System.Xml.XmlDocument doc = new System.Xml.XmlDocument();
            doc.Load(path);
            List<string> sizes = new List<string>();
            foreach (System.Xml.XmlNode node in doc.DocumentElement.ChildNodes) if (node.OuterXml.Length > 20000) sizes.Add(node.Name + " " + (node.OuterXml.Length / 1024) + " KB");
            System.IO.File.Delete(path);
            Console.WriteLine("  save size of the Network node after 18 in-game years: " + (bytes / 1024) + " KB (" + string.Join(", ", sizes.ToArray()) + ")");
            T.Check(bytes < 3500L * 1024, "save growth stays under the 3.5 MB ceiling (" + (bytes / 1024) + " KB)");
            T.Check(r.spatial.routesRebuilt > 0 && r.simulatedLoads > 0, "route caches dropped by simulated loads were rebuilt (" + r.spatial.routesRebuilt + ")");
            T.Check(r.spatial.lklNear > 0 && r.spatial.lklFallback == 0, "Last Known Locations used spatial truth (" + r.spatial.lklNear + ")");
            T.Check(r.avgMoveWork < 1000, "movement work stays small per simulated day (avg " + r.avgMoveWork.ToString("0") + ")");

            // What spatial continuity adds to the save: the same state with the spatial data stripped.
            List<TheNetwork.Persist.SpatialState> kept = new List<TheNetwork.Persist.SpatialState>();
            List<TheNetwork.Persist.ContractorSimulation> sims = new List<TheNetwork.Persist.ContractorSimulation>();
            foreach (TheNetwork.Domain.Actors.NetworkActor a in r.state.actors.actors)
            {
                TheNetwork.Persist.ContractorSimulation sim = a.Get<TheNetwork.Persist.ContractorSimulation>();
                if (sim == null) continue;
                sims.Add(sim);
                kept.Add(sim.spatial);
                sim.spatial = null;
            }
            List<OperationSpatialPlan> plans = new List<OperationSpatialPlan>();
            foreach (Operation op in r.state.operations.operations)
            {
                plans.Add(op.spatial);
                op.spatial = null;
            }
            string bare = PersistenceTests.SaveState(r.state, 2);
            long without = new System.IO.FileInfo(bare).Length;
            System.IO.File.Delete(bare);
            for (int i = 0; i < sims.Count; i++) sims[i].spatial = kept[i];
            for (int i = 0; i < plans.Count; i++) r.state.operations.operations[i].spatial = plans[i];
            Console.WriteLine("  spatial continuity adds " + ((bytes - without) / 1024) + " KB to that save (" + sims.Count + " contractor states, " + plans.Count + " operation plans)");
            T.Check(bytes - without < 400L * 1024, "the spatial data stays small (" + ((bytes - without) / 1024) + " KB)");
        }

        private static void CapturedMissingRecords()
        {
            TestNet n = World(0);
            NetworkActor team = Reliable(n);
            OrganizationProfile org = team.Get<OrganizationProfile>();
            List<CharacterId> members = new List<CharacterId>(org.knownMembers);
            members.Remove(org.leader);
            T.Check(members.Count >= 2, "a team with known members");
            CasualtyReport r = new CasualtyReport();
            r.fates.Add(new CharacterFate { character = members[0], fate = Fate.Captured });
            r.fates.Add(new CharacterFate { character = members[1], fate = Fate.Missing });
            n.ctx.Contractors.ApplyCasualties(team, r, ContractId.None, OperationId.None, false);
            T.Eq(CharacterStatus.Captured, n.ctx.characters.Get(members[0]).status, "captured is a record state");
            T.Eq(CharacterStatus.Missing, n.ctx.characters.Get(members[1]).status, "missing is a record state");
            T.Check(n.ctx.characters.Get(members[0]).IsAlive && !n.ctx.characters.Get(members[0]).IsAvailable, "alive but unavailable");
            T.Eq(CustodyState.Unmaterialized, n.ctx.characters.Get(members[0]).custody, "no pawn: custody stays Unmaterialized");
            T.Eq(ActorStatus.Active, team.status, "the organization goes on");
        }

        private static void OriginGone()
        {
            TestNet n = World(0);
            n.world.factions.Add(FakeWorld.Faction(88, "Old Home", "ludeon.rimworld", 4, false));
            NetworkActor a = Reliable(n);
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            sim.origin = new FactionRef { loadId = 88, name = "Old Home", defName = "OldHome" };
            sim.originSnapshot = "Old Home";
            n.world.factions.Clear();
            n.ctx.Upkeep.RunUpkeep(a, sim);
            T.Check(sim.originLost, "the origin is marked unavailable");
            T.Eq(ActorStatus.Active, a.status, "the contractor survives its faction");
            T.Check(n.ctx.actors.Get(a.id) != null, "and is never deleted");
            T.Eq(1, n.recorder.Count(EventKeys.ContractorOriginLost), "recorded");
            T.Check(a.Get<ContractorSimulation>().origin.NameSnapshot == "Old Home", "the name snapshot remains for history");
        }

        private static void ContractorContacts()
        {
            TestNet n = World(0);
            NetworkActor scout = Reliable(n);
            scout.Get<ContractorProfile>().specialties.Add("scouting");
            NetworkActor brawler = Reliable(n);
            brawler.Get<ContractorProfile>().specialties.Clear();
            brawler.Get<ContractorProfile>().specialties.Add("demolition");
            n.ctx.Contractors.RefreshIntelSource(scout);
            T.Check(!scout.Has<IntelSourceProfile>(), "an unknown contractor is not a contact");
            ActorId player = n.ctx.actors.PlayerProxyId;
            n.ctx.Relations.Apply(player, scout.id, new RelationDelta { familiarity = 0.1f }, HistoryRecordId.None);
            n.ctx.Relations.Apply(player, brawler.id, new RelationDelta { familiarity = 0.1f }, HistoryRecordId.None);
            n.ctx.Contractors.RefreshIntelSource(scout);
            n.ctx.Contractors.RefreshIntelSource(brawler);
            T.Check(scout.Has<IntelSourceProfile>(), "a known scout will tell you what they know");
            T.Check(!brawler.Has<IntelSourceProfile>(), "not every contractor is an Intel source");
            T.Check(!scout.Has<FixerProfile>(), "a contractor contact is not a Fixer");
            T.Check(n.ctx.Actors.IntelSources().Contains(scout), "listed as a contact");
            T.Eq(Domain.Intel.SourcePolicies.ContSingle, scout.Get<IntelSourceProfile>().continuationPolicyKey, "one search at a time, no brokering");
        }

        /// <summary>Contracts in every persistent state, their offers and operations, through the real Scribe.</summary>
        private static void PersistEveryState()
        {
            TestNet n = World(10);
            NetworkActor fixer = Fixer(n, "Standard", "Basic");
            Dictionary<string, Contract> cs = new Dictionary<string, Contract>();
            Contract bidding = Post(n, fixer, "TestSteel", 150, Reliable(n));
            cs["Bidding"] = bidding;
            NetworkActor tired = Reliable(n);
            tired.Get<ContractorSimulation>().morale.descriptor = MoraleDescriptor.Exhausted;
            Contract unfilled = Post(n, fixer, "TestSteel", 150, tired);
            n.AdvanceTo(unfilled.windowCloseTick);
            cs["Unfilled"] = unfilled;
            Contract preparing = Awarded(n, fixer, Reliable(n), "TestSteel", 150, true);
            cs["Active/Preparing"] = preparing;
            Contract partial = Awarded(n, fixer, Reliable(n), "TestSteel", 200);
            ProcurementDevOverrides.forceBand = OutcomeBand.Partial;
            RunUntil(n, () => partial.status == ContractStatus.Renegotiating || partial.IsTerminal);
            cs["Renegotiating/Partial"] = partial;
            Contract troubled = Awarded(n, fixer, Reliable(n), "TestSteel", 150);
            ProcurementDevOverrides.forceBand = OutcomeBand.Failure;
            ProcurementDevOverrides.forceTroubled = SubStatus.Stranded;
            RunUntil(n, () => troubled.status == ContractStatus.Troubled || troubled.IsTerminal);
            cs["Troubled"] = troubled;
            Contract hold = Awarded(n, fixer, Reliable(n), "TestSteel", 100);
            n.delivery.homes.Clear();
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            RunUntil(n, () => hold.subStatus == SubStatus.Hold || hold.IsTerminal);
            cs["Hold"] = hold;
            n.delivery.homes.Add(1);
            Contract done = Awarded(n, fixer, Reliable(n), "TestSteel", 100);
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            RunUntil(n, () => done.IsTerminal);
            cs["Fulfilled"] = done;
            Contract voided = Awarded(n, fixer, Reliable(n), "TestSteel", 100);
            n.ctx.Procurement.Void(voided, Causes.DefMissing);
            cs["Voided"] = voided;
            Contract quoted = Post(n, fixer, "TestSteel", 150, Reliable(n));
            Bid(n, quoted);
            cs["Bidding/Quoted"] = quoted;

            TheNetwork.Core.NetworkState state = new TheNetwork.Core.NetworkState { contracts = n.ctx.contracts, operations = n.ctx.operations, consequences = n.ctx.consequences, characters = n.ctx.characters, actors = n.ctx.actors };
            string path = PersistenceTests.SaveState(state, 2);
            TheNetwork.Core.NetworkState loaded = new TheNetwork.Core.NetworkState();
            Verse.Scribe.loader.InitLoading(path);
            try
            {
                List<string> failures = new List<string>();
                loaded.ExposeStores(failures);
                T.Eq(0, failures.Count, "no store failed (" + string.Join("; ", failures.ToArray()) + ")");
            }
            finally
            {
                Verse.Scribe.loader.FinalizeLoading();
            }
            loaded.RebuildIndexes();
            System.IO.File.Delete(path);
            foreach (KeyValuePair<string, Contract> kv in cs)
            {
                Contract a = kv.Value, b = loaded.contracts.Get(kv.Value.id);
                T.Check(b != null && b.status == a.status && b.subStatus == a.subStatus && b.decisionDueTick == a.decisionDueTick && b.seed == a.seed, kv.Key + ": status " + a.status + "/" + a.subStatus + " survives");
                if (b == null) continue;
                T.Check(b.quarantinedReason == null, kv.Key + ": not quarantined");
                T.Check(SameLedger(a, b), kv.Key + ": money (every record, direction, purpose and counterpart)");
                T.Eq(a.offers.Count, b.offers.Count, kv.Key + ": offers");
                Operation oa = n.ctx.Procurement.CurrentOperation(a), ob = b.operations.Count == 0 ? null : loaded.operations.Get(b.operations[b.operations.Count - 1]);
                if (oa == null) continue;
                T.Check(ob != null && ob.phase == oa.phase && ob.status == oa.status, kv.Key + ": operation " + oa.phase + "/" + oa.status);
                if (oa.outcome != null) T.Check(ob.outcome != null && ob.outcome.band == oa.outcome.band && ob.outcome.secured == oa.outcome.secured && ob.outcome.Killed == oa.outcome.Killed && ob.outcome.securedPayload.Count == oa.outcome.securedPayload.Count, kv.Key + ": committed outcome, casualties and cargo never rerolled");
            }
            HashSet<OfferState> offerStates = new HashSet<OfferState>();
            foreach (Offer o in loaded.contracts.offers) offerStates.Add(o.state);
            T.Check(offerStates.Contains(OfferState.Accepted) && offerStates.Contains(OfferState.Proposed), "offer states persisted (" + string.Join(",", new List<OfferState>(offerStates).ConvertAll(x => x.ToString()).ToArray()) + ")");
        }

        private static void NoPhaseCreep()
        {
            Assembly net = typeof(ProcurementService).Assembly;
            foreach (AssemblyName r in net.GetReferencedAssemblies()) T.Check(r.Name.IndexOf("Harmony", StringComparison.OrdinalIgnoreCase) < 0, "no Harmony reference (" + r.Name + ")");
            foreach (Type t in net.GetTypes())
            {
                T.Check(t.Name != "PawnRef", "no PawnRef type");
                T.Check(t.Name.IndexOf("Encounter", StringComparison.Ordinal) < 0, "no encounter faction machinery (" + t.Name + ")");
                T.Check(t.Name.IndexOf("Rumor", StringComparison.Ordinal) < 0 && t.Name.IndexOf("Gossip", StringComparison.Ordinal) < 0, "no rumors or gossip (" + t.Name + ")");
                T.Check(t.Name.IndexOf("Registry", StringComparison.Ordinal) < 0 || t.Name == "ContractKindRegistry", "no registry quest (" + t.Name + ")");
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    T.Check(f.FieldType.Name != "Pawn", "no Pawn fields (" + t.Name + "." + f.Name + ")");
                }
            }
            TestNet n = World(10);
            NetworkActor player = n.ctx.actors.PlayerProxy;
            T.Check(!player.Has<ContractorProfile>() && !player.Has<ContractorSimulation>() && player.Has<IssuerProfile>(), "the player is an issuer only");
            NetworkActor fixer = Fixer(n);
            Post(n, fixer, "TestSteel", 150);
            n.Advance(60 * Ticks.PerDay);
            foreach (Contract c in n.ctx.contracts.contracts) T.Eq(player.id, c.parties.issuer, "no NPC-issued contracts");
        }
    }
}
