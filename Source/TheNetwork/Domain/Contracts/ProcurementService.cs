using System;
using System.Collections.Generic;
using RimWorld;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Operations;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;
using Verse;

namespace TheNetwork.Domain.Contracts
{
    /// <summary>What the player asks the Network for: an EXACT item and quantity (never an Intel topic).</summary>
    public sealed class ProcurementRequest
    {
        public string defName;
        public int count;
        public ActorId broker;
        public ProcurementMode mode = ProcurementMode.Open;
        public ActorId invited;
        public int premiumContribution;
        public bool wantsInsurance;
        public int preferredMapId = -1;
        public string preferredMapLabel;
    }

    /// <summary>
    /// The contract machine for procurement (STATE_MACHINES § 3–5). Every contract transition happens
    /// here or in the outcome half of this class. Commands are player commands gated by the Comms
    /// Console; bidding passes, decisions and deliveries run from scheduler jobs; every random result
    /// is drawn from the contract's own seed and committed, so reloading never rerolls it.
    /// </summary>
    public sealed partial class ProcurementService
    {
        public const string BiddingJob = "contract.bidding";
        public const string OffersJob = "contract.offers";
        public const string ExpireJob = "contract.expire";
        public const string DecisionJob = "contract.decision";
        public const string DeliveryJob = "contract.delivery";
        public const string RefundJob = "contract.refund";

        public const string NoteDeposit = "deposit";
        public const string NotePremium = "premium";
        public const string NoteInsurance = "insurance.premium";
        public const string NoteBalance = "balance";
        public const string NoteRenegotiation = "renegotiation.extra";
        public const string NoteTransferred = "deposit.transferred";

        public const int MaxPremium = 100000;

        private readonly DomainContext ctx;

        public ProcurementService(DomainContext ctx)
        {
            this.ctx = ctx;
        }

        public static ContractKindRules Rules(Contract c)
        {
            return ContractKindRegistry.Get(c?.kindKey) ?? new ContractKindRules();
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

        public static bool IsBroker(NetworkActor a)
        {
            return a != null && a.IsActive && a.Has<FixerProfile>();
        }

        public CommandResult CanPost(ProcurementRequest req)
        {
            string reason;
            if (!CommsOk(out reason)) return CommandResult.Fail(reason);
            if (ctx.diagnostics.IsDegraded("contracts")) return CommandResult.Fail("SubsystemDegraded");
            if (!ServiceToggles.ProcurementEnabled) return CommandResult.Fail("ProcurementDisabled");
            if (req == null || string.IsNullOrEmpty(req.defName)) return CommandResult.Fail("NoItem");
            if (!ctx.catalog.IsRequestable(req.defName, out reason)) return CommandResult.Fail(reason ?? "NotRequestable");
            ItemFacts f = ctx.catalog.Facts(req.defName);
            if (f == null) return CommandResult.Fail("NoItem");
            if (req.count <= 0) return CommandResult.Fail("NoQuantity");
            if (req.count > Valuation.MaxQuantity(f, ContractKindRegistry.Get(ContractKinds.Procurement))) return CommandResult.Fail("QuantityTooLarge");
            if (!IsBroker(ctx.actors.Get(req.broker))) return CommandResult.Fail("NoBroker");
            if (req.premiumContribution < 0 || req.premiumContribution > MaxPremium) return CommandResult.Fail("BadPremium");
            if (req.mode == ProcurementMode.Direct)
            {
                NetworkActor inv = ctx.actors.Get(req.invited);
                if (!ContractorService.IsNpcContractor(inv) || !inv.IsActive) return CommandResult.Fail("ContractorUnavailable");
                if (!inv.Get<ContractorProfile>().Supports(ContractKinds.Procurement)) return CommandResult.Fail("ContractorUnavailable");
            }
            if (ctx.actors.PlayerProxy == null) return CommandResult.Fail("NoIssuer");
            return CommandResult.Ok;
        }

        // ================================================================== posting

        /// <summary>PostContract: Posted, then the bidding window opens in the same tick.</summary>
        public CommandResult Post(ProcurementRequest req)
        {
            CommandResult can = CanPost(req);
            if (!can.ok) return can;
            ItemFacts f = ctx.catalog.Facts(req.defName);
            NetworkActor player = ctx.actors.PlayerProxy;
            Contract c = NewContract(player.id, req.broker, f, req.count, req.mode, req.invited, req.premiumContribution, req.wantsInsurance, req.preferredMapId, req.preferredMapLabel);
            c.lineage.root = c.id;
            ctx.contracts.Add(c);
            ctx.bus.Publish(NewEvent(EventKeys.ContractPosted, Importance.Minor, c));
            OpenWindow(c, f);
            NetLog.Trace(LogCategory.Contracts, "Posted " + c + " via " + ctx.actors.NameOf(req.broker));
            lastPosted = c.id;
            return CommandResult.Ok;
        }

        /// <summary>The contract created by the last successful <see cref="Post"/> (runtime only, for the UI and tests).</summary>
        public ContractId lastPosted;

        private Contract NewContract(ActorId issuer, ActorId broker, ItemFacts f, int count, ProcurementMode mode, ActorId invited, int premium, bool insurance, int mapId, string mapLabel)
        {
            Contract c = new Contract
            {
                id = new ContractId(ctx.ids.NextId()),
                kindKey = ContractKinds.Procurement,
                status = ContractStatus.Posted,
                createdTick = ctx.Now,
                postedTick = ctx.Now
            };
            c.seed = NetHash.Combine(NetHash.Combine(ctx.networkSeed, c.id.Value), "contract");
            c.parties.issuer = issuer;
            c.parties.beneficiary = issuer;
            c.parties.broker = broker;
            if (mode == ProcurementMode.Direct && invited.IsValid) c.parties.invited.Add(invited);
            c.request.mode = mode;
            c.request.premiumContribution = premium;
            c.request.wantsInsurance = insurance;
            c.objectives.Add(new AcquireObjective
            {
                thing = new DefRef<ThingDef> { defName = f.defName, label = f.label, packageId = f.packageId, modName = f.modName },
                count = count
            });
            if (mapId < 0) mapId = ctx.delivery?.DefaultHomeMapId(out mapLabel) ?? -1;
            c.objectives.Add(new DeliverObjective { preferredMapId = mapId, preferredMapLabel = mapLabel });
            return c;
        }

        // ================================================================== bidding

        private void OpenWindow(Contract c, ItemFacts f)
        {
            ContractKindRules rules = Rules(c);
            c.status = ContractStatus.Bidding;
            c.biddingRound++;
            c.windowOpenTick = ctx.Now;
            float days = c.Mode == ProcurementMode.Direct ? rules.biddingWindowDaysDirect : rules.biddingWindowDaysOpen;
            c.windowCloseTick = ctx.Now + Math.Max(Ticks.PerHour * 2, (int)(days * Ticks.PerDay));
            c.evaluated.Clear();
            c.candidates = SelectCandidates(c, f);
            ScheduleBiddingPass(c, 1);
            StateVersion.Bump();
        }

        private int PassDue(Contract c, int pass)
        {
            int window = Math.Max(1, c.windowCloseTick - c.windowOpenTick);
            if (pass <= 1) return c.windowOpenTick + Math.Max(Ticks.PerHour, window / 10);
            if (pass == 2) return c.windowOpenTick + window / 2;
            return c.windowCloseTick;
        }

        private void ScheduleBiddingPass(Contract c, int pass)
        {
            ctx.scheduler.Schedule(BiddingJob, Math.Max(ctx.Now + 1, PassDue(c, pass)), c.id.Value, pass);
        }

        /// <summary>
        /// Eligible contractors (SIMULATION § 5.2). Direct: the invited group only. Open: active
        /// contractors reachable through the Fixer, ranked by relevance (knowledge of the item and the
        /// market, familiarity, specialties), capped by the Fixer's contractor reach. A thin pool, or a
        /// seeded chance, may introduce a new persistent contractor as a bidder.
        /// </summary>
        public List<ActorId> SelectCandidates(Contract c, ItemFacts f)
        {
            List<ActorId> result = new List<ActorId>();
            if (c.Mode == ProcurementMode.Direct)
            {
                for (int i = 0; i < c.parties.invited.Count; i++)
                {
                    NetworkActor a = ctx.actors.Get(c.parties.invited[i]);
                    if (ContractorService.IsNpcContractor(a) && a.IsActive) result.Add(a.id);
                }
                return result;
            }
            NetworkActor broker = ctx.actors.Get(c.parties.broker);
            FixerProfile fp = broker?.Get<FixerProfile>();
            int cap = FixerPolicies.CandidateCap(fp?.contractorReach ?? ReachBand.Local);
            List<KeyValuePair<float, NetworkActor>> scored = new List<KeyValuePair<float, NetworkActor>>();
            int available = 0;
            List<NetworkActor> all = ctx.actors.actors;
            string itemTopic = Knowledge.Topics.Thing(f.defName);
            for (int i = 0; i < all.Count; i++)
            {
                NetworkActor a = all[i];
                if (!ContractorService.IsNpcContractor(a) || !a.IsActive) continue;
                ContractorProfile p = a.Get<ContractorProfile>();
                if (p.suspended || !p.Supports(c.kindKey)) continue;
                float score = 2f * ctx.Knowledge.Proficiency(a.id, itemTopic)
                    + ctx.Knowledge.Proficiency(a.id, Knowledge.Topics.Market)
                    + ctx.Relations.Get(a.id, c.parties.issuer).familiarity
                    + (broker != null ? 0.5f * ctx.Relations.Get(broker.id, a.id).familiarity : 0f)
                    + 0.3f * Valuation.Specialization(p, f)
                    + 0.2f * ((NetHash.Combine(c.seed, a.id.Value) & 0x3ff) / 1024f);
                scored.Add(new KeyValuePair<float, NetworkActor>(score, a));
            }
            scored.Sort((x, y) =>
            {
                int cmp = y.Key.CompareTo(x.Key);
                return cmp != 0 ? cmp : x.Value.id.Value.CompareTo(y.Value.id.Value);
            });
            for (int i = 0; i < scored.Count && result.Count < cap; i++)
            {
                result.Add(scored[i].Value.id);
                if (ctx.Contractors.AvailabilityOf(scored[i].Value) == Availability.Available) available++;
            }
            NetworkActor newcomer = ctx.Upkeep.TryNewcomerBidder(c.seed, c.biddingRound, available < 2);
            if (newcomer != null)
            {
                if (result.Count >= cap && result.Count > 0) result.RemoveAt(result.Count - 1);
                result.Insert(0, newcomer.id);
            }
            return result;
        }

        /// <summary>contract.bidding: one evaluation pass. Offers trickle in; the last pass closes the window.</summary>
        public void RunBiddingPass(ScheduledJob job)
        {
            Contract c = ctx.contracts.Get(new ContractId(job.target));
            if (c == null || c.quarantinedReason != null || c.status != ContractStatus.Bidding) return;
            int pass = Math.Max(1, Math.Min(3, job.arg));
            ItemFacts f = ctx.catalog.Facts(c.Acquire.DefName);
            if (f == null)
            {
                Void(c, Causes.DefMissing);
                return;
            }
            if (!IsBroker(ctx.actors.Get(c.parties.broker)))
            {
                Void(c, Causes.BrokerGone);
                return;
            }
            EvaluateUpTo(c, f, (int)Math.Ceiling(c.candidates.Count * pass / 3f));
            if (pass < 3)
            {
                ScheduleBiddingPass(c, pass + 1);
                return;
            }
            CloseWindow(c);
        }

        private void EvaluateUpTo(Contract c, ItemFacts f, int upTo)
        {
            int maxOffers = Rules(c).maxOffers;
            for (int i = 0; i < c.candidates.Count && i < upTo; i++)
            {
                if (OpenOffers(c).Count >= maxOffers) return;
                ActorId id = c.candidates[i];
                if (c.evaluated.Contains(id)) continue;
                c.evaluated.Add(id);
                NetworkActor a = ctx.actors.Get(id);
                if (a == null || !a.IsActive) continue;
                WillingnessDecision d = Willingness.Evaluate(ctx, a, c, f);
                if (d.accept) MakeOffer(c, a, f, d);
                else Refuse(c, a, d);
            }
        }

        private void Refuse(Contract c, NetworkActor a, WillingnessDecision d)
        {
            if (c.refusals.Count >= Contract.MaxRefusals) c.refusals.RemoveAt(0);
            c.refusals.Add(new Refusal { actor = a.id, actorName = a.name.Display, reasonKeys = new List<string>(d.reasonKeys), tick = ctx.Now, round = c.biddingRound });
            ContractEvent e = NewEvent(EventKeys.ContractRefused, Importance.Minor, c);
            e.contractor = a.id;
            e.contractorName = a.name.Display;
            e.reasonKeys.AddRange(d.reasonKeys);
            ctx.bus.Publish(e);
            StateVersion.Bump();
        }

        /// <summary>
        /// The contractor bids its own contribution; the Fixer wraps it into the client-facing quote.
        /// Everything is frozen on the Offer now and never recomputed.
        /// </summary>
        private Offer MakeOffer(Contract c, NetworkActor a, ItemFacts f, WillingnessDecision d)
        {
            NetworkActor broker = ctx.actors.Get(c.parties.broker);
            FixerProfile fp = broker?.Get<FixerProfile>();
            Offer o = new Offer
            {
                id = new OfferId(ctx.ids.NextId()),
                contract = c.id,
                bidder = a.id,
                bidderName = a.name.Display,
                createdTick = ctx.Now,
                stateTick = ctx.Now,
                round = c.biddingRound,
                riskTolerance = d.appetite
            };
            int seed = NetHash.Combine(NetHash.Combine(c.seed, a.id.Value), "offer." + c.biddingRound);
            NetRng rng = new NetRng(seed, "quote");
            int eta;
            List<QuoteComponent> parts = ContractorPricing.Bid(a, f, c.Quantity, d.danger, d.relationKey, out eta, rng);
            o.contractorQuote = ContractorPricing.Sum(parts);
            o.etaTicks = eta;
            o.conditions.AddRange(d.conditions);
            o.basis = new OfferBasis
            {
                relationKey = d.relationKey,
                moraleKey = a.Get<ContractorSimulation>()?.morale.descriptor.ToString(),
                doctrineKey = ContractorService.DoctrineLabel(a),
                experienceKey = ContractorService.Experience(a).ToString(),
                preparedness = d.preparedness,
                danger = d.danger,
                newcomer = a.provenance.source == ProvenanceSource.WorldGenerated && a.foundedTick >= c.windowOpenTick
            };
            o.quote = Wrap(c, a, broker, fp, f, parts, eta, d, rng);
            o.expiresTick = o.quote.validUntilTick;
            ctx.contracts.Add(o);
            c.offers.Add(o.id);

            ContractEvent e = NewEvent(EventKeys.ContractOfferReceived, Importance.Minor, c);
            e.offer = o.id;
            e.contractor = a.id;
            e.contractorName = a.name.Display;
            e.silver = o.contractorQuote;
            ctx.bus.Publish(e);
            ContractEvent q = NewEvent(EventKeys.ContractQuoted, Importance.Minor, c);
            q.offer = o.id;
            q.contractor = a.id;
            q.contractorName = a.name.Display;
            q.silver = o.quote.finalPrice;
            ctx.bus.Publish(q);
            ScheduleOfferExpiry(c);
            StateVersion.Bump();
            return o;
        }

        private ProcurementQuote Wrap(Contract c, NetworkActor a, NetworkActor broker, FixerProfile fp, ItemFacts f, List<QuoteComponent> contractorParts, int eta, WillingnessDecision d, NetRng rng)
        {
            ProcurementQuote q = new ProcurementQuote
            {
                broker = broker?.id ?? ActorId.None,
                brokerName = broker?.name.Display,
                createdTick = ctx.Now,
                etaTicks = eta,
                goodsBasis = Valuation.GoodsBasis(f, c.Quantity),
                marketFloor = Valuation.MarketFloor(f, c.Quantity)
            };
            q.components.AddRange(contractorParts);
            int bid = ContractorPricing.Sum(contractorParts);
            ActorId by = q.broker;
            string brokerage = FixerPolicies.Brokerage(fp);
            ContractorPricing.Add(q.components, QuoteComponentKind.FixerFee, bid * FixerPolicies.FeeShare(fp?.feeBand ?? Band.Medium), by, "Brokerage");
            ContractorPricing.Add(q.components, QuoteComponentKind.Coordination, bid * FixerPolicies.CoordinationShare(brokerage), by, "Coordination." + brokerage);
            ContractorPricing.Add(q.components, QuoteComponentKind.MarketAccess, q.goodsBasis * FixerPolicies.MarketAccessShare(fp?.marketAccess ?? ReachBand.Local), by, "MarketAccess");
            ContractorPricing.Add(q.components, QuoteComponentKind.Contingency, bid * FixerPolicies.ContingencyShare(d.danger), by, "Contingency");
            int total = ContractorPricing.Sum(q.components);
            // Never cheaper than simply buying the goods (master § 20, § 77).
            if (total < q.marketFloor) ContractorPricing.Add(q.components, QuoteComponentKind.MarketFloor, q.marketFloor - total, by, "MarketFloor");
            q.finalPrice = Math.Max(q.marketFloor, ContractorPricing.Sum(q.components));
            float trust = ctx.Relations.Get(a.id, c.parties.issuer).trust;
            q.depositShare = FixerPolicies.DepositShare(brokerage, d.danger, trust) + (d.conditions.Contains("CashUpFront") ? 0.1f : 0f);
            q.depositShare = ContractorService.Clamp(q.depositShare, 0.3f, 0.75f);
            q.depositPolicyKey = fp?.depositPolicyKey ?? "fixer.deposit.Standard";
            q.deposit = Math.Max(1, (int)Math.Round(q.finalPrice * q.depositShare));
            q.balance = Math.Max(0, q.finalPrice - q.deposit);
            q.insuranceOffer = FixerPolicies.InsuranceOffer(fp, q.deposit, d.danger);
            q.replacementPolicyKey = (fp?.replacementPolicyKey ?? "fixer.replacement.Standard") + "/" + brokerage;
            q.refundPolicyKey = FixerPolicies.RefundPolicyKey(brokerage);
            q.validUntilTick = ctx.Now + FixerPolicies.ValidityTicks(rng);
            return q;
        }

        private void CloseWindow(Contract c)
        {
            if (OpenOffers(c).Count > 0)
            {
                StateVersion.Bump();
                return; // stays in Bidding until the client accepts, or every quote expires
            }
            BecomeUnfilled(c);
        }

        private void BecomeUnfilled(Contract c)
        {
            c.status = ContractStatus.Unfilled;
            c.deadlineTick = ctx.Now + (int)(Rules(c).unfilledExpiryDays * Ticks.PerDay);
            ctx.scheduler.Cancel(BiddingJob, c.id.Value);
            ctx.scheduler.Cancel(OffersJob, c.id.Value);
            ctx.scheduler.Schedule(ExpireJob, c.deadlineTick, c.id.Value);
            ContractEvent e = NewEvent(EventKeys.ContractUnfilled, Importance.Minor, c);
            e.causeKey = Causes.NoOffers;
            for (int i = 0; i < c.refusals.Count; i++)
            {
                if (c.refusals[i].round != c.biddingRound) continue;
                for (int k = 0; k < c.refusals[i].reasonKeys.Count; k++) if (!e.reasonKeys.Contains(c.refusals[i].reasonKeys[k])) e.reasonKeys.Add(c.refusals[i].reasonKeys[k]);
            }
            ctx.bus.Publish(e);
            StateVersion.Bump();
        }

        public List<Offer> OpenOffers(Contract c)
        {
            List<Offer> list = new List<Offer>();
            for (int i = 0; i < c.offers.Count; i++)
            {
                Offer o = ctx.contracts.Get(c.offers[i]);
                if (o != null && o.IsOpen) list.Add(o);
            }
            return list;
        }

        public List<Offer> OffersOf(Contract c)
        {
            List<Offer> list = new List<Offer>();
            for (int i = 0; i < c.offers.Count; i++)
            {
                Offer o = ctx.contracts.Get(c.offers[i]);
                if (o != null) list.Add(o);
            }
            return list;
        }

        private void ScheduleOfferExpiry(Contract c)
        {
            int next = int.MaxValue;
            List<Offer> open = OpenOffers(c);
            for (int i = 0; i < open.Count; i++) next = Math.Min(next, open[i].expiresTick);
            if (next < int.MaxValue) ctx.scheduler.Schedule(OffersJob, Math.Max(ctx.Now + 1, next), c.id.Value);
        }

        /// <summary>contract.offers: quotes past their validity expire (the client may repost for new quotes).</summary>
        public void ExpireOffers(ScheduledJob job)
        {
            Contract c = ctx.contracts.Get(new ContractId(job.target));
            if (c == null || c.quarantinedReason != null || c.status != ContractStatus.Bidding) return;
            List<Offer> open = OpenOffers(c);
            for (int i = 0; i < open.Count; i++)
            {
                if (open[i].expiresTick <= ctx.Now) SetOffer(open[i], OfferState.Expired);
            }
            if (OpenOffers(c).Count > 0)
            {
                ScheduleOfferExpiry(c);
                return;
            }
            if (ctx.Now >= c.windowCloseTick) BecomeUnfilled(c);
        }

        private void SetOffer(Offer o, OfferState s)
        {
            o.state = s;
            o.stateTick = ctx.Now;
        }

        /// <summary>contract.expire: an Unfilled contract nobody reposted expires.</summary>
        public void ExpireJobRun(ScheduledJob job)
        {
            Contract c = ctx.contracts.Get(new ContractId(job.target));
            if (c == null || c.quarantinedReason != null || c.status != ContractStatus.Unfilled) return;
            if (c.deadlineTick > ctx.Now)
            {
                ctx.scheduler.Schedule(ExpireJob, c.deadlineTick, c.id.Value);
                return;
            }
            Close(c, ContractStatus.Expired, "NeverAwarded", EventKeys.ContractExpired, Importance.Minor);
        }

        // ================================================================== client commands

        public CommandResult CanRepost(ContractId id)
        {
            Contract c = ctx.contracts.Get(id);
            if (c == null) return CommandResult.Fail("ContractMissing");
            if (c.status != ContractStatus.Unfilled) return CommandResult.Fail("NotUnfilled");
            string reason;
            if (!CommsOk(out reason)) return CommandResult.Fail(reason);
            if (ctx.catalog.Facts(c.Acquire.DefName) == null) return CommandResult.Fail("ItemMissing");
            if (!IsBroker(ctx.actors.Get(c.parties.broker))) return CommandResult.Fail("NoBroker");
            return CommandResult.Ok;
        }

        /// <summary>Unfilled → Bidding (a new window, a new round). Optionally opened up to every eligible contractor.</summary>
        public CommandResult Repost(ContractId id, bool openToAll)
        {
            CommandResult can = CanRepost(id);
            if (!can.ok) return can;
            Contract c = ctx.contracts.Get(id);
            ctx.scheduler.Cancel(ExpireJob, c.id.Value);
            c.deadlineTick = -1;
            if (openToAll)
            {
                c.request.mode = ProcurementMode.Open;
                c.parties.invited.Clear();
            }
            OpenWindow(c, ctx.catalog.Facts(c.Acquire.DefName));
            return CommandResult.Ok;
        }

        /// <summary>The total the client pays at award for this offer (deposit + premium + optional insurance premium).</summary>
        public static int DueAtAward(Contract c, Offer o, bool insure)
        {
            if (o?.quote == null) return 0;
            int due = o.quote.deposit + c.request.premiumContribution;
            if (insure && o.quote.insuranceOffer != null) due += o.quote.insuranceOffer.premium;
            return due;
        }

        public CommandResult CanAccept(OfferId offerId, bool insure)
        {
            Offer o = ctx.contracts.Get(offerId);
            if (o == null || o.quarantinedReason != null) return CommandResult.Fail("OfferMissing");
            Contract c = ctx.contracts.Get(o.contract);
            if (c == null || c.quarantinedReason != null) return CommandResult.Fail("ContractMissing");
            if (c.status != ContractStatus.Bidding) return CommandResult.Fail("NotBidding");
            if (!o.IsOpen) return CommandResult.Fail("OfferClosed");
            if (o.expiresTick <= ctx.Now) return CommandResult.Fail("QuoteExpired");
            string reason;
            if (!CommsOk(out reason)) return CommandResult.Fail(reason);
            if (insure && o.quote.insuranceOffer == null) return CommandResult.Fail("NoInsuranceOffered");
            int due = DueAtAward(c, o, insure);
            if (due > 0 && !ctx.payment.CanCharge(due, out reason)) return CommandResult.Fail("CannotAffordDeposit");
            return CommandResult.Ok;
        }

        /// <summary>
        /// The client accepts a quote: the deposit (plus premium and optional insurance) is charged, or the
        /// award is refused with CannotAffordDeposit. Terms are copied from the frozen quote; the other
        /// offers are superseded; the operation starts.
        /// </summary>
        public CommandResult Accept(OfferId offerId, bool insure)
        {
            CommandResult can = CanAccept(offerId, insure);
            if (!can.ok) return can;
            Offer o = ctx.contracts.Get(offerId);
            Contract c = ctx.contracts.Get(o.contract);
            NetworkActor a = ctx.actors.Get(o.bidder);
            ItemFacts f = ctx.catalog.Facts(c.Acquire.DefName);
            if (f == null)
            {
                Void(c, Causes.DefMissing);
                return CommandResult.Fail("ItemMissing");
            }
            // The bidder's state may have changed since it bid (STATE_MACHINES § 5).
            Availability av = ctx.Contractors.AvailabilityOf(a);
            if (a == null || (av != Availability.Available && av != Availability.Committed))
            {
                SetOffer(o, OfferState.Withdrawn);
                ContractEvent w = NewEvent(EventKeys.ContractRefused, Importance.Minor, c);
                w.offer = o.id;
                w.contractor = o.bidder;
                w.contractorName = o.bidderName;
                w.reasonKeys.Add("Withdrawn");
                ctx.bus.Publish(w);
                if (OpenOffers(c).Count == 0 && ctx.Now >= c.windowCloseTick) BecomeUnfilled(c);
                return CommandResult.Fail("BidderWithdrew");
            }
            int due = DueAtAward(c, o, insure);
            string reason;
            if (due > 0 && !ctx.payment.TryCharge(due, out reason)) return CommandResult.Fail("CannotAffordDeposit");

            c.ledger.Add(Money(o.quote.deposit, MoneyDirection.PlayerPaid, NoteDeposit));
            if (c.request.premiumContribution > 0) c.ledger.Add(Money(c.request.premiumContribution, MoneyDirection.PlayerPaid, NotePremium));
            Insurance bought = insure ? o.quote.insuranceOffer?.Copy() : null;
            if (bought != null) c.ledger.Add(Money(bought.premium, MoneyDirection.PlayerPaid, NoteInsurance));
            Award(c, o, a, f, bought);
            return CommandResult.Ok;
        }

        private void Award(Contract c, Offer o, NetworkActor a, ItemFacts f, Insurance bought, bool transferred = false)
        {
            ProcurementQuote q = o.quote;
            c.terms = new Terms
            {
                price = q.finalPrice,
                deposit = q.deposit,
                balance = q.balance,
                etaTicks = o.etaTicks,
                premiumContribution = c.request.premiumContribution,
                insurance = bought,
                depositPolicyKey = q.depositPolicyKey,
                refundPolicyKey = q.refundPolicyKey,
                replacementPolicyKey = q.replacementPolicyKey
            };
            c.terms.conditions.AddRange(o.conditions);
            c.terms.schedule.Add(new PaymentStep { key = PaymentStep.OnAward, amount = q.deposit });
            c.terms.schedule.Add(new PaymentStep { key = PaymentStep.OnDelivery, amount = q.balance });
            c.parties.contractor = a.id;
            c.acceptedOffer = o.id;
            SetOffer(o, OfferState.Accepted);
            List<Offer> all = OffersOf(c);
            for (int i = 0; i < all.Count; i++) if (all[i].IsOpen) SetOffer(all[i], OfferState.Superseded);
            ctx.scheduler.Cancel(BiddingJob, c.id.Value);
            ctx.scheduler.Cancel(OffersJob, c.id.Value);
            c.status = ContractStatus.Awarded;
            c.awardedTick = ctx.Now;
            PayContractor(c, a, q.deposit + c.request.premiumContribution);

            ContractEvent e = NewEvent(EventKeys.ContractAwarded, Importance.Minor, c);
            e.offer = o.id;
            e.silver = q.finalPrice;
            e.insuranceSilver = bought?.premium ?? 0;
            ctx.bus.Publish(e);
            if (!transferred)
            {
                ContractEvent paid = NewEvent(EventKeys.PaymentReceived, Importance.Minor, c);
                paid.silver = q.deposit;
                paid.causeKey = "Deposit";
                ctx.bus.Publish(paid);
            }

            // Awarded → Active once the operation has started (deposit settled).
            ctx.Operations.Start(c, a, f);
            c.status = ContractStatus.Active;
            StateVersion.Bump();
        }

        /// <summary>The contractor's share of silver the client paid lands in its funds (the Fixer keeps its part).</summary>
        private void PayContractor(Contract c, NetworkActor a, int silver)
        {
            ContractorSimulation sim = a?.Get<ContractorSimulation>();
            Offer o = ctx.contracts.Get(c.acceptedOffer);
            if (sim == null || silver <= 0 || o?.quote == null || o.quote.finalPrice <= 0) return;
            float share = ContractorService.Clamp(o.quote.ContractorShare() / (float)o.quote.finalPrice, 0f, 1f);
            sim.funds += (int)(silver * share);
        }

        public CommandResult CanDecline(OfferId offerId)
        {
            Offer o = ctx.contracts.Get(offerId);
            if (o == null) return CommandResult.Fail("OfferMissing");
            if (!o.IsOpen) return CommandResult.Fail("OfferClosed");
            return CommandResult.Ok;
        }

        public CommandResult Decline(OfferId offerId)
        {
            CommandResult can = CanDecline(offerId);
            if (!can.ok) return can;
            Offer o = ctx.contracts.Get(offerId);
            SetOffer(o, OfferState.Declined);
            Contract c = ctx.contracts.Get(o.contract);
            if (c != null && c.status == ContractStatus.Bidding && OpenOffers(c).Count == 0 && ctx.Now >= c.windowCloseTick) BecomeUnfilled(c);
            StateVersion.Bump();
            return CommandResult.Ok;
        }

        public CommandResult CanCancel(ContractId id)
        {
            Contract c = ctx.contracts.Get(id);
            if (c == null) return CommandResult.Fail("ContractMissing");
            if (c.IsTerminal) return CommandResult.Fail("AlreadyClosed");
            string reason;
            if (!CommsOk(out reason)) return CommandResult.Fail(reason);
            if (c.IsSeeking) return CommandResult.Ok;
            Operation op = CurrentOperation(c);
            if (c.status == ContractStatus.Awarded || (c.status == ContractStatus.Active && op != null && op.phase <= OpPhase.Transit && op.outcome == null)) return CommandResult.Ok;
            if (c.status == ContractStatus.Renegotiating && c.subStatus == SubStatus.WorseThanExpected) return CommandResult.Ok;
            return CommandResult.Fail("TooLateToCancel");
        }

        /// <summary>
        /// Before award: free. After award and before the work is engaged: the Fixer's refund policy
        /// returns part of the deposit and premium; the operation is aborted and the contractor's people
        /// return unharmed. During a "worse than expected" renegotiation: cancelled, nothing refunded.
        /// </summary>
        public CommandResult Cancel(ContractId id)
        {
            CommandResult can = CanCancel(id);
            if (!can.ok) return can;
            Contract c = ctx.contracts.Get(id);
            if (c.IsSeeking)
            {
                DeclineAll(c);
                Close(c, ContractStatus.Cancelled, Causes.IssuerCancelled, EventKeys.ContractCancelled, Importance.Minor);
                return CommandResult.Ok;
            }
            Operation op = CurrentOperation(c);
            bool renegotiating = c.status == ContractStatus.Renegotiating;
            int refund = 0;
            if (!renegotiating)
            {
                bool preparing = op == null || op.phase == OpPhase.Preparing;
                float share = FixerPolicies.CancelRefundShare(c.terms?.refundPolicyKey, preparing);
                refund = (int)Math.Round((c.PaidFor(NoteDeposit) + c.PaidFor(NotePremium)) * share);
            }
            if (op != null) ctx.Operations.Abort(op, "IssuerCancelled");
            if (refund > 0) Refund(c, refund, "refund.cancel");
            Close(c, ContractStatus.Cancelled, renegotiating ? Causes.IssuerCancelledDuringRenegotiation : Causes.IssuerCancelledAfterAward, EventKeys.ContractCancelled, Importance.Minor, refund);
            return CommandResult.Ok;
        }

        private void DeclineAll(Contract c)
        {
            List<Offer> open = OpenOffers(c);
            for (int i = 0; i < open.Count; i++) SetOffer(open[i], OfferState.Declined);
        }

        // ================================================================== technical invalidation

        /// <summary>
        /// Any non-terminal state → Voided (the item def or the kind is gone, the save is being prepared for
        /// removal, the broker vanished before award). Not an in-world failure: EVERY silver paid is
        /// refunded (pending and retried daily if it cannot be delivered now). A running operation is
        /// aborted and its people return unharmed.
        /// </summary>
        public void Void(Contract c, string causeKey)
        {
            if (c == null || c.IsTerminal) return;
            Operation op = CurrentOperation(c);
            if (op != null) ctx.Operations.Abort(op, causeKey);
            DeclineAll(c);
            int refund = c.Paid() - c.Refunded();
            if (refund > 0) Refund(c, refund, "refund.void");
            Close(c, ContractStatus.Voided, causeKey, EventKeys.ContractVoided, Importance.Minor, refund);
            NetLog.Info(LogCategory.Contracts, "Contract " + c.id + " (" + c.Quantity + "x " + c.ItemLabel + ") voided: " + causeKey + (refund > 0 ? ", refunded " + refund + " silver" : "") + ".");
        }

        // ================================================================== money

        private MoneyRecord Money(int silver, MoneyDirection dir, string note)
        {
            return new MoneyRecord { tick = ctx.Now, silver = silver, direction = dir, noteKey = note };
        }

        private void Refund(Contract c, int silver, string noteKey)
        {
            if (silver <= 0) return;
            string reason;
            bool delivered = ctx.payment.TryRefund(silver, out reason);
            MoneyRecord m = Money(silver, MoneyDirection.PlayerRefunded, noteKey);
            m.pending = !delivered;
            c.ledger.Add(m);
            if (!delivered)
            {
                ctx.scheduler.Schedule(RefundJob, ctx.Now + Ticks.PerDay, c.id.Value);
                NetLog.Info(LogCategory.Payment, "Refund of " + silver + " silver for " + c.id + " is pending (" + reason + "); retrying daily.");
            }
        }

        /// <summary>contract.refund: retries undelivered refunds.</summary>
        public void RetryRefunds(ScheduledJob job)
        {
            Contract c = ctx.contracts.Get(new ContractId(job.target));
            if (c == null) return;
            bool stillPending = false;
            for (int i = 0; i < c.ledger.Count; i++)
            {
                MoneyRecord m = c.ledger[i];
                if (!m.pending) continue;
                string reason;
                if (ctx.payment.TryRefund(m.silver, out reason)) m.pending = false;
                else stillPending = true;
            }
            if (stillPending) ctx.scheduler.Schedule(RefundJob, ctx.Now + Ticks.PerDay, c.id.Value);
            StateVersion.Bump();
        }

        // ================================================================== closing and events

        /// <summary>Any state → a terminal state. Terminal states are never reopened.</summary>
        private void Close(Contract c, ContractStatus result, string causeKey, string eventKey, Importance importance, int silver = 0, int insurance = 0)
        {
            if (c.IsTerminal) return;
            c.status = result;
            c.closedTick = ctx.Now;
            c.subStatus = null;
            c.decisionDueTick = -1;
            c.causeKey = causeKey;
            c.outcome = new ContractOutcome
            {
                result = result,
                causeKey = causeKey,
                requested = c.Quantity,
                delivered = c.Acquire?.delivered ?? 0,
                bandKey = CurrentOperation(c)?.outcome?.band.ToString(),
                tick = ctx.Now
            };
            ctx.scheduler.Cancel(BiddingJob, c.id.Value);
            ctx.scheduler.Cancel(OffersJob, c.id.Value);
            ctx.scheduler.Cancel(ExpireJob, c.id.Value);
            ctx.scheduler.Cancel(DecisionJob, c.id.Value);
            ctx.scheduler.Cancel(DeliveryJob, c.id.Value);
            Operation op = CurrentOperation(c);
            if (op != null && !op.IsFinished && op.outcome != null && op.status != OpStatus.Troubled) ctx.Operations.Finish(op);
            ContractEvent e = NewEvent(eventKey, importance, c);
            PruneClosed(c);
            e.causeKey = causeKey;
            e.silver = silver;
            e.insuranceSilver = insurance;
            e.bandKey = c.outcome.bandKey;
            e.delivered = c.outcome.delivered;
            ctx.bus.Publish(e);
            StateVersion.Bump();
        }

        /// <summary>
        /// Retention at close (EVENTS_AND_HISTORY § 11): the losing bids are dropped (they were never
        /// recomputed; they are simply no longer needed) and refusals are trimmed to the last three. The
        /// accepted offer and its frozen quote stay with the contract until compaction.
        /// </summary>
        private void PruneClosed(Contract c)
        {
            for (int i = c.offers.Count - 1; i >= 0; i--)
            {
                if (c.offers[i] == c.acceptedOffer) continue;
                Offer o = ctx.contracts.Get(c.offers[i]);
                if (o != null) ctx.contracts.Remove(o);
                c.offers.RemoveAt(i);
            }
            if (c.refusals.Count > 3) c.refusals.RemoveRange(0, c.refusals.Count - 3);
        }

        public Operation CurrentOperation(Contract c)
        {
            if (c == null || c.operations.Count == 0) return null;
            return ctx.operations.Get(c.operations[c.operations.Count - 1]);
        }

        private ContractEvent NewEvent(string key, Importance importance, Contract c)
        {
            ContractEvent e = EventFactory.Make<ContractEvent>(key, importance, c.id.Ref, c.parties.issuer.Ref, c.parties.contractor.Ref, c.parties.broker.Ref);
            e.contract = c.id;
            e.issuer = c.parties.issuer;
            e.contractor = c.parties.contractor;
            e.broker = c.parties.broker;
            e.kindKey = c.kindKey;
            e.itemDefName = c.Acquire?.DefName;
            e.itemLabel = c.ItemLabel;
            e.quantity = c.Quantity;
            e.contractorName = c.parties.contractor.IsValid ? ctx.actors.NameOf(c.parties.contractor) : null;
            e.brokerName = c.parties.broker.IsValid ? ctx.actors.NameOf(c.parties.broker) : null;
            e.parent = c.lineage.parent;
            Operation op = CurrentOperation(c);
            if (op != null) e.operation = op.id;
            return e;
        }

        // ================================================================== queries

        /// <summary>Does the issuer owe this contractor a balance right now (an unpaid AwaitingPayment)?</summary>
        public bool OwesContractor(ActorId issuer, ActorId contractor)
        {
            List<Contract> all = ctx.contracts.contracts;
            for (int i = 0; i < all.Count; i++)
            {
                Contract c = all[i];
                if (c.subStatus == SubStatus.AwaitingPayment && !c.IsTerminal && c.parties.issuer == issuer && c.parties.contractor == contractor) return true;
            }
            return false;
        }

        public List<Contract> Live()
        {
            List<Contract> list = new List<Contract>();
            for (int i = 0; i < ctx.contracts.contracts.Count; i++) if (!ctx.contracts.contracts[i].IsTerminal) list.Add(ctx.contracts.contracts[i]);
            return list;
        }

        public Contract Get(ContractId id) => ctx.contracts.Get(id);
    }
}
