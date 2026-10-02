using System;
using System.Collections.Generic;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Operations;
using TheNetwork.Kernel;
using TheNetwork.Persist;

namespace TheNetwork.Diagnostics.RuntimeTests.Suites
{
    /// <summary>
    /// Small scenario helpers shared by the sandbox suites. They only COMPOSE production calls (post, bid, accept, advance the
    /// sandbox clock): no money, outcome or career rule is reimplemented here. Dev overrides are set and cleared inside one call,
    /// so they are never held across a yield.
    /// </summary>
    internal static class RuntimeScenarios
    {
        /// <summary>Runs the sandbox until the contract is closed, with the next resolution forced (and the forcing always cleared).</summary>
        public static void RunToEnd(RuntimeTestContext ctx, Contract c, OutcomeBand band, bool notTroubled = true, int? secured = null, int? delayTicks = null, int maxDays = 80)
        {
            try
            {
                ProcurementDevOverrides.forceBand = band;
                ProcurementDevOverrides.forceNotTroubled = notTroubled;
                if (secured.HasValue) ProcurementDevOverrides.forceSecured = secured.Value;
                if (delayTicks.HasValue) ProcurementDevOverrides.forceDelayTicks = delayTicks.Value;
                ctx.Assert.Eventually(() => c.IsTerminal, "contract " + c.id.Value + " to close (" + c.status + ")", maxDays * Ticks.PerDay);
            }
            finally
            {
                ctx.ClearOverrides();
            }
        }

        /// <summary>Runs until a condition holds with the next resolution forced; the forcing is cleared before returning.</summary>
        public static void RunUntil(RuntimeTestContext ctx, Func<bool> done, string what, OutcomeBand? band, bool notTroubled = true, int? secured = null, int maxDays = 80)
        {
            try
            {
                if (band.HasValue) ProcurementDevOverrides.forceBand = band.Value;
                ProcurementDevOverrides.forceNotTroubled = notTroubled;
                if (secured.HasValue) ProcurementDevOverrides.forceSecured = secured.Value;
                ctx.Assert.Eventually(done, what, maxDays * Ticks.PerDay);
            }
            finally
            {
                ctx.ClearOverrides();
            }
        }

        /// <summary>The contractor's share of what the player paid on a contract, summed from the ledger's own attribution.</summary>
        public static int CreditOf(Contract c)
        {
            int s = 0;
            foreach (MoneyRecord m in c.ledger) if (m.contractorSilver > 0) s += m.contractorSilver;
            return s;
        }

        public static MoneyRecord Find(Contract c, MoneyDirection dir, MoneyPurpose purpose)
        {
            return c.ledger.Find(m => m.direction == dir && m.purpose == purpose);
        }

        public static int Count(Contract c, MoneyDirection dir, MoneyPurpose purpose)
        {
            int n = 0;
            foreach (MoneyRecord m in c.ledger) if (m.direction == dir && m.purpose == purpose) n++;
            return n;
        }

        /// <summary>Weakens a contractor so a client that cannot pay the balance meets a contractor that HOLDS the goods.</summary>
        public static void MakeHoldingContractor(NetworkActor a)
        {
            ContractorSimulation s = RuntimeTestSandbox.Sim(a);
            s.doctrine.greed = 0.9f;
            s.doctrine.professionalism = 0.3f;
            s.doctrine.loyalty = 0.2f;
        }

        /// <summary>The books balance: every contractor's funds equal the sum of every tallied flow (nothing credited off the books).</summary>
        public static bool BooksBalance(RuntimeTestSandbox sb, out long funds, out long tallied)
        {
            funds = 0;
            foreach (NetworkActor a in sb.Ctx.actors.actors) if (Domain.Contractors.ContractorService.IsNpcContractor(a)) funds += RuntimeTestSandbox.Sim(a).funds;
            tallied = sb.Ctx.Career.counters.Net;
            return funds == tallied;
        }
    }
}
