using System;
using System.Collections.Generic;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Domain.Ports;
using TheNetwork.History;
using TheNetwork.Kernel;
using TheNetwork.Persist.Events;
using TheNetwork.Settings;

namespace TheNetwork.Tests
{
    // Fake adapter ports. They let Domain logic run headlessly; they say nothing about vanilla behaviour.

    public sealed class FakeComms : ICommsAccess
    {
        public bool usable = true;
        public string reason = "NoUsableCommsConsole";

        public bool CanContact(out string reasonKey)
        {
            reasonKey = usable ? null : reason;
            return usable;
        }
    }

    public sealed class FakePayment : IPayment
    {
        public int silver = 5000;
        public bool canRefund = true;
        public int charged;
        public int refunded;
        public int chargeCalls;

        public bool CanCharge(int amount, out string reasonKey)
        {
            reasonKey = amount <= silver ? null : "NotEnoughBeaconSilver";
            return reasonKey == null;
        }

        public bool TryCharge(int amount, out string reasonKey)
        {
            if (!CanCharge(amount, out reasonKey)) return false;
            silver -= amount;
            charged += amount;
            chargeCalls++;
            return true;
        }

        public bool TryRefund(int amount, out string reasonKey)
        {
            reasonKey = canRefund ? null : "NoHomeMap";
            if (!canRefund) return false;
            silver += amount;
            refunded += amount;
            return true;
        }
    }

    public sealed class FakeCatalog : ICatalog
    {
        public readonly Dictionary<string, ItemFacts> items = new Dictionary<string, ItemFacts>();
        public readonly HashSet<string> notRequestable = new HashSet<string>();
        public readonly HashSet<string> unusable = new HashSet<string>();
        public readonly List<ItemFacts> extras = new List<ItemFacts>();

        public FakeCatalog()
        {
            Add(new ItemFacts { defName = "TestSteel", label = "test steel", packageId = "ludeon.rimworld", isLudeon = true, techLevel = 4, marketValue = 1.9f, stackLimit = 75, tradeable = true, craftable = false, isResource = true });
            Add(new ItemFacts { defName = "TestRifle", label = "test rifle", packageId = "ludeon.rimworld", isLudeon = true, techLevel = 4, marketValue = 350f, stackLimit = 1, tradeable = true, craftable = true, hasQuality = true, isWeapon = true });
            Add(new ItemFacts { defName = "ModX_Weirdium", label = "weirdium", packageId = "someone.weirdmod", modName = "Weird Mod", isLudeon = false, techLevel = 5, marketValue = 40f, stackLimit = 50, tradeable = true, isResource = true });
            Add(new ItemFacts { defName = "ModX_Relic", label = "odd relic", packageId = "someone.weirdmod", isLudeon = false, techLevel = 6, marketValue = 2500f, stackLimit = 1, unique = true });
            extras.Add(new ItemFacts { defName = "TestComponent", label = "test component", isLudeon = true, techLevel = 4, marketValue = 32f, stackLimit = 25, tradeable = true });
            extras.Add(new ItemFacts { defName = "TestMedicine", label = "test medicine", isLudeon = true, techLevel = 4, marketValue = 18f, stackLimit = 25, tradeable = true });
            extras.Add(new ItemFacts { defName = "TestSilverish", label = "test coins", isLudeon = true, techLevel = 2, marketValue = 1f, stackLimit = 500, tradeable = true });
            foreach (ItemFacts e in extras) items[e.defName] = e;
        }

        public void Add(ItemFacts f)
        {
            items[f.defName] = f;
        }

        public bool IsRequestable(string defName, out string reasonKey)
        {
            reasonKey = null;
            if (defName == null || !items.ContainsKey(defName)) reasonKey = "NotInCatalog";
            else if (unusable.Contains(defName)) reasonKey = "FailedToGenerate";
            else if (notRequestable.Contains(defName)) reasonKey = "Ineligible";
            return reasonKey == null;
        }

        public ItemFacts Facts(string defName)
        {
            ItemFacts f;
            return defName != null && items.TryGetValue(defName, out f) ? f : null;
        }

        public IList<ItemFacts> ExtraCargoPool(int maxTechLevel)
        {
            List<ItemFacts> list = new List<ItemFacts>();
            foreach (ItemFacts e in extras) if (e.techLevel <= maxTechLevel && !unusable.Contains(e.defName)) list.Add(e);
            return list;
        }

        public void MarkUnusable(string defName, string reason)
        {
            if (defName != null) unusable.Add(defName);
        }
    }

    public sealed class FakeWorld : IWorldFacts
    {
        public readonly List<FactionFacts> factions = new List<FactionFacts>();
        public float threat = 450f;

        public static FactionFacts Player()
        {
            return new FactionFacts { loadId = 1, name = "Test Colony", defName = "PlayerColony", isPlayer = true, humanlike = true };
        }

        public List<FactionFacts> LiveFactions() { return factions; }
        public float BaseThreatPoints() { return threat; }
        /// <summary>Optional: a deterministic stuff choice for stuff-made test items (null = none).</summary>
        public Func<string, int, string> stuffPicker;

        public string PickStuff(string thingDefName, int seed) { return stuffPicker?.Invoke(thingDefName, seed); }
        public int TilesFromPlayerHome(TileRef tile) { return tile == null ? -1 : 6; }
        public int TravelTicksFromPlayerHome(TileRef tile) { return tile == null ? -1 : 108000; }
        public FactionFacts PlayerFaction() { return Player(); }

        public static FactionFacts Faction(int loadId, string name, string package, int tech, bool hostile, bool pirate = false, bool ludeon = false)
        {
            return new FactionFacts
            {
                loadId = loadId, name = name, defName = name.Replace(" ", ""), defPackageId = package, defIsLudeon = ludeon, techLevel = tech,
                hostileToPlayer = hostile, goodwill = hostile ? -80 : 40, humanlike = true, permanentEnemy = pirate, canGuardSite = hostile
            };
        }
    }

    public sealed class FakeSites : ISiteAdapter
    {
        private int nextSiteId = 1000;
        public readonly HashSet<int> sites = new HashSet<int>();
        public readonly HashSet<int> maps = new HashSet<int>();
        /// <summary>What a sample would count on the site map (everything of the target def on it).</summary>
        public readonly Dictionary<int, int> remainingByOpp = new Dictionary<int, int>();
        public readonly HashSet<string> failDefs = new HashSet<string>();
        public readonly List<int> tileSeeds = new List<int>();
        public bool noTile;

        /// <summary>The synthetic world a Last Known Location is placed in, near the incident (Phase 2.5).</summary>
        public TheNetwork.Diagnostics.GridWorldGraph graph;
        public bool noNearTile;
        public readonly List<TileRef> nearRequests = new List<TileRef>();

        public bool TryFindTileNear(TileRef near, int minDist, int maxDist, int seed, out TileRef tile)
        {
            nearRequests.Add(near);
            tile = null;
            if (noNearTile || graph == null) return false;
            return graph.TryFindPassableNear(near, minDist, maxDist, seed, out tile);
        }

        public bool TryFindTile(int seed, int minDist, int maxDist, out TileRef tile)
        {
            tileSeeds.Add(seed);
            tile = noTile ? null : new TileRef { tileId = Math.Abs(seed % 100000), layerId = 0, layerDef = "Surface", regionKey = "S0:R" + Math.Abs(seed % 48).ToString("00") };
            return !noTile;
        }

        public MaterializeResult Materialize(Opportunity opp)
        {
            foreach (var p in opp.payload)
            {
                TheNetwork.Persist.ItemPayload ip = p as TheNetwork.Persist.ItemPayload;
                if (ip != null && ip.count > 0 && failDefs.Contains(ip.thing.defName))
                {
                    return new MaterializeResult { thingCreationFailed = true, failedDefName = ip.thing.defName, failureReason = "FailedToGenerate:Test" };
                }
            }
            int id = nextSiteId++;
            sites.Add(id);
            remainingByOpp[opp.id.Value] = opp.TargetCount;
            return new MaterializeResult { ok = true, site = new WorldObjectRef { id = id, defName = "Site", label = "cache" }, threatProfileUsed = opp.threat.profileKey };
        }

        public bool SiteExists(WorldObjectRef site) { return site != null && sites.Contains(site.id); }
        public bool SiteHasMap(WorldObjectRef site) { return site != null && maps.Contains(site.id); }

        public bool TrySampleRemaining(Opportunity opp, out int remaining)
        {
            remaining = 0;
            if (opp.site == null || !maps.Contains(opp.site.id)) return false;
            remainingByOpp.TryGetValue(opp.id.Value, out remaining);
            return true;
        }

        public void ReleaseSite(Opportunity opp, bool destroyIfNoMap)
        {
            if (opp.site != null && destroyIfNoMap && !maps.Contains(opp.site.id)) sites.Remove(opp.site.id);
        }
    }

    public sealed class RecordingConsumer : IEventConsumer
    {
        public readonly List<NetworkEvent> events = new List<NetworkEvent>();
        public string Name => "Recorder";
        public void Handle(NetworkEvent evt) { events.Add(evt); }

        public int Count(string key)
        {
            int n = 0;
            foreach (NetworkEvent e in events) if (e.typeKey == key) n++;
            return n;
        }
    }

    /// <summary>A complete Domain context over fakes, a manual clock and real stores/services.</summary>
    /// <summary>Drop-pod delivery as plain data: a list of home maps, each with or without a drop spot.</summary>
    public sealed class FakeDelivery : IDelivery
    {
        public List<int> homes = new List<int> { 1 };
        public HashSet<int> noSpot = new HashSet<int>();
        public bool failCreation;

        /// <summary>The next N drops fail AFTER the balance was charged (a pod that did not land), without ever being a creation failure.</summary>
        public int failNextDeliveries;
        public int deliveries;
        public int delivered;
        public List<TheNetwork.Persist.ItemPayload> lastPayload = new List<TheNetwork.Persist.ItemPayload>();

        public int DefaultHomeMapId(out string label)
        {
            label = homes.Count > 0 ? "Home " + homes[0] : null;
            return homes.Count > 0 ? homes[0] : -1;
        }

        public DeliveryPlan Plan(int preferredMapId, int seed)
        {
            if (homes.Count == 0) return new DeliveryPlan { failureKey = "NoHomeMap" };
            List<int> order = new List<int>(homes);
            order.Sort((a, b) => a == preferredMapId ? -1 : (b == preferredMapId ? 1 : a.CompareTo(b)));
            foreach (int m in order)
            {
                if (noSpot.Contains(m)) continue;
                return new DeliveryPlan { ok = true, mapId = m, mapLabel = "Home " + m, cellX = 10, cellZ = 10, rerouted = m != preferredMapId };
            }
            return new DeliveryPlan { failureKey = "NoDropSpot" };
        }

        public DeliveryResult Deliver(DeliveryPlan plan, List<TheNetwork.Persist.ItemPayload> payload, int seed)
        {
            if (failNextDeliveries > 0)
            {
                failNextDeliveries--;
                return new DeliveryResult { failureKey = "PodDidNotLand" };
            }
            if (failCreation) return new DeliveryResult { failureKey = "FailedToGenerate", thingCreationFailed = true, failedDefName = payload.Count > 0 ? payload[0].thing?.defName : null };
            int n = 0;
            lastPayload = payload;
            foreach (TheNetwork.Persist.ItemPayload p in payload) n += p.count;
            deliveries++;
            delivered += n;
            return new DeliveryResult { ok = true, mapId = plan.mapId, mapLabel = plan.mapLabel, delivered = n };
        }
    }

    public sealed class TestNet
    {
        public readonly ManualClock clock = new ManualClock { Now = 100000 };
        public readonly IdAllocator ids = new IdAllocator();
        public readonly DiagnosticsState diag = new DiagnosticsState();
        public readonly EventJournal journal = new EventJournal();
        public readonly FakeComms comms = new FakeComms();
        public readonly FakePayment pay = new FakePayment();
        public readonly FakeCatalog cat = new FakeCatalog();
        public readonly FakeWorld world = new FakeWorld();
        public readonly FakeSites sites = new FakeSites();
        public readonly FakeDelivery delivery = new FakeDelivery();
        public readonly RecordingConsumer recorder = new RecordingConsumer();
        public readonly TheNetwork.Diagnostics.RuntimeTests.FakePhysicalWorldPort physical = new TheNetwork.Diagnostics.RuntimeTests.FakePhysicalWorldPort();
        public readonly HistoryLedger ledger = new HistoryLedger();
        public readonly SummaryStore summaries = new SummaryStore();
        public NetScheduler scheduler;
        public NetworkEventBus bus;
        public DomainContext ctx;
        public HistoryService history;
        public readonly TheNetwork.Diagnostics.GridWorldGraph graph;

        public TestNet(int seed = 424242)
        {
            physical.episodeResolver = id => ctx?.episodes?.Get(id);
            graph = TheNetwork.Diagnostics.GridWorldGraph.Default(null, seed);
            sites.graph = graph;
            IntelDevOverrides.Clear();
            ProcurementDevOverrides.Clear();
            ServiceToggles.ProcurementEnabled = true;
            IntelDevOverrides.commsGateOverride = false;
            IntelDevOverrides.waiveFees = false;
            scheduler = new NetScheduler(ids, clock);
            bus = new NetworkEventBus(ids, clock, journal, diag);
            ctx = new DomainContext
            {
                networkSeed = seed, ids = ids, clock = clock, scheduler = scheduler, bus = bus, diagnostics = diag,
                cast = new WorldCastSnapshot(), actors = new ActorStore(), characters = new CharacterStore(),
                intel = new IntelStore(), opportunities = new OpportunityStore(), summaries = summaries, ledger = ledger,
                relations = new TheNetwork.Domain.Relations.RelationStore(), knowledge = new TheNetwork.Domain.Knowledge.KnowledgeStore(),
                contracts = new TheNetwork.Domain.Contracts.ContractStore(), operations = new TheNetwork.Domain.Operations.OperationStore(),
                consequences = new TheNetwork.Domain.Consequences.ConsequenceStore(),
                episodes = new TheNetwork.Domain.Physical.EpisodeStore(), physicalPort = physical,
                catalog = cat, comms = comms, payment = pay, world = world, sites = sites, delivery = delivery, graph = graph
            };
            ctx.Actors = new ActorService(ctx);
            ctx.Intel = new IntelService(ctx);
            ctx.Opportunities = new OpportunityService(ctx);
            ctx.Contractors = new TheNetwork.Domain.Contractors.ContractorService(ctx);
            ctx.Upkeep = new TheNetwork.Domain.Contractors.UpkeepService(ctx);
            ctx.Relations = new TheNetwork.Domain.Relations.RelationService(ctx);
            ctx.Knowledge = new TheNetwork.Domain.Knowledge.KnowledgeService(ctx);
            ctx.Procurement = new TheNetwork.Domain.Contracts.ProcurementService(ctx);
            ctx.Operations = new TheNetwork.Domain.Operations.OperationService(ctx);
            ctx.Consequences = new TheNetwork.Domain.Consequences.ConsequenceEngine(ctx);
            ctx.Spatial = new TheNetwork.Domain.Spatial.SpatialService(ctx);
            ctx.FieldLog = new TheNetwork.Domain.Contracts.FieldLogService(ctx);
            ctx.Career = new TheNetwork.Domain.Contractors.CareerService(ctx);
            ctx.Lifecycle = new TheNetwork.Domain.Physical.PhysicalLifecycleService(ctx);
            history = new HistoryService(ledger, summaries, ctx.actors, ids, clock, seed);
            scheduler.RegisterKind(JobKinds.IntelRound, ctx.Intel.RunRound, true, true);
            scheduler.RegisterKind(JobKinds.IntelClose, ctx.Intel.CloseJob, true, true);
            scheduler.RegisterKind(JobKinds.OppSample, ctx.Opportunities.SampleJob, true, true);
            scheduler.RegisterKind(JobKinds.OppWarn, ctx.Opportunities.WarnJob, true, true);
            scheduler.RegisterKind(JobKinds.OppClose, ctx.Opportunities.CloseJob, true, true);
            scheduler.RegisterKind(JobKinds.RefundRetry, ctx.Intel.RetryRefunds, true, false);
            scheduler.RegisterKind(JobKinds.ContractorUpkeep, ctx.Upkeep.UpkeepJob, true, true);
            scheduler.RegisterKind(JobKinds.PopulationWeekly, ctx.Upkeep.PopulationJobRun, true, true);
            TheNetwork.Core.NetworkRuntime.RegisterPhaseTwoJobs(scheduler, ctx);
            TheNetwork.Core.NetworkRuntime.RegisterPhaseThreeJobs(scheduler, ctx);
            bus.Register(ConsumerOrder.History, history, HistoryService.ConsumedKeys);
            bus.Register(ConsumerOrder.Relationships, ctx.Relations, TheNetwork.Domain.Relations.RelationService.ConsumedKeys);
            bus.Register(ConsumerOrder.Consequences, ctx.Consequences, TheNetwork.Domain.Consequences.ConsequenceEngine.ConsumedKeys);
            bus.Register(ConsumerOrder.Presentation, recorder);
            ctx.Actors.EnsurePlayerProxy(world.PlayerFaction());
            ctx.Actors.EnsureExchange();
        }

        public NetworkActor AddFixer(string intelStyle, Band speed = Band.Medium, Band reliability = Band.Medium, Band fee = Band.Medium, string brokerage = "Standard", string insurance = "None", ReachBand reach = ReachBand.Regional)
        {
            GlobalNetworkRoster roster = new GlobalNetworkRoster();
            roster.fixerTemplates.Add(new FixerTemplate
            {
                templateId = Guid.NewGuid().ToString("N"), provenance = TemplateProvenance.Custom, displayName = "Fixer " + intelStyle + " " + ctx.actors.actors.Count,
                intelStyle = intelStyle, speedBand = speed, reliabilityBand = reliability, feeBand = fee, brokerageStyle = brokerage, insuranceStyle = insurance,
                contractorReach = reach, geographicReach = reach
            });
            ctx.cast.imported = false;
            ctx.Actors.ImportCast(roster, 1, null);
            ctx.Actors.InstantiateFixers();
            NetworkActor last = null;
            foreach (NetworkActor a in ctx.actors.actors) if (a.Has<TheNetwork.Persist.FixerProfile>()) last = a;
            return last;
        }

        /// <summary>Runs every job due up to <paramref name="tick"/>, in order, like the tick loop.</summary>
        public void AdvanceTo(int tick)
        {
            int guard = 0;
            while (scheduler.NextDueTick <= tick && guard++ < 100000)
            {
                clock.Now = Math.Max(clock.Now, scheduler.NextDueTick);
                scheduler.RunDue();
            }
            clock.Now = Math.Max(clock.Now, tick);
        }

        public void Advance(int ticks)
        {
            AdvanceTo(clock.Now + ticks);
        }

        public IntelRequest Only()
        {
            return ctx.intel.requests[ctx.intel.requests.Count - 1];
        }

        /// <summary>Resolves the running round of a request now (the job's due tick is reached).</summary>
        public void ResolveRound(IntelRequest r)
        {
            AdvanceTo(r.nextRoundDueTick);
        }
    }
}
