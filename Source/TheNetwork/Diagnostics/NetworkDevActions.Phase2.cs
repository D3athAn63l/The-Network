using System;
using System.Collections.Generic;
using System.Text;
using LudeonTK;
using RimWorld;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Catalog;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Knowledge;
using TheNetwork.Domain.Operations;
using TheNetwork.Domain.Ports;
using TheNetwork.Domain.Relations;
using TheNetwork.Integration;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using Verse;

namespace TheNetwork.Diagnostics
{
    /// <summary>
    /// Phase 2 dev actions: contractors, contracts, operations, delivery, consequences, relationships,
    /// knowledge and the soak harness. Each runs the ordinary code path sooner or with a forced draw;
    /// none skips a state's bookkeeping.
    /// </summary>
    public static partial class NetworkDevActions
    {
        private const string Cat2 = "The Network (Phase 2)";

        private static void PickContractor(NetworkRuntime rt, Action<NetworkActor> act, bool activeOnly = true)
        {
            List<FloatMenuOption> opts = new List<FloatMenuOption>();
            foreach (NetworkActor a in rt.State.actors.actors)
            {
                if (!ContractorService.IsNpcContractor(a) || (activeOnly && !a.IsActive)) continue;
                NetworkActor captured = a;
                opts.Add(new FloatMenuOption(a.name.Display + " (" + a.kind + ", " + rt.Ctx.Contractors.AvailabilityOf(a) + ")", () => act(captured)));
            }
            if (opts.Count == 0) opts.Add(new FloatMenuOption("(no contractors)", null));
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        private static void PickContract(NetworkRuntime rt, Func<Contract, bool> filter, Action<Contract> act)
        {
            List<FloatMenuOption> opts = new List<FloatMenuOption>();
            foreach (Contract c in rt.State.contracts.contracts)
            {
                if (filter != null && !filter(c)) continue;
                Contract captured = c;
                opts.Add(new FloatMenuOption(c.ToString(), () => act(captured)));
            }
            if (opts.Count == 0) opts.Add(new FloatMenuOption("(no matching contract)", null));
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        // ================================================================== contractors

        [DebugAction(Cat2, "Inspect contractor…", allowedGameStates = AllowedGameStates.Playing)]
        public static void InspectContractor()
        {
            NetworkRuntime rt = Rt;
            if (rt == null) return;
            PickContractor(rt, a => Out(DescribeContractor(rt, a)), false);
        }

        private static string DescribeContractor(NetworkRuntime rt, NetworkActor a)
        {
            DomainContext ctx = rt.Ctx;
            ContractorSimulation sim = a.Get<ContractorSimulation>();
            OrganizationProfile org = a.Get<OrganizationProfile>();
            StringBuilder sb = new StringBuilder(DescribeActor(rt, a));
            sb.AppendLine("  availability " + ctx.Contractors.AvailabilityOf(a) + ", experience " + ContractorService.Experience(a) + ", doctrine label " + ContractorService.DoctrineLabel(a) + ", strength " + ctx.Contractors.Strength(a).ToString("0.0") + " (dev only)");
            if (sim != null)
            {
                Doctrine d = sim.doctrine;
                sb.AppendLine("  doctrine " + d.style + ": caution " + d.caution.ToString("0.00") + " greed " + d.greed.ToString("0.00") + " loyalty " + d.loyalty.ToString("0.00") + " discretion " + d.discretion.ToString("0.00") + " professionalism " + d.professionalism.ToString("0.00") + " ambition " + d.ambition.ToString("0.00") + " cruelty " + d.cruelty.ToString("0.00"));
                sb.AppendLine("  morale " + sim.morale.descriptor + " (cohesion " + sim.morale.cohesion.ToString("0.00") + ", confidence " + sim.morale.confidence.ToString("0.00") + ", fatigue " + sim.morale.fatigue.ToString("0.00") + "), skill " + sim.skill.ToString("0.00") + ", funds " + sim.funds + ", career " + sim.careerStage + ", ops " + sim.opsCompleted);
                sb.AppendLine("  equipment tier " + sim.equipment.tier + " condition " + sim.equipment.condition.ToString("0.00") + ", mobility " + string.Join("/", sim.mobility.modes.ToArray()) + " range " + sim.mobility.rangeBand + " speed " + sim.mobility.speedBand + ", commitments " + sim.commitments.Count + ", next upkeep " + sim.nextUpkeepTick);
                if (sim.origin != null) sb.AppendLine("  origin " + sim.origin.NameSnapshot + (sim.originLost ? " (lost)" : ""));
            }
            if (org != null)
            {
                sb.AppendLine("  leader " + ctx.characters.Get(org.leader)?.name.Display + ", lieutenants " + org.lieutenants.Count + ", known members " + org.knownMembers.Count + ", successions " + org.succession.successions);
                foreach (TierCount t in org.tiers) sb.AppendLine("  tier " + t.tier + ": healthy " + t.healthy + ", wounded " + t.wounded);
                foreach (TierCount t in org.committed) sb.AppendLine("  committed " + t.tier + ": " + t.healthy);
                foreach (RecoveryBucket b in org.woundedRecovery) sb.AppendLine("  recovering " + b.count + " " + b.tier + " until " + b.dueTick);
                foreach (CharacterId id in org.knownMembers)
                {
                    KnownCharacter c = ctx.characters.Get(id);
                    if (c != null) sb.AppendLine("  person " + c.name.Display + " " + c.role + " " + c.status + " notability " + c.notability.ToString("0.00"));
                }
            }
            KnowledgeBook book = ctx.knowledge.Get(a.id);
            if (book != null) foreach (KnowledgeEntry e in book.entries) sb.AppendLine("  knows " + e.topic + " proficiency " + ctx.Knowledge.Proficiency(a.id, e.topic).ToString("0.00"));
            RelationView rel = ctx.Relations.Get(a.id, ctx.actors.PlayerProxyId);
            if (rel.exists) sb.AppendLine("  toward the player: " + ctx.Relations.Descriptor(a.id, ctx.actors.PlayerProxyId) + " standing " + rel.standing.ToString("0.0") + " trust " + rel.trust.ToString("0.00") + " jobs " + rel.jobs + " defaults " + rel.defaults);
            return sb.ToString();
        }

        [DebugAction(Cat2, "Create world-generated contractor", allowedGameStates = AllowedGameStates.Playing)]
        public static void CreateContractor()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            NetworkActor a = rt.Ctx.Contractors.CreateWorldGenerated(rt.State.actors.worldGeneratedCount++, "Dev");
            Out("[TheNetwork] Created " + a + ".");
        }

        [DebugAction(Cat2, "Kill contractor (Solo) or its leader…", allowedGameStates = AllowedGameStates.Playing)]
        public static void KillContractor()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            PickContractor(rt, a =>
            {
                KnownCharacter c = rt.Ctx.Contractors.Leader(a);
                if (c == null)
                {
                    Out("[TheNetwork] " + a + " has no known leader.");
                    return;
                }
                CasualtyReport r = new CasualtyReport();
                r.fates.Add(new CharacterFate { character = c.id, fate = Fate.Killed });
                rt.Ctx.Contractors.ApplyCasualties(a, r, ContractId.None, OperationId.None, false);
                Out("[TheNetwork] " + c.name.Display + " of " + a.name.Display + " killed; actor now " + a.status + ".");
            });
        }

        [DebugAction(Cat2, "Force leader succession…", allowedGameStates = AllowedGameStates.Playing)]
        public static void ForceSuccession()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            PickContractor(rt, a =>
            {
                OrganizationProfile org = a.Get<OrganizationProfile>();
                if (org == null)
                {
                    Out("[TheNetwork] " + a + " is a Solo: no succession.");
                    return;
                }
                KnownCharacter old = rt.State.characters.Get(org.leader);
                if (old != null && !Domain.Physical.AuthorityGate.Allows(old, "DevForceSuccession"))
                {
                    Out("[TheNetwork] " + old.name.Display + " is not abstractly simulatable (" + Domain.Physical.AuthorityGate.AuthorityOf(old) + "): no dev succession.");
                    return;
                }
                if (old != null)
                {
                    old.status = CharacterStatus.Retired;
                    old.statusTick = rt.Clock.Now;
                }
                rt.Ctx.Contractors.RunSuccession(a, org.leader);
                Out("[TheNetwork] Succession in " + a.name.Display + ": new leader " + rt.State.characters.Get(org.leader)?.name.Display + ", actor " + a.status + ".");
            });
        }

        [DebugAction(Cat2, "Set morale descriptor…", allowedGameStates = AllowedGameStates.Playing)]
        public static void SetMorale()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            PickContractor(rt, a =>
            {
                List<FloatMenuOption> opts = new List<FloatMenuOption>();
                foreach (MoraleDescriptor d in Enum.GetValues(typeof(MoraleDescriptor)))
                {
                    MoraleDescriptor captured = d;
                    opts.Add(new FloatMenuOption(d.ToString(), () =>
                    {
                        ContractorSimulation sim = a.Get<ContractorSimulation>();
                        sim.morale.descriptor = captured;
                        sim.morale.descriptorTick = rt.Clock.Now;
                        sim.MarkDirty();
                        Out("[TheNetwork] " + a.name.Display + " morale set to " + captured + " (it drifts again at upkeep).");
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(opts));
            });
        }

        [DebugAction(Cat2, "Run upkeep now (every contractor)", allowedGameStates = AllowedGameStates.Playing)]
        public static void RunUpkeepNow()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            int n = 0;
            foreach (NetworkActor a in new List<NetworkActor>(rt.State.actors.actors))
            {
                ContractorSimulation sim = a.Get<ContractorSimulation>();
                if (sim == null || a.status != ActorStatus.Active) continue;
                rt.Ctx.Upkeep.RunUpkeep(a, sim);
                n++;
            }
            int added = rt.Ctx.Upkeep.RunPopulation();
            Out("[TheNetwork] Upkeep run for " + n + " contractors; population manager added " + added + ".");
        }

        // ================================================================== contracts

        [DebugAction(Cat2, "Post procurement contract (dev)…", allowedGameStates = AllowedGameStates.Playing)]
        public static void PostContract()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            ItemCatalog cat = CatalogCache.Get();
            NetworkActor fixer = null;
            foreach (NetworkActor a in rt.State.actors.actors) if (ProcurementService.IsBroker(a)) { fixer = a; break; }
            if (fixer == null)
            {
                Out("[TheNetwork] No Fixer available.");
                return;
            }
            List<DebugMenuOption> opts = new List<DebugMenuOption>();
            foreach (CatalogEntry e in cat.Entries)
            {
                string reason;
                if (!cat.IsRequestable(e.DefName, out reason)) continue;
                string def = e.DefName;
                int count = Math.Max(1, Math.Min(e.item.stackLimit * 2, 150));
                opts.Add(new DebugMenuOption(e.Label + " x" + count, DebugMenuOptionMode.Action, () =>
                {
                    bool old = IntelDevOverrides.commsGateOverride;
                    IntelDevOverrides.commsGateOverride = true;
                    CommandResult r = rt.Ctx.Procurement.Post(new ProcurementRequest { defName = def, count = count, broker = fixer.id });
                    IntelDevOverrides.commsGateOverride = old;
                    Out("[TheNetwork] Post " + count + "x " + def + " via " + fixer.name.Display + ": " + r + (r.ok ? " → " + rt.Ctx.Procurement.lastPosted : ""));
                }));
            }
            Find.WindowStack.Add(new Dialog_DebugOptionListLister(opts));
        }

        [DebugAction(Cat2, "Close bidding window now…", allowedGameStates = AllowedGameStates.Playing)]
        public static void CloseWindow()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            PickContract(rt, c => c.status == ContractStatus.Bidding, c =>
            {
                rt.Ctx.Procurement.DevRunBiddingNow(c);
                Out("[TheNetwork] " + c + ": " + rt.Ctx.Procurement.OpenOffers(c).Count + " open offers, " + c.refusals.Count + " refusals.");
            });
        }

        [DebugAction(Cat2, "Dump contract (bidders, refusals, quotes, operation)…", allowedGameStates = AllowedGameStates.Playing)]
        public static void DumpContract()
        {
            NetworkRuntime rt = Rt;
            if (rt == null) return;
            PickContract(rt, null, c => Out(rt.Ctx.Procurement.Describe(c)));
        }

        [DebugAction(Cat2, "Force an offer from a contractor…", allowedGameStates = AllowedGameStates.Playing)]
        public static void ForceOffer()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            PickContract(rt, c => c.status == ContractStatus.Bidding || c.status == ContractStatus.Unfilled, c => PickContractor(rt, a =>
            {
                Offer o = rt.Ctx.Procurement.DevForceOffer(c, a);
                Out("[TheNetwork] Forced offer: " + (o == null ? "none (item missing?)" : o.ToString()));
            }));
        }

        [DebugAction(Cat2, "Accept the cheapest open offer…", allowedGameStates = AllowedGameStates.Playing)]
        public static void AcceptOffer()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            PickContract(rt, c => c.status == ContractStatus.Bidding && rt.Ctx.Procurement.OpenOffers(c).Count > 0, c =>
            {
                Offer best = null;
                foreach (Offer o in rt.Ctx.Procurement.OpenOffers(c)) if (best == null || o.quote.finalPrice < best.quote.finalPrice) best = o;
                bool old = IntelDevOverrides.commsGateOverride;
                IntelDevOverrides.commsGateOverride = true;
                CommandResult r = rt.Ctx.Procurement.Accept(best.id, false);
                IntelDevOverrides.commsGateOverride = old;
                Out("[TheNetwork] Accept " + best + ": " + r);
            });
        }

        [DebugAction(Cat2, "Apply the pending decision's grace default now…", allowedGameStates = AllowedGameStates.Playing)]
        public static void DecideNow()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            PickContract(rt, c => !c.IsTerminal && c.subStatus != null && c.decisionDueTick >= 0, c =>
            {
                rt.Ctx.Procurement.DevDecideNow(c);
                Out("[TheNetwork] " + c);
            });
        }

        // ================================================================== operations

        [DebugAction(Cat2, "Run next operation checkpoint now…", allowedGameStates = AllowedGameStates.Playing)]
        public static void AdvanceCheckpoint()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            PickContract(rt, c => rt.Ctx.Procurement.CurrentOperation(c) != null && !rt.Ctx.Procurement.CurrentOperation(c).IsFinished, c =>
            {
                Operation op = rt.Ctx.Procurement.CurrentOperation(c);
                bool ran = rt.Ctx.Operations.DevAdvance(op);
                Out("[TheNetwork] " + (ran ? "Advanced " : "Nothing to advance for ") + op + "; contract " + c);
            });
        }

        [DebugAction(Cat2, "Force outcome band on next resolution…", allowedGameStates = AllowedGameStates.Playing)]
        public static void ForceBand()
        {
            List<FloatMenuOption> opts = new List<FloatMenuOption>();
            foreach (OutcomeBand b in Enum.GetValues(typeof(OutcomeBand)))
            {
                OutcomeBand captured = b;
                opts.Add(new FloatMenuOption(b.ToString(), () =>
                {
                    ProcurementDevOverrides.forceBand = captured;
                    Out("[TheNetwork] The next operation to resolve will be " + captured + ".");
                }));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        [DebugAction(Cat2, "Force partial result on next resolution", allowedGameStates = AllowedGameStates.Playing)]
        public static void ForcePartial()
        {
            ProcurementDevOverrides.forceBand = OutcomeBand.Partial;
            Out("[TheNetwork] The next operation to resolve will be Partial.");
        }

        [DebugAction(Cat2, "Force catastrophe on next resolution", allowedGameStates = AllowedGameStates.Playing)]
        public static void ForceCatastrophe()
        {
            ProcurementDevOverrides.forceBand = OutcomeBand.Disaster;
            ProcurementDevOverrides.forceSecured = 0;
            Out("[TheNetwork] The next operation to resolve will be a Disaster with nothing secured.");
        }

        [DebugAction(Cat2, "Force 3-day delay on next resolution", allowedGameStates = AllowedGameStates.Playing)]
        public static void ForceDelay()
        {
            ProcurementDevOverrides.forceDelayTicks = 3 * Ticks.PerDay;
            Out("[TheNetwork] The next operation to resolve will be three days late.");
        }

        [DebugAction(Cat2, "Force Troubled (missing) on next resolution", allowedGameStates = AllowedGameStates.Playing)]
        public static void ForceTroubled()
        {
            ProcurementDevOverrides.forceTroubled = SubStatus.Missing;
            Out("[TheNetwork] The next operation to resolve will be Troubled (missing).");
        }

        [DebugAction(Cat2, "Force 'worse than expected' on next engagement", allowedGameStates = AllowedGameStates.Playing)]
        public static void ForceWorse()
        {
            ProcurementDevOverrides.forceWorseThanExpected = true;
            Out("[TheNetwork] The next operation to engage will ask for new terms.");
        }

        [DebugAction(Cat2, "Force Last Known Location on next loss", allowedGameStates = AllowedGameStates.Playing)]
        public static void ForceLastKnown()
        {
            ProcurementDevOverrides.forceFollowUp = true;
            Out("[TheNetwork] The next catastrophic loss or missing/stranded contractor will leave a last known location (budget and depth guards still apply).");
        }

        [DebugAction(Cat2, "Force a newcomer bidder on the next open contract", allowedGameStates = AllowedGameStates.Playing)]
        public static void ForceNewcomer()
        {
            ProcurementDevOverrides.forceNewcomer = true;
            Out("[TheNetwork] The next Open contract will bring a new contractor into the world.");
        }

        // ================================================================== delivery

        [DebugAction(Cat2, "Deliver now…", allowedGameStates = AllowedGameStates.Playing)]
        public static void DeliverNow()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            PickContract(rt, c => !c.IsTerminal && c.Deliver != null && c.Deliver.InProgress, c =>
            {
                rt.Ctx.Procurement.DevDeliverNow(c);
                Out("[TheNetwork] " + c + (c.Deliver.lastFailureKey != null ? " (last failure " + c.Deliver.lastFailureKey + ")" : ""));
            });
        }

        [DebugAction(Cat2, "Force the next 3 delivery attempts to fail (→ Hold)", allowedGameStates = AllowedGameStates.Playing)]
        public static void ForceDeliveryFailure()
        {
            ProcurementDevOverrides.forceDeliveryFailures = 3;
            Out("[TheNetwork] The next three delivery attempts will find no drop spot.");
        }

        [DebugAction(Cat2, "Delivery plan report (where would pods land now?)", allowedGameStates = AllowedGameStates.Playing)]
        public static void DeliveryPlan()
        {
            NetworkRuntime rt = Rt;
            if (rt == null) return;
            string label;
            int preferred = rt.Ctx.delivery.DefaultHomeMapId(out label);
            DeliveryPlan p = rt.Ctx.delivery.Plan(preferred, 12345);
            Out("[TheNetwork] Default home map " + preferred + " (" + label + "); plan: " + (p.ok ? "map " + p.mapId + " (" + p.mapLabel + ") cell " + p.cellX + "," + p.cellZ + (p.rerouted ? " REROUTED" : "") : "FAILED " + p.failureKey));
        }

        // ================================================================== relationships and knowledge

        [DebugAction(Cat2, "Dump relationships (strongest 40)", allowedGameStates = AllowedGameStates.Playing)]
        public static void DumpRelations()
        {
            NetworkRuntime rt = Rt;
            if (rt == null) return;
            List<RelationEdge> edges = new List<RelationEdge>(rt.State.relations.edges);
            edges.Sort((a, b) => Math.Abs(b.standing).CompareTo(Math.Abs(a.standing)));
            StringBuilder sb = new StringBuilder("[TheNetwork] " + edges.Count + " relation edges (cap " + RelationService.GlobalCap + ")\n");
            for (int i = 0; i < edges.Count && i < 40; i++)
            {
                RelationEdge e = edges[i];
                sb.AppendLine("  " + rt.State.actors.NameOf(e.from) + " → " + rt.State.actors.NameOf(e.to) + ": " + rt.Ctx.Relations.Descriptor(e.from, e.to) + " standing " + e.standing.ToString("0.0") + " trust " + e.trust.ToString("0.00") + " familiarity " + e.familiarity.ToString("0.00") + " jobs " + e.counters.jobsTogether + " defaults " + e.counters.defaults);
            }
            Out(sb.ToString());
        }

        [DebugAction(Cat2, "Dump knowledge…", allowedGameStates = AllowedGameStates.Playing)]
        public static void DumpKnowledge()
        {
            NetworkRuntime rt = Rt;
            if (rt == null) return;
            PickContractor(rt, a =>
            {
                KnowledgeBook book = rt.State.knowledge.Get(a.id);
                StringBuilder sb = new StringBuilder("[TheNetwork] Knowledge of " + a.name.Display + ": " + (book == null ? 0 : book.entries.Count) + " topics (cap " + KnowledgeService.CapPerActor + ")\n");
                if (book != null) foreach (KnowledgeEntry e in book.entries) sb.AppendLine("  " + e.topic + ": exp " + rt.Ctx.Knowledge.Exp(a.id, e.topic).ToString("0.00") + ", proficiency " + rt.Ctx.Knowledge.Proficiency(a.id, e.topic).ToString("0.00"));
                Out(sb.ToString());
            }, false);
        }

        // ================================================================== soak

        [DebugAction(Cat2, "Simulate procurement (soak harness: 100 contractors, 1 year, synthetic)", allowedGameStates = AllowedGameStates.Playing | AllowedGameStates.Entry)]
        public static void Soak()
        {
            SoakHarness.Result r = SoakHarness.Run(100, 14, 360, 20260927);
            Out(r.text);
        }

        [DebugAction(Cat2, "Simulate procurement with this game's catalog (soak harness)", allowedGameStates = AllowedGameStates.Playing)]
        public static void SoakLiveCatalog()
        {
            ItemCatalog cat = CatalogCache.Get();
            List<ItemFacts> items = new List<ItemFacts>();
            foreach (CatalogEntry e in cat.Entries)
            {
                string reason;
                if (e.facts.category == "Item" && cat.IsRequestable(e.DefName, out reason)) items.Add(e.item);
            }
            if (items.Count == 0)
            {
                Out("[TheNetwork] No requestable items in the catalog.");
                return;
            }
            // A spread across the catalog, stable for a given mod list.
            List<ItemFacts> sample = new List<ItemFacts>();
            int step = Math.Max(1, items.Count / 60);
            for (int i = 0; i < items.Count; i += step) sample.Add(items[i]);
            SoakHarness.Result r = SoakHarness.Run(100, 14, 360, 20260927, sample);
            Out(r.text + "  items sampled from this game's catalog: " + sample.Count + "\n");
        }
    }
}
