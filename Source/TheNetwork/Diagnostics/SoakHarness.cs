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
            public bool TryFindTile(int seed, int minDist, int maxDist, out TileRef tile) { tile = new TileRef { tileId = Math.Abs(seed % 50000), layerDef = "Surface" }; return true; }
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

        public static Result Run(int contractors, int contractsPerWeek, int days, int seed, IList<ItemFacts> items = null)
        {
            Result res = new Result { days = days };
            IdAllocator ids = new IdAllocator();
            ManualClock clock = new ManualClock { Now = Ticks.PerDay * 5 };
            NetScheduler scheduler = new NetScheduler(ids, clock) { BudgetJobs = 100000, BudgetMs = 100000 };
            EventJournal journal = new EventJournal();
            DiagnosticsState diag = new DiagnosticsState();
            NetworkEventBus bus = new NetworkEventBus(ids, clock, journal, diag);
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
                catalog = catalog, comms = new Comms(), payment = payment, world = world, sites = new Sites(), delivery = new Delivery()
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
                // The client: accepts the cheapest open quote, reposts an unfilled contract once.
                foreach (Contract c in ctx.Procurement.Live())
                {
                    if (c.status == ContractStatus.Bidding && clock.Now >= c.windowCloseTick)
                    {
                        Offer best = null;
                        foreach (Offer o in ctx.Procurement.OpenOffers(c)) if (best == null || o.quote.finalPrice < best.quote.finalPrice) best = o;
                        if (best != null) ctx.Procurement.Accept(best.id, best.quote.insuranceOffer != null && rng.Chance(0.3f));
                    }
                    else if (c.status == ContractStatus.Unfilled && reposted.Add(c.id.Value))
                    {
                        ctx.Procurement.Repost(c.id, true);
                    }
                }
                dayMs.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            }
            res.totalMs = (Stopwatch.GetTimestamp() - all0) * 1000.0 / Stopwatch.Frequency;

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
            sb.AppendLine("  time per simulated day: avg " + res.avgDayMs.ToString("0.00") + " ms, p95 " + p95.ToString("0.00") + " ms, max " + res.maxDayMs.ToString("0.00") + " ms (total " + res.totalMs.ToString("0") + " ms)");
            res.text = sb.ToString();
            res.state = state;
            ProcurementDevOverrides.Clear();
            return res;
        }
    }
}
