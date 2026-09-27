using System;
using System.Collections.Generic;
using System.Reflection;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Consequences;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Knowledge;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Domain.Operations;
using TheNetwork.Domain.Ports;
using TheNetwork.History;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;
using TheNetwork.Settings;
using Verse;

namespace TheNetwork.Tests
{
    /// <summary>
    /// Regressions for the Phase 2 final correction pass: named-character exclusivity, job capacity at
    /// acceptance, Last Known Location cargo, ID repair, upkeep job repair, the Decline comms gate and
    /// knowledge that only survivors carry home.
    /// </summary>
    public static class CorrectionTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("Exclusivity.NamedCharacterInOneLiveOperation", Exclusivity));
            t.Add(new KeyValuePair<string, Action>("Exclusivity.GoneCharactersNeverReappear", GoneNeverReappear));
            t.Add(new KeyValuePair<string, Action>("Exclusivity.SurvivesSaveLoad", ExclusivitySaveLoad));
            t.Add(new KeyValuePair<string, Action>("Exclusivity.TwoContractsShareNoOne", ExclusivityContracts));
            t.Add(new KeyValuePair<string, Action>("Capacity.StaleOffersRespectJobCapacity", CapacityStaleOffers));
            t.Add(new KeyValuePair<string, Action>("Capacity.FreedWhenTheJobEnds", CapacityFreed));
            t.Add(new KeyValuePair<string, Action>("Consequence.LastKnownLocationBoundedBySecured", LklBounded));
            t.Add(new KeyValuePair<string, Action>("Consequence.LastKnownLocationSameAfterReload", LklReload));
            t.Add(new KeyValuePair<string, Action>("Ids.RepairCoversEveryEntityKind", IdsRepair));
            t.Add(new KeyValuePair<string, Action>("Upkeep.MissingJobRecreated", UpkeepRepair));
            t.Add(new KeyValuePair<string, Action>("Comms.DeclineRequiresConsole", DeclineComms));
            t.Add(new KeyValuePair<string, Action>("Knowledge.OnlySurvivorsReport", SurvivorRule));
            t.Add(new KeyValuePair<string, Action>("Knowledge.DisasterNeedsSurvivors", SurvivorsIntegration));
        }

        // ================================================================== helpers

        /// <summary>A reliable company resized to exactly the job capacity asked for (six able people per job).</summary>
        private static NetworkActor Org(TestNet n, int capacity)
        {
            NetworkActor a = ProcurementTests.Reliable(n, ContractorForm.Company);
            OrganizationProfile org = a.Get<OrganizationProfile>();
            org.tiers.Clear();
            org.tiers.Add(new TierCount(Tier.Regular, Math.Max(1, 6 * capacity - org.knownMembers.Count)));
            a.Get<ContractorSimulation>().MarkDirty();
            T.Eq(capacity, ContractorService.JobCapacity(a), "job capacity " + capacity);
            return a;
        }

        private static Operation NewOp(TestNet n, NetworkActor a, float danger, out ForceCommitment f)
        {
            Operation op = new Operation { id = new OperationId(n.ids.NextId()), contractor = a.id, contractorName = a.name.Display, startedTick = n.clock.Now };
            n.ctx.operations.Add(op);
            f = n.ctx.Contractors.Checkout(a, op.id, danger);
            op.forces = f.forces;
            op.characters = f.characters;
            return op;
        }

        private static bool Overlap(List<CharacterId> a, List<CharacterId> b)
        {
            for (int i = 0; i < a.Count; i++) if (b.Contains(a[i])) return true;
            return false;
        }

        private static NetworkState StateOf(TestNet n)
        {
            return new NetworkState
            {
                actors = n.ctx.actors, characters = n.ctx.characters, knowledge = n.ctx.knowledge, contracts = n.ctx.contracts,
                operations = n.ctx.operations, consequences = n.ctx.consequences
            };
        }

        /// <summary>Saves the TestNet's stores through the real Scribe and loads them back.</summary>
        private static NetworkState SaveLoad(TestNet n)
        {
            string path = PersistenceTests.SaveState(StateOf(n), 2);
            NetworkState loaded = new NetworkState();
            Scribe.loader.InitLoading(path);
            try
            {
                List<string> failures = new List<string>();
                loaded.ExposeStores(failures);
                T.Eq(0, failures.Count, "no store failed (" + string.Join("; ", failures.ToArray()) + ")");
            }
            finally
            {
                Scribe.loader.FinalizeLoading();
            }
            loaded.RebuildIndexes();
            System.IO.File.Delete(path);
            return loaded;
        }

        private static void Swap(TestNet n, NetworkState s)
        {
            n.ctx.actors = s.actors;
            n.ctx.characters = s.characters;
            n.ctx.knowledge = s.knowledge;
            n.ctx.contracts = s.contracts;
            n.ctx.operations = s.operations;
            n.ctx.consequences = s.consequences;
        }

        // ================================================================== named-character exclusivity

        private static void Exclusivity()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Org(n, 3);
            OrganizationProfile org = a.Get<OrganizationProfile>();
            T.Check(org.lieutenants.Count >= 1 && org.knownMembers.Count > org.lieutenants.Count + 1, "a leader, lieutenants and other named members");

            ForceCommitment f1, f2, f3, f4;
            Operation op1 = NewOp(n, a, 0.3f, out f1);
            foreach (CharacterId lt in org.lieutenants) T.Check(f1.characters.Contains(lt), "a lieutenant goes on the first job");
            T.Check(!f1.characters.Contains(org.leader), "the leader stays home from ordinary work");
            Operation op2 = NewOp(n, a, 0.9f, out f2);
            T.Check(f2.characters.Contains(org.leader), "the leader may go on another job");
            T.Check(!Overlap(f1.characters, f2.characters), "no lieutenant is selected twice");
            T.Check(f2.characters.Count > 1, "other named people may be selected");
            Operation op3 = NewOp(n, a, 0.9f, out f3);
            T.Eq(0, f3.characters.Count, "every named person is already out: none selected again");
            T.Check(f3.forces.Count > 0 && f3.Headcount > 0, "the third job gets generic force only");
            T.Eq(3, a.Get<ContractorSimulation>().commitments.Count, "three jobs at capacity three");
            T.Eq(0, n.ctx.Contractors.DoubleBooked(), "nobody on two live operations");
            T.Eq(0, n.ctx.Contractors.overCapacityCheckouts, "no checkout past capacity");

            n.ctx.Contractors.Return(a, op1.id, op1.Commitment(), null);
            op1.phase = OpPhase.Done;
            op1.outcomeApplied = true;
            Operation op4 = NewOp(n, a, 0.9f, out f4);
            foreach (CharacterId lt in org.lieutenants) T.Check(f4.characters.Contains(lt), "a lieutenant home again is eligible again");
            T.Check(!f4.characters.Contains(org.leader), "the leader is still out on the second job");
            T.Check(!Overlap(f4.characters, f2.characters), "still nobody on two live jobs");
            T.Eq(0, n.ctx.Contractors.DoubleBooked(), "DoubleBooked stays 0");
        }

        private static void GoneNeverReappear()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Org(n, 3);
            OrganizationProfile org = a.Get<OrganizationProfile>();
            List<CharacterId> gone = new List<CharacterId>();
            CharacterStatus[] fates = { CharacterStatus.Dead, CharacterStatus.Captured, CharacterStatus.Missing };
            int k = 0;
            foreach (CharacterId id in org.knownMembers)
            {
                if (id == org.leader || k >= fates.Length) continue;
                n.ctx.characters.Get(id).status = fates[k++];
                gone.Add(id);
            }
            T.Check(gone.Count >= 3, "a killed, a captured and a missing member");
            for (int i = 0; i < 3; i++)
            {
                ForceCommitment f;
                Operation op = NewOp(n, a, 0.95f, out f);
                T.Check(!Overlap(f.characters, gone), "killed, captured or missing people are never selected (job " + (i + 1) + ")");
                n.ctx.Contractors.Return(a, op.id, op.Commitment(), null);
                op.phase = OpPhase.Done;
                op.outcomeApplied = true;
            }
        }

        private static void ExclusivitySaveLoad()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor a = Org(n, 3);
            ForceCommitment f1;
            NewOp(n, a, 0.95f, out f1);
            T.Check(f1.characters.Count >= 3, "the first job takes the named people");
            NetworkState loaded = SaveLoad(n);
            Swap(n, loaded);
            NetworkActor la = n.ctx.actors.Get(a.id);
            T.Eq(1, la.Get<ContractorSimulation>().commitments.Count, "the commitment survives");
            ForceCommitment f2;
            NewOp(n, la, 0.95f, out f2);
            T.Check(!Overlap(f1.characters, f2.characters), "after loading, the people out on the first job are still out");
            T.Eq(0, n.ctx.Contractors.DoubleBooked(), "no double booking after load");
        }

        private static void ExclusivityContracts()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor a = Org(n, 2);
            Contract c1 = ProcurementTests.Awarded(n, fixer, a, "TestSteel", 200);
            Contract c2 = ProcurementTests.Awarded(n, fixer, a, "TestSteel", 200);
            Operation o1 = ProcurementTests.Op(n, c1), o2 = ProcurementTests.Op(n, c2);
            T.Check(o1 != null && o2 != null, "two jobs under way");
            if (o1 == null || o2 == null) return;
            T.Check(!Overlap(o1.characters, o2.characters), "two live jobs share no named person");
            T.Eq(0, n.ctx.Contractors.DoubleBooked(), "DoubleBooked 0");
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            ProcurementTests.RunUntil(n, () => c1.IsTerminal && c2.IsTerminal);
            T.Check(c1.IsTerminal && c2.IsTerminal, "both finish");
            T.Eq(0, a.Get<ContractorSimulation>().commitments.Count, "everyone home");
        }

        // ================================================================== job capacity at acceptance

        private static void CapacityStaleOffers()
        {
            for (int cap = 1; cap <= 3; cap++)
            {
                TestNet n = ProcurementTests.World(0);
                NetworkActor fixer = ProcurementTests.Fixer(n);
                NetworkActor a = Org(n, cap);
                ContractorSimulation sim = a.Get<ContractorSimulation>();
                List<Contract> cs = new List<Contract>();
                for (int i = 0; i <= cap; i++) cs.Add(ProcurementTests.Post(n, fixer, "TestSteel", 200, a));
                List<Offer> offers = new List<Offer>();
                foreach (Contract c in cs)
                {
                    Offer o = ProcurementTests.Bid(n, c);
                    T.Check(o != null, "cap " + cap + ": it quoted while it had room (" + ProcurementTests.Refusals(c) + ")");
                    offers.Add(o);
                }
                if (offers.Contains(null)) continue;
                for (int i = 0; i < cap; i++)
                {
                    CommandResult ok = n.ctx.Procurement.Accept(offers[i].id, false);
                    T.Check(ok.ok, "cap " + cap + ": acceptance " + (i + 1) + " (" + ok + ")");
                }
                T.Eq(cap, sim.commitments.Count, "cap " + cap + ": at capacity");
                int charged = n.pay.charged, ops = n.ctx.operations.operations.Count;
                Contract last = cs[cap];
                T.Eq("BidderNowCommitted", n.ctx.Procurement.CanAccept(offers[cap].id, false).reasonKey, "cap " + cap + ": CanAccept names the reason");
                CommandResult r = n.ctx.Procurement.Accept(offers[cap].id, false);
                T.Eq("BidderNowCommitted", r.reasonKey, "cap " + cap + ": acceptance " + (cap + 1) + " refused");
                T.Eq(charged, n.pay.charged, "cap " + cap + ": nothing charged");
                T.Eq(ops, n.ctx.operations.operations.Count, "cap " + cap + ": no operation started");
                T.Check(last.status == ContractStatus.Bidding && last.operations.Count == 0 && offers[cap].IsOpen, "cap " + cap + ": the job still waits; the offer is not consumed");
                T.Eq(cap, sim.commitments.Count, "cap " + cap + ": commitments never exceed capacity");
                T.Eq(0, n.ctx.Contractors.overCapacityCheckouts, "cap " + cap + ": no checkout past capacity");

                n.ctx.Procurement.Void(cs[0], Causes.DefMissing);
                T.Eq(cap - 1, sim.commitments.Count, "cap " + cap + ": one job ended");
                CommandResult again = n.ctx.Procurement.Accept(offers[cap].id, false);
                T.Check(again.ok, "cap " + cap + ": with room again the same offer can be accepted (" + again + ")");
                T.Eq(cap, sim.commitments.Count, "cap " + cap + ": back at capacity, not above");
            }

            // A contractor with room left may take more work, and its quote says so truthfully.
            TestNet m = ProcurementTests.World(0);
            NetworkActor f2 = ProcurementTests.Fixer(m);
            NetworkActor b = Org(m, 2);
            ProcurementTests.Awarded(m, f2, b, "TestSteel", 200);
            Contract more = ProcurementTests.Post(m, f2, "TestSteel", 200, b);
            Offer extra = ProcurementTests.Bid(m, more);
            T.Check(extra != null, "room for one more job: it quotes");
            if (extra == null) return;
            T.Check(extra.conditions.Contains("AlongsideOtherWork") && !extra.conditions.Contains("AfterCurrentJob"), "the condition says it runs alongside other work (no queue is promised)");
            T.Check(m.ctx.Procurement.Accept(extra.id, false).ok, "and it can be accepted");
            Contract full = ProcurementTests.Post(m, f2, "TestSteel", 200, b);
            m.AdvanceTo(full.windowCloseTick);
            T.Eq(0, m.ctx.Procurement.OpenOffers(full).Count, "at capacity it does not quote");
        }

        private static void CapacityFreed()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            NetworkActor a = Org(n, 1);
            Contract c1 = ProcurementTests.Post(n, fixer, "TestSteel", 200, a);
            Contract c2 = ProcurementTests.Post(n, fixer, "TestSteel", 200, a);
            Offer o1 = ProcurementTests.Bid(n, c1), o2 = ProcurementTests.Bid(n, c2);
            T.Check(o1 != null && o2 != null, "two quotes");
            if (o1 == null || o2 == null) return;
            T.Check(n.ctx.Procurement.Accept(o1.id, false).ok, "first accepted");
            T.Eq("BidderNowCommitted", n.ctx.Procurement.Accept(o2.id, false).reasonKey, "second refused at capacity 1");
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            ProcurementTests.RunUntil(n, () => c1.IsTerminal);
            T.Eq(ContractStatus.Fulfilled, c1.status, "the first job finished");
            T.Eq(0, a.Get<ContractorSimulation>().commitments.Count, "capacity is free again");
            Contract c3 = ProcurementTests.Post(n, fixer, "TestSteel", 200, a);
            Offer o3 = ProcurementTests.Bid(n, c3);
            T.Check(o3 != null && n.ctx.Procurement.Accept(o3.id, false).ok, "a new job can be accepted after the operation finished");
        }

        // ================================================================== Last Known Location cargo

        private static readonly string[] Stuffs = { "TestStuffA", "TestStuffB", "TestStuffC", "TestStuffD", "TestStuffE" };

        private static TestNet LklWorld(out NetworkActor fixer)
        {
            TestNet n = ProcurementTests.World(0);
            n.world.stuffPicker = (def, seed) => def == "TestBlade" ? Stuffs[(seed & 0x7fffffff) % Stuffs.Length] : null;
            n.world.factions.Add(FakeWorld.Faction(21, "Blood Hawks", "ludeon.rimworld", 4, true, true, true));
            n.cat.Add(new ItemFacts { defName = "TestBlade", label = "test blade", packageId = "ludeon.rimworld", isLudeon = true, techLevel = 3, marketValue = 90f, stackLimit = 1, tradeable = true, craftable = true, hasQuality = true, madeFromStuff = true, isWeapon = true });
            fixer = ProcurementTests.Fixer(n);
            return n;
        }

        /// <summary>A job that comes back missing with a forced secured count; the rule is forced to fire.</summary>
        private static Contract MissingWith(TestNet n, NetworkActor fixer, int secured, string def = "TestBlade", int count = 20)
        {
            Contract c = ProcurementTests.Awarded(n, fixer, ProcurementTests.Reliable(n), def, count);
            ProcurementDevOverrides.forceBand = OutcomeBand.Failure;
            ProcurementDevOverrides.forceSecured = secured;
            ProcurementDevOverrides.forceTroubled = SubStatus.Missing;
            ProcurementDevOverrides.forceFollowUp = true;
            // Small steps: stop in the window between the rule firing and its generation job (two hours).
            int end = n.clock.Now + 40 * Ticks.PerDay;
            while (ProcurementTests.Op(n, c)?.outcome == null && !c.IsTerminal && n.clock.Now < end) n.Advance(Ticks.PerHour / 2);
            return c;
        }

        private static Opportunity FollowUpOf(TestNet n, Contract c)
        {
            foreach (Opportunity o in n.ctx.opportunities.opportunities) if (o.origin == OpportunityOrigin.ConsequenceRule && o.originRef.Equals(c.id.Ref)) return o;
            return null;
        }

        private static void LklBounded()
        {
            NetworkActor fixer;
            TestNet n = LklWorld(out fixer);
            Contract none = MissingWith(n, fixer, 0);
            T.Eq(0, ProcurementTests.Op(n, none).outcome.secured, "nothing secured");
            ProcurementTests.RunUntil(n, () => FollowUpOf(n, none) != null, 2);
            Opportunity o0 = FollowUpOf(n, none);
            T.Check(o0 != null, "a last known location with nothing secured");
            if (o0 != null)
            {
                T.Eq(0, o0.TargetCount, "secured 0 → none of the goods at the site");
                foreach (object p in o0.payload)
                {
                    ItemPayload ip = p as ItemPayload;
                    if (ip != null && ip.role != PayloadRole.Target) T.Check(ip.thing.defName != "TestBlade", "extra cargo is unrelated loot, never the goods");
                }
            }

            NetworkActor f2;
            TestNet m = LklWorld(out f2);
            Contract ten = MissingWith(m, f2, 10);
            Operation op = ProcurementTests.Op(m, ten);
            T.Eq(10, op.outcome.secured, "ten secured");
            ItemPayload committed = op.outcome.securedPayload.Count > 0 ? op.outcome.securedPayload[0] : null;
            T.Check(committed != null && committed.stuff != null && committed.count == 10, "the committed payload has a stuff and a quality");
            ProcurementTests.RunUntil(m, () => FollowUpOf(m, ten) != null, 2);
            Opportunity o10 = FollowUpOf(m, ten);
            T.Check(o10 != null, "a last known location with ten secured");
            if (o10 == null || committed == null) return;
            ItemPayload target = o10.Target;
            T.Check(target.count >= 0 && target.count <= 10, "secured 10 → between 0 and 10 at the site (" + target.count + ")");
            T.Eq("TestBlade", target.thing.defName, "the same ThingDef");
            T.Eq(committed.stuff.defName, target.stuff?.defName, "the committed stuff, not rerolled");
            T.Eq(committed.qualityBand, target.qualityBand, "the committed quality, not rerolled");
            int extras = 0;
            foreach (object p in o10.payload)
            {
                ItemPayload ip = p as ItemPayload;
                if (ip == null || ip.role == PayloadRole.Target) continue;
                extras++;
                T.Check(ip.thing.defName != "TestBlade", "extras never add more of the goods");
            }
            T.Check(extras >= 0, "unrelated extra loot is allowed");
        }

        private static void LklReload()
        {
            NetworkActor fixer;
            TestNet n = LklWorld(out fixer);
            Contract c = MissingWith(n, fixer, 8);
            PendingFollowUp p = n.ctx.consequences.Find(c.id);
            T.Check(p != null, "the rule fired; generation is pending");
            if (p == null) return;
            int seed = p.seed, lost = p.lostCount, depth = p.depth;
            string flavor = p.flavorKey;
            T.Check(lost >= 0 && lost <= 8, "the pending count is bounded by secured (" + lost + ")");

            NetworkState loaded = SaveLoad(n);
            Swap(n, loaded);
            PendingFollowUp lp = n.ctx.consequences.Find(c.id);
            T.Check(lp != null && lp.seed == seed && lp.lostCount == lost && lp.depth == depth, "the pending rule survives save/load exactly");
            Contract lc = n.ctx.contracts.Get(c.id);
            ProcurementTests.RunUntil(n, () => FollowUpOf(n, lc) != null, 2);
            Opportunity first = FollowUpOf(n, lc);
            T.Check(first != null, "generated after loading");
            if (first == null) return;
            T.Eq(0, n.ctx.consequences.pending.Count, "generated once; nothing left pending");

            // Generation is a pure function of the saved inputs: the same inputs give the same content.
            Operation op = n.ctx.operations.Get(lp.operation);
            string failure;
            Opportunity again = n.ctx.Opportunities.GenerateFollowUp(n.cat.Facts("TestBlade"), lost, op.outcome.securedPayload[0], seed, lc.id.Ref, depth, null, out failure);
            T.Check(again != null, "regenerated for comparison (" + failure + ")");
            if (again == null) return;
            T.Eq(Describe(first), Describe(again), "identical content: def, stuff, quality, count and extras");
        }

        private static string Describe(Opportunity o)
        {
            List<string> parts = new List<string>();
            foreach (object x in o.payload)
            {
                ItemPayload p = x as ItemPayload;
                if (p == null) continue;
                parts.Add(p.role + ":" + p.thing?.defName + "/" + p.stuff?.defName + "/" + p.qualityBand + "x" + p.count);
            }
            return string.Join(", ", parts.ToArray());
        }

        // ================================================================== ID repair

        private static void IdsRepair()
        {
            Dictionary<string, Action<NetworkState>> kinds = new Dictionary<string, Action<NetworkState>>
            {
                { "actor", s => s.actors.actors.Add(new NetworkActor { id = new ActorId(900) }) },
                { "character", s => s.characters.characters.Add(new KnownCharacter { id = new CharacterId(900) }) },
                { "intel request", s => s.intel.requests.Add(new IntelRequest { id = new IntelRequestId(900) }) },
                { "lead", s => s.intel.leads.Add(new Lead { id = new LeadId(900) }) },
                { "opportunity", s => s.opportunities.opportunities.Add(new Opportunity { id = new OpportunityId(900) }) },
                { "history record", s => s.history.records.Add(new HistoryRecord { id = new HistoryRecordId(900) }) },
                { "contract", s => s.contracts.contracts.Add(new Contract { id = new ContractId(900) }) },
                { "offer", s => s.contracts.offers.Add(new Offer { id = new OfferId(900) }) },
                { "operation", s => s.operations.operations.Add(new Operation { id = new OperationId(900) }) }
            };
            foreach (KeyValuePair<string, Action<NetworkState>> kv in kinds)
            {
                NetworkState s = new NetworkState();
                kv.Value(s);
                IdAllocator ids = new IdAllocator();
                T.Check(s.RepairIdCounters(ids), kv.Key + ": a behind counter is detected");
                T.Eq(901, ids.PeekNextId, kv.Key + ": the counter is raised above the id in use");
                T.Check(!s.RepairIdCounters(ids), kv.Key + ": idempotent");
            }

            // Every persisted type that owns a typed id is covered above.
            HashSet<Type> covered = new HashSet<Type> { typeof(NetworkActor), typeof(KnownCharacter), typeof(IntelRequest), typeof(Lead), typeof(Opportunity), typeof(HistoryRecord), typeof(Contract), typeof(Offer), typeof(Operation) };
            Type[] idTypes = { typeof(ActorId), typeof(CharacterId), typeof(IntelRequestId), typeof(LeadId), typeof(OpportunityId), typeof(HistoryRecordId), typeof(ContractId), typeof(OfferId), typeof(OperationId) };
            foreach (Type t in typeof(NetworkState).Assembly.GetTypes())
            {
                if (!typeof(IExposable).IsAssignableFrom(t)) continue;
                FieldInfo f = t.GetField("id", BindingFlags.Instance | BindingFlags.Public);
                if (f == null || Array.IndexOf(idTypes, f.FieldType) < 0) continue;
                T.Check(covered.Contains(t), t.Name + " owns a typed id and is covered by MaxEntityId");
            }

            // A real world with contracts, offers and operations and a deliberately behind allocator.
            TestNet n = ProcurementTests.World(0);
            Contract c = ProcurementTests.Awarded(n, ProcurementTests.Fixer(n), ProcurementTests.Reliable(n));
            NetworkState st = StateOf(n);
            IdAllocator behind = new IdAllocator();
            T.Check(st.RepairIdCounters(behind), "the behind allocator is repaired");
            int max = 0;
            foreach (Contract x in n.ctx.contracts.contracts) max = Math.Max(max, x.id.Value);
            foreach (Offer x in n.ctx.contracts.offers) max = Math.Max(max, x.id.Value);
            foreach (Operation x in n.ctx.operations.operations) max = Math.Max(max, x.id.Value);
            T.Check(behind.PeekNextId > max && max > 0, "above every contract, offer and operation id (" + max + ")");
            T.Check(c.id.IsValid, "contract awarded");
        }

        // ================================================================== upkeep job repair

        private static void UpkeepRepair()
        {
            TestNet n = ContractorTests.WorldWithCast(6);
            List<NetworkActor> cs = ContractorTests.Contractors(n);
            int now = n.clock.Now;
            string kind = ContractorService.UpkeepJob;
            NetworkActor valid = cs[0], late = cs[1], bad = cs[2], far = cs[3], ended = cs[4], quarantined = cs[5];
            valid.Get<ContractorSimulation>().nextUpkeepTick = now + Ticks.PerDay / 2;
            late.Get<ContractorSimulation>().nextUpkeepTick = now - Ticks.PerHour * 3;
            bad.Get<ContractorSimulation>().nextUpkeepTick = -1;
            far.Get<ContractorSimulation>().nextUpkeepTick = now + Ticks.PerDay * 40;
            n.ctx.Contractors.EndActor(ended, "Test");
            quarantined.quarantinedReason = "Test";
            foreach (NetworkActor a in cs) n.scheduler.Cancel(kind, a.id.Value);

            List<string> findings = new List<string>();
            T.Eq(4, n.ctx.Upkeep.EnsureUpkeepJobs(findings), "four live contractors lost their job (" + string.Join("; ", findings.ToArray()) + ")");
            T.Eq(now + Ticks.PerDay / 2, n.scheduler.Find(kind, valid.id.Value)?.dueTick ?? -1, "a valid saved due tick is kept");
            int lateDue = n.scheduler.Find(kind, late.id.Value)?.dueTick ?? -1;
            T.Check(lateDue > now && lateDue <= now + Ticks.PerHour, "past due: runs shortly (" + (lateDue - now) + " ticks)");
            T.Eq(NetScheduler.StaggeredDue(now + 1, bad.seed, kind, Ticks.PerDay), n.scheduler.Find(kind, bad.id.Value)?.dueTick ?? -1, "no valid due tick: the normal stagger");
            T.Eq(NetScheduler.StaggeredDue(now + 1, far.seed, kind, Ticks.PerDay), n.scheduler.Find(kind, far.id.Value)?.dueTick ?? -1, "an impossible due tick: the normal stagger");
            T.Check(!n.scheduler.Has(kind, ended.id.Value), "never for an ended contractor");
            T.Check(!n.scheduler.Has(kind, quarantined.id.Value), "never for a quarantined contractor");
            T.Eq(0, n.ctx.Upkeep.EnsureUpkeepJobs(null), "idempotent");
            int jobs = 0;
            foreach (ScheduledJob j in n.scheduler.AllJobs) if (j.kind == kind && j.target == valid.id.Value) jobs++;
            T.Eq(1, jobs, "no duplicate job");
            T.Eq(lateDue, late.Get<ContractorSimulation>().nextUpkeepTick, "the saved due tick follows the job");

            // Load reconciliation: the saved due tick survives, and a job lost on the way is rebuilt from it.
            int saved = valid.Get<ContractorSimulation>().nextUpkeepTick;
            NetworkState loaded = SaveLoad(n);
            Swap(n, loaded);
            n.scheduler.Cancel(kind, valid.id.Value);
            T.Eq(1, n.ctx.Upkeep.EnsureUpkeepJobs(null), "after loading, the missing job is recreated");
            T.Eq(saved, n.scheduler.Find(kind, valid.id.Value)?.dueTick ?? -1, "at the saved due tick");
            n.Advance(Ticks.PerDay);
            T.Check(n.ctx.actors.Get(valid.id).Get<ContractorSimulation>().lastUpkeepTick > now, "and upkeep runs again");
        }

        // ================================================================== Decline and the Comms Console

        private static void DeclineComms()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            Contract c = ProcurementTests.Post(n, fixer, "TestSteel", 200, ProcurementTests.Reliable(n));
            Offer o = ProcurementTests.Bid(n, c);
            T.Check(o != null, "an offer");
            if (o == null) return;

            n.comms.usable = false;
            n.comms.reason = "NoUsableCommsConsole";
            T.Eq("NoUsableCommsConsole", n.ctx.Procurement.CanDecline(o.id).reasonKey, "no console: cannot decline");
            T.Eq("NoUsableCommsConsole", n.ctx.Procurement.Decline(o.id).reasonKey, "Decline itself is gated");
            n.comms.reason = "CommsConsoleUnpowered";
            T.Eq("CommsConsoleUnpowered", n.ctx.Procurement.CanDecline(o.id).reasonKey, "unpowered console: cannot decline");
            T.Check(o.IsOpen, "the offer is untouched");
            T.Eq(1, n.ctx.Procurement.OpenOffers(c).Count, "reading still works without comms");
            T.Check(n.ctx.Procurement.Describe(c).Contains("offer"), "the contract can still be read in full");

            n.comms.usable = true;
            Contract other = ProcurementTests.Post(n, fixer, "TestSteel", 200, ProcurementTests.Reliable(n));
            n.comms.usable = false;
            n.AdvanceTo(other.windowCloseTick);
            T.Eq(1, n.ctx.Procurement.OpenOffers(other).Count, "bidding continues without comms");

            n.comms.usable = true;
            T.Check(n.ctx.Procurement.CanDecline(o.id).ok, "usable console: can decline");
            T.Check(n.ctx.Procurement.Decline(o.id).ok, "declined");
            T.Eq(OfferState.Declined, o.state, "the offer is Declined");
        }

        // ================================================================== knowledge needs survivors

        private static void SurvivorRule()
        {
            string[] topics = { Topics.Thing("TestSteel"), Topics.Market };
            List<TierCount> forces = new List<TierCount> { new TierCount(Tier.Regular, 3) };

            OperationOutcome wiped = new OperationOutcome { band = OutcomeBand.Disaster };
            wiped.killed.Add(new TierCount(Tier.Regular, 3));
            wiped.fates.Add(new CharacterFate { character = new CharacterId(1), fate = Fate.Killed });
            T.Eq(0, Resolver.Survivors(wiped, forces, 1), "all killed: no survivors");
            T.Eq(0, Resolver.GainsIfReported(wiped, forces, 1, topics).Count, "Disaster with nobody back: nothing learned");

            OperationOutcome gone = new OperationOutcome { band = OutcomeBand.Disaster };
            gone.killed.Add(new TierCount(Tier.Regular, 1));
            gone.captured.Add(new TierCount(Tier.Regular, 1));
            gone.missing.Add(new TierCount(Tier.Regular, 1));
            gone.fates.Add(new CharacterFate { character = new CharacterId(1), fate = Fate.Captured });
            gone.fates.Add(new CharacterFate { character = new CharacterId(2), fate = Fate.Missing });
            T.Eq(0, Resolver.Survivors(gone, forces, 2), "killed, captured and missing do not come back");
            T.Eq(0, Resolver.GainsIfReported(gone, forces, 2, topics).Count, "captured and missing people carry no knowledge home");

            OperationOutcome one = new OperationOutcome { band = OutcomeBand.Disaster };
            one.killed.Add(new TierCount(Tier.Regular, 2));
            one.wounded.Add(new TierCount(Tier.Regular, 1));
            T.Eq(1, Resolver.Survivors(one, forces, 0), "a wounded survivor comes back");
            T.Check(Resolver.GainsIfReported(one, forces, 0, topics).Count > 0, "Disaster with a survivor: learned");

            OperationOutcome failed = new OperationOutcome { band = OutcomeBand.Failure };
            failed.killed.Add(new TierCount(Tier.Regular, 2));
            T.Check(Resolver.GainsIfReported(failed, forces, 0, topics).Count > 0, "an ordinary failure with a survivor still teaches");
        }

        private static float Exp(TestNet n, ActorId a)
        {
            KnowledgeBook b = n.ctx.knowledge.Get(a);
            float s = 0f;
            if (b != null) foreach (KnowledgeEntry e in b.entries) s += e.exp;
            return s;
        }

        private static void SurvivorsIntegration()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            bool wipedOut = false, survived = false, failureLearned = false, persisted = false;
            for (int i = 0; i < 60 && !(wipedOut && survived && failureLearned && persisted); i++)
            {
                bool solo = i % 3 == 0;
                OutcomeBand band = i % 3 == 2 ? OutcomeBand.Failure : OutcomeBand.Disaster;
                NetworkActor a = ProcurementTests.Reliable(n, solo ? ContractorForm.Solo : ContractorForm.Team);
                Contract c = ProcurementTests.Post(n, fixer, "TestSteel", solo ? 40 : 200, a);
                Offer o = ProcurementTests.Bid(n, c);
                if (o == null || !n.ctx.Procurement.Accept(o.id, false).ok) continue;
                Operation op = ProcurementTests.Op(n, c);
                int named = op.characters.Count;
                float before = Exp(n, a.id);
                ProcurementDevOverrides.forceBand = band;
                ProcurementTests.RunUntil(n, () => op.outcome != null || c.IsTerminal);
                if (op.outcome == null) continue;
                int survivors = Resolver.Survivors(op.outcome, op.forces, named);
                float after = Exp(n, a.id);
                if (survivors == 0)
                {
                    T.Eq(0, op.outcome.knowledgeGains.Count, "nobody back: no knowledge committed");
                    T.Check(Math.Abs(after - before) < 0.0001f, "nobody back: the contractor learned nothing");
                    if (band == OutcomeBand.Disaster) wipedOut = true;
                    continue;
                }
                T.Check(op.outcome.knowledgeGains.Count > 0 && after > before, band + " with " + survivors + " back: learned");
                if (band == OutcomeBand.Disaster) survived = true;
                else failureLearned = true;
                if (persisted || c.IsTerminal) continue;

                // Save and load after the resolve, then finish the job: nothing is applied twice.
                persisted = true;
                Operation copy = ProcurementTests.RoundTrip(op);
                T.Eq(op.outcome.knowledgeGains.Count, copy.outcome.knowledgeGains.Count, "the committed knowledge survives save/load");
                NetworkState loaded = SaveLoad(n);
                Swap(n, loaded);
                T.Check(Math.Abs(Exp(n, a.id) - after) < 0.0001f, "the loaded knowledge is what was learned, once");
                Contract lc = n.ctx.contracts.Get(c.id);
                ProcurementTests.RunUntil(n, () => lc.IsTerminal, 30);
                T.Check(Math.Abs(Exp(n, a.id) - after) < 0.0001f, "finishing the job after loading adds no knowledge twice");
            }
            T.Check(wipedOut, "a Disaster where nobody came back was exercised");
            T.Check(survived, "a Disaster with survivors was exercised");
            T.Check(failureLearned, "an ordinary failure with survivors was exercised");
            T.Check(persisted, "the save/load case was exercised");
        }

    }
}
