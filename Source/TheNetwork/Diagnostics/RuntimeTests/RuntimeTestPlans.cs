using System.Collections.Generic;
using TheNetwork.Diagnostics.RuntimeTests.Suites;

namespace TheNetwork.Diagnostics.RuntimeTests
{
    /// <summary>The named plans a Dev Mode action (or a headless test) can run. Order inside a plan is fixed.</summary>
    public static class RuntimeTestPlans
    {
        public const string QuickName = "Quick smoke";
        public const string FullName = "Full safe regression";
        public const string LiveName = "Live integration scan";
        public const string SandboxName = "Sandbox suites";

        /// <summary>Seconds or less: the mod is loaded and started in this process (game only).</summary>
        public static RuntimeTestPlan QuickSmoke(IRuntimeTestHost host)
        {
            return new RuntimeTestPlan(QuickName).AddRange(RuntimeSmokeSuite.Cases(host));
        }

        /// <summary>Read-only checks of the real integration against the loaded game (game only).</summary>
        public static RuntimeTestPlan LiveScan(IRuntimeTestHost host)
        {
            return new RuntimeTestPlan(LiveName).AddRange(RuntimeLiveSuite.Cases(host));
        }

        /// <summary>The isolated scenarios (procurement, careers, spatial): production services in scratch worlds. Runs in any host.</summary>
        public static RuntimeTestPlan SandboxOnly(IRuntimeTestHost host)
        {
            return new RuntimeTestPlan(SandboxName).AddRange(SandboxCases(host));
        }

        /// <summary>Every safe suite: smoke, the read-only live scan and the sandbox scenarios (game only).</summary>
        public static RuntimeTestPlan FullSafe(IRuntimeTestHost host)
        {
            RuntimeTestPlan plan = new RuntimeTestPlan(FullName);
            plan.AddRange(RuntimeSmokeSuite.Cases(host));
            plan.AddRange(RuntimeLiveSuite.Cases(host));
            plan.AddRange(SandboxCases(host));
            return plan;
        }

        private static IEnumerable<RuntimeTestCase> SandboxCases(IRuntimeTestHost host)
        {
            foreach (RuntimeTestCase c in ProcurementRuntimeSuite.Cases(host)) yield return c;
            foreach (RuntimeTestCase c in CareerRuntimeSuite.Cases(host)) yield return c;
            foreach (RuntimeTestCase c in SpatialRuntimeSuite.Cases(host)) yield return c;
        }
    }
}
