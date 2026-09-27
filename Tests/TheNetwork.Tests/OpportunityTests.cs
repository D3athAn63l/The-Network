using System;
using System.Collections.Generic;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;
using TheNetwork.Persist.Events;

namespace TheNetwork.Tests
{
    public static class OpportunityTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("Resolver.SameSourceHostilePreferred", SameSourcePreferred));
            t.Add(new KeyValuePair<string, Action>("Resolver.NonHostileSamePackageNeverAGuard", NonHostileNeverGuard));
            t.Add(new KeyValuePair<string, Action>("Resolver.DefeatedAndHiddenFallBack", DefeatedFallsBack));
            t.Add(new KeyValuePair<string, Action>("Resolver.LudeonPackageIsNotEvidence", LudeonNotEvidence));
            t.Add(new KeyValuePair<string, Action>("Resolver.NoFactionsStillWorks", NoFactions));
            t.Add(new KeyValuePair<string, Action>("Resolver.TrapKeepsHolder", TrapKeepsHolder));
            t.Add(new KeyValuePair<string, Action>("Generator.QuantityFromGeneratorAndDivergence", Quantity));
            t.Add(new KeyValuePair<string, Action>("Claim.PartialRecoveryByCaravan", PartialCaravan));
            t.Add(new KeyValuePair<string, Action>("Claim.BroughtOnlyCargoRecoversNothing", BroughtOnly));
            t.Add(new KeyValuePair<string, Action>("Claim.MixedProvenanceCountsOnlyCache", MixedProvenance));
            t.Add(new KeyValuePair<string, Action>("Claim.NoDoubleCountOnReentry", Reentry));
            t.Add(new KeyValuePair<string, Action>("Claim.TransportPodsCountedOnce", Pods));
            t.Add(new KeyValuePair<string, Action>("Claim.NeverInflatedByOwnCargo", NeverInflated));
            t.Add(new KeyValuePair<string, Action>("Claim.EmptyHandedAndBadIntel", EmptyHanded));
            t.Add(new KeyValuePair<string, Action>("Claim.SettledIsClaimedAll", Settled));
            t.Add(new KeyValuePair<string, Action>("Claim.RecoveryBasisIgnoresHolderStock", Basis));
            t.Add(new KeyValuePair<string, Action>("Opportunity.ExpiredDestroyedVanished", Lifecycle));
            t.Add(new KeyValuePair<string, Action>("Opportunity.WarnAndCloseJobs", WarnAndClose));
        }

        private static ItemFacts Weirdium()
        {
            return new ItemFacts { defName = "ModX_Weirdium", label = "weirdium", packageId = "someone.weirdmod", isLudeon = false, techLevel = 5, marketValue = 40f, stackLimit = 50, tradeable = true };
        }

        private static Dictionary<SourceKind, int> Draw(ItemFacts item, List<FactionFacts> f, int n, Action<SourceDecision> each = null, LeadDivergence div = LeadDivergence.Accurate)
        {
            Dictionary<SourceKind, int> counts = new Dictionary<SourceKind, int>();
            for (int i = 0; i < n; i++)
            {
                SourceDecision d = SourceResolver.Resolve(item, f, div, new NetRng(i * 7919 + 1, "opp.source"));
                int c;
                counts.TryGetValue(d.kind, out c);
                counts[d.kind] = c + 1;
                each?.Invoke(d);
            }
            return counts;
        }

        private static int Get(Dictionary<SourceKind, int> d, SourceKind k)
        {
            int v;
            return d.TryGetValue(k, out v) ? v : 0;
        }

        private static void SameSourcePreferred()
        {
            List<FactionFacts> f = new List<FactionFacts>
            {
                FakeWorld.Faction(20, "Red Hands", "someone.weirdmod", 5, true),
                FakeWorld.Faction(21, "Rust Pirates", "ludeon.rimworld", 4, true, pirate: true, ludeon: true)
            };
            Dictionary<SourceKind, int> c = Draw(Weirdium(), f, 2000, d =>
            {
                if (d.kind == SourceKind.SameSourceFaction) T.Check(d.holder.loadId == 20 && d.evidence.Contains("sameSourcePackage"), "same-source holder is the package's faction, with evidence");
            });
            int same = Get(c, SourceKind.SameSourceFaction);
            T.Check(same > 900, "an active, fitting same-package faction is the preferred candidate (" + same + "/2000)");
            foreach (KeyValuePair<SourceKind, int> kv in c) T.Check(kv.Key == SourceKind.SameSourceFaction || kv.Value < same, "…more likely than " + kv.Key);
            T.Check(Get(c, SourceKind.Pirates) + Get(c, SourceKind.AncientSite) + Get(c, SourceKind.NoCredibleSource) > 0, "the hierarchy is a prior, not a rule: other contexts still occur");
        }

        private static void NonHostileNeverGuard()
        {
            List<FactionFacts> f = new List<FactionFacts>
            {
                FakeWorld.Faction(20, "Weird Traders", "someone.weirdmod", 5, false),
                FakeWorld.Faction(21, "Rust Pirates", "ludeon.rimworld", 4, true, pirate: true, ludeon: true)
            };
            bool friendlyEverGuard = false;
            Dictionary<SourceKind, int> c = Draw(Weirdium(), f, 2000, d =>
            {
                if (d.holder != null && d.holder.loadId == 20) friendlyEverGuard = true;
                if (d.holder != null) T.Check(d.holderStance == Stance.Hostile, "every holder of a guarded cache is really hostile");
            });
            T.Check(!friendlyEverGuard, "a non-hostile same-package faction is never turned into a guard");
            T.Eq(0, Get(c, SourceKind.SameSourceFaction), "no same-source guard without a hostile same-package faction");
            List<string> skipped = new List<string>();
            SourceResolver.BuildCandidates(Weirdium(), f, skipped);
            T.Check(skipped.Exists(s => s.Contains("Weird Traders") && s.Contains("not hostile")), "the skip is explained");
            T.Eq(false, f[0].hostileToPlayer, "the faction's stance was not changed");
        }

        private static void DefeatedFallsBack()
        {
            FactionFacts defeated = FakeWorld.Faction(20, "Red Hands", "someone.weirdmod", 5, true);
            defeated.defeated = true;
            FactionFacts hiddenOther = FakeWorld.Faction(22, "Hidden Ones", "ludeon.rimworld", 5, true, ludeon: true);
            hiddenOther.hidden = true;
            List<FactionFacts> f = new List<FactionFacts> { defeated, hiddenOther };
            Dictionary<SourceKind, int> c = Draw(Weirdium(), f, 1000);
            T.Eq(0, Get(c, SourceKind.SameSourceFaction), "a defeated same-package faction is not a candidate");
            T.Eq(0, Get(c, SourceKind.SimilarTechFaction) + Get(c, SourceKind.HostileFaction), "a hidden unrelated faction is not a candidate");
            T.Check(Get(c, SourceKind.AncientSite) > 0 && Get(c, SourceKind.NoCredibleSource) > 0, "falls down the hierarchy, including no credible lead");
            FactionFacts hiddenSame = FakeWorld.Faction(23, "Deep Weird", "someone.weirdmod", 5, true);
            hiddenSame.hidden = true;
            Dictionary<SourceKind, int> c2 = Draw(Weirdium(), new List<FactionFacts> { hiddenSame }, 500);
            T.Check(Get(c2, SourceKind.SameSourceFaction) > 0, "a hidden faction is allowed where the package fits");
        }

        private static void LudeonNotEvidence()
        {
            ItemFacts steel = new ItemFacts { defName = "TestSteel", packageId = "ludeon.rimworld", isLudeon = true, techLevel = 4, marketValue = 2f, stackLimit = 75 };
            List<FactionFacts> f = new List<FactionFacts> { FakeWorld.Faction(21, "Vanilla Outlaws", "ludeon.rimworld", 4, true, ludeon: true) };
            Dictionary<SourceKind, int> c = Draw(steel, f, 500);
            T.Eq(0, Get(c, SourceKind.SameSourceFaction), "shared Ludeon package is not same-source evidence");
            T.Check(Get(c, SourceKind.SimilarTechFaction) > 0, "vanilla items resolve through tech and hostility");
        }

        private static void NoFactions()
        {
            Dictionary<SourceKind, int> c = Draw(Weirdium(), new List<FactionFacts>(), 1000);
            T.Check(Get(c, SourceKind.NoCredibleSource) > 0, "NoCredibleLead is a legitimate result");
            T.Check(Get(c, SourceKind.AncientSite) + Get(c, SourceKind.AbandonedCache) > 0, "unowned caches still possible");
            ItemFacts relic = new ItemFacts { defName = "ModX_Relic", packageId = "someone.weirdmod", techLevel = 6, marketValue = 2500f, unique = true };
            Dictionary<SourceKind, int> r = Draw(relic, new List<FactionFacts>(), 1000);
            T.Check(Get(r, SourceKind.NoCredibleSource) > Get(c, SourceKind.NoCredibleSource), "unique items are harder to find");
        }

        private static void TrapKeepsHolder()
        {
            List<FactionFacts> f = new List<FactionFacts> { FakeWorld.Faction(20, "Red Hands", "someone.weirdmod", 5, true) };
            SourceDecision d = SourceResolver.Resolve(Weirdium(), f, LeadDivergence.Trap, new NetRng(1, "x"), SourceKind.SameSourceFaction);
            T.Eq(SourceKind.SameSourceFaction, d.kind, "forced kind");
            T.Eq(ThreatProfiles.AmbushHidden, d.threatProfileKey, "a trap is expressed with a hidden ambush");
            T.Eq(20, d.holder.loadId, "the trap does not change who holds it");
        }

        private static void Quantity()
        {
            ItemFacts steel = new ItemFacts { defName = "TestSteel", techLevel = 4, marketValue = 1.9f, stackLimit = 75 };
            ItemFacts rifle = new ItemFacts { defName = "TestRifle", techLevel = 4, marketValue = 350f, stackLimit = 1 };
            int s = OpportunityGenerator.NominalCount(steel, 400f, 400f, new NetRng(1, "q"));
            int r = OpportunityGenerator.NominalCount(rifle, 400f, 400f, new NetRng(1, "q"));
            T.Check(s > 50 && s <= 300, "cheap stackables come in quantity (" + s + ")");
            T.Check(r >= 1 && r <= 4, "expensive singles come in ones (" + r + ")");
            T.Eq(0, OpportunityGenerator.ShapeByDivergence(100, LeadDivergence.Bad, steel, new NetRng(2, "q")), "bad intel: none of the target");
            int jack = OpportunityGenerator.ShapeByDivergence(100, LeadDivergence.Jackpot, steel, new NetRng(3, "q"));
            T.Check(jack >= 200, "jackpot: more than reported (" + jack + ")");
            int outdated = OpportunityGenerator.ShapeByDivergence(100, LeadDivergence.Outdated, steel, new NetRng(4, "q"));
            T.Check(outdated <= 40, "outdated: remnants (" + outdated + ")");
            T.Eq(100, OpportunityGenerator.ShapeByDivergence(100, LeadDivergence.Accurate, steel, new NetRng(5, "q")), "accurate: as is");
        }

        /// <summary>A materialized cache of 100 test steel with its site, ready to engage.</summary>
        private static Opportunity MakeCache(TestNet n, int count = 100)
        {
            ItemFacts steel = n.cat.Facts("TestSteel");
            OpportunityDraft draft = new OpportunityDraft
            {
                decision = new SourceDecision { kind = SourceKind.AbandonedCache, threatProfileKey = ThreatProfiles.None },
                targetCount = count,
                nominalCount = count,
                windowDays = 15
            };
            TileRef tile;
            n.sites.TryFindTile(5, 4, 16, out tile);
            Opportunity o = n.ctx.Opportunities.Commit(draft, steel, tile, 99, OpportunityOrigin.Debug, EntityRef.None);
            MaterializeResult m = n.sites.Materialize(o);
            o.site = m.site;
            o.state = OpportunityState.Materialized;
            return o;
        }

        private static void Engage(TestNet n, Opportunity o)
        {
            n.sites.maps.Add(o.site.id);
            n.ctx.Opportunities.OnMapGenerated(o.id);
        }

        private static void RemoveMap(TestNet n, Opportunity o)
        {
            n.ctx.Opportunities.OnMapAboutToBeRemoved(o.id);
            n.sites.maps.Remove(o.site.id);
            n.sites.sites.Remove(o.site.id);
            n.ctx.Opportunities.OnMapRemoved(o.id);
        }

        /// <summary>
        /// A site map driven in vanilla's order (decompiled 1.6): the map is generated and
        /// <c>PostMapGenerate</c> runs before the arriving caravan or pods put anything on it; a sample
        /// counts every target-def item on the map, the player's included; caravan pawns exit before
        /// <c>Notify_CaravanFormed</c>; pods leave the map with no callback; the comp's pre-removal sample
        /// runs in the tick that removes the map. It tracks true provenance so tests can compare the
        /// Network's estimate with what really happened. It proves Network accounting, not vanilla.
        /// </summary>
        private sealed class MapSim
        {
            private readonly TestNet n;
            private readonly Opportunity o;
            public int cacheLeft;   // site stock where the site put it
            public int heldCache;   // cache items carried by player pawns on the map
            public int heldOwn;     // the player's own copies carried on the map
            public int groundCache; // cache items the player moved and put down again
            public int groundOwn;   // the player's own copies left on the map
            public int awayCache;   // cache items that left the map with the player (the truth)
            public int awayOwn;

            public MapSim(TestNet n, Opportunity o)
            {
                this.n = n;
                this.o = o;
            }

            private void Sync()
            {
                n.sites.remainingByOpp[o.id.Value] = cacheLeft + heldCache + heldOwn + groundCache + groundOwn;
            }

            public MapSim Generate(int holderStock = 0)
            {
                cacheLeft = o.TargetCount + holderStock;
                Sync();
                Engage(n, o);
                return this;
            }

            /// <summary>A caravan or pods arrive carrying the player's own copies (vanilla sends no comp callback).</summary>
            public void Arrive(int own)
            {
                heldOwn += own;
                Sync();
            }

            public void Take(int k)
            {
                k = Math.Min(k, cacheLeft);
                cacheLeft -= k;
                heldCache += k;
                Sync();
            }

            public void DropOwn(int k)
            {
                k = Math.Min(k, heldOwn);
                heldOwn -= k;
                groundOwn += k;
                Sync();
            }

            public void DropCache(int k)
            {
                k = Math.Min(k, heldCache);
                heldCache -= k;
                groundCache += k;
                Sync();
            }

            private void Depart(int cache, int own)
            {
                cache = Math.Min(cache, heldCache);
                own = Math.Min(own, heldOwn);
                heldCache -= cache;
                heldOwn -= own;
                awayCache += cache;
                awayOwn += own;
                Sync();
            }

            public void LeaveByCaravan(int cache, int own)
            {
                Depart(cache, own);
                n.ctx.Opportunities.OnCaravanFormed(o.id, cache + own);
            }

            public void LaunchPods(int cache, int own)
            {
                Depart(cache, own);
            }

            /// <summary>A caravan that left comes back onto the still-existing map.</summary>
            public void Return(int cache, int own)
            {
                cache = Math.Min(cache, awayCache);
                own = Math.Min(own, awayOwn);
                awayCache -= cache;
                awayOwn -= own;
                heldCache += cache;
                heldOwn += own;
                Sync();
            }

            public void Periodic()
            {
                n.Advance(JobKinds.SamplePeriod);
            }

            /// <summary>The last pawns are gone: vanilla removes the map (pre-removal sample first unless another path removed it).</summary>
            public void Remove(bool preRemovalSample = true)
            {
                if (preRemovalSample) n.ctx.Opportunities.OnMapAboutToBeRemoved(o.id);
                n.sites.maps.Remove(o.site.id);
                n.sites.sites.Remove(o.site.id);
                n.ctx.Opportunities.OnMapRemoved(o.id);
            }
        }

        private static void PartialCaravan()
        {
            // CASE C: no same-def cargo brought; part of the cache taken; defenders alive.
            TestNet n = new TestNet();
            Opportunity o = MakeCache(n);
            MapSim m = new MapSim(n, o).Generate();
            T.Eq(OpportunityState.Engaged, o.state, "engaged at map generation");
            T.Eq(100, o.engagement.initialOnMap, "initial sample taken at map generation");
            T.Check(n.scheduler.Find(JobKinds.OppSample, o.id.Value) != null, "low-frequency sample scheduled while the map exists");
            m.Take(40);
            m.LeaveByCaravan(40, 0);
            m.Remove();
            T.Eq(OpportunityState.Claimed, o.state, "leaving with part of it is a claim (no extermination needed)");
            T.Eq(40, o.engagement.recovered, "recovered estimate");
            T.Eq(RecoveredBand.Some, o.engagement.recoveredBand, "coarse band");
            T.Eq("PartialRecovery", o.outcomeKey, "partial recovery");
            T.Check(n.ledger.records.Exists(h => h.typeKey == EventKeys.OpportunityClaimed && h.outcomeKey == "PartialRecovery"), "history records a partial recovery");
            T.Check(n.scheduler.Find(JobKinds.OppSample, o.id.Value) == null, "sampling stops with the map");
            T.Eq(1, n.summaries.Get(n.ctx.actors.PlayerProxyId).Lifetime("opp.claimed.partial"), "summary counter");
        }

        private static void BroughtOnly()
        {
            // CASE A: the player brings 100 of the target def, takes none of the cache, leaves with their own 100.
            TestNet n = new TestNet();
            Opportunity o = MakeCache(n);
            MapSim m = new MapSim(n, o).Generate();
            m.Arrive(100);
            m.Periodic();
            T.Eq(200, o.engagement.lastRemaining, "a sample while the player is on the map counts their cargo too");
            m.LeaveByCaravan(0, 100);
            m.Remove();
            T.Eq(0, o.engagement.recovered, "own cargo is never recovered Network payload");
            T.Eq(OpportunityState.Abandoned, o.state, "not a claim");
            T.Eq("LeftEmptyHanded", o.outcomeKey, "left empty-handed");
            T.Eq(100, o.engagement.caravanTally, "the caravan did carry 100 of the def (diagnostics only)");

            // The same by pods.
            TestNet n2 = new TestNet();
            Opportunity o2 = MakeCache(n2);
            MapSim p = new MapSim(n2, o2).Generate();
            p.Arrive(100);
            p.LaunchPods(0, 100);
            p.Remove();
            T.Eq(0, o2.engagement.recovered, "own cargo sent home by pods is not recovered either");
            T.Eq(OpportunityState.Abandoned, o2.state, "not a claim");
        }

        private static void MixedProvenance()
        {
            // CASE B: cache 100; the player brings 50, takes 30, leaves carrying 80.
            TestNet n = new TestNet();
            Opportunity o = MakeCache(n);
            MapSim m = new MapSim(n, o).Generate();
            m.Arrive(50);
            m.Take(30);
            m.Periodic();
            m.LeaveByCaravan(30, 50);
            m.Remove();
            T.Eq(30, o.engagement.recovered, "only the 30 from the cache are recovered, not the 80 carried");
            T.Eq(80, o.engagement.caravanTally, "the caravan carried 80 of the def (diagnostics only)");
            T.Eq(OpportunityState.Claimed, o.state, "a claim");
            T.Eq(RecoveredBand.Some, o.engagement.recoveredBand, "band from 30 of 100");

            // Mixed by pods, with holder stock of the same def on the site as well.
            TestNet n2 = new TestNet();
            Opportunity o2 = MakeCache(n2);
            MapSim p = new MapSim(n2, o2).Generate(holderStock: 40);
            p.Arrive(50);
            p.Take(30);
            p.LaunchPods(30, 50);
            p.Remove();
            T.Eq(30, o2.engagement.recovered, "pods: 30 of the site's stock, not the 50 brought");
        }

        private static void Reentry()
        {
            // CASE D: repeated departures and re-entry never double-count cache loot.
            TestNet n = new TestNet();
            Opportunity o = MakeCache(n);
            MapSim m = new MapSim(n, o).Generate();
            m.Arrive(20);
            m.Take(40);
            m.LeaveByCaravan(40, 20);   // some pawns stay behind, so the map remains
            m.Return(40, 20);           // they come back with the same loot and their own cargo
            m.DropCache(40);            // and put the loot down again
            m.Periodic();
            m.Take(40);
            m.LeaveByCaravan(40, 20);   // and leave again with the same 40
            m.Remove();
            T.Eq(40, o.engagement.recovered, "items that came back and left again are not counted twice");
            T.Eq(2, o.engagement.caravanDepartures, "each departure tallied once");
            T.Eq(120, o.engagement.caravanTally, "the tally saw 120 leave (diagnostics only; never used)");
        }

        private static void Pods()
        {
            // CASE E: pod cargo counts once; own copies in the pods do not inflate it.
            TestNet n = new TestNet();
            Opportunity o = MakeCache(n);
            MapSim m = new MapSim(n, o).Generate();
            m.Arrive(50);
            m.Take(30);
            m.LaunchPods(30, 50);     // the last pawns leave by pods; no callback
            m.Remove();               // the comp's pre-removal sample, then the removal
            T.Eq(OpportunityState.Claimed, o.state, "pods departure is a claim");
            T.Eq(30, o.engagement.recovered, "pod cargo counted once, own copies excluded");

            // Pods launched while others stay; a periodic sample; then the rest leave by caravan.
            TestNet n2 = new TestNet();
            Opportunity o2 = MakeCache(n2);
            MapSim p = new MapSim(n2, o2).Generate();
            p.Take(50);
            p.LaunchPods(30, 0);
            p.Periodic();
            p.Periodic();
            p.LeaveByCaravan(20, 0);
            p.Remove();
            T.Eq(50, o2.engagement.recovered, "30 by pods + 20 by caravan, each once");

            // If the map is removed by a path without the pre-removal sample, the estimate falls back to
            // the last sample: it may under-count, never over-count.
            TestNet n3 = new TestNet();
            Opportunity o3 = MakeCache(n3);
            MapSim q = new MapSim(n3, o3).Generate();
            q.Arrive(50);
            q.Take(30);
            q.Periodic();
            q.LaunchPods(30, 50);
            q.Remove(preRemovalSample: false);
            T.Check(o3.engagement.recovered <= 30, "without the final sample the estimate never exceeds the truth (" + o3.engagement.recovered + ")");
        }

        private static void NeverInflated()
        {
            // Randomized sequences of arrive / take / drop / leave (caravan or pods) / return / sample.
            // Invariant: the estimate never exceeds the cache items that really left with the player,
            // and equals it (capped at the target) whenever no own copies are left on the map.
            System.Random rnd = new System.Random(4242);
            int exact = 0;
            for (int run = 0; run < 400; run++)
            {
                TestNet n = new TestNet();
                Opportunity o = MakeCache(n, 1 + rnd.Next(150));
                MapSim m = new MapSim(n, o).Generate(holderStock: rnd.Next(3) == 0 ? rnd.Next(60) : 0);
                int steps = 3 + rnd.Next(12);
                for (int s = 0; s < steps; s++)
                {
                    switch (rnd.Next(9))
                    {
                        case 0: m.Arrive(rnd.Next(120)); break;
                        case 1:
                        case 2: m.Take(rnd.Next(60)); break;
                        case 3: m.DropOwn(rnd.Next(40)); break;
                        case 4: m.DropCache(rnd.Next(40)); break;
                        case 5: m.LeaveByCaravan(rnd.Next(80), rnd.Next(80)); break;
                        case 6: m.LaunchPods(rnd.Next(80), rnd.Next(80)); break;
                        case 7: m.Return(rnd.Next(40), rnd.Next(40)); break;
                        case 8: m.Periodic(); break;
                    }
                }
                // Everyone leaves (the map can only go once no player pawn is on it).
                if (rnd.Next(2) == 0) m.LeaveByCaravan(m.heldCache, m.heldOwn);
                else m.LaunchPods(m.heldCache, m.heldOwn);
                m.Remove();
                int truth = Math.Min(m.awayCache, o.TargetCount);
                T.Check(o.engagement.recovered <= truth, "run " + run + ": estimate " + o.engagement.recovered + " never exceeds the cache that left (" + truth + ")");
                if (m.groundOwn == 0)
                {
                    // Without holder stock the site stock is exactly the target, so the estimate is exact.
                    if (o.engagement.initialOnMap == o.TargetCount)
                    {
                        T.Eq(truth, o.engagement.recovered, "run " + run + ": exact when no own copies stay behind");
                        exact++;
                    }
                }
                T.Eq(o.engagement.recovered > 0 ? OpportunityState.Claimed : OpportunityState.Abandoned, o.state, "run " + run + ": claimed only with a recovery");
            }
            T.Check(exact > 100, "enough exact cases exercised (" + exact + ")");
        }

        private static void EmptyHanded()
        {
            TestNet n = new TestNet();
            Opportunity o = MakeCache(n);
            Engage(n, o);
            RemoveMap(n, o);
            T.Eq(OpportunityState.Abandoned, o.state, "leaving empty-handed is Abandoned");
            T.Eq("LeftEmptyHanded", o.outcomeKey, "reason");
            T.Check(n.ledger.records.Exists(h => h.typeKey == EventKeys.OpportunityAbandoned), "history records the abandonment");
            Opportunity bad = MakeCache(n, 0);
            Engage(n, bad);
            RemoveMap(n, bad);
            T.Eq(OpportunityState.Abandoned, bad.state, "bad intel with none of the target");
            T.Eq("NothingThere", bad.outcomeKey, "nothing there");
        }

        private static void Settled()
        {
            TestNet n = new TestNet();
            Opportunity o = MakeCache(n);
            Engage(n, o);
            n.sites.remainingByOpp[o.id.Value] = 70;
            n.ctx.Opportunities.OnMapSettled(o.id);
            T.Eq(OpportunityState.Claimed, o.state, "settling claims the cache");
            T.Eq(RecoveredBand.All, o.engagement.recoveredBand, "all of it is now the player's");
            n.ctx.Opportunities.OnSiteDestroyed(o.id);
            T.Eq(OpportunityState.Claimed, o.state, "the settle's site destruction changes nothing");
        }

        private static void Basis()
        {
            Engagement e = new Engagement { initialOnMap = 150, lastRemaining = 120 };
            T.Eq(30, OpportunityService.RecoveredEstimate(e, 100), "recovered measured from the map");
            Engagement all = new Engagement { initialOnMap = 150, lastRemaining = 0 };
            T.Eq(100, OpportunityService.RecoveredEstimate(all, 100), "capped at the committed target (holder stock is not the cache)");
            Engagement none = new Engagement { initialOnMap = 20, lastRemaining = 0 };
            T.Eq(0, OpportunityService.RecoveredEstimate(none, 0), "a zero target recovers nothing whatever else is there");
            Engagement tallied = new Engagement { initialOnMap = 100, lastRemaining = 100, caravanTally = 100 };
            T.Eq(0, OpportunityService.RecoveredEstimate(tallied, 100), "a caravan tally alone (own cargo) never counts as recovery");
            Engagement unsampled = new Engagement { initialOnMap = -1, lastRemaining = 130 };
            T.Eq(0, OpportunityService.RecoveredEstimate(unsampled, 100), "no map-generation count: the committed target is the basis, and extra on the map only lowers it");
            T.Eq(RecoveredBand.Little, BandUtility.RecoveredBandFor(10, 100), "little");
            T.Eq(RecoveredBand.Most, BandUtility.RecoveredBandFor(80, 100), "most");
            T.Eq(RecoveredBand.All, BandUtility.RecoveredBandFor(100, 100), "all");
        }

        private static void Lifecycle()
        {
            TestNet n = new TestNet();
            Opportunity early = MakeCache(n);
            n.sites.sites.Remove(early.site.id);
            n.ctx.Opportunities.OnSiteDestroyed(early.id);
            T.Eq(OpportunityState.Destroyed, early.state, "destroyed by others before the window");
            Opportunity late = MakeCache(n);
            n.clock.Now = late.expiresTick;
            n.ctx.Opportunities.OnSiteDestroyed(late.id);
            T.Eq(OpportunityState.Expired, late.state, "timed out without a visit");
            Opportunity gone = MakeCache(n);
            n.sites.sites.Remove(gone.site.id);
            n.ctx.Opportunities.SampleJob(new ScheduledJob { kind = JobKinds.OppSample, target = gone.id.Value });
            T.Eq(OpportunityState.Vanished, gone.state, "reconciliation: vanished without a callback");
            Opportunity inv = MakeCache(n);
            n.ctx.Opportunities.Invalidate(inv, "DefMissing");
            T.Eq(OpportunityState.Invalidated, inv.state, "invalidated");
            T.Check(n.sites.SiteExists(inv.site), "the site is left for vanilla to time out");
        }

        private static void WarnAndClose()
        {
            TestNet n = new TestNet();
            NetworkActor fixer = n.AddFixer("Thorough");
            n.ctx.Intel.Submit(SourceKey.ForActor(fixer.id), "TestSteel");
            IntelRequest r = n.Only();
            IntelTests.ForceLead();
            n.ResolveRound(r);
            Opportunity o = n.ctx.opportunities.Get(n.ctx.intel.Get(r.leads[0]).opportunity);
            T.Check(n.scheduler.Find(JobKinds.OppWarn, o.id.Value) != null, "expiry warning scheduled");
            n.AdvanceTo(o.expiresTick - Ticks.PerDay + 1);
            T.Check(o.expiryWarned, "warned about a day before the window closes");
            T.Eq(1, n.recorder.Count(EventKeys.OpportunityExpiringSoon), "one warning event (letter)");
            n.sites.sites.Remove(o.site.id);
            n.ctx.Opportunities.OnSiteDestroyed(o.id);
            n.Advance(Ticks.PerDay * 2);
            T.Eq(OpportunityState.Closed, o.state, "closed after the archive delay");
        }
    }
}
