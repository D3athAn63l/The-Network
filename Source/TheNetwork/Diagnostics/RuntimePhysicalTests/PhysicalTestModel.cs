using System;
using System.Collections.Generic;
using System.Text;

namespace TheNetwork.Diagnostics.RuntimePhysicalTests
{
    /// <summary>
    /// Tier P, the physical integration suite (PHYSICAL_LIFECYCLE § 21.2): the PURE half (no RimWorld types), so the headless tests can
    /// prove the guard itself. Names, the scenario table, the runtime-only session arm and the verdict.
    /// </summary>
    public static class PhysicalTestIds
    {
        /// <summary>A separate Dev Mode category: never reachable from Quick smoke or Full safe regression.</summary>
        public const string Category = "The Network (PHYSICAL TESTS: disposable environment only)";

        /// <summary>The exact phrase the session arm requires (typed, never clicked).</summary>
        public const string ArmPhrase = "ARM PHYSICAL TESTS";

        public const string LogPrefix = "[TheNetwork][PHYS] ";

        /// <summary>Every entity the suite itself creates (disposable pawns, fixture factions) carries this tag plus the run id.</summary>
        public const string TestTagPrefix = "TheNetwork.Test.";

        /// <summary>The dedicated test map's world object (a plain MapParent def owned by the suite; 1.6/Defs/PhysicalTests).</summary>
        public const string TestMapDef = "TheNetwork_PhysicalTestSite";

        /// <summary>The episode cause of every suite-driven episode: "PhysicalTest:&lt;runId&gt;:&lt;scenario&gt;" (provenance, never identity).</summary>
        public const string DevKeyPrefix = "PhysicalTest:";

        /// <summary>The name prefix of the suite's own fixture factions (RT-PHYX-011/012/007), so cleanup can recognise them.</summary>
        public const string FixtureFactionName = "Network test fixture";

        /// <summary>A suite visit lasts this long at the chill spot (the same vanilla Lord as production, a shorter stay; runtime-only override).</summary>
        public const int TestVisitTicks = 1250;

        public static string RunTag(string runId)
        {
            return TestTagPrefix + runId;
        }

        public static string DevKey(string runId, string scenarioId)
        {
            return DevKeyPrefix + runId + ":" + scenarioId;
        }

        public static bool IsTestDevKey(string devKey)
        {
            return devKey != null && devKey.StartsWith(DevKeyPrefix, StringComparison.Ordinal);
        }
    }

    /// <summary>What a scenario needs from the Solo it drives.</summary>
    public enum SoloNeed
    {
        /// <summary>No Solo (the scenario makes its own disposable pawns).</summary>
        None,

        /// <summary>A never-materialized Solo (custody Unmaterialized): first creation, or a destructive scenario that must not spend a stored person.</summary>
        Fresh,

        /// <summary>A stored retained Solo (custody Stored): rematerialization and the registry checks.</summary>
        Stored,

        /// <summary>Either; a fresh one is preferred.</summary>
        Any
    }

    /// <summary>One physical scenario as the owner sees it in the menu.</summary>
    public sealed class PhysicalScenarioInfo
    {
        public string id;
        public string name;
        public SoloNeed solo;
        public bool armed = true;
        public string slice = "3.1";

        /// <summary>The Dev Mode label: the scenario id and name FIRST, so nobody translates letters into menu items.</summary>
        public string Label => id + " — " + name;
    }

    /// <summary>The 3.1 scenario table (§ 21.2). RT-PHYX-013/014 are 3.2 and are deliberately absent.</summary>
    public static class PhysicalScenarioTable
    {
        public static readonly List<PhysicalScenarioInfo> All = new List<PhysicalScenarioInfo>
        {
            new PhysicalScenarioInfo { id = "RT-PHYX-001", name = "First materialization", solo = SoloNeed.Fresh },
            new PhysicalScenarioInfo { id = "RT-PHYX-002", name = "Normal visitor exit", solo = SoloNeed.Any },
            new PhysicalScenarioInfo { id = "RT-PHYX-003", name = "Downed and recovery", solo = SoloNeed.Any },
            new PhysicalScenarioInfo { id = "RT-PHYX-004", name = "Killed: death recorded once", solo = SoloNeed.Fresh },
            new PhysicalScenarioInfo { id = "RT-PHYX-005", name = "Test map removed while present", solo = SoloNeed.Any },
            new PhysicalScenarioInfo { id = "RT-PHYX-006", name = "Same-pawn rematerialization", solo = SoloNeed.Stored },
            new PhysicalScenarioInfo { id = "RT-PHYX-007", name = "Registry, redress and GC protection", solo = SoloNeed.Stored },
            new PhysicalScenarioInfo { id = "RT-PHYX-008", name = "Temporary faction lifecycle", solo = SoloNeed.Any },
            new PhysicalScenarioInfo { id = "RT-PHYX-009", name = "Unsupported custody: dev arrest quarantines", solo = SoloNeed.Any },
            new PhysicalScenarioInfo { id = "RT-PHYX-010", name = "Save/load matrix", solo = SoloNeed.Any, slice = "3.1 (S24)" },
            new PhysicalScenarioInfo { id = "RT-PHYX-011", name = "Role-constrained real pawn generation", solo = SoloNeed.None, slice = "3.1 (S25)" },
            new PhysicalScenarioInfo { id = "RT-PHYX-012", name = "Truthful aging mechanism", solo = SoloNeed.None, slice = "3.1 (S12)" },
            new PhysicalScenarioInfo { id = "RT-PHYX-015", name = "Normal-exit M1 regression", solo = SoloNeed.Any, slice = "3.1 (M1)" },
            new PhysicalScenarioInfo { id = "RT-PHYX-016", name = "Map-removal M1 regression", solo = SoloNeed.Any, slice = "3.1 (M1)" }
        };

        public static PhysicalScenarioInfo Get(string id)
        {
            for (int i = 0; i < All.Count; i++) if (All[i].id == id) return All[i];
            return null;
        }
    }

    /// <summary>
    /// The session arm (§ 21.2): a RUNTIME-ONLY flag bound to one game object. It is false after every load and after quitting (a different
    /// game object, or none), it is spent by every destructive action, and nothing about it is ever saved. Pure: the game is an opaque
    /// object, so the headless tests drive it with stand-ins.
    /// </summary>
    public sealed class PhysicalTestArm
    {
        private object armedFor;
        private int spentCount;

        public string LastChange { get; private set; } = "never armed this session";

        /// <summary>Arms ONE destructive action for this game. Refused unless the typed text is exactly the phrase.</summary>
        public bool Arm(object game, string typed, out string refusal)
        {
            refusal = null;
            if (game == null) refusal = "no game is running";
            else if (typed == null || typed != PhysicalTestIds.ArmPhrase) refusal = "type exactly \"" + PhysicalTestIds.ArmPhrase + "\" (capitals, no extra spaces)";
            if (refusal != null) return false;
            armedFor = game;
            LastChange = "armed";
            return true;
        }

        public bool IsArmedFor(object game)
        {
            return game != null && armedFor != null && ReferenceEquals(armedFor, game);
        }

        /// <summary>Consumes the arm (one arm = one destructive action). True when it was armed for this game.</summary>
        public bool Spend(object game, string what)
        {
            bool was = IsArmedFor(game);
            armedFor = null;
            if (was)
            {
                spentCount++;
                LastChange = "spent by " + what;
            }
            return was;
        }

        /// <summary>Clears the arm (load, new game, cleanup). Idempotent.</summary>
        public void Clear(string why)
        {
            if (armedFor != null) LastChange = "cleared: " + why;
            armedFor = null;
        }

        public int SpentCount => spentCount;
    }

    public enum PhysicalOutcome
    {
        Running,
        Pass,
        Fail,
        Inconclusive,
        Stopped
    }

    /// <summary>The evidence of one run: checks, notes and gaps, in order. FAIL beats INCONCLUSIVE beats PASS.</summary>
    public sealed class PhysicalVerdict
    {
        public readonly string scenario;
        public readonly string runId;
        public readonly List<string> lines = new List<string>();
        public int passed;
        public int failed;
        public int gaps;
        public bool stopped;
        public bool finished;

        public PhysicalVerdict(string scenario, string runId)
        {
            this.scenario = scenario;
            this.runId = runId;
        }

        public bool Check(bool condition, string what)
        {
            if (condition)
            {
                passed++;
                lines.Add("  PASS  " + what);
            }
            else
            {
                failed++;
                lines.Add("  FAIL  " + what);
            }
            return condition;
        }

        public void Fail(string what)
        {
            failed++;
            lines.Add("  FAIL  " + what);
        }

        public void Gap(string what)
        {
            gaps++;
            lines.Add("  GAP   " + what + " (inconclusive)");
        }

        public void Note(string what)
        {
            lines.Add("  note  " + what);
        }

        public PhysicalOutcome Outcome
        {
            get
            {
                if (failed > 0) return PhysicalOutcome.Fail;
                if (stopped) return PhysicalOutcome.Stopped;
                if (!finished) return PhysicalOutcome.Running;
                if (gaps > 0 || passed == 0) return PhysicalOutcome.Inconclusive;
                return PhysicalOutcome.Pass;
            }
        }

        public string Render(string title)
        {
            StringBuilder b = new StringBuilder();
            b.AppendLine(PhysicalTestIds.LogPrefix + "===== " + title + ": " + Outcome.ToString().ToUpperInvariant() + " (run " + runId + ") =====");
            for (int i = 0; i < lines.Count; i++) b.AppendLine(lines[i]);
            b.Append(PhysicalTestIds.LogPrefix + "===== end " + scenario + ": " + passed + " passed, " + failed + " failed, " + gaps + " inconclusive =====");
            return b.ToString();
        }
    }

    /// <summary>
    /// The facts the guard checks before any armed action (§ 21.2): never a guess about whether the save matters. Each refusal is named.
    /// </summary>
    public static class PhysicalTestGuard
    {
        public static string Refusal(bool devMode, bool armed, bool networkRunning, bool adapterAvailable, bool runActive, int incompleteEpisodes)
        {
            if (!devMode) return "Dev Mode is off";
            if (!armed) return "not armed: use \"PHYX — Arm physical tests\" and type the phrase (the arm is cleared on load and spent by each action)";
            if (!networkRunning) return "the Network runtime is not running in this game (unpause for one tick and retry)";
            if (!adapterAvailable) return "the physical adapter is unavailable (prepared for removal, or no world)";
            if (runActive) return "another physical test run is in progress (PHYX — Show status / Stop current run)";
            if (incompleteEpisodes > 0) return incompleteEpisodes + " physical episode(s) are not complete yet (Planned, Open, Quarantined or release pending): let them finish, see the Episode Monitor";
            return null;
        }
    }
}
