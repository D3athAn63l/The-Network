using System;
using System.Collections.Generic;
using System.Diagnostics;
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

namespace TheNetwork.Diagnostics
{
    /// <summary>
    /// Phase 2 soak and performance harness (PERFORMANCE § 7): a scratch world entirely in memory (its
    /// own stores, scheduler, bus and simple in-memory ports) with about 100 contractors, a handful of
    /// Fixers and a steady stream of procurement contracts, run for simulated days. It never touches a
    /// live save. It reports outcome counts, store sizes and time per simulated day, and checks that no
    /// contract is left stuck.
    /// </summary>
    public static class SoakHarness
    {
        public sealed class Result
        {
            public int days;
            public int posted;
            public readonly Dictionary<ContractStatus, int> byStatus = new Dictionary<ContractStatus, int>();
            public int stuck;
            public int followUps;
            public int contractorsStart;
            public int contractorsEnd;
            public int contractorsEnded;
            public int newcomers;
            public int npcIssued;
            public int relations;
            public int knowledgeBooks;
            public int historyRecords;
            public int journal;
            public int jobs;
            public int operations;
            public int contractsKept;
            public int offersKept;

            // Invariants (every one must be 0): checked daily over the whole run.
            /// <summary>Checkouts that took a contractor past its job capacity.</summary>
            public int overCapacity;
            /// <summary>Named people found on two live operations at once (summed over days).</summary>
            public int doubleBooked;
            /// <summary>Money records that are empty or negative, or contracts that returned more than they held.</summary>
            public int moneyViolations;
            /// <summary>Contracts with more than one refund or more than one insurance payout.</summary>
            public int duplicateRefunds;
            /// <summary>Last Known Locations holding more of the goods than the operation secured.</summary>
            public int lklAboveSecured;
            /// <summary>|charged − refunded at the payment port − (external charges − external refunds in every contract ledger)|.</summary>
            public long moneyDrift;
            /// <summary>Σ(transfers in − transfers out) over every contract ever seen.</summary>
            public long transferDrift;

            /// <summary>Acceptances refused because the bidder had filled its capacity since quoting (not a violation).</summary>
            public int staleRefused;

            // Phase 2.5 spatial continuity (every violation count must be 0).
            public Domain.Spatial.SpatialCounters spatial;
            /// <summary>Active contractors found without a valid anchor on a daily check (summed over days).</summary>
            public int spatialInvalid;
            /// <summary>Daily moves longer than any speed band allows (a teleport).</summary>
            public int teleports;
            /// <summary>The longest single-day move seen, in world tiles.</summary>
            public int maxDailyMove;
            /// <summary>Field Log entries equal to the one before them, on any contract.</summary>
            public int fieldLogDuplicates;
            /// <summary>Field Log entries on a contract the player did not issue, or on a closed contract.</summary>
            public int fieldLogLeaks;
            /// <summary>Live player contracts holding a Field Log at the end, and their entries.</summary>
            public int fieldLogsLive;
            public int fieldLogEntriesLive;
            /// <summary>Simulated loads (the runtime route cache dropped) and the routes rebuilt afterwards.</summary>
            public int simulatedLoads;

            /// <summary>A contractor moved farther between two observations than its own pace allows in the time it had (unexplained).</summary>
            public int routeBudgetViolations;

            /// <summary>An ended contractor's position changed after it ended.</summary>
            public int endedMoved;

            /// <summary>A Troubled operation Phase 2 declared recovered, whose group Spatial did not put back where it returns to.</summary>
            public int recoveredMismatches;

            /// <summary>An ambient journey with charter ends (charter is operation travel only).</summary>
            public int ambientCharters;

            /// <summary>Charter providers' settlements removed and founded again elsewhere during the run.</summary>
            public int providersChurned;

            /// <summary>Discontinuities with a stated reason (a chartered crossing, a lifecycle reconciliation, re-anchoring); excluded from the pace checks.</summary>
            public int explainedJumps;

            public double avgMoveWork;
            public long maxMoveWork;

            public int Violations => overCapacity + doubleBooked + moneyViolations + duplicateRefunds + lklAboveSecured + (moneyDrift != 0 ? 1 : 0) + (transferDrift != 0 ? 1 : 0)
                + spatialInvalid + teleports + fieldLogDuplicates + fieldLogLeaks;
            public double totalMs;
            public double maxDayMs;

            /// <summary>The scratch world's stores (for a save-size measurement by the caller).</summary>
            public Core.NetworkState state;
            public double avgDayMs;
            public string text;

            public int Count(ContractStatus s)
            {
                int n;
                return byStatus.TryGetValue(s, out n) ? n : 0;
            }
        }

        // ------------------------------------------------------------------ scratch ports

        private sealed class Comms : ICommsAccess
        {
            public bool CanContact(out string reasonKey) { reasonKey = null; return true; }
        }

        private sealed class Payment : IPayment
        {
            public long spent, refunded;
            public bool CanCharge(int amount, out string reasonKey) { reasonKey = null; return true; }
            public bool TryCharge(int amount, out string reasonKey) { reasonKey = null; spent += amount; return true; }
            public bool TryRefund(int amount, out string reasonKey) { reasonKey = null; refunded += amount; return true; }
        }

        private sealed class Catalog : ICatalog
        {
            public readonly Dictionary<string, ItemFacts> items = new Dictionary<string, ItemFacts>();
            public readonly List<ItemFacts> list = new List<ItemFacts>();
            public void Add(ItemFacts f) { if (f != null && !items.ContainsKey(f.defName)) { items[f.defName] = f; list.Add(f); } }
            public bool IsRequestable(string defName, out string reasonKey) { reasonKey = items.ContainsKey(defName ?? "") ? null : "NotInCatalog"; return reasonKey == null; }
            public ItemFacts Facts(string defName) { ItemFacts f; return defName != null && items.TryGetValue(defName, out f) ? f : null; }
            public IList<ItemFacts> ExtraCargoPool(int maxTechLevel) { List<ItemFacts> l = new List<ItemFacts>(); foreach (ItemFacts f in list) if (f.techLevel <= maxTechLevel && !f.unique) l.Add(f); return l; }
            public void MarkUnusable(string defName, string reason) { }
        }

        private sealed class World : IWorldFacts
        {
            public readonly List<FactionFacts> factions = new List<FactionFacts>();
            public List<FactionFacts> LiveFactions() { return factions; }
            public float BaseThreatPoints() { return 600f; }
            public string PickStuff(string thingDefName, int seed) { return null; }
            public int TilesFromPlayerHome(TileRef tile) { return 8; }
            public int TravelTicksFromPlayerHome(TileRef tile) { return Ticks.PerDay * 2; }
            public FactionFacts PlayerFaction() { return new FactionFacts { loadId = 1, name = "Soak Colony", defName = "PlayerColony", isPlayer = true, humanlike = true }; }
        }

        private sealed class Sites : ISiteAdapter
        {
            private int next = 1;
            public GridWorldGraph graph;
            public bool TryFindTile(int seed, int minDist, int maxDist, out TileRef tile) { tile = graph.OnLayerOf(graph.Tile(0, 1), Math.Abs(seed % graph.Count)); return true; }
            public bool TryFindTileNear(TileRef near, int minDist, int maxDist, int seed, out TileRef tile) { return graph.TryFindPassableNear(near, minDist, maxDist, seed, out tile); }
            public MaterializeResult Materialize(Opportunity opp) { return new MaterializeResult { ok = true, site = new WorldObjectRef { id = next++, defName = "Site" }, threatProfileUsed = opp.threat.profileKey }; }
            public bool SiteExists(WorldObjectRef site) { return site != null; }
            public bool SiteHasMap(WorldObjectRef site) { return false; }
            public bool TrySampleRemaining(Opportunity opp, out int remaining) { remaining = 0; return false; }
            public void ReleaseSite(Opportunity opp, bool destroyIfNoMap) { }
        }

        private sealed class Delivery : IDelivery
        {
            public int attempts;
            public int DefaultHomeMapId(out string label) { label = "Home"; return 1; }
            public DeliveryPlan Plan(int preferredMapId, int seed)
            {
                attempts++;
                // One attempt in twenty finds no drop spot: exercises the retry path.
                if ((seed & 0x7fffffff) % 20 == 0) return new DeliveryPlan { failureKey = "NoDropSpot" };
                return new DeliveryPlan { ok = true, mapId = 1, mapLabel = "Home" };
            }
            public DeliveryResult Deliver(DeliveryPlan plan, List<ItemPayload> payload, int seed)
            {
                int n = 0;
                foreach (ItemPayload p in payload) n += p.count;
                return new DeliveryResult { ok = true, mapId = 1, mapLabel = "Home", delivered = n };
            }
        }

        /// <summary>A small spread of generic goods: cheap bulk, weapons, spacer parts, a unique relic.</summary>
        public static List<ItemFacts> SyntheticItems()
        {
            return new List<ItemFacts>
            {
                new ItemFacts { defName = "Soak_Bulk", label = "bulk metal", techLevel = 4, marketValue = 2f, stackLimit = 75, tradeable = true, mineable = true, isResource = true, categoryMedian = 20f },
                new ItemFacts { defName = "Soak_Food", label = "packaged food", techLevel = 4, marketValue = 4f, stackLimit = 50, tradeable = true, craftable = true, isResource = true, categoryMedian = 10f },
                new ItemFacts { defName = "Soak_Meds", label = "medicine", techLevel = 4, marketValue = 18f, stackLimit = 25, tradeable = true, craftable = true, categoryMedian = 20f, recipeInputValue = 12f },
                new ItemFacts { defName = "Soak_Parts", label = "machine parts", techLevel = 4, marketValue = 32f, stackLimit = 25, tradeable = true, craftable = true, categoryMedian = 20f },
                new ItemFacts { defName = "Soak_Rifle", label = "rifle", techLevel = 4, marketValue = 350f, stackLimit = 1, tradeable = true, craftable = true, hasQuality = true, isWeapon = true, categoryMedian = 300f },
                new ItemFacts { defName = "Soak_Armor", label = "armor vest", techLevel = 5, marketValue = 900f, stackLimit = 1, tradeable = true, craftable = true, hasQuality = true, isApparel = true, categoryMedian = 200f },
                new ItemFacts { defName = "Soak_Advanced", label = "advanced parts", techLevel = 5, marketValue = 60f, stackLimit = 25, tradeable = true, categoryMedian = 20f },
                new ItemFacts { defName = "Soak_Core", label = "ultratech core", techLevel = 6, marketValue = 1800f, stackLimit = 1, tradeable = false, categoryMedian = 300f },
                new ItemFacts { defName = "Soak_Relic", label = "strange relic", techLevel = 7, marketValue = 3000f, stackLimit = 1, unique = true, categoryMedian = 300f }
            };
        }

        // ------------------------------------------------------------------ run

        /// <summary>Removes one charter provider's settlement and founds another on free land (the synthetic world only).</summary>
        private static void ChurnProvider(GridWorldGraph graph, NetRng rng, Result res)
        {
            List<int> providers = new List<int>();
            for (int i = 0; i < graph.settlements.Count; i++) if (graph.settlements[i].canProvideCharterTransport) providers.Add(i);
            if (providers.Count == 0) return;
            SettlementFacts gone = graph.settlements[providers[rng.Range(0, providers.Count)]];
            graph.settlements.Remove(gone);
            for (int tries = 0; tries < 40; tries++)
            {
                int x = rng.Range(2, graph.width - 2), y = rng.Range(2, graph.height - 2);
                if (!graph.IsPassable(graph.Tile(x, y))) continue;
                graph.AddSettlement(x, y, gone.factionLoadId, false, true);
                break;
            }
            res.providersChurned++;
        }

        /// <summary>What the daily spatial pass last saw of a contractor.</summary>
        private sealed class Seen
        {
            public int tick;
            public TileRef anchor;
            public int updated;
            public bool ended;
        }

        private static bool SameTile(TileRef a, TileRef b)
        {
            return a != null && b != null && a.tileId == b.tileId && a.layerId == b.layerId;
        }

        /// <summary>
        /// The daily spatial pass: every active contractor has a valid anchor; nobody moved farther than any
        /// speed band allows (no teleport) nor farther than ITS OWN pace allows in the time it had (no route
        /// budget broken) unless a stated reason explains it (a chartered crossing, a lifecycle
        /// reconciliation, re-anchoring); an ended contractor never moves again; ambient movement never
        /// charters; a Troubled group Phase 2 declared recovered is where it returns to; and Field Logs exist
        /// only on the player's live contracts, without repeated lines.
        /// </summary>
        private static void CheckSpatial(DomainContext ctx, Result res, Dictionary<int, Seen> seen, HashSet<int> recoveredChecked)
        {
            int limitPerDay = Domain.Spatial.SpatialPolicy.TilesPerDay(Band.VeryHigh) * 2 + 2;
            foreach (NetworkActor a in ctx.actors.actors)
            {
                if (!ContractorService.IsNpcContractor(a)) continue;
                ContractorSimulation sim = a.Get<ContractorSimulation>();
                SpatialState s = sim.spatial;
                Seen last;
                bool had = seen.TryGetValue(a.id.Value, out last);
                if (a.status != ActorStatus.Active)
                {
                    if (had && last.ended && s.anchor != null && !SameTile(last.anchor, s.anchor)) res.endedMoved++;
                    if (!had || !last.ended) seen[a.id.Value] = new Seen { tick = ctx.Now, anchor = s.anchor?.Copy(), updated = s.lastUpdateTick, ended = true };
                    continue;
                }
                if (!s.IsInitialized || !ctx.graph.IsValid(s.anchor))
                {
                    res.spatialInvalid++;
                    continue;
                }
                if (s.purpose == SpatialPurpose.Ambient && s.bridgeFrom != null) res.ambientCharters++;
                if (had && last.anchor != null && !SameTile(last.anchor, s.anchor))
                {
                    if (ctx.Spatial.LastExplainedJump(a.id.Value) >= last.tick) res.explainedJumps++;
                    else
                    {
                        int d = ctx.graph.ApproxDistance(last.anchor, s.anchor);
                        int days = Math.Max(1, (ctx.Now - last.tick + Ticks.PerDay - 1) / Ticks.PerDay);
                        if (d != int.MaxValue && d / days > res.maxDailyMove) res.maxDailyMove = d / days;
                        if (d == int.MaxValue || d > limitPerDay * days) res.teleports++;
                        // Its own pace: the tiles between the two positions cannot outnumber the steps it
                        // could walk between the ticks each position was reached (plus rounding).
                        int tpt = Domain.Spatial.SpatialPolicy.TicksPerTile(sim.mobility.speedBand);
                        int dt = Math.Max(0, s.lastUpdateTick - last.updated);
                        if (d == int.MaxValue || d > dt / tpt + 1) res.routeBudgetViolations++;
                    }
                }
                seen[a.id.Value] = new Seen { tick = ctx.Now, anchor = s.anchor.Copy(), updated = s.lastUpdateTick };
            }
            foreach (Operation op in ctx.operations.operations)
            {
                if (op.spatial == null || op.spatial.detached || op.outcome == null || op.outcome.troubledKey == null) continue;
                Checkpoint ret = op.Find(Checkpoint.Return);
                if (ret == null || !ret.done || !recoveredChecked.Add(op.id.Value)) continue;
                NetworkActor a = ctx.actors.Get(op.contractor);
                SpatialState s = a?.Get<ContractorSimulation>()?.spatial;
                if (a == null || a.status != ActorStatus.Active || s == null) continue;
                if (s.destination == null && !SameTile(s.anchor, op.spatial.returnTo)) res.recoveredMismatches++;
            }
            ActorId player = ctx.actors.PlayerProxyId;
            foreach (Contract c in ctx.contracts.contracts)
            {
                if (c.fieldLog.Count == 0) continue;
                if (c.parties.issuer != player || c.IsTerminal) res.fieldLogLeaks += c.fieldLog.Count;
                for (int i = 1; i < c.fieldLog.Count; i++) if (c.fieldLog[i].SameAs(c.fieldLog[i - 1])) res.fieldLogDuplicates++;
            }
        }

        /// <summary>The daily invariant pass: capacity, exclusivity, money and Last Known Location cargo.</summary>
        private static void CheckInvariants(DomainContext ctx, Result res, Dictionary<int, long[]> money)
        {
            res.doubleBooked += ctx.Contractors.DoubleBooked();
            foreach (Contract c in ctx.contracts.contracts)
            {
                int refunds = 0, payouts = 0;
                long ch = 0, rf = 0, ti = 0, to = 0;
                foreach (MoneyRecord m in c.ledger)
                {
                    if (m.silver <= 0) res.moneyViolations++;
                    switch (m.direction)
                    {
                        case MoneyDirection.PlayerPaid: ch += m.silver; break;
                        case MoneyDirection.PlayerRefunded:
                            rf += m.silver;
                            if (m.purpose == MoneyPurpose.InsurancePayout) payouts++;
                            else refunds++;
                            break;
                        case MoneyDirection.TransferIn: ti += m.silver; break;
                        case MoneyDirection.TransferOut: to += m.silver; break;
                    }
                }
                if (c.ExternalRefunded() > c.TotalFunding() || c.TotalFunding() < 0) res.moneyViolations++;
                if (refunds > 1 || payouts > 1) res.duplicateRefunds++;
                long[] last;
                bool seen = money.TryGetValue(c.id.Value, out last);
                if (seen && last[4] == 1 && (last[0] != ch || last[1] != rf)) res.moneyViolations++; // closed money never moves again
                money[c.id.Value] = new[] { ch, rf, ti, to, c.IsTerminal ? 1L : 0L };
            }
            foreach (Opportunity o in ctx.opportunities.opportunities)
            {
                if (o.origin != OpportunityOrigin.ConsequenceRule || o.originRef.Kind != EntityKind.Contract) continue;
                Contract c = ctx.contracts.Get(new ContractId(o.originRef.Id));
                Operation op = c == null ? null : ctx.Procurement.CurrentOperation(c);
                if (op?.outcome == null) continue;
                if (o.TargetCount > op.outcome.secured) res.lklAboveSecured++;
            }
        }

        /// <param name="archipelago">Closes the land bridge too: the two halves of the synthetic world have no
        /// ground connection at all, so work across the sea band needs a charter (a stress of ADR-045).</param>
        public static Result Run(int contractors, int contractsPerWeek, int days, int seed, IList<ItemFacts> items = null, bool archipelago = false)
        {
            Result res = new Result { days = days };
            IdAllocator ids = new IdAllocator();
            ManualClock clock = new ManualClock { Now = Ticks.PerDay * 5 };
            NetScheduler scheduler = new NetScheduler(ids, clock) { BudgetJobs = 100000, BudgetMs = 100000 };
            EventJournal journal = new EventJournal();
            DiagnosticsState diag = new DiagnosticsState();
            NetworkEventBus bus = new NetworkEventBus(ids, clock, journal, diag);
            // A charter world: an island with no ground connection, and some high-tech providers.
            GridWorldGraph graph = GridWorldGraph.Default(new[] { 10, 11 }, seed, true);
            if (archipelago) graph.Block(30, 27, 33, 31);
            Catalog catalog = new Catalog();
            foreach (ItemFacts f in items ?? SyntheticItems()) catalog.Add(f);
            World world = new World();
            world.factions.Add(new FactionFacts { loadId = 10, name = "Traders", defName = "Traders", humanlike = true, techLevel = 4, goodwill = 30 });
            world.factions.Add(new FactionFacts { loadId = 11, name = "Raiders", defName = "Raiders", humanlike = true, techLevel = 4, hostileToPlayer = true, goodwill = -80, canGuardSite = true, permanentEnemy = true });
            Payment payment = new Payment();
            HistoryLedger ledger = new HistoryLedger();
            SummaryStore summaries = new SummaryStore();
            DomainContext ctx = new DomainContext
            {
                networkSeed = seed, ids = ids, clock = clock, scheduler = scheduler, bus = bus, diagnostics = diag,
                cast = new WorldCastSnapshot(), actors = new ActorStore(), characters = new CharacterStore(), intel = new IntelStore(),
                opportunities = new OpportunityStore(), summaries = summaries, ledger = ledger, relations = new RelationStore(), knowledge = new KnowledgeStore(),
                contracts = new ContractStore(), operations = new OperationStore(), consequences = new ConsequenceStore(),
                catalog = catalog, comms = new Comms(), payment = payment, world = world, sites = new Sites { graph = graph }, delivery = new Delivery(),
                graph = graph
            };
            ctx.tuning.targetContractorCount = contractors;
            ctx.Actors = new ActorService(ctx);
            ctx.Intel = new IntelService(ctx);
            ctx.Opportunities = new OpportunityService(ctx);
            ctx.Contractors = new ContractorService(ctx);
            ctx.Upkeep = new UpkeepService(ctx);
            ctx.Relations = new RelationService(ctx);
            ctx.Knowledge = new KnowledgeService(ctx);
            ctx.Procurement = new ProcurementService(ctx);
            ctx.Operations = new OperationService(ctx);
            ctx.Consequences = new ConsequenceEngine(ctx);
            ctx.Spatial = new Domain.Spatial.SpatialService(ctx);
            ctx.FieldLog = new FieldLogService(ctx);
            HistoryService history = new HistoryService(ledger, summaries, ctx.actors, ids, clock, seed);
            scheduler.RegisterKind(JobKinds.ContractorUpkeep, ctx.Upkeep.UpkeepJob, true, true);
            scheduler.RegisterKind(JobKinds.PopulationWeekly, ctx.Upkeep.PopulationJobRun, true, true);
            scheduler.RegisterKind(JobKinds.OppSample, ctx.Opportunities.SampleJob, true, true);
            scheduler.RegisterKind(JobKinds.OppWarn, ctx.Opportunities.WarnJob, true, true);
            scheduler.RegisterKind(JobKinds.OppClose, ctx.Opportunities.CloseJob, true, true);
            Core.NetworkRuntime.RegisterPhaseTwoJobs(scheduler, ctx);
            // Retention, as in a game: history sweeps and compaction every quadrum.
            Core.NetworkState state = new Core.NetworkState
            {
                actors = ctx.actors, characters = ctx.characters, intel = ctx.intel, opportunities = ctx.opportunities, contracts = ctx.contracts,
                operations = ctx.operations, consequences = ctx.consequences, relations = ctx.relations, knowledge = ctx.knowledge, history = ledger, summaries = summaries, journal = journal
            };
            Core.CompactionService compaction = new Core.CompactionService(state, scheduler, clock, ctx);
            scheduler.RegisterKind(JobKinds.HistorySweep, job => history.SweepJob(job, scheduler), true, true);
            scheduler.RegisterKind(JobKinds.CompactSweep, compaction.SweepJob, true, true);
            scheduler.Schedule(JobKinds.HistorySweep, clock.Now + JobKinds.SweepPeriod, 0);
            scheduler.Schedule(JobKinds.CompactSweep, clock.Now + JobKinds.SweepPeriod, 0);
            bus.Register(ConsumerOrder.History, history, HistoryService.ConsumedKeys);
            bus.Register(ConsumerOrder.Relationships, ctx.Relations, RelationService.ConsumedKeys);
            bus.Register(ConsumerOrder.Consequences, ctx.Consequences, ConsequenceEngine.ConsumedKeys);
            ProcurementDevOverrides.Clear();

            ctx.Actors.EnsurePlayerProxy(world.PlayerFaction());
            ctx.Actors.EnsureExchange();
            GlobalNetworkRoster roster = new GlobalNetworkRoster();
            CastGenerator.RegenerateGenerated(roster, NamePools.Fallback(), seed, contractors, 6, null, "soak");
            ctx.Actors.ImportCast(roster, NetworkSettings.CurrentVersion, null);
            ctx.Actors.InstantiateFixers();
            ctx.Contractors.InstantiateFromSnapshot();
            ctx.Upkeep.EnsurePopulationJob();
            List<NetworkActor> fixers = new List<NetworkActor>();
            foreach (NetworkActor a in ctx.actors.actors) if (ProcurementService.IsBroker(a)) fixers.Add(a);
            res.contractorsStart = ctx.Upkeep.ActiveContractorCount();
            HashSet<int> startIds = new HashSet<int>();
            foreach (NetworkActor a in ctx.actors.actors) if (ContractorService.IsNpcContractor(a)) startIds.Add(a.id.Value);

            NetRng rng = new NetRng(seed, "soak");
            // Last seen (charged, refunded, transfers in, transfers out) per contract, kept after compaction.
            Dictionary<int, long[]> money = new Dictionary<int, long[]>();
            Dictionary<int, Seen> seen = new Dictionary<int, Seen>();
            HashSet<int> recoveredChecked = new HashSet<int>();
            List<long> moveWork = new List<long>();
            long workBefore = ctx.Spatial.counters.Work;
            List<double> dayMs = new List<double>();
            HashSet<int> reposted = new HashSet<int>();
            long all0 = Stopwatch.GetTimestamp();
            for (int day = 0; day < days; day++)
            {
                long t0 = Stopwatch.GetTimestamp();
                // Post today's contracts.
                int today = contractsPerWeek / 7 + (rng.Chance((contractsPerWeek % 7) / 7f) ? 1 : 0);
                for (int k = 0; k < today && fixers.Count > 0; k++)
                {
                    ItemFacts f = catalog.list[rng.Range(0, catalog.list.Count)];
                    int count = f.stackLimit <= 1 ? rng.RangeInclusive(1, f.unique ? 1 : 3) : rng.RangeInclusive(f.stackLimit / 2, f.stackLimit * 3);
                    ProcurementRequest req = new ProcurementRequest { defName = f.defName, count = count, broker = fixers[rng.Range(0, fixers.Count)].id, premiumContribution = rng.Chance(0.2f) ? rng.RangeInclusive(50, 400) : 0 };
                    if (rng.Chance(0.2f))
                    {
                        List<NetworkActor> cs = new List<NetworkActor>();
                        foreach (NetworkActor a in ctx.actors.actors) if (ContractorService.IsNpcContractor(a) && a.IsActive) cs.Add(a);
                        if (cs.Count > 0)
                        {
                            req.mode = ProcurementMode.Direct;
                            req.invited = cs[rng.Range(0, cs.Count)].id;
                        }
                    }
                    if (ctx.Procurement.Post(req).ok) res.posted++;
                }
                // A day passes, hour by hour, like the tick loop.
                for (int h = 0; h < 24; h++)
                {
                    clock.Now += Ticks.PerHour;
                    scheduler.RunDue();
                }
                // The client: accepts the cheapest quote it can (a bidder busy since quoting is skipped), reposts an unfilled contract once.
                foreach (Contract c in ctx.Procurement.Live())
                {
                    if (c.status == ContractStatus.Bidding && clock.Now >= c.windowCloseTick)
                    {
                        List<Offer> open = ctx.Procurement.OpenOffers(c);
                        open.Sort((x, y) => x.quote.finalPrice != y.quote.finalPrice ? x.quote.finalPrice.CompareTo(y.quote.finalPrice) : x.id.Value.CompareTo(y.id.Value));
                        for (int i = 0; i < open.Count; i++)
                        {
                            if (!open[i].IsOpen) continue;
                            CommandResult accepted = ctx.Procurement.Accept(open[i].id, open[i].quote.insuranceOffer != null && rng.Chance(0.3f));
                            if (accepted.ok) break;
                            if (accepted.reasonKey == "BidderNowCommitted") res.staleRefused++;
                            if (c.status != ContractStatus.Bidding) break;
                        }
                    }
                    else if (c.status == ContractStatus.Unfilled && reposted.Add(c.id.Value))
                    {
                        ctx.Procurement.Repost(c.id, true);
                    }
                }
                dayMs.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                CheckInvariants(ctx, res, money);
                CheckSpatial(ctx, res, seen, recoveredChecked);
                long work = ctx.Spatial.counters.Work;
                moveWork.Add(work - workBefore);
                workBefore = work;
                // A load drops every runtime route cache; journeys continue from the saved anchors.
                if (day % 97 == 96)
                {
                    ctx.Spatial.ClearRouteCache();
                    res.simulatedLoads++;
                }
                // Settlements come and go: every half year a high-tech provider's settlement is gone and
                // another is founded, so committed charters must reconcile from current truth.
                if (day % 180 == 179) ChurnProvider(graph, rng, res);
            }
            res.totalMs = (Stopwatch.GetTimestamp() - all0) * 1000.0 / Stopwatch.Frequency;
            long charged = 0, refunded = 0, transfers = 0;
            foreach (long[] m in money.Values)
            {
                charged += m[0];
                refunded += m[1];
                transfers += m[2] - m[3];
            }
            res.moneyDrift = Math.Abs((payment.spent - payment.refunded) - (charged - refunded));
            res.transferDrift = transfers;
            res.overCapacity = ctx.Contractors.overCapacityCheckouts;
            res.spatial = ctx.Spatial.counters;
            long moveSum = 0;
            foreach (long w in moveWork)
            {
                moveSum += w;
                if (w > res.maxMoveWork) res.maxMoveWork = w;
            }
            res.avgMoveWork = moveWork.Count == 0 ? 0 : moveSum / (double)moveWork.Count;
            foreach (Contract c in ctx.contracts.contracts)
            {
                if (c.IsTerminal || c.fieldLog.Count == 0) continue;
                res.fieldLogsLive++;
                res.fieldLogEntriesLive += c.fieldLog.Count;
            }

            ActorId player = ctx.actors.PlayerProxyId;
            int stuckAfter = clock.Now - 60 * Ticks.PerDay;
            // Archived (compacted) contracts are terminal by construction; count what is still stored.
            foreach (Contract c in ctx.contracts.contracts)
            {
                int n;
                res.byStatus.TryGetValue(c.status, out n);
                res.byStatus[c.status] = n + 1;
                if (c.parties.issuer != player) res.npcIssued++;
                bool waitingOnClient = c.status == ContractStatus.Unfilled;
                if (!c.IsTerminal && !waitingOnClient && c.createdTick < stuckAfter) res.stuck++;
            }
            foreach (Opportunity o in ctx.opportunities.opportunities) if (o.origin == OpportunityOrigin.ConsequenceRule) res.followUps++;
            foreach (NetworkActor a in ctx.actors.actors)
            {
                if (!ContractorService.IsNpcContractor(a)) continue;
                if (a.status != ActorStatus.Active) res.contractorsEnded++;
                if (!startIds.Contains(a.id.Value)) res.newcomers++;
            }
            int missingStart = 0;
            foreach (int id in startIds) if (ctx.actors.Get(new ActorId(id)) == null) missingStart++;
            res.contractorsEnd = ctx.Upkeep.ActiveContractorCount();
            res.relations = ctx.relations.Count;
            res.knowledgeBooks = ctx.knowledge.books.Count;
            res.historyRecords = ledger.records.Count;
            res.journal = journal.entries.Count;
            res.jobs = scheduler.Count;
            res.operations = ctx.operations.Count;
            res.contractsKept = ctx.contracts.Count;
            res.offersKept = ctx.contracts.offers.Count;
            dayMs.Sort();
            double sum = 0;
            foreach (double d in dayMs) sum += d;
            res.avgDayMs = dayMs.Count == 0 ? 0 : sum / dayMs.Count;
            res.maxDayMs = dayMs.Count == 0 ? 0 : dayMs[dayMs.Count - 1];
            double p95 = dayMs.Count == 0 ? 0 : dayMs[Math.Min(dayMs.Count - 1, (int)(dayMs.Count * 0.95))];

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[TheNetwork] Phase 2 soak harness (synthetic, in memory; never touches the save)");
            sb.AppendLine("  " + days + " simulated days, " + res.contractorsStart + " contractors at start, " + fixers.Count + " Fixers, " + res.posted + " contracts posted (" + contractsPerWeek + "/week)");
            sb.Append("  statuses:");
            foreach (KeyValuePair<ContractStatus, int> kv in res.byStatus) sb.Append(" " + kv.Key + "=" + kv.Value);
            sb.AppendLine();
            sb.AppendLine("  stuck (non-terminal after 60 days, not waiting on the client): " + res.stuck + "; NPC-issued: " + res.npcIssued + "; last known locations: " + res.followUps);
            sb.AppendLine("  contractors: " + res.contractorsEnd + " active at end, " + res.contractorsEnded + " ended (death/no successor), " + res.newcomers + " newcomers, " + missingStart + " starting actors deleted");
            sb.AppendLine("  stores after retention (history sweeps, compaction a year after closing): " + ctx.contracts.Count + " contracts, " + ctx.contracts.offers.Count + " offers, " + res.operations + " operations, " + res.relations + " relation edges, " + res.knowledgeBooks + " knowledge books, " + res.historyRecords + " history records, " + res.journal + " journal entries, " + res.jobs + " scheduled jobs");
            sb.AppendLine("  silver: spent " + payment.spent + ", refunded " + payment.refunded);
            sb.AppendLine("  invariants (all must be 0): commitments above capacity " + res.overCapacity + ", named people on two live jobs " + res.doubleBooked
                + ", bad money records " + res.moneyViolations + ", duplicate refunds/payouts " + res.duplicateRefunds + ", LKL cargo above secured " + res.lklAboveSecured
                + ", money drift " + res.moneyDrift + ", transfer drift " + res.transferDrift);
            sb.AppendLine("  stale quotes refused at acceptance (bidder at capacity since quoting; another quote was tried): " + res.staleRefused);
            sb.AppendLine("  spatial: " + res.spatial);
            sb.AppendLine("  spatial invariants (all must be 0): contractors without a valid anchor " + res.spatialInvalid + ", teleports " + res.teleports + " (longest daily move " + res.maxDailyMove + " tiles)"
                + ", route-budget violations " + res.routeBudgetViolations + ", ended contractors that moved " + res.endedMoved + ", recovered operations disagreeing with Phase 2 " + res.recoveredMismatches
                + ", ambient charters " + res.ambientCharters + ", Field Log duplicates " + res.fieldLogDuplicates + ", Field Log leaks " + res.fieldLogLeaks
                + " (explained discontinuities, not counted: " + res.explainedJumps + "; provider settlements replaced: " + res.providersChurned + ")");
            sb.AppendLine("  movement work per day: avg " + res.avgMoveWork.ToString("0.0") + ", max " + res.maxMoveWork + " units; " + res.simulatedLoads + " simulated loads (route caches dropped); live Field Logs " + res.fieldLogsLive + " with " + res.fieldLogEntriesLive + " entries");
            sb.AppendLine("  time per simulated day: avg " + res.avgDayMs.ToString("0.00") + " ms, p95 " + p95.ToString("0.00") + " ms, max " + res.maxDayMs.ToString("0.00") + " ms (total " + res.totalMs.ToString("0") + " ms)");
            res.text = sb.ToString();
            res.state = state;
            ProcurementDevOverrides.Clear();
            return res;
        }
    }
}
