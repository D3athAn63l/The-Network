using System;
using System.Collections.Generic;
using System.Text;
using LudeonTK;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using Verse;

namespace TheNetwork.Diagnostics
{
    /// <summary>
    /// Phase 2.75 dev actions (DEBUGGING § 3): contractor careers. Development mode only. Readouts go to the
    /// log; none of these is reachable from the normal UI, and the grants go through the same service paths as
    /// real play (a granted score still derives its band; granted funds still saturate).
    /// </summary>
    public static partial class NetworkDevActions
    {
        private const string CatCareer = "The Network (Phase 2.75)";

        [DebugAction(CatCareer, "Inspect contractor career…", allowedGameStates = AllowedGameStates.Playing)]
        public static void InspectCareer()
        {
            NetworkRuntime rt = Rt;
            if (rt == null) return;
            PickContractor(rt, a => Out(rt.Ctx.Career.Describe(a)), false);
        }

        [DebugAction(CatCareer, "Grant reputation to contractor…", allowedGameStates = AllowedGameStates.Playing)]
        public static void GrantReputation()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            PickContractor(rt, a =>
            {
                List<FloatMenuOption> opts = new List<FloatMenuOption>();
                foreach (int points in new[] { 10, 50, 100, 300, 800, 2000 })
                {
                    int captured = points;
                    opts.Add(new FloatMenuOption("+" + points, () =>
                    {
                        FameBand before = a.reputation.fame;
                        int applied = rt.Ctx.Career.AddReputation(a, captured, false);
                        StateVersion.Bump();
                        Out("[TheNetwork] " + a.name.Display + ": +" + applied + " reputation (score " + a.reputation.score + "); fame " + before + " → " + a.reputation.fame + ".");
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(opts));
            });
        }

        [DebugAction(CatCareer, "Add test funds to contractor…", allowedGameStates = AllowedGameStates.Playing)]
        public static void AddFunds()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            PickContractor(rt, a =>
            {
                List<FloatMenuOption> opts = new List<FloatMenuOption>();
                foreach (int silver in new[] { 500, 5000, 50000, 500000, 2000000000 })
                {
                    int captured = silver;
                    opts.Add(new FloatMenuOption("+" + silver + " silver", () =>
                    {
                        ContractorSimulation sim = a.Get<ContractorSimulation>();
                        int applied = rt.Ctx.Career.MoveFunds(sim, captured, FundsFlow.Dev);
                        StateVersion.Bump();
                        Out("[TheNetwork] " + a.name.Display + ": funds +" + applied + " (now " + sim.funds + "; the bound saturates, it never wraps).");
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(opts));
            });
        }

        [DebugAction(CatCareer, "Run career advancement now…", allowedGameStates = AllowedGameStates.Playing)]
        public static void RunAdvancement()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            PickContractor(rt, a =>
            {
                int cost, reserve;
                AdvancementBlock block = rt.Ctx.Career.BlockedBy(a, out cost, out reserve);
                bool advanced = block == AdvancementBlock.None && rt.Ctx.Career.RunAdvancement(a);
                StateVersion.Bump();
                Out("[TheNetwork] Advancement for " + a.name.Display + ": " + (advanced ? "advanced (cost " + cost + ")" : "not now: " + block + (cost > 0 ? " (cost " + cost + ", reserve " + reserve + ")" : "")) + ".\n" + rt.Ctx.Career.Describe(a));
            });
        }

        [DebugAction(CatCareer, "Dump career distribution…", allowedGameStates = AllowedGameStates.Playing)]
        public static void DumpCareerDistribution()
        {
            NetworkRuntime rt = Rt;
            if (rt == null) return;
            CareerDistribution d = CareerDistribution.Of(rt.Ctx);
            StringBuilder sb = new StringBuilder("[TheNetwork] Career distribution over " + d.active + " active contractors\n");
            sb.Append(d.Text());
            CareerCounters c = rt.Ctx.Career.counters;
            sb.AppendLine("  this session: " + c.outcomesApplied + " career results applied, " + c.fameChanges + " fame changes, " + c.advancements + " equipment advances (" + c.advancementRuns + " advancement checks), "
                + "funds flows credit " + c.Flow(FundsFlow.Credit) + " clawback " + c.Flow(FundsFlow.ClawBack) + " upkeep " + c.Flow(FundsFlow.Upkeep) + " repair " + c.Flow(FundsFlow.Repair) + " recruitment " + c.Flow(FundsFlow.Recruitment) + " advancement " + c.Flow(FundsFlow.Advancement));
            Out(sb.ToString());
        }
    }
}
