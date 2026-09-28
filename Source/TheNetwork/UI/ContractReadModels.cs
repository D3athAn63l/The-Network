using System;
using System.Collections.Generic;
using RimWorld;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Operations;
using TheNetwork.Domain.Ports;
using TheNetwork.History;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using Verse;

namespace TheNetwork.UI
{
    public sealed class FixerChoiceView
    {
        public ActorId id;
        public string name;
        public string line;
        public bool offersInsurance;
    }

    public sealed class ContractorChoiceView
    {
        public ActorId id;
        public string name;
        public string line;
        public bool ready;
    }

    public sealed class OfferView
    {
        public OfferId id;
        public string bidder;
        public string bidderLine;
        public int price;
        public int deposit;
        public int balance;
        public string eta;
        public string insurance;
        public int dueWithout;
        public int dueWith;
        public bool insurable;
        public string conditions;
        public string stateLabel;
        public bool open;
        public bool newcomer;
        public string validity;
    }

    public sealed class ContractRowView
    {
        public ContractId id;
        public ContractStatus status;
        public string subStatus;
        public string title;
        public string statusLine;
        public string contractor;
        public string broker;
        public string eta;
        public string money;
        public string insurance;
        public string result;
        public string lineage;
        public string mode;
        public List<OfferView> offers = new List<OfferView>();
        public List<string> refusals = new List<string>();

        /// <summary>The live Field Log of the player's running job (dated report lines, oldest first); empty otherwise.</summary>
        public List<string> fieldLog = new List<string>();
        public int askExtra;
        public int askReduced;
        public int balanceDue;
        public int secured;
        public int quantity;
        public bool seeking;
        public bool terminal;
        public bool needsDecision;
        public int closedTick;
    }

    public sealed class ContractorCardView
    {
        public ActorId id;
        public string name;
        public string kind;
        public string experience;
        public string fame;
        public string doctrine;
        public string readiness;
        public string morale;
        public string people;
        public string leader;
        public string specialties;
        public string record;
        public string relation;
        public string origin;
        public bool intelContact;
        public bool dealtWith;
        public bool ended;
        public string endReason;
        public int sortKey;
    }

    /// <summary>
    /// Read models for the Procurement, Contracts and Contractors tabs (ARCHITECTURE § 9). Immutable,
    /// pre-formatted, descriptors only; rebuilt when StateVersion changes. Nothing here draws
    /// randomness or reveals a committed outcome before the contractor has reported it.
    /// </summary>
    public sealed class ContractReadModels
    {
        private readonly NetworkRuntime runtime;
        private int contractsVersion = -1, contractsSlot = -1;
        private List<ContractRowView> contracts;
        private int cardsVersion = -1;
        private List<ContractorCardView> cards;
        private int fixersVersion = -1;
        private List<FixerChoiceView> fixers;

        public ContractReadModels(NetworkRuntime runtime)
        {
            this.runtime = runtime;
        }

        private DomainContext Ctx => runtime.Ctx;

        private static string T(string key) => key.Translate().Resolve();

        // ------------------------------------------------------------------ choices

        public List<FixerChoiceView> Fixers()
        {
            int v = StateVersion.Current;
            if (fixers != null && fixersVersion == v) return fixers;
            List<FixerChoiceView> list = new List<FixerChoiceView>();
            foreach (NetworkActor a in Ctx.actors.actors)
            {
                if (!ProcurementService.IsBroker(a)) continue;
                FixerProfile fp = a.Get<FixerProfile>();
                string insurance = FixerPolicies.InsuranceStyle(fp);
                list.Add(new FixerChoiceView
                {
                    id = a.id,
                    name = a.name.Display,
                    offersInsurance = insurance != null,
                    line = ("TheNetwork_Brokerage_" + FixerPolicies.Brokerage(fp)).Translate().Resolve() + ", "
                        + ("TheNetwork_Reach_" + fp.contractorReach).Translate().Resolve() + ", "
                        + (insurance != null ? ("TheNetwork_InsuranceStyle_" + insurance).Translate().Resolve() : T("TheNetwork_InsuranceStyle_None"))
                });
            }
            list.Sort((x, y) => string.Compare(x.name, y.name, StringComparison.OrdinalIgnoreCase));
            fixers = list;
            fixersVersion = v;
            return list;
        }

        /// <summary>Contractors the player can hire directly: those the Network lists, with their readiness in words.</summary>
        public List<ContractorChoiceView> DirectChoices()
        {
            List<ContractorChoiceView> list = new List<ContractorChoiceView>();
            List<ContractorCardView> all = Cards();
            for (int i = 0; i < all.Count; i++)
            {
                ContractorCardView c = all[i];
                if (c.ended) continue;
                list.Add(new ContractorChoiceView { id = c.id, name = c.name, line = c.experience + ", " + c.doctrine + ", " + c.readiness, ready = c.sortKey == 0 });
            }
            return list;
        }

        // ------------------------------------------------------------------ contracts

        public List<ContractRowView> Contracts()
        {
            int v = StateVersion.Current;
            int slot = Ctx.Now / Ticks.PerHour;
            if (contracts != null && contractsVersion == v && contractsSlot == slot) return contracts;
            List<ContractRowView> list = new List<ContractRowView>();
            List<Contract> all = Ctx.contracts.contracts;
            ActorId player = Ctx.actors.PlayerProxyId;
            for (int i = 0; i < all.Count; i++)
            {
                Contract c = all[i];
                if (c.parties.issuer != player) continue;
                list.Add(Build(c));
            }
            list.Sort((a, b) =>
            {
                if (a.needsDecision != b.needsDecision) return a.needsDecision ? -1 : 1;
                if (a.terminal != b.terminal) return a.terminal ? 1 : -1;
                return b.id.Value.CompareTo(a.id.Value);
            });
            contracts = list;
            contractsVersion = v;
            contractsSlot = slot;
            return list;
        }

        private ContractRowView Build(Contract c)
        {
            Operation op = Ctx.Procurement.CurrentOperation(c);
            int now = Ctx.Now;
            ContractRowView row = new ContractRowView
            {
                id = c.id,
                status = c.status,
                subStatus = c.subStatus,
                title = "TheNetwork_ContractTitle".Translate(c.Quantity, c.ItemLabel).Resolve(),
                statusLine = ContractNarrative.StatusLine(c, op),
                contractor = c.parties.contractor.IsValid ? Ctx.actors.NameOf(c.parties.contractor) : null,
                broker = Ctx.actors.NameOf(c.parties.broker),
                eta = ContractNarrative.Eta(c, op, now),
                money = ContractNarrative.Money(c),
                insurance = ContractNarrative.InsuranceLine(c.terms?.insurance),
                lineage = ContractNarrative.Lineage(c),
                mode = (c.Mode == ProcurementMode.Direct ? "TheNetwork_Mode_Direct" : "TheNetwork_Mode_Open").Translate().Resolve(),
                seeking = c.IsSeeking,
                terminal = c.IsTerminal,
                quantity = c.Quantity,
                secured = c.Acquire?.secured ?? 0,
                closedTick = c.closedTick,
                balanceDue = c.Deliver?.balanceDue ?? 0
            };
            row.needsDecision = c.status == ContractStatus.Renegotiating || c.subStatus == SubStatus.AwaitingPayment || (c.status == ContractStatus.Bidding && Ctx.Procurement.OpenOffers(c).Count > 0) || c.status == ContractStatus.Unfilled;
            if (c.renegotiation != null)
            {
                row.askExtra = c.renegotiation.extraSilver;
                row.askReduced = c.renegotiation.reducedCount;
            }
            // Results are shown once the contractor has reported them (the return), never at resolution.
            if (c.IsTerminal && c.outcome != null)
            {
                row.result = "TheNetwork_ContractResult".Translate(c.outcome.delivered, c.outcome.requested, ContractNarrative.Cause(c.outcome.causeKey)).Resolve();
            }
            else if (c.status == ContractStatus.Renegotiating && c.subStatus == SubStatus.PartialResult)
            {
                row.result = "TheNetwork_ContractSecured".Translate(c.Acquire.secured, c.Quantity).Resolve();
            }
            List<FieldLogEntry> log = Ctx.FieldLog != null ? Ctx.FieldLog.Visible(c) : null;
            if (log != null)
            {
                for (int i = 0; i < log.Count; i++) row.fieldLog.Add(Narrative.Date(log[i].tick) + " — " + ContractNarrative.FieldLogLine(log[i]));
            }
            if (c.IsSeeking || c.status == ContractStatus.Bidding)
            {
                List<Offer> offers = Ctx.Procurement.OffersOf(c);
                for (int i = 0; i < offers.Count; i++)
                {
                    if (offers[i].round != c.biddingRound) continue;
                    row.offers.Add(BuildOffer(c, offers[i], now));
                }
                for (int i = 0; i < c.refusals.Count; i++)
                {
                    Refusal r = c.refusals[i];
                    if (r.round != c.biddingRound) continue;
                    row.refusals.Add("TheNetwork_RefusalLine".Translate(r.actorName ?? "?", ContractNarrative.Reasons(r.reasonKeys)).Resolve());
                }
            }
            return row;
        }

        private OfferView BuildOffer(Contract c, Offer o, int now)
        {
            ProcurementQuote q = o.quote;
            NetworkActor bidder = Ctx.actors.Get(o.bidder);
            List<string> who = new List<string>();
            if (o.basis.experienceKey != null) who.Add(("TheNetwork_Experience_" + o.basis.experienceKey).Translate().Resolve());
            if (bidder != null) who.Add(ContractNarrative.Fame(bidder.reputation.fame));
            if (o.basis.doctrineKey != null) who.Add(ContractNarrative.Doctrine(o.basis.doctrineKey));
            if (o.basis.relationKey != null && o.basis.relationKey != "Unknown") who.Add(("TheNetwork_Relation_" + o.basis.relationKey).Translate().Resolve());
            OfferView v = new OfferView
            {
                id = o.id,
                bidder = o.bidderName,
                bidderLine = string.Join(", ", who.ToArray()),
                price = q?.finalPrice ?? 0,
                deposit = q?.deposit ?? 0,
                balance = q?.balance ?? 0,
                eta = "TheNetwork_OfferEta".Translate(((int)Math.Round(o.etaTicks / (float)Ticks.PerDay)).ToString()).Resolve(),
                insurance = q?.insuranceOffer == null ? null : "TheNetwork_OfferInsurance".Translate(q.insuranceOffer.premium, ContractNarrative.Coverage(q.insuranceOffer.coverage)).Resolve(),
                insurable = q?.insuranceOffer != null,
                dueWithout = ProcurementService.DueAtAward(c, o, false),
                dueWith = ProcurementService.DueAtAward(c, o, true),
                conditions = ContractNarrative.Conditions(o.conditions),
                stateLabel = ("TheNetwork_OfferState_" + o.state).Translate().Resolve(),
                open = o.IsOpen && o.expiresTick > now,
                newcomer = o.basis.newcomer,
                validity = o.expiresTick > now ? "TheNetwork_OfferValid".Translate((o.expiresTick - now).ToStringTicksToPeriod(false, true)).Resolve() : T("TheNetwork_OfferExpired")
            };
            return v;
        }

        // ------------------------------------------------------------------ contractors

        public List<ContractorCardView> Cards()
        {
            int v = StateVersion.Current;
            if (cards != null && cardsVersion == v) return cards;
            List<ContractorCardView> list = new List<ContractorCardView>();
            ActorId player = Ctx.actors.PlayerProxyId;
            foreach (NetworkActor a in Ctx.actors.actors)
            {
                if (!ContractorService.IsNpcContractor(a) || a.quarantinedReason != null) continue;
                list.Add(Card(a, player));
            }
            list.Sort((x, y) =>
            {
                if (x.ended != y.ended) return x.ended ? 1 : -1;
                if (x.dealtWith != y.dealtWith) return x.dealtWith ? -1 : 1;
                if (x.sortKey != y.sortKey) return x.sortKey.CompareTo(y.sortKey);
                return string.Compare(x.name, y.name, StringComparison.OrdinalIgnoreCase);
            });
            cards = list;
            cardsVersion = v;
            return list;
        }

        private ContractorCardView Card(NetworkActor a, ActorId player)
        {
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            OrganizationProfile org = a.Get<OrganizationProfile>();
            ContractorProfile p = a.Get<ContractorProfile>();
            Availability av = Ctx.Contractors.AvailabilityOf(a);
            ActorRecordSummary s = runtime.State.summaries.Get(a.id);
            string relation = Ctx.Relations.Descriptor(a.id, player);
            bool dealt = Ctx.Relations.Get(a.id, player).exists;
            KnownCharacter leader = Ctx.Contractors.Leader(a);
            int people = org == null ? 1 : org.Healthy + org.Wounded + org.Committed + org.knownMembers.Count;
            ContractorCardView c = new ContractorCardView
            {
                id = a.id,
                name = a.name.Display,
                kind = (org == null ? "TheNetwork_Kind_Solo" : "TheNetwork_Kind_Organization").Translate().Resolve(),
                experience = ContractNarrative.Experience(ContractorService.Experience(a)),
                fame = ContractNarrative.Fame(a.reputation.fame),
                doctrine = ContractNarrative.Doctrine(ContractorService.DoctrineLabel(a)),
                readiness = ContractNarrative.Readiness(av),
                morale = sim == null ? null : ContractNarrative.Morale(sim.morale.descriptor.ToString()),
                people = ContractNarrative.Headcount(people),
                leader = leader != null && org != null ? "TheNetwork_LedBy".Translate(leader.name.Display).Resolve() : null,
                specialties = p == null || p.specialties.Count == 0 ? null : string.Join(", ", p.specialties.ToArray()),
                relation = relation == "Unknown" ? null : ("TheNetwork_Relation_" + relation).Translate().Resolve(),
                origin = sim?.origin == null ? null : "TheNetwork_Origin".Translate(sim.originSnapshot ?? sim.origin.NameSnapshot).Resolve(),
                intelContact = a.Has<IntelSourceProfile>(),
                dealtWith = dealt,
                ended = a.status != ActorStatus.Active,
                endReason = a.status != ActorStatus.Active ? ContractNarrative.EndReason(a.endReasonKey) : null,
                sortKey = av == Availability.Available ? 0 : (av == Availability.Committed ? 1 : 2)
            };
            if (s != null)
            {
                int done = s.Lifetime("contract.completed"), partial = s.Lifetime("contract.partial"), failed = s.Lifetime("contract.failed"), lost = s.Lifetime("casualties.taken");
                if (done + partial + failed > 0 || lost > 0) c.record = "TheNetwork_ContractorRecord".Translate(done, partial, failed, lost).Resolve();
            }
            return c;
        }
    }
}
