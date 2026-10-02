using System;
using System.Collections.Generic;
using System.Text;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Consequences;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Knowledge;
using TheNetwork.Domain.Operations;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Domain.Ports;
using TheNetwork.Domain.Relations;
using TheNetwork.History;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Settings;

namespace TheNetwork.Diagnostics.RuntimeTests
{
    /// <summary>
    /// An isolated Network world for one runtime test (Phase 2.9). It owns EVERYTHING mutable: its own id allocator, clock,
    /// scheduler, event bus, stores and ports, and runs the production domain services over them (ProcurementService,
    /// OperationService, CareerService, SpatialService, ...): the sandbox exists to CALL production logic, never to copy it.
    /// It shares nothing writable with the live Network, registers no letter or presentation consumer, and is plain memory:
    /// it is never saved, and a completed run drops it. External effects go through the Sandbox* ports (a private purse, a
    /// delivery that records, a catalog whose failures stay local), so no silver, Thing, map or world object is reachable.
    /// </summary>
    public sealed class RuntimeTestSandbox
    {
        /// <summary>Synthetic goods modelled on vanilla steel and a rifle, so a scenario never depends on the owner's mod list.</summary>
        public const string Steel = "RT_Steel";
        public const string Rifle = "RT_Rifle";

        public readonly string TestId;
        public readonly int Seed;
        public readonly IdAllocator Ids = new IdAllocator();
        public readonly ManualClock Clock = new ManualClock { Now = 100000 };
        public readonly EventJournal Journal = new EventJournal();
        public readonly DiagnosticsState Diag = new DiagnosticsState();
        public readonly NetScheduler Scheduler;
        public readonly NetworkEventBus Bus;
        public readonly HistoryLedger Ledger = new HistoryLedger();
        public readonly SummaryStore Summaries = new SummaryStore();
        public readonly HistoryService History;
        public readonly DomainContext Ctx;
        public readonly SandboxComms Comms = new SandboxComms();
        public readonly SandboxPayment Payment = new SandboxPayment();
        public readonly SandboxCatalog Catalog = new SandboxCatalog();
        public readonly SandboxWorldFacts World = new SandboxWorldFacts();
        public readonly SandboxSites Sites = new SandboxSites();
        public readonly SandboxDelivery Delivery = new SandboxDelivery();
        public readonly SandboxEventRecorder Events = new SandboxEventRecorder();
        public readonly GridWorldGraph Graph;

        private int made;

        public bool Disposed { get; private set; }

        public int Now => Clock.Now;

        /// <param name="attempt">0 for the test's own seed; a scenario that needs to look for a suitable synthetic world (the way the
        /// headless suite tries seeds) asks for attempt 1, 2, ...: each is a fixed, derived seed, so two runs search the same worlds.</param>
        public RuntimeTestSandbox(string testId, int attempt = 0)
        {
            TestId = testId;
            Seed = attempt == 0 ? RuntimeTestSeeds.ForTest(testId) : NetHash.Combine(RuntimeTestSeeds.ForTest(testId), attempt);
            Graph = GridWorldGraph.Default(null, Seed);
            Sites.graph = Graph;
            Catalog.Add(new ItemFacts { defName = Steel, label = "sandbox steel", packageId = "ludeon.rimworld", isLudeon = true, techLevel = 4, marketValue = 1.9f, stackLimit = 75, tradeable = true, craftable = false, isResource = true });
            Catalog.Add(new ItemFacts { defName = Rifle, label = "sandbox rifle", packageId = "ludeon.rimworld", isLudeon = true, techLevel = 4, marketValue = 350f, stackLimit = 1, tradeable = true, craftable = true, hasQuality = true, isWeapon = true });
            Catalog.extras.Add(new ItemFacts { defName = "RT_Component", label = "sandbox component", isLudeon = true, techLevel = 4, marketValue = 32f, stackLimit = 25, tradeable = true });
            Catalog.extras.Add(new ItemFacts { defName = "RT_Medicine", label = "sandbox medicine", isLudeon = true, techLevel = 4, marketValue = 18f, stackLimit = 25, tradeable = true });
            foreach (ItemFacts e in Catalog.extras) Catalog.Add(e);

            Scheduler = new NetScheduler(Ids, Clock);
            Bus = new NetworkEventBus(Ids, Clock, Journal, Diag);
            Ctx = new DomainContext
            {
                networkSeed = Seed, ids = Ids, clock = Clock, scheduler = Scheduler, bus = Bus, diagnostics = Diag,
                cast = new WorldCastSnapshot(), actors = new ActorStore(), characters = new CharacterStore(),
                intel = new IntelStore(), opportunities = new OpportunityStore(), summaries = Summaries, ledger = Ledger,
                relations = new RelationStore(), knowledge = new KnowledgeStore(),
                contracts = new ContractStore(), operations = new OperationStore(), consequences = new ConsequenceStore(),
                catalog = Catalog, comms = Comms, payment = Payment, world = World, sites = Sites, delivery = Delivery, graph = Graph
            };
            Ctx.Actors = new ActorService(Ctx);
            Ctx.Intel = new IntelService(Ctx);
            Ctx.Opportunities = new OpportunityService(Ctx);
            Ctx.Contractors = new ContractorService(Ctx);
            Ctx.Upkeep = new UpkeepService(Ctx);
            Ctx.Relations = new RelationService(Ctx);
            Ctx.Knowledge = new KnowledgeService(Ctx);
            Ctx.Procurement = new ProcurementService(Ctx);
            Ctx.Operations = new OperationService(Ctx);
            Ctx.Consequences = new ConsequenceEngine(Ctx);
            Ctx.Spatial = new Domain.Spatial.SpatialService(Ctx);
            Ctx.FieldLog = new FieldLogService(Ctx);
            Ctx.Career = new CareerService(Ctx);
            History = new HistoryService(Ledger, Summaries, Ctx.actors, Ids, Clock, Seed);

            // The same jobs the real runtime registers (RegisterPhaseTwoJobs is the production method), on THIS scheduler.
            Scheduler.RegisterKind(JobKinds.IntelRound, Ctx.Intel.RunRound, true, true);
            Scheduler.RegisterKind(JobKinds.IntelClose, Ctx.Intel.CloseJob, true, true);
            Scheduler.RegisterKind(JobKinds.OppSample, Ctx.Opportunities.SampleJob, true, true);
            Scheduler.RegisterKind(JobKinds.OppWarn, Ctx.Opportunities.WarnJob, true, true);
            Scheduler.RegisterKind(JobKinds.OppClose, Ctx.Opportunities.CloseJob, true, true);
            Scheduler.RegisterKind(JobKinds.RefundRetry, Ctx.Intel.RetryRefunds, true, false);
            Scheduler.RegisterKind(JobKinds.ContractorUpkeep, Ctx.Upkeep.UpkeepJob, true, true);
            Scheduler.RegisterKind(JobKinds.PopulationWeekly, Ctx.Upkeep.PopulationJobRun, true, true);
            Core.NetworkRuntime.RegisterPhaseTwoJobs(Scheduler, Ctx);
            Bus.Register(ConsumerOrder.History, History, HistoryService.ConsumedKeys);
            Bus.Register(ConsumerOrder.Relationships, Ctx.Relations, RelationService.ConsumedKeys);
            Bus.Register(ConsumerOrder.Consequences, Ctx.Consequences, ConsequenceEngine.ConsumedKeys);
            Bus.Register(ConsumerOrder.Presentation, Events);
            Ctx.Actors.EnsurePlayerProxy(World.PlayerFaction());
            Ctx.Actors.EnsureExchange();
        }

        /// <summary>Drops this world. Anything that touches it afterwards is a test bug and is refused loudly.</summary>
        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            Scheduler.Clear();
            Events.events.Clear();
        }

        private void Alive()
        {
            if (Disposed) throw new InvalidOperationException("The sandbox of " + TestId + " was already discarded.");
        }

        // ------------------------------------------------------------------ cast

        public NetworkActor AddFixer(string brokerage = "Standard", string insurance = "None", string intelStyle = "Thorough", ReachBand reach = ReachBand.Vast)
        {
            Alive();
            GlobalNetworkRoster roster = new GlobalNetworkRoster();
            roster.fixerTemplates.Add(new FixerTemplate
            {
                templateId = "rt-fixer-" + (++made), provenance = TemplateProvenance.Custom, displayName = "Sandbox Fixer " + made,
                intelStyle = intelStyle, speedBand = Band.Medium, reliabilityBand = Band.Medium, feeBand = Band.Medium,
                brokerageStyle = brokerage, insuranceStyle = insurance, contractorReach = reach, geographicReach = reach
            });
            Ctx.cast.imported = false;
            Ctx.Actors.ImportCast(roster, 1, null);
            Ctx.Actors.InstantiateFixers();
            NetworkActor last = null;
            foreach (NetworkActor a in Ctx.actors.actors) if (a.Has<FixerProfile>()) last = a;
            return last;
        }

        /// <summary>
        /// A capable, steady, professional team that will take ordinary work, and (by default) will not wander off on its own
        /// during a scenario. The same shape the headless suite uses, built by the production ContractorService.
        /// </summary>
        public NetworkActor AddContractor(ContractorForm form = ContractorForm.Team, ExperienceBand experience = ExperienceBand.Veteran, FameBand fame = FameBand.Local, bool still = true)
        {
            Alive();
            ContractorTemplate t = new ContractorTemplate
            {
                templateId = "rt-contractor-" + (++made), provenance = TemplateProvenance.Custom, displayName = form + " " + experience + " " + made,
                form = form, startingExperience = experience, startingFame = fame, canIssueWork = false, doctrineStyle = "Professional",
                specialties = new List<string> { "combat acquisition" }
            };
            NetworkActor a = Ctx.Contractors.Instantiate(t, ProvenanceSource.GlobalCast, t.templateId);
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
            if (still)
            {
                sim.spatial.nextAmbientTick = int.MaxValue / 2;
                sim.mobility.speedBand = Band.Medium;
            }
            return a;
        }

        /// <summary>A generated cast of NPC contractors, imported and instantiated by the production CastGenerator and ContractorService.</summary>
        public int AddGeneratedCast(int count)
        {
            Alive();
            int n = 0;
            CastGenerator gen = new CastGenerator(NamePools.Fallback(), 777, () => "rt-tpl-" + (++n), null);
            GlobalNetworkRoster roster = new GlobalNetworkRoster();
            roster.contractorTemplates.AddRange(gen.GenerateContractors(count));
            Ctx.cast.imported = false;
            Ctx.Actors.ImportCast(roster, NetworkSettings.CurrentVersion, null);
            return Ctx.Contractors.InstantiateFromSnapshot();
        }

        public static ContractorSimulation Sim(NetworkActor a) { return a.Get<ContractorSimulation>(); }

        // ------------------------------------------------------------------ contracts

        /// <summary>Posts a procurement request. A refusal is a failed scenario, reported with the production reason.</summary>
        public Contract Post(NetworkActor fixer, string def, int count, NetworkActor direct = null, int premium = 0)
        {
            Alive();
            CommandResult r = Ctx.Procurement.Post(new ProcurementRequest
            {
                defName = def, count = count, broker = fixer.id, premiumContribution = premium,
                mode = direct != null ? ProcurementMode.Direct : ProcurementMode.Open, invited = direct != null ? direct.id : ActorId.None
            });
            if (!r.ok) throw new RuntimeAssertionException("The sandbox could not post '" + def + "' x" + count + " (" + r + ")", "posted", r.ToString());
            return Ctx.contracts.Get(Ctx.Procurement.lastPosted);
        }

        /// <summary>Lets the bidding window run out and returns the first open offer (null when nobody bid).</summary>
        public Offer CollectOffer(Contract c)
        {
            Alive();
            AdvanceTo(c.windowCloseTick);
            List<Offer> open = Ctx.Procurement.OpenOffers(c);
            return open.Count > 0 ? open[0] : null;
        }

        /// <summary>Post to one contractor, take its quote and accept it: the contract is Active and its operation has started.</summary>
        public Contract Award(NetworkActor fixer, NetworkActor contractor, string def = Steel, int count = 150, bool insure = false, int premium = 0)
        {
            Contract c = Post(fixer, def, count, contractor, premium);
            Offer o = CollectOffer(c);
            if (o == null) throw new RuntimeAssertionException("The sandbox contractor made no offer (refusals: " + Refusals(c) + ")", "an offer", "none");
            CommandResult r = Ctx.Procurement.Accept(o.id, insure);
            if (!r.ok) throw new RuntimeAssertionException("The sandbox quote could not be accepted (" + r + ")", "accepted", r.ToString());
            return c;
        }

        public static string Refusals(Contract c)
        {
            List<string> s = new List<string>();
            foreach (Refusal r in c.refusals) s.Add(r.actorName + ":" + string.Join("+", r.reasonKeys.ToArray()));
            return string.Join(", ", s.ToArray());
        }

        public Operation OpOf(Contract c)
        {
            return Ctx.Procurement.CurrentOperation(c);
        }

        // ------------------------------------------------------------------ time (bounded, synchronous, on this sandbox's own clock)

        /// <summary>Runs every job due up to <paramref name="tick"/>, in order, like the tick loop (bounded).</summary>
        public void AdvanceTo(int tick)
        {
            Alive();
            int guard = 0;
            while (Scheduler.NextDueTick <= tick && guard++ < 100000)
            {
                Clock.Now = Math.Max(Clock.Now, Scheduler.NextDueTick);
                Scheduler.RunDue();
            }
            Clock.Now = Math.Max(Clock.Now, tick);
        }

        public void Advance(int ticks)
        {
            AdvanceTo(Clock.Now + ticks);
        }

        /// <summary>
        /// Advances in steps until the condition holds or <paramref name="maxTicks"/> of sandbox time have passed (never longer).
        /// Returns whether the condition holds. A bounded loop on the sandbox clock: it can never run forever, and it never waits
        /// in real time.
        /// </summary>
        public bool AdvanceUntil(Func<bool> done, int maxTicks, int stepTicks = Ticks.PerDay / 4)
        {
            Alive();
            int end = Clock.Now + maxTicks;
            int guard = 0;
            while (!done() && Clock.Now < end && guard++ < 100000) Advance(stepTicks);
            return done();
        }

        // ------------------------------------------------------------------ inspection

        /// <summary>
        /// A compact, readable account of the entities a scenario involved (never the whole world): the contracts with their
        /// funding and ledger, the operations, the contractors, the scheduler's pending jobs for them and the Field Log.
        /// </summary>
        public string Describe(RuntimeTestContext ctx)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("SANDBOX of " + TestId + (Disposed ? " (discarded)" : "") + ": tick " + Clock.Now + ", " + Ctx.contracts.contracts.Count + " contracts, " + Ctx.operations.operations.Count + " operations, " + Ctx.actors.actors.Count + " actors, " + Scheduler.Count + " pending jobs");
            sb.AppendLine("  purse " + Payment.silver + " silver (charged " + Payment.charged + " in " + Payment.chargeCalls + " calls, refunded " + Payment.refunded + " in " + Payment.refundCalls + "); delivery mode " + Delivery.mode + " (" + Delivery.deliveries + " deliveries, " + Delivery.deliveredItems + " items); comms " + (Comms.usable ? "usable" : "blocked"));
            HashSet<int> targets = new HashSet<int>();
            List<Contract> contracts = new List<Contract>();
            List<Operation> ops = new List<Operation>();
            List<NetworkActor> actors = new List<NetworkActor>();
            if (ctx != null)
            {
                foreach (int id in ctx.TrackedContracts) { Contract c = Ctx.contracts.Get(new ContractId(id)); if (c != null) contracts.Add(c); }
                foreach (int id in ctx.TrackedOperations) { Operation o = Ctx.operations.Get(new OperationId(id)); if (o != null) ops.Add(o); }
                foreach (int id in ctx.TrackedActors) { NetworkActor a = Ctx.actors.Get(new ActorId(id)); if (a != null) actors.Add(a); }
            }
            if (contracts.Count == 0 && ops.Count == 0 && actors.Count == 0)
            {
                // Nothing was tracked: the sandbox is small, so describe what it has.
                contracts.AddRange(Ctx.contracts.contracts);
                ops.AddRange(Ctx.operations.operations);
                foreach (NetworkActor a in Ctx.actors.actors) if (ContractorService.IsNpcContractor(a)) actors.Add(a);
            }
            foreach (Contract c in contracts)
            {
                targets.Add(c.id.Value);
                DescribeContract(sb, c);
                Operation op = Ctx.Procurement.CurrentOperation(c);
                if (op != null && !ops.Contains(op)) ops.Add(op);
            }
            foreach (Operation o in ops)
            {
                targets.Add(o.id.Value);
                DescribeOperation(sb, o);
                NetworkActor a = Ctx.actors.Get(o.contractor);
                if (a != null && !actors.Contains(a)) actors.Add(a);
            }
            foreach (NetworkActor a in actors) DescribeContractor(sb, a);
            sb.AppendLine("SCHEDULER (jobs of the entities above)");
            int shown = 0;
            foreach (ScheduledJob j in Scheduler.AllJobs)
            {
                if (!targets.Contains(j.target)) continue;
                sb.AppendLine("  " + j.kind + " target " + j.target + " due " + j.dueTick + " (in " + (j.dueTick - Clock.Now) + " ticks)");
                shown++;
            }
            if (shown == 0) sb.AppendLine("  (none)");
            return sb.ToString();
        }

        private void DescribeContract(StringBuilder sb, Contract c)
        {
            sb.AppendLine("CONTRACT " + c.id + " " + c.Quantity + "x " + c.ItemLabel + ": status " + c.status + (c.subStatus != null ? "/" + c.subStatus : "") + (c.causeKey != null ? ", cause " + c.causeKey : ""));
            sb.AppendLine("  money: charged " + c.ExternalCharged() + ", returned " + c.ExternalRefunded() + ", transferred in " + c.TransferredIn() + " out " + c.TransferredOut() + ", net funding " + c.NetFunding() + ", contractor holds " + c.ContractorHeld());
            if (c.Deliver != null) sb.AppendLine("  delivery: " + (c.Deliver.InProgress ? "in progress" : "not in progress") + ", pending " + c.Deliver.pendingCount + ", balance due " + c.Deliver.balanceDue + (c.Deliver.balancePaid ? " (paid)" : " (unpaid)") + ", attempts " + c.Deliver.attempts);
            if (c.Acquire != null) sb.AppendLine("  goods: secured " + c.Acquire.secured + ", delivered " + c.Acquire.delivered);
            for (int i = 0; i < c.ledger.Count; i++)
            {
                MoneyRecord m = c.ledger[i];
                sb.AppendLine("  ledger " + i + ": " + m.direction + " " + m.purpose + " " + m.silver + (m.contractorSilver != 0 ? " (contractor " + (m.contractorSilver > 0 ? "+" : "") + m.contractorSilver + ")" : "") + (m.fromOwnFunding != 0 ? " fromOwn " + m.fromOwnFunding : "") + (m.fullReversal ? " FULL-REVERSAL" : "") + (m.pending ? " PENDING" : "") + " [" + m.noteKey + "]");
            }
            if (c.lineage != null) sb.AppendLine("  lineage: parent " + c.lineage.parent + ", children " + c.lineage.children.Count + ", " + (c.lineage.relationKey ?? "-"));
            foreach (FieldLogEntry e in c.fieldLog) sb.AppendLine("  field log: " + e.key + (e.args.Count > 0 ? " (" + string.Join(", ", e.args.ToArray()) + ")" : ""));
        }

        private void DescribeOperation(StringBuilder sb, Operation o)
        {
            sb.AppendLine("OPERATION " + o.id + ": " + o.status + " / " + o.phase + ", contractor " + o.contractor + ", career eligible " + o.careerEligible + " applied " + o.careerOutcomeApplied);
            if (o.outcome != null) sb.AppendLine("  outcome: " + o.outcome.band + ", secured " + o.outcome.secured + " of " + o.outcome.requested + (o.outcome.troubledKey != null ? ", troubled " + o.outcome.troubledKey : ""));
            StringBuilder cps = new StringBuilder();
            foreach (string k in new[] { Checkpoint.Prep, Checkpoint.Arrive, Checkpoint.Resolve, Checkpoint.Return, Checkpoint.Deliver })
            {
                Checkpoint cp = o.Find(k);
                if (cp != null) cps.Append(k).Append(cp.done ? "[done] " : "[due " + cp.dueTick + "] ");
            }
            sb.AppendLine("  checkpoints: " + cps.ToString().TrimEnd());
            if (o.spatial != null) sb.AppendLine("  spatial plan: charter " + o.spatial.Charter + ", origin " + Tile(o.spatial.origin) + ", work " + Tile(o.spatial.workRegion) + ", return " + Tile(o.spatial.returnTo) + ", incident " + Tile(o.spatial.incident));
        }

        private void DescribeContractor(StringBuilder sb, NetworkActor a)
        {
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            sb.AppendLine("CONTRACTOR " + a.id + " " + a.name.Display + " (" + a.status + "): fame " + a.reputation.fame + " (score " + a.reputation.score + ")" + (sim != null ? ", experience " + ContractorService.Experience(a) + ", funds " + sim.funds + ", equipment tier " + sim.equipment.tier + " condition " + sim.equipment.condition.ToString("0.00") : ""));
            if (sim == null) return;
            CareerRecord r = sim.career;
            sb.AppendLine("  career: " + r.triumphs + " triumph, " + r.successes + " success, " + r.partials + " partial, " + r.failures + " failure, " + r.disasters + " disaster (" + r.legacyResolved + " before records), earnings " + r.careerEarnings + ", reputation earned " + r.reputationEarned + ", advancements " + r.advancementCount);
            SpatialState sp = sim.spatial;
            if (sp != null) sb.AppendLine("  spatial: " + sp.status + "/" + sp.purpose + ", anchor " + Tile(sp.anchor) + ", destination " + Tile(sp.destination) + ", arrival tick " + sp.arrivalTick + (sp.bridgeFrom != null ? ", charter " + Tile(sp.bridgeFrom) + " -> " + Tile(sp.bridgeTo) + (sp.bridged ? " (crossed)" : "") : ""));
            sb.AppendLine("  commitments: " + sim.commitments.Count);
        }

        private static string Tile(TileRef t)
        {
            return t == null ? "-" : t.layerId + ":" + t.tileId;
        }
    }
}
