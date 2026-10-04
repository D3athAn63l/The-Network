using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using TheNetwork.Core;
using TheNetwork.Diagnostics.RuntimeTests;
using TheNetwork.Diagnostics.RuntimeTests.Suites;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Operations;
using TheNetwork.Domain.Physical;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;
using TheNetwork.Settings;
using Verse;

namespace TheNetwork.Tests
{
    /// <summary>
    /// Phase 3.0 (PHYSICAL_LIFECYCLE § 21.1): the headless half of the RT-PHYS tier (the cases § 21.1 marks headless-only: 007, 011,
    /// 015, 027, 028) plus the Phase 3.0 structural checks: the save-format bump and its no-op migration, the renamed entity kind,
    /// the fail-closed production port, the source tripwires, compaction, follow-up re-entrancy and zero idle cost. The in-game-safe
    /// RT-PHYS cases run headlessly too, through Runner.SandboxPhysicalSuite. Everything here runs over the scriptable fake port.
    /// </summary>
    public static class PhysicalLifecycleTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("Phys.RT007_SaveLoadRoundTrip", SaveLoadRoundTrip));
            t.Add(new KeyValuePair<string, Action>("Phys.RT011_WriterInventoryHonoursTheGate", WriterInventory));
            t.Add(new KeyValuePair<string, Action>("Phys.RT011_GateBlocksEveryAbstractWriter", GateBlocksWriters));
            t.Add(new KeyValuePair<string, Action>("Phys.RT015_PlannedAtLoadResolvesByEvidence", PlannedAtLoad));
            t.Add(new KeyValuePair<string, Action>("Phys.RT027_CommitPurity", CommitPurity));
            t.Add(new KeyValuePair<string, Action>("Phys.RT027_ParityWithTheAbstractCasualtyPath", Parity));
            t.Add(new KeyValuePair<string, Action>("Phys.RT028_FinishPendingAcrossSaveLoad", FinishPendingAcrossLoad));
            t.Add(new KeyValuePair<string, Action>("Phys.FollowUpRetryIsExactlyOnce", FollowUpExactlyOnce));
            t.Add(new KeyValuePair<string, Action>("Phys.InvalidPlanChangesNothing", InvalidPlan));
            t.Add(new KeyValuePair<string, Action>("Phys.StoredAloneIsNotAbstract", StoredAloneInsufficient));
            t.Add(new KeyValuePair<string, Action>("Phys.MigrationV4ToV5InventsNothing", MigrationInventsNothing));
            t.Add(new KeyValuePair<string, Action>("Phys.SaveFormatBumpedExactlyOnce", SaveFormatOnce));
            t.Add(new KeyValuePair<string, Action>("Phys.EpisodeKindKeepsValueNine", EpisodeKind));
            t.Add(new KeyValuePair<string, Action>("Phys.UnavailablePortFailsClosed", ProductionFailsClosed));
            t.Add(new KeyValuePair<string, Action>("Phys.NoProductionPhysicalCreation", NoPhysicalCreation));
            t.Add(new KeyValuePair<string, Action>("Phys.CompactionRefusesPendingStages", Compaction));
            t.Add(new KeyValuePair<string, Action>("Phys.ZeroIdleCost", ZeroIdleCost));
            t.Add(new KeyValuePair<string, Action>("Phys.DurableSnapshotRestoresInPlace", SnapshotRestores));
            t.Add(new KeyValuePair<string, Action>("Phys.Fix1_RefusedPassToWorldBlocksRelease", RefusedPassBlocksRelease));
            t.Add(new KeyValuePair<string, Action>("Phys.Fix1_AlreadyInWorldPawnsIsASuccessfulNoOp", AlreadyWorldPawnIsSuccess));
            t.Add(new KeyValuePair<string, Action>("Phys.Fix2_DuplicateTierRowsAreAggregated", DuplicateTierRows));
            t.Add(new KeyValuePair<string, Action>("Phys.Fix3_ReturnedMissingOrCapturedIsResolved", ReturnedMissingOrCaptured));
            t.Add(new KeyValuePair<string, Action>("Phys.Fix3_DeadAndLostStayImmutable", DeadAndLostImmutable));
            t.Add(new KeyValuePair<string, Action>("Phys.FinalV3_ReturnedMissingMemberSucceedsKilledLeader", ReturnedMissingSucceeds));
            t.Add(new KeyValuePair<string, Action>("Phys.FinalV3_ReturnedCapturedMemberSucceedsKilledLeader", ReturnedCapturedSucceeds));
            t.Add(new KeyValuePair<string, Action>("Phys.FinalV3_InjuredReturnFollowsTheAbstractWoundedRule", InjuredReturnSucceeds));
            t.Add(new KeyValuePair<string, Action>("Phys.FinalV3_NeverPlacedResolvesNothing", NeverPlacedResolvesNothing));
            t.Add(new KeyValuePair<string, Action>("Phys.FinalV3_ProjectedEligibilityMatrix", ProjectedEligibilityMatrix));
        }

        // ================================================================== helpers

        private static PhysicalLifecycleService L(TestNet n) { return n.ctx.Lifecycle; }

        private static ContractorTemplate Fixed(ContractorForm form, string id)
        {
            return new ContractorTemplate
            {
                templateId = "phys-" + id, provenance = TemplateProvenance.Custom, displayName = form + " " + id,
                form = form, startingExperience = ExperienceBand.Veteran, startingFame = FameBand.Local, doctrineStyle = "Professional",
                specialties = new List<string> { "combat acquisition" }
            };
        }

        private static NetworkActor Make(TestNet n, ContractorForm form, string id)
        {
            NetworkActor a = ContractorTests.Make(n, Fixed(form, id));
            n.ctx.Spatial.EnsureInitialized(a);
            return a;
        }

        private static KnownCharacter Self(TestNet n, NetworkActor a) { return n.ctx.characters.Get(a.bindings.embodies); }

        private static KnownCharacter Leader(TestNet n, NetworkActor a) { return n.ctx.characters.Get(a.Get<OrganizationProfile>().leader); }

        private static List<KnownCharacter> Others(TestNet n, NetworkActor a)
        {
            List<KnownCharacter> l = new List<KnownCharacter>();
            OrganizationProfile org = a.Get<OrganizationProfile>();
            foreach (CharacterId id in org.knownMembers)
            {
                KnownCharacter c = n.ctx.characters.Get(id);
                if (c != null && c.id != org.leader) l.Add(c);
            }
            return l;
        }

        private static PhysicalEpisode Begin(TestNet n, NetworkActor a, IEnumerable<KnownCharacter> people, int anon = 0, OperationId op = default(OperationId), bool materialize = true)
        {
            EpisodeRequest r = PhysicalRuntimeSuite.Request(a, people, anon);
            r.cause.operation = op;
            PhysicalEpisode e;
            CommandResult res = L(n).Plan(r, out e);
            T.Check(res.ok, "episode planned (" + res + ")");
            if (materialize && e != null) L(n).Materialize(e);
            return e;
        }

        private static NetworkState StateOf(TestNet n)
        {
            return new NetworkState
            {
                actors = n.ctx.actors, characters = n.ctx.characters, knowledge = n.ctx.knowledge, contracts = n.ctx.contracts,
                operations = n.ctx.operations, consequences = n.ctx.consequences, deployments = n.ctx.episodes
            };
        }

        /// <summary>Saves the TestNet's stores (episodes included) through the real Scribe, loads them back and swaps them in.</summary>
        private static NetworkState SaveLoad(TestNet n, int version = SaveMigrations.Current)
        {
            string path = PersistenceTests.SaveState(StateOf(n), version);
            NetworkState loaded = Load(path);
            File.Delete(path);
            Swap(n, loaded);
            return loaded;
        }

        private static NetworkState Load(string path)
        {
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
            n.ctx.episodes = s.deployments;
        }

        private static LiveFingerprint Print(TestNet n) { return LiveFingerprint.Of(n.ctx, n.ids, n.scheduler, n.journal); }

        private static void Same(LiveFingerprint a, LiveFingerprint b, string what)
        {
            List<string> d = a.Diff(b);
            T.Check(d.Count == 0, what + (d.Count > 0 ? ": " + string.Join("; ", d.ToArray()) : ""));
        }

        /// <summary>The production source tree (the runner runs from a temporary folder; the script passes the repository root).</summary>
        internal static string Root
        {
            get
            {
                string repo = Environment.GetEnvironmentVariable("THENETWORK_REPO");
                if (string.IsNullOrEmpty(repo))
                {
                    for (DirectoryInfo d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
                    {
                        if (Directory.Exists(Path.Combine(d.FullName, "Source/TheNetwork"))) return Path.Combine(d.FullName, "Source/TheNetwork");
                    }
                    throw new InvalidOperationException("the repository source tree was not found (set THENETWORK_REPO)");
                }
                return Path.Combine(repo, "Source/TheNetwork");
            }
        }

        internal static string Src(string relative)
        {
            return File.ReadAllText(Path.Combine(Root, relative));
        }

        internal static List<string> SourceFiles(string dir)
        {
            return new List<string>(Directory.GetFiles(Path.Combine(Root, dir), "*.cs", SearchOption.AllDirectories));
        }

        internal static string[] AllSources()
        {
            return Directory.GetFiles(Root, "*.cs", SearchOption.AllDirectories);
        }

        internal static string Rel(string f)
        {
            return f.Replace('\\', '/').Substring(Root.Replace('\\', '/').Length - "Source/TheNetwork".Length);
        }

        /// <summary>The text of a method body (from its signature to the matching brace).</summary>
        private static int[] Body(string src, string signature)
        {
            int at = src.IndexOf(signature, StringComparison.Ordinal);
            if (at < 0) return null;
            int open = src.IndexOf('{', at);
            int depth = 0;
            for (int i = open; i < src.Length; i++)
            {
                if (src[i] == '{') depth++;
                else if (src[i] == '}' && --depth == 0) return new[] { at, i };
            }
            return null;
        }

        private static string BodyText(string src, string signature)
        {
            int[] b = Body(src, signature);
            return b == null ? null : src.Substring(b[0], b[1] - b[0] + 1);
        }

        // ================================================================== RT-PHYS-007

        private static void SaveLoadRoundTrip()
        {
            TestNet n = new TestNet(9101);
            NetworkActor org = Make(n, ContractorForm.Company, "rt007");
            KnownCharacter lt = Others(n, org)[0];
            PhysicalEpisode e = Begin(n, org, new[] { lt }, 2);
            int creates = n.physical.creates;
            string before = Describe(e) + "|" + Describe(lt);
            NetworkState loaded = SaveLoad(n);
            PhysicalEpisode le = loaded.deployments.Get(e.id);
            KnownCharacter llt = loaded.characters.Get(lt.id);
            T.Check(le != null && llt != null, "the episode and the person load");
            T.Eq(before, Describe(le) + "|" + Describe(llt), "every episode, member, binding and character field round-trips");
            T.Check(n.physical.Resolves(llt.pawn), "the same token resolves after load");
            T.Eq(creates, n.physical.creates, "nothing was generated at load (P3-INV-008)");
            T.Check(loaded.deployments.HasIncomplete(org.id), "the derived index is rebuilt");
            T.Check(!AuthorityGate.CanSimulateAbstractly(llt), "the loaded person is still not abstract");
            // And the loaded episode reconciles normally.
            n.physical.ExitNormally(llt.pawn, 30);
            foreach (EpisodeMember m in le.members) if (!m.IsNamed) n.physical.ExitNormally(m.pawn, 30);
            T.Check(L(n).Reconcile(le, "after load"), "it reconciles after load");
            T.Check(le.IsComplete && AuthorityGate.CanSimulateAbstractly(llt), "and completes");
        }

        private static string Describe(PhysicalEpisode e)
        {
            string s = e.id + "," + e.actor + "," + e.purposeKey + "," + e.cause.devKey + "," + e.cause.operation + "," + e.state + "," + e.createdTick + "," + e.openedTick + "," + e.closedTick
                + "," + e.consequencesApplied + "," + e.releaseApplied + "," + e.followUpApplied + "," + e.publishCursor + "," + e.publishedTick + "," + e.publications.Count + "," + e.whereTile?.tileId + "," + e.whereMapId + "," + e.seed;
            foreach (EpisodeMember m in e.members) s += ";" + m.character + "/" + m.slot + "/" + m.tier + "/" + m.state + "/" + m.outcome + "/" + m.releaseStep + "/" + m.pawn;
            return s;
        }

        private static string Describe(KnownCharacter c)
        {
            return c.id + "," + c.status + "," + c.custody + "," + c.episode + "," + c.heldBy + "," + c.heldSinceTick + "," + c.opRole + "," + c.firstEncounterTick + ","
                + (c.pawn == null ? "-" : c.pawn.thingIdNumber + "/" + c.pawn.defName + "/" + c.pawn.boundTick + "/" + c.pawn.agedThroughTick);
        }

        // ================================================================== RT-PHYS-011

        /// <summary>One known writer: the file, the method, and the gate call it must contain.</summary>
        private struct Writer
        {
            public string file;
            public string signature;
            public string gate;

            public Writer(string file, string signature, string gate)
            {
                this.file = file;
                this.signature = signature;
                this.gate = gate;
            }
        }

        private static readonly Writer[] Inventory =
        {
            new Writer("Domain/Contractors/ContractorService.cs", "public float Strength(NetworkActor a)", "AuthorityGate.CanSimulateAbstractly"),
            new Writer("Domain/Contractors/ContractorService.cs", "public Availability AvailabilityOf(NetworkActor a)", "AuthorityGate.CanSimulateAbstractly"),
            new Writer("Domain/Contractors/ContractorService.cs", "private int AvailableKnown(NetworkActor a, OrganizationProfile org)", "AuthorityGate.CanSimulateAbstractly"),
            new Writer("Domain/Contractors/ContractorService.cs", "public ForceCommitment Checkout(NetworkActor a, OperationId op, float danger)", "AuthorityGate.CanSimulateAbstractly"),
            new Writer("Domain/Contractors/ContractorService.cs", "public void ApplyCasualties(", "AuthorityGate.Allows"),
            new Writer("Domain/Contractors/ContractorService.cs", "private static bool SuccessionEligible(KnownCharacter c)", "AuthorityGate.CanSimulateAbstractly"),
            new Writer("Domain/Contractors/ContractorService.cs", "public void RunSuccession(NetworkActor a, CharacterId oldLeaderId)", "SuccessionEligible"),
            new Writer("Domain/Contractors/UpkeepService.cs", "private void HealCharacter(KnownCharacter c, int now)", "AuthorityGate.Allows"),
            new Writer("Domain/Operations/OperationService.cs", "public void TroubledDeadline(ScheduledJob job)", "AuthorityGate.Allows"),
            new Writer("Domain/Operations/OperationService.cs", "public bool ContractorCanWork(NetworkActor a)", "AuthorityGate.CanSimulateAbstractly"),
            new Writer("Domain/Spatial/SpatialService.cs", "public void CatchUp(NetworkActor a)", "AuthorityGate.SpatialFrozen"),
            new Writer("Domain/Spatial/SpatialService.cs", "private bool MaybeRelocate(NetworkActor a, bool force)", "AuthorityGate.SpatialFrozen"),
            new Writer("Domain/Contractors/CareerService.cs", "public AdvancementBlock BlockedBy(NetworkActor a, out int cost, out int reserve)", "AuthorityGate.HasPhysicalPresence"),
            new Writer("Domain/Contractors/CareerService.cs", "public bool RunAdvancement(NetworkActor a)", "BlockedBy("),
            new Writer("Diagnostics/NetworkDevActions.Phase2.cs", "public static void ForceSuccession()", "AuthorityGate.Allows")
        };

        private static readonly Regex PersonWrite = new Regex(@"\.\s*(status\s*=\s*CharacterStatus\.|woundedUntilTick\s*=[^=]|diedTick\s*=[^=]|deathCauseKey\s*=[^=]|custody\s*=[^=]|episode\s*=[^=]|heldBy\s*=[^=]|heldSinceTick\s*=[^=]|pawn\s*=[^=])");

        private static void WriterInventory()
        {
            // 1. Every known writer calls the gate (directly, or through the named gated function).
            foreach (Writer w in Inventory)
            {
                string body = BodyText(Src(w.file), w.signature);
                T.Check(body != null, "writer found: " + w.file + " " + w.signature);
                if (body != null) T.Check(body.Contains(w.gate), w.signature + " calls " + w.gate);
            }
            // Spatial upkeep reaches the person's location only through the two gated functions.
            string upkeepCore = BodyText(Src("Domain/Spatial/SpatialService.cs"), "private void UpkeepCore(NetworkActor a)");
            T.Check(upkeepCore != null && upkeepCore.Contains("CatchUp(a)") && upkeepCore.Contains("MaybeRelocate(a)") && !upkeepCore.Contains("Advance("), "spatial upkeep goes through CatchUp and MaybeRelocate only");
            // Procurement candidate selection inherits the gate through AvailabilityOf.
            T.Check(Src("Domain/Contracts/Willingness.cs").Contains("ctx.Contractors.AvailabilityOf(a)"), "willingness (procurement selection) reads AvailabilityOf");

            // 2. The shared fate rules are reachable only from the gated abstract path and the lifecycle's commit.
            Regex fateCall = new Regex(@"FateRules\.(SetStatus|Killed|Wounded|Captured|Missing|Lost|ReturnedFree)\(");
            foreach (string f in AllSources())
            {
                string rel = Rel(f);
                if (rel.Contains("/Diagnostics/RuntimeTests/")) continue; // sandbox-only test code
                string src = File.ReadAllText(f);
                if (!fateCall.IsMatch(src)) continue;
                bool allowed = rel.EndsWith("Domain/Contractors/ContractorService.cs") || rel.EndsWith("Domain/Physical/ReconciliationApplier.cs");
                T.Check(allowed, "only the gated abstract path and the commit call the fate rules (" + rel + ")");
                if (rel.EndsWith("ContractorService.cs"))
                {
                    foreach (Match m in fateCall.Matches(src))
                    {
                        int[] b = Body(src, "public void ApplyCasualties(");
                        T.Check(b != null && m.Index > b[0] && m.Index < b[1], "ContractorService calls a fate rule only inside the gated ApplyCasualties");
                    }
                }
            }

            // 3. Every direct write of person-specific truth in production code is inside a listed writer or the lifecycle itself.
            int sites = 0;
            foreach (string f in AllSources())
            {
                string rel = Rel(f);
                if (rel.Contains("/Diagnostics/RuntimeTests/")) continue;
                string src = File.ReadAllText(f);
                foreach (Match m in PersonWrite.Matches(src))
                {
                    int line = src.Substring(0, m.Index).Split('\n').Length;
                    string text = src.Split('\n')[line - 1];
                    if (text.TrimStart().StartsWith("//") || text.TrimStart().StartsWith("///")) continue;
                    if (IsLifecycle(rel) || IsListedWriter(rel, src, m.Index) || IsUnrelated(rel, text)) continue;
                    sites++;
                    T.Check(false, "an unlisted writer of person truth: " + rel + ":" + line + ": " + text.Trim());
                }
            }
            T.Eq(0, sites, "no abstract writer bypasses the authority gate");
        }

        private static bool IsLifecycle(string rel)
        {
            // The lifecycle owns the authority transitions (Materialize and Reconcile, § 3.3 A2) and the shared fate rules.
            return rel.Contains("/Domain/Physical/") || rel.EndsWith("Domain/Contractors/FateRules.cs");
        }

        private static bool IsListedWriter(string rel, string src, int index)
        {
            foreach (Writer w in Inventory)
            {
                if (!rel.EndsWith(w.file)) continue;
                int[] b = Body(src, w.signature);
                if (b != null && index > b[0] && index < b[1]) return true;
            }
            return false;
        }

        /// <summary>Assignments that only share a field NAME with person truth (another type's status, an operation's episode, ...).</summary>
        private static bool IsUnrelated(string rel, string text)
        {
            if (text.Contains("CharacterStatus") || text.Contains("woundedUntilTick") || text.Contains("diedTick") || text.Contains("deathCauseKey")) return false;
            if (text.Contains(".custody") || text.Contains("heldBy") || text.Contains("heldSinceTick")) return false;
            if (text.Contains(".episode =")) return false;
            // ".pawn =" on anything but a KnownCharacter / PawnRef (e.g. a vanilla letter target) is unrelated.
            return text.Contains("pawn =") && !text.Contains("c.pawn") && !text.Contains(".pawn.pawn");
        }

        private static void GateBlocksWriters()
        {
            TestNet n = new TestNet(9111);
            NetworkActor org = Make(n, ContractorForm.Company, "gate-org");
            NetworkActor solo = Make(n, ContractorForm.Solo, "gate-solo");
            KnownCharacter lt = Others(n, org)[0], self = Self(n, solo);
            float strength = n.ctx.Contractors.Strength(org);
            Begin(n, org, new[] { lt });
            Begin(n, solo, new[] { self });
            // Probe only: as if the Solo's record said Wounded while physical. Abstract recovery must still not touch it.
            self.status = CharacterStatus.Wounded;
            self.woundedUntilTick = n.clock.Now + Ticks.PerDay;
            int refused = AuthorityGate.refusedWrites;
            T.Check(n.ctx.Contractors.Strength(org) < strength, "a physical person does not count in strength");
            T.Eq(Availability.Unavailable, n.ctx.Contractors.AvailabilityOf(solo), "a physical Solo is simply unavailable");
            OperationId probe = new OperationId(n.ids.NextId());
            ForceCommitment f = n.ctx.Contractors.Checkout(org, probe, 0.9f);
            T.Check(!f.characters.Contains(lt.id), "checkout skips the physical person");
            n.ctx.Contractors.Return(org, probe, f, null);
            n.clock.Now = self.woundedUntilTick + 1;
            n.ctx.Upkeep.UpkeepJob(new ScheduledJob { kind = ContractorService.UpkeepJob, target = solo.id.Value });
            T.Eq(CharacterStatus.Wounded, self.status, "abstract recovery skips a physical person");
            CasualtyReport r = new CasualtyReport();
            r.fates.Add(new CharacterFate { character = lt.id, fate = Fate.Killed });
            n.ctx.Contractors.ApplyCasualties(org, r, ContractId.None, OperationId.None, false);
            T.Check(lt.IsAlive, "an abstract casualty never reaches a physical person");
            T.Check(!n.ctx.Operations.ContractorCanWork(solo), "a physical Solo cannot work an operation");
            int cost, reserve;
            T.Eq(AdvancementBlock.Committed, n.ctx.Career.BlockedBy(org, out cost, out reserve), "an open episode counts as a job for careers");
            T.Check(AuthorityGate.refusedWrites > refused, "the refusals are counted (" + AuthorityGate.lastRefusal + ")");
        }

        // ================================================================== RT-PHYS-015

        private static void PlannedAtLoad()
        {
            TestNet n = new TestNet(9151);
            NetworkActor a = Make(n, ContractorForm.Company, "rt015");
            List<KnownCharacter> others = Others(n, a);
            KnownCharacter created = others[0], unbound = others[1];
            PhysicalEpisode e = Begin(n, a, new[] { created, unbound }, 0, default(OperationId), false);
            // A save landed between creation and placement: one member is bound (Created), the other was never reached.
            EpisodeMember m = e.MemberFor(created.id);
            PawnRef made = n.physical.Create(new ProjectionRequest { episode = e.id, actor = a.id, character = created.id });
            made.boundTick = n.clock.Now;
            made.agedThroughTick = n.clock.Now;
            created.pawn = made;
            m.pawn = made.Copy();
            m.state = MemberState.Created;
            int creates = n.physical.creates;
            SaveLoad(n);
            PhysicalEpisode le = n.ctx.episodes.Get(e.id);
            T.Eq(EpisodeState.Planned, le.state, "loads Planned");
            T.Check(L(n).OnLoaded() >= 1, "the load pass watches it");
            T.Eq(EpisodeState.Planned, le.state, "the load pass itself decides nothing");
            n.Advance(5);
            T.Eq(EpisodeState.Open, le.state, "resolvable ⇒ Present ⇒ Open, at the first tick");
            T.Eq(MemberState.Present, le.MemberFor(created.id).state, "the bound member is Present");
            T.Eq(creates, n.physical.creates, "never regenerated");

            // The same, but the binding no longer resolves: NeverPlaced, by evidence.
            TestNet n2 = new TestNet(9152);
            NetworkActor b = Make(n2, ContractorForm.Solo, "rt015b");
            KnownCharacter self = Self(n2, b);
            PhysicalEpisode e2 = Begin(n2, b, new[] { self }, 0, default(OperationId), false);
            PawnRef gone = n2.physical.Create(new ProjectionRequest { episode = e2.id, actor = b.id, character = self.id });
            gone.boundTick = n2.clock.Now;
            self.pawn = gone;
            e2.members[0].pawn = gone.Copy();
            e2.members[0].state = MemberState.Created;
            n2.physical.Vanish(gone);
            SaveLoad(n2);
            PhysicalEpisode le2 = n2.ctx.episodes.Get(e2.id);
            L(n2).OnLoaded();
            n2.Advance(5);
            T.Eq(EpisodeState.Closed, le2.state, "no resolvable pointer ⇒ Closed");
            T.Eq(ReconciliationPlanner.CloseNeverPlaced, le2.closeReasonKey, "NeverPlaced");
            T.Eq(MemberOutcome.NeverPlaced, le2.members[0].outcome, "the member was never placed");
            T.Eq(1, n2.physical.creates, "and nothing was generated to replace it");
        }

        // ================================================================== RT-PHYS-027

        private static readonly string[] Impure = { "ctx.bus", ".Publish(", "scheduler", "Scheduler", "NetLog", "Find.", "Verse.", "Rand.", "System.Random", "UnityEngine", "physicalPort", "Port.", "Letter", "ctx.Spatial", "ctx.Operations" };

        private static void CommitPurity()
        {
            foreach (string file in new[] { "Domain/Physical/ReconciliationApplier.cs", "Domain/Contractors/FateRules.cs" })
            {
                string src = Src(file);
                foreach (string bad in Impure) T.Check(!Code(src).Contains(bad), file + " references no " + bad);
            }
            string planner = Code(Src("Domain/Physical/ReconciliationPlanner.cs"));
            foreach (string bad in new[] { "ctx.bus", ".Publish(", "scheduler", "NetLog", "Find.", "Rand.", "System.Random", "physicalPort", "Port." }) T.Check(!planner.Contains(bad), "the planner (pure) references no " + bad);
            // The Applier's vocabulary is closed: no op kind names an event, a job, a letter or a port action.
            foreach (string k in Enum.GetNames(typeof(CommitOpKind)))
            {
                T.Check(k.IndexOf("Event", StringComparison.Ordinal) < 0 && k.IndexOf("Job", StringComparison.Ordinal) < 0 && k.IndexOf("Letter", StringComparison.Ordinal) < 0 && k.IndexOf("Pass", StringComparison.Ordinal) < 0, "commit op " + k + " is a durable assignment");
            }
        }

        /// <summary>Source text without comments (the purity scan reads code, not prose).</summary>
        internal static string Code(string src)
        {
            string noBlock = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Replace(noBlock, @"//[^\n]*", "");
        }

        private delegate void Scenario(TestNet n, NetworkActor a, CasualtyReport r);

        private static void Parity()
        {
            ParityCase("org leader killed, a lieutenant succeeds", ContractorForm.Company, (n, a, r) =>
            {
                List<KnownCharacter> o = Others(n, a);
                r.fates.Add(new CharacterFate { character = Leader(n, a).id, fate = Fate.Killed });
                r.fates.Add(new CharacterFate { character = o[o.Count - 1].id, fate = Fate.Wounded });
                r.killed.Add(new TierCount(Tier.Regular, 1));
                r.wounded.Add(new TierCount(Tier.Recruit, 1));
            });
            ParityCase("org leader killed, nobody eligible: a Veteran is promoted", ContractorForm.Company, (n, a, r) =>
            {
                r.fates.Add(new CharacterFate { character = Leader(n, a).id, fate = Fate.Killed });
                foreach (KnownCharacter c in Others(n, a)) r.fates.Add(new CharacterFate { character = c.id, fate = Fate.Captured });
            });
            ParityCase("a duo loses both: the organization dissolves", ContractorForm.Duo, (n, a, r) =>
            {
                r.fates.Add(new CharacterFate { character = Leader(n, a).id, fate = Fate.Killed });
                foreach (KnownCharacter c in Others(n, a)) r.fates.Add(new CharacterFate { character = c.id, fate = Fate.Killed });
            });
            ParityCase("leader missing, a member killed", ContractorForm.Team, (n, a, r) =>
            {
                r.fates.Add(new CharacterFate { character = Leader(n, a).id, fate = Fate.Missing });
                r.fates.Add(new CharacterFate { character = Others(n, a)[0].id, fate = Fate.Killed });
                r.missing.Add(new TierCount(Tier.Regular, 1));
            });
            ParityCase("a Solo is killed", ContractorForm.Solo, (n, a, r) => r.fates.Add(new CharacterFate { character = Self(n, a).id, fate = Fate.Killed }));
            ParityCase("a Solo is wounded", ContractorForm.Solo, (n, a, r) => r.fates.Add(new CharacterFate { character = Self(n, a).id, fate = Fate.Wounded }));
            ParityCase("no casualty at all", ContractorForm.Team, (n, a, r) => { });
        }

        /// <summary>
        /// Two identical worlds; the same report. A: the existing abstract path (ApplyCasualties). B: the physical split (plan → validate →
        /// Applier under the snapshot), plus the per-operation bookkeeping only an operation does, the actor-end clean-up RELEASE does, and
        /// the outbox published in order. The durable truth and the published events must be identical.
        /// </summary>
        private static void ParityCase(string name, ContractorForm form, Scenario scenario)
        {
            TestNet na = new TestNet(9271), nb = new TestNet(9271);
            NetworkActor a = Make(na, form, "parity"), b = Make(nb, form, "parity");
            T.Eq(a.id, b.id, name + ": identical worlds");
            CasualtyReport ra = new CasualtyReport(), rb = new CasualtyReport();
            scenario(na, a, ra);
            scenario(nb, b, rb);

            na.ctx.Contractors.ApplyCasualties(a, ra, ContractId.None, OperationId.None, false);

            ReconciliationPlan plan = ReconciliationPlanner.PlanCasualties(nb.ctx, b, rb, ContractId.None, OperationId.None);
            ReconciliationPlanner.Validate(nb.ctx, plan);
            List<PublicationSpec> outbox = new List<PublicationSpec>();
            ReconciliationApplier.Commit(plan, new CommitTarget { now = nb.clock.Now, ids = nb.ids, characters = nb.ctx.characters, outbox = outbox }, -1);
            ContractorSimulation sim = b.Get<ContractorSimulation>();
            bool hadCasualties = plan.killed + plan.wounded + plan.captured + plan.missing > 0;
            if (!hadCasualties) nb.ctx.Contractors.MoraleShiftCheck(b, sim); // the abstract path always re-checks; the split only when a fate moved morale
            FateRules.OperationBookkeeping(sim, b.Get<OrganizationProfile>() != null, false);
            if (b.status != ActorStatus.Active)
            {
                nb.scheduler.Cancel(ContractorService.UpkeepJob, b.id.Value);
                nb.ctx.Spatial.OnActorEnded(b);
            }
            foreach (PublicationSpec s in outbox) nb.bus.Publish(Publications.Build(s));

            T.Eq(Canon(na, a), Canon(nb, b), name + ": equivalent durable contractor state (fates, roster, headcount, morale, doctrine, succession, end)");
            T.Eq(Keys(na), Keys(nb), name + ": the same events, in the same order");
            T.Eq(na.ledger.records.Count, nb.ledger.records.Count, name + ": the same history");
            T.Eq(na.ids.PeekNextId, nb.ids.PeekNextId, name + ": the same number of ids drawn");
            if (!plan.addsRecord)
            {
                // With no record added, nothing can even reorder an id: the WHOLE durable world is identical.
                Same(LiveFingerprint.Of(na.ctx, na.ids, na.scheduler, na.journal), LiveFingerprint.Of(nb.ctx, nb.ids, nb.scheduler, nb.journal), name + ": identical durable state, journal and jobs");
            }
        }

        /// <summary>
        /// The contractor's durable truth with people named by NAME, not id: when a successor is promoted the abstract path draws the
        /// history records' ids before the new person's (it publishes mid-way) while the commit draws the person's first (it publishes
        /// after), so only the id numbering may differ.
        /// </summary>
        private static string Canon(TestNet n, NetworkActor a)
        {
            Func<CharacterId, string> nm = id => { KnownCharacter c = n.ctx.characters.Get(id); return c == null ? "-" : c.name.Display; };
            List<string> l = new List<string>();
            ContractorSimulation s = a.Get<ContractorSimulation>();
            OrganizationProfile o = a.Get<OrganizationProfile>();
            l.Add(a.status + "|" + a.endedTick + "|" + a.endReasonKey + "|" + a.Get<ContractorProfile>().suspended + "|" + a.reputation.score);
            l.Add(s.funds + "|" + s.skill.ToString("R") + "|" + s.opsCompleted + "|" + s.opsSincePromotion + "|" + s.morale.confidence.ToString("R") + "|" + s.morale.cohesion.ToString("R")
                + "|" + s.morale.fatigue.ToString("R") + "|" + s.morale.descriptor + "|" + s.morale.descriptorTick + "|" + s.morale.lastShockTick + "|" + s.doctrine.caution.ToString("R") + "|" + s.doctrine.greed.ToString("R")
                + "|" + s.spatial.status + "|" + (s.spatial.anchor?.tileId ?? -1) + "|" + s.spatial.lastUpdateTick + "|" + s.spatial.purpose);
            if (o != null)
            {
                l.Add("leader " + nm(o.leader) + "; lieutenants " + string.Join(",", o.lieutenants.ConvertAll(x => nm(x)).ToArray()) + "; known " + string.Join(",", o.knownMembers.ConvertAll(x => nm(x)).ToArray()));
                l.Add("tiers " + string.Join(",", o.tiers.ConvertAll(x => x.tier + ":" + x.healthy + ":" + x.wounded).ToArray()) + "; committed " + string.Join(",", o.committed.ConvertAll(x => x.tier + ":" + x.healthy).ToArray())
                    + "; recovery " + string.Join(",", o.woundedRecovery.ConvertAll(x => x.tier + ":" + x.count + ":" + x.dueTick).ToArray()) + "; successions " + o.succession.successions + "@" + o.succession.lastSuccessionTick);
            }
            List<string> people = new List<string>();
            foreach (KnownCharacter c in n.ctx.characters.characters)
            {
                if (c.org != a.id && c.embodiedBy != a.id) continue;
                people.Add(c.name.Display + ":" + c.role + ":" + c.status + ":" + c.statusTick + ":" + c.diedTick + ":" + c.deathCauseKey + ":" + c.woundedUntilTick + ":" + c.notability.ToString("R") + ":" + c.custody + ":" + c.createdTick);
            }
            people.Sort(StringComparer.Ordinal);
            l.Add(string.Join(" / ", people.ToArray()));
            l.Add("upkeep job " + n.scheduler.Has(ContractorService.UpkeepJob, a.id.Value));
            return string.Join("\n", l.ToArray());
        }

        private static string Keys(TestNet n)
        {
            List<string> k = new List<string>();
            foreach (NetworkEvent e in n.recorder.events)
            {
                ContractorEvent ce = e as ContractorEvent;
                k.Add(e.typeKey + (ce != null ? "(" + ce.characterName + "→" + ce.successorName + " k" + ce.killed + " w" + ce.wounded + " c" + ce.captured + " m" + ce.missing + " " + ce.descriptorKey + "/" + ce.reasonKey + ")" : ""));
            }
            return string.Join(",", k.ToArray());
        }

        // ================================================================== RT-PHYS-028

        private static void FinishPendingAcrossLoad()
        {
            // (a) between the commit and RELEASE.
            TestNet n = new TestNet(9281);
            NetworkActor a = Make(n, ContractorForm.Solo, "rt028a");
            KnownCharacter c = Self(n, a);
            PhysicalEpisode e = Begin(n, a, new[] { c });
            n.physical.ExitNormally(c.pawn, 31);
            n.physical.ThrowOn("normalize");
            L(n).Reconcile(e, "test");
            T.Check(e.consequencesApplied && !e.releaseApplied, "(a) committed, release pending");
            SaveLoad(n);
            PhysicalEpisode le = n.ctx.episodes.Get(e.id);
            KnownCharacter lc = n.ctx.characters.Get(c.id);
            T.Check(le.consequencesApplied && !le.releaseApplied && le.members[0].releaseStep == 0, "(a) the markers survive the load");
            T.Check(!AuthorityGate.CanSimulateAbstractly(lc) && lc.custody == CustodyState.Stored, "(a) Stored, still blocked after load");
            L(n).OnLoaded();
            n.Advance(5);
            T.Check(le.IsComplete, "(a) resumed and completed");
            T.Eq(1, n.physical.TokenOf(lc.pawn).normalized, "(a) normalized once");
            T.Eq(1, n.recorder.Count(EventKeys.EpisodeClosed), "(a) published once");
            T.Check(AuthorityGate.CanSimulateAbstractly(lc), "(a) abstract only after COMPLETE");

            // (b) between RELEASE and FOLLOW-UP.
            TestNet n2 = new TestNet(9282);
            NetworkActor org = Make(n2, ContractorForm.Company, "rt028b");
            int healthy = org.Get<OrganizationProfile>().Healthy;
            Operation op = LiveOperation(n2, org);
            KnownCharacter member = n2.ctx.characters.Get(op.characters[0]);
            PhysicalEpisode e2 = Begin(n2, org, new[] { member }, 0, op.id);
            T.Eq(OpStatus.Physical, op.status, "(b) the episode holds the operation");
            n2.physical.ExitNormally(member.pawn, 32);
            n2.ctx.Operations.physicalStepFaultAfter = 1;
            L(n2).Reconcile(e2, "test");
            T.Check(e2.releaseApplied && !e2.followUpApplied, "(b) released, follow-up pending");
            T.Eq(OpStatus.Physical, op.status, "(b) a half-done follow-up never looks resolved");
            SaveLoad(n2);
            PhysicalEpisode le2 = n2.ctx.episodes.Get(e2.id);
            Operation lop = n2.ctx.operations.Get(op.id);
            T.Check(!le2.followUpApplied && (lop.physicalSteps & OperationService.PhysForces) != 0, "(b) the marker and the sub-step progress survive the load");
            L(n2).OnLoaded();
            n2.Advance(5);
            T.Check(le2.IsComplete, "(b) resumed and completed");
            T.Eq(OpStatus.Resolved, lop.status, "(b) the operation resolved");
            OrganizationProfile lp = n2.ctx.actors.Get(org.id).Get<OrganizationProfile>();
            T.Eq(healthy, lp.Healthy, "(b) forces returned exactly once");
            T.Eq(0, lp.Committed, "(b) and nothing is left checked out");

            // (c) between FOLLOW-UP and PUBLISH.
            TestNet n3 = new TestNet(9283);
            NetworkActor org3 = Make(n3, ContractorForm.Company, "rt028c");
            KnownCharacter leader = Leader(n3, org3), lt = Others(n3, org3)[0];
            PhysicalEpisode e3 = Begin(n3, org3, new[] { leader, lt });
            n3.physical.Die(leader.pawn);
            n3.physical.Die(lt.pawn);
            L(n3).publishInterruptAfter = 1;
            L(n3).Reconcile(e3, "test");
            List<string> keys = new List<string>();
            foreach (PublicationSpec s in e3.publications) keys.Add(s.typeKey);
            T.Check(e3.followUpApplied && e3.publishCursor == 1 && keys.Count >= 3, "(c) one of " + keys.Count + " specs published");
            int before = EventsOf(n3, org3.id, e3.id);
            SaveLoad(n3);
            PhysicalEpisode le3 = n3.ctx.episodes.Get(e3.id);
            T.Check(le3.publishCursor == 1 && le3.publications.Count == keys.Count, "(c) the outbox and cursor survive the load");
            L(n3).OnLoaded();
            n3.Advance(5);
            T.Check(le3.IsComplete, "(c) resumed and completed");
            T.Eq(keys.Count - 1, EventsOf(n3, org3.id, e3.id) - before, "(c) only the remaining specs were published");
            T.Eq(keys.Count, EventsOf(n3, org3.id, e3.id), "(c) every spec exactly once");
            int dead = 0;
            foreach (KnownCharacter k in n3.ctx.characters.characters) if (k.status == CharacterStatus.Dead) dead++;
            T.Eq(2, dead, "(c) no consequence was replayed");
        }

        /// <summary>Events this actor's episode published (contractor events of the actor, and the episode's own).</summary>
        private static int EventsOf(TestNet n, ActorId actor, EpisodeId episode)
        {
            int k = 0;
            foreach (NetworkEvent e in n.recorder.events)
            {
                ContractorEvent ce = e as ContractorEvent;
                EpisodeEvent ee = e as EpisodeEvent;
                if ((ce != null && ce.actor == actor) || (ee != null && ee.episode == episode)) k++;
            }
            return k;
        }

        /// <summary>A live operation record holding a checkout (test data: no contract, no money).</summary>
        private static Operation LiveOperation(TestNet n, NetworkActor a)
        {
            OperationId id = new OperationId(n.ids.NextId());
            ForceCommitment f = n.ctx.Contractors.Checkout(a, id, 0.9f);
            Operation op = new Operation { id = id, contractor = a.id, contractorName = a.name.Display, status = OpStatus.Troubled, startedTick = n.clock.Now };
            op.characters.AddRange(f.characters);
            op.forces.AddRange(f.forces);
            n.ctx.operations.Add(op);
            return op;
        }

        private static void FollowUpExactlyOnce()
        {
            // The written-off branch has five sub-steps; a throw is injected after each count of them, then the stage is retried.
            for (int k = 0; k <= 4; k++)
            {
                TestNet n = new TestNet(9300 + k);
                NetworkActor org = Make(n, ContractorForm.Company, "followup" + k);
                OrganizationProfile p = org.Get<OrganizationProfile>();
                int healthy = p.Healthy;
                Operation op = LiveOperation(n, org);
                KnownCharacter member = n.ctx.characters.Get(op.characters[0]);
                PhysicalEpisode e = Begin(n, org, new[] { member }, 0, op.id);
                n.physical.Die(member.pawn); // nobody came back: the operation is written off
                n.ctx.Operations.physicalStepFaultAfter = k;
                L(n).Reconcile(e, "test");
                T.Eq(PhysicalResolution.WrittenOff, op.physicalResolution, "k=" + k + ": the commit recorded the result");
                T.Check(e.releaseApplied && !e.followUpApplied, "k=" + k + ": the interrupted follow-up is pending (its marker, not the operation, says so)");
                T.Eq(OpStatus.Physical, op.status, "k=" + k + ": the operation never looks resolved half-way");
                L(n).FinishPending(e);
                L(n).FinishPending(e);
                T.Check(e.IsComplete, "k=" + k + ": completed on retry");
                T.Eq(OpStatus.Resolved, op.status, "k=" + k + ": resolved");
                T.Check(op.outcomeApplied && op.IsFinished, "k=" + k + ": forces returned and finished");
                T.Eq(healthy, p.Healthy, "k=" + k + ": headcount returned exactly once");
                T.Eq(0, p.Committed, "k=" + k + ": nothing left checked out");
                int steps = op.physicalSteps;
                n.ctx.Operations.OnPhysicalResolved(op);
                T.Eq(steps, op.physicalSteps, "k=" + k + ": a re-run repeats nothing");
                T.Eq(ActorStatus.Active, org.status, "k=" + k + ": the organization continues under a successor");
            }
        }

        private static void InvalidPlan()
        {
            TestNet n = new TestNet(9310);
            NetworkActor a = Make(n, ContractorForm.Solo, "invalid");
            KnownCharacter c = Self(n, a);
            PhysicalEpisode e = Begin(n, a, new[] { c });
            n.physical.ExitNormally(c.pawn, 33);
            c.custody = CustodyState.Stored; // corrupted from outside: the plan must refuse, not "fix"
            int events = n.recorder.events.Count;
            T.Check(!L(n).Reconcile(e, "test"), "an invalid plan commits nothing");
            T.Check(!e.consequencesApplied && e.state == EpisodeState.Open, "nothing applied");
            T.Check(e.lastError != null && e.lastError.Contains("CustodyMismatch"), "and says why (" + e.lastError + ")");
            T.Eq(events, n.recorder.events.Count, "nothing published");
            for (int i = 0; i < PhysicalEpisode.MaxAttempts; i++) L(n).Reconcile(e, "retry");
            T.Eq(EpisodeState.Quarantined, e.state, "bounded retries end in quarantine, never in a guess");
        }

        private static void StoredAloneInsufficient()
        {
            KnownCharacter c = new KnownCharacter { id = new CharacterId(5), custody = CustodyState.Stored };
            T.Check(AuthorityGate.CanSimulateAbstractly(c), "Stored with no membership is abstract");
            c.episode = new EpisodeId(77);
            T.Check(!AuthorityGate.CanSimulateAbstractly(c), "Stored with a membership is NOT (release pending)");
            T.Eq(PersonAuthority.PendingRelease, AuthorityGate.AuthorityOf(c), "authority: PendingRelease");
            foreach (CustodyState s in new[] { CustodyState.Deployed, CustodyState.OutOfCustody, CustodyState.Released, CustodyState.Lost })
            {
                c.episode = EpisodeId.None;
                c.custody = s;
                T.Check(!AuthorityGate.CanSimulateAbstractly(c), s + " is never abstract");
            }
            T.Check(!AuthorityGate.CanSimulateAbstractly(null), "no record, no abstraction");
        }

        // ================================================================== save format, migration, entity kind

        private static void MigrationInventsNothing()
        {
            TestNet n = new TestNet(9320);
            NetworkActor org = Make(n, ContractorForm.Company, "v4org");
            NetworkActor solo = Make(n, ContractorForm.Solo, "v4solo");
            FateRules.Wounded(Others(n, org)[0], n.clock.Now, 5);
            string facts = Facts(n);
            // A version-4 file: the deployments node is the old empty reservation and no character carries a Phase 3 field.
            string path = PersistenceTests.SaveState(StateOf(n), 4);
            string xml = File.ReadAllText(path);
            xml = Regex.Replace(xml, @"<deployments>.*?</deployments>|<deployments\s*/>", "<deployments />", RegexOptions.Singleline);
            xml = Regex.Replace(xml, @"\s*<(episode|heldBy|heldSince|opRole|firstEncounter)>[^<]*</\1>", "");
            xml = Regex.Replace(xml, @"\s*<pawn[^>]*?/>|\s*<pawn>.*?</pawn>", "", RegexOptions.Singleline);
            File.WriteAllText(path, xml);
            NetworkState state = Load(path);
            File.Delete(path);
            MigrationContext mc = new MigrationContext();
            T.Eq(SaveMigrations.Current, SaveMigrations.Run(state, 4, mc, 0), "4 → 5");
            T.Check(mc.log.Exists(l => l.Contains("PhysicalLifecycle") || l.Contains("stay abstract")), "the 4 → 5 step ran and said what it did");
            T.Eq(0, state.diagnostics.failedMigrations.Count, "no migration failed");
            T.Eq(0, state.deployments.Count, "no episode is invented");
            foreach (KnownCharacter c in state.characters.characters)
            {
                T.Check(c.custody == CustodyState.Unmaterialized && !c.episode.IsValid && c.pawn == null && c.heldBy == HeldKind.None && c.heldSinceTick == -1
                    && c.opRole == OperationalRole.Unset && c.firstEncounterTick == -1, c.id + ": neutral defaults (no custody, encounter, role or binding history)");
            }
            Swap(n, state);
            T.Eq(facts, Facts(n), "careers, reputation, money, spatial state, wounds and operations are untouched");
        }

        private static string Facts(TestNet n)
        {
            List<string> l = new List<string>();
            foreach (NetworkActor a in n.ctx.actors.actors)
            {
                ContractorSimulation s = a.Get<ContractorSimulation>();
                l.Add(a.id + ":" + a.status + ":" + a.reputation.score + ":" + a.reputation.fame + (s == null ? "" : ":" + s.funds + ":" + s.equipment.tier + ":" + s.career.Classified + ":" + s.spatial.status + ":" + (s.spatial.anchor?.tileId ?? -1) + ":" + s.opsCompleted));
            }
            foreach (KnownCharacter c in n.ctx.characters.characters) l.Add(c.id + ":" + c.status + ":" + c.woundedUntilTick + ":" + c.role + ":" + c.notability.ToString("0.000"));
            return string.Join("|", l.ToArray());
        }

        private static void SaveFormatOnce()
        {
            T.Eq(5, SaveMigrations.Current, "Phase 3.0 is save format 5");
            int fromFour = 0;
            foreach (INetworkMigration m in SaveMigrations.Registry) if (m.From == 4) fromFour++;
            T.Eq(1, fromFour, "exactly one 4 → 5 step");
            T.Eq(5, SaveMigrations.Registry[SaveMigrations.Registry.Count - 1].To, "and nothing beyond it");
        }

        private static void EpisodeKind()
        {
            T.Eq(9, (int)EntityKind.Episode, "the reserved kind keeps its value");
            T.Eq('D', EntityKindUtility.Prefix(EntityKind.Episode), "and its persisted prefix");
            T.Eq(EntityKind.Episode, EntityRef.Parse("D12").Kind, "an old reference parses as an episode");
            T.Eq(new EpisodeId(12), EntityRef.Parse("D12").AsEpisode, "with its id");
            T.Eq("D12", new EpisodeId(12).Ref.ToString(), "and writes back unchanged");
            NetworkState s = new NetworkState();
            s.deployments.Add(new PhysicalEpisode { id = new EpisodeId(4321), actor = new ActorId(1) });
            T.Check(s.MaxEntityId() >= 4321, "MaxEntityId counts episodes");
            IdAllocator ids = new IdAllocator();
            s.RepairIdCounters(ids);
            T.Check(ids.PeekNextId > 4321, "and the counter is raised above them");
        }

        // ================================================================== production safety

        private static void ProductionFailsClosed()
        {
            UnavailablePhysicalWorldPort port = new UnavailablePhysicalWorldPort();
            T.Check(!port.Available, "the production port is never available");
            T.Eq(ObservedKind.Unknown, port.Observe(null, EpisodeId.None).kind, "it observes nothing (Unknown is never terminal)");
            T.Check(!port.Resolves(new PawnRef { thingIdNumber = 5 }), "and resolves nothing");
            T.Eq(PassToWorldCheck.Unknown, port.CheckPassToWorld(null), "and allows no pass");
            T.Throws(() => port.Create(new ProjectionRequest()), "creation is refused");
            T.Throws(() => port.Place(null, EpisodeId.None, null, -1, null), "placement is refused");
            T.Throws(() => port.EnsureEncounterFaction(EpisodeId.None, ActorId.None, null, 0), "no encounter faction is made");
            T.Throws(() => port.ReleaseEncounterFaction(null), "and none is released");
            T.Throws(() => port.PassToWorld(null), "a pass is refused");
            T.Throws(() => port.StripEpisodeTag(null, EpisodeId.None), "a tag strip is refused");

            TestNet n = new TestNet(9330);
            n.ctx.physicalPort = port;
            NetworkActor a = Make(n, ContractorForm.Solo, "prod");
            KnownCharacter c = Self(n, a);
            LiveFingerprint before = Print(n);
            PhysicalEpisode e;
            CommandResult r = L(n).Plan(PhysicalRuntimeSuite.Request(a, new[] { c }), out e);
            T.Check(!r.ok && r.reasonKey == "PhysicalWorldUnavailable", "no episode can be planned over the fail-closed port");
            Same(before, Print(n), "and nothing changed");
            T.Eq(0, n.ctx.episodes.Count, "no episode exists");

            // Phase 3.1: the live runtime holds the REAL adapter (built from the same context, after a fail-closed default); the soak and
            // headless contexts keep the fail-closed port. Who may ask the real adapter to create anything is proven by the source scans.
            string runtime = Src("Core/NetworkRuntime.cs");
            T.Check(runtime.Contains("physicalPort = new Domain.Physical.UnavailablePhysicalWorldPort()") && runtime.Contains("Ctx.physicalPort = PhysicalWorld;"),
                "the live runtime starts fail-closed and then holds the real adapter");
            foreach (string f in AllSources())
            {
                string rel = Rel(f);
                if (rel.Contains("/Diagnostics/RuntimeTests/")) continue;
                T.Check(!File.ReadAllText(f).Contains("FakePhysicalWorldPort"), "no production file names the fake port (" + rel + ")");
            }
        }

        private static readonly string[] PhysicalApis = { "PawnGenerator", "GeneratePawn", "GenSpawn", "WorldPawns", "LordMaker", "MakeNewLord", "FactionGenerator", "NewGeneratedFaction", "HarmonyLib", "0Harmony", "QuestGen", "ThingMaker", "BirthAbsTicks" };

        private static void NoPhysicalCreation()
        {
            List<string> files = SourceFiles("Domain/Physical");
            files.Add(Path.Combine(Root, "Domain/Contractors/FateRules.cs"));
            files.Add(Path.Combine(Root, "Diagnostics/RuntimeTests/FakePhysicalWorldPort.cs"));
            files.Add(Path.Combine(Root, "Diagnostics/RuntimeTests/Suites/PhysicalRuntimeSuite.cs"));
            foreach (string f in files)
            {
                string code = Code(File.ReadAllText(f));
                foreach (string api in PhysicalApis) T.Check(!Regex.IsMatch(code, @"\b" + Regex.Escape(api) + @"\b"), Path.GetFileName(f) + " calls no " + api);
                T.Check(!code.Contains("Find."), Path.GetFileName(f) + " reads no live game state (Find.)");
            }
            // Nothing anywhere ages a pawn by rewriting its birth tick (P3-INV-022): no write, no access to the backing field, and the
            // property is READ only by the physical test tier, as evidence that it never changed.
            Regex birthWrite = new Regex(@"BirthAbsTicks\s*(=(?!=)|\+=|-=|\+\+|--)|birthAbsTicksInt");
            foreach (string f in AllSources())
            {
                string code = Code(File.ReadAllText(f));
                T.Check(!birthWrite.IsMatch(code), "no BirthAbsTicks write (" + Path.GetFileName(f) + ")");
                if (!f.Replace('\\', '/').Contains("/Diagnostics/RuntimePhysicalTests/")) T.Check(!code.Contains("BirthAbsTicks"), "BirthAbsTicks is read only as test evidence (" + Path.GetFileName(f) + ")");
            }
            // Pawn-typed state exists only in the binding's own pointer, the production adapter and the physical test tier (Phase 3.1).
            foreach (Type t in typeof(PawnRef).Assembly.GetTypes())
            {
                bool physical = t.Namespace == "TheNetwork.Integration.Physical" || t.Namespace == "TheNetwork.Diagnostics.RuntimePhysicalTests";
                foreach (System.Reflection.FieldInfo fi in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                {
                    if (fi.FieldType == typeof(Pawn)) T.Check((t == typeof(PawnRef) && fi.Name == "pawn") || physical, "a Pawn field only as PawnRef.pawn or in the physical adapter (" + t.FullName + "." + fi.Name + ")");
                }
            }
            // A real pawn pointer enters a binding in exactly ONE place: the adapter's Create, after the authoritative role verdict (the
            // lifecycle then writes the binding, once, before the pawn is spawned).
            List<string> binders = new List<string>();
            foreach (string f in AllSources())
            {
                string code = Code(File.ReadAllText(f));
                bool binds = Regex.IsMatch(code, @"\.pawn\s*=\s*(?!null)[^=;]*\bPawn\b") || Regex.IsMatch(code, @"new\s+PawnRef\s*\{\s*pawn\s*=(?!\s*pawn\b)");
                if (binds) binders.Add(Rel(f));
            }
            T.Eq(1, binders.Count, "exactly one source binds a real pawn (" + string.Join(", ", binders.ToArray()) + ")");
            T.Check(binders.Count == 1 && binders[0].EndsWith("Integration/Physical/RimWorldPhysicalWorldPort.cs", StringComparison.Ordinal), "and it is the production adapter's Create");
        }

        private static void Compaction()
        {
            int now = 10 * Ticks.PerYear;
            Func<PhysicalEpisode> done = () => new PhysicalEpisode
            {
                id = new EpisodeId(1), state = EpisodeState.Closed, consequencesApplied = true, closedTick = now - 2 * Ticks.PerYear,
                releaseApplied = true, followUpApplied = true, publishedTick = now - 2 * Ticks.PerYear
            };
            T.Check(CompactionService.CanCompact(done(), now, new HashSet<int>()), "a complete, year-old, unreferenced episode may go");
            PhysicalEpisode e;
            e = done(); e.releaseApplied = false;
            T.Check(!CompactionService.CanCompact(e, now, null), "never while RELEASE is incomplete");
            e = done(); e.followUpApplied = false;
            T.Check(!CompactionService.CanCompact(e, now, null), "never while FOLLOW-UP is incomplete");
            e = done(); e.publishedTick = -1;
            T.Check(!CompactionService.CanCompact(e, now, null), "never while PUBLISH is incomplete");
            e = done(); e.publications.Add(new PublicationSpec { typeKey = "x" });
            T.Check(!CompactionService.CanCompact(e, now, null), "never while the outbox holds a spec");
            e = done(); e.state = EpisodeState.Open;
            T.Check(!CompactionService.CanCompact(e, now, null), "never before reconciliation is terminal");
            e = done(); e.consequencesApplied = false;
            T.Check(!CompactionService.CanCompact(e, now, null), "never without its commit");
            e = done(); e.closedTick = now - Ticks.PerDay;
            T.Check(!CompactionService.CanCompact(e, now, null), "not within the year");
            T.Check(!CompactionService.CanCompact(done(), now, new HashSet<int> { 1 }), "never while a character still references it");

            // In a sweep: a pending episode survives; a complete old one goes; the linked operation of a pending one is kept.
            TestNet n = new TestNet(9340);
            NetworkActor a = Make(n, ContractorForm.Solo, "compact");
            KnownCharacter c = Self(n, a);
            PhysicalEpisode pending = Begin(n, a, new[] { c });
            n.physical.ExitNormally(c.pawn, 34);
            n.physical.ThrowOn("strip", 99);
            L(n).Reconcile(pending, "test");
            NetworkState state = StateOf(n);
            n.clock.Now += 3 * Ticks.PerYear;
            bool finished;
            new CompactionService(state, n.scheduler, n.clock, null).Run(n.clock.Now, out finished);
            T.Check(n.ctx.episodes.Get(pending.id) != null, "a closed episode with RELEASE pending is kept");
            n.physical.ClearFaults();
            L(n).FinishPending(pending);
            T.Check(pending.IsComplete, "once complete");
            n.clock.Now += 2 * Ticks.PerYear;
            new CompactionService(state, n.scheduler, n.clock, null).Run(n.clock.Now, out finished);
            T.Check(n.ctx.episodes.Get(pending.id) == null, "it is compacted a year later");
            T.Eq(CustodyState.Stored, c.custody, "and the person's own truth stays where it belongs");
        }

        private static void ZeroIdleCost()
        {
            TestNet n = ContractorTests.WorldWithCast(30, 9350);
            n.ctx.physicalPort = new UnavailablePhysicalWorldPort();
            int refused = AuthorityGate.refusedWrites;
            n.Advance(60 * Ticks.PerDay);
            int watch = 0;
            foreach (ScheduledJob j in n.scheduler.AllJobs) if (j.kind == PhysicalLifecycleService.WatchJob) watch++;
            T.Eq(0, watch, "no episode job exists when nobody is physical");
            T.Eq(0, n.ctx.episodes.Count, "no episode was created by 60 days of play");
            T.Eq(0, L(n).counters.wakeups + L(n).counters.planned + L(n).counters.commits, "the lifecycle did no work");
            T.Eq(refused, AuthorityGate.refusedWrites, "the gate refused nothing (it only confirmed the abstract state)");
            T.Eq(0, L(n).OnLoaded(), "and the load pass schedules nothing");
            foreach (KnownCharacter c in n.ctx.characters.characters) T.Check(c.custody == CustodyState.Unmaterialized && !c.episode.IsValid && c.pawn == null, "every person stays abstract and unbound");
        }

        // ================================================================== PR #8 corrections

        /// <summary>
        /// Fix 1: a never-placed, bound named pawn whose § 7.5 precondition is refused (spawned, held, dead, unknown) keeps RELEASE pending
        /// through the PRODUCTION lifecycle: the cursor stays on the pass, COMPLETE never runs, the link stays and the gate stays closed;
        /// once the observed state allows it, the retry passes it exactly once and completes.
        /// </summary>
        private static void RefusedPassBlocksRelease()
        {
            string[] states = { "Spawned", "Held", "Dead", "Unknown" };
            for (int i = 0; i < states.Length; i++)
            {
                string s = states[i];
                TestNet n = new TestNet(9400 + i);
                NetworkActor a = Make(n, ContractorForm.Solo, "pass-" + s);
                KnownCharacter c = Self(n, a);
                n.physical.failPlace = true;
                n.physical.onPlaceFailed = t =>
                {
                    if (s == "Spawned") t.spawned = true;
                    else if (s == "Held") t.held = true;
                    else if (s == "Dead") t.dead = true;
                    else t.gone = true;
                };
                PhysicalEpisode e = Begin(n, a, new[] { c });
                n.physical.failPlace = false;
                n.physical.onPlaceFailed = null;
                EpisodeMember m = e.members[0];
                FakePhysicalWorldPort.Token tok = n.physical.TokenOf(c.pawn);
                T.Check(e.state == EpisodeState.Closed && e.consequencesApplied && m.outcome == MemberOutcome.NeverPlaced, s + ": committed as NeverPlaced");
                T.Eq((byte)0, m.releaseStep, s + ": the cursor does not advance past PassToWorldIfAllowed");
                T.Check(!e.releaseApplied, s + ": releaseApplied stays false");
                T.Eq(e.id, c.episode, s + ": the episode link stays");
                T.Check(!AuthorityGate.CanSimulateAbstractly(c), s + ": the person stays blocked (custody " + c.custody + ")");
                T.Eq(0, n.physical.passCalls, s + ": no PassToWorld was requested");
                T.Eq(0, n.physical.passRejected, s + ": so none could be rejected either");
                T.Check(e.lastError != null && e.lastError.Contains("PhysicalPreconditionException"), s + ": diagnosed (" + e.lastError + ")");
                // Retries while the precondition still fails change nothing (the watch keeps trying).
                L(n).FinishPending(e);
                n.Advance(PhysicalLifecycleService.WatchPeriod + 5);
                T.Check(!e.releaseApplied && m.releaseStep == 0 && n.physical.passCalls == 0 && !AuthorityGate.CanSimulateAbstractly(c), s + ": retries stay blocked");
                // The observed state becomes valid: not spawned, not held, alive, resolvable, not yet a world pawn.
                tok.spawned = false;
                tok.held = false;
                tok.dead = false;
                tok.gone = false;
                tok.inWorldPawns = false;
                L(n).FinishPending(e);
                T.Check(e.IsComplete, s + ": the retry completes RELEASE (and the episode)");
                T.Eq(1, tok.passedToWorld, s + ": passed to the world exactly once");
                T.Eq((byte)ReleasePolicy.ActionsFor(m).Length, m.releaseStep, s + ": every release action done");
                T.Check(!c.episode.IsValid && AuthorityGate.CanSimulateAbstractly(c) && c.custody == CustodyState.Stored, s + ": only now abstract (Stored)");
                L(n).FinishPending(e);
                L(n).Reconcile(e, "again");
                n.Advance(PhysicalLifecycleService.WatchPeriod + 5);
                T.Check(tok.passedToWorld == 1 && tok.retainCalls == 1 && tok.tagStrips == 1, s + ": exactly once (pass " + tok.passedToWorld + ", retain " + tok.retainCalls + ", strip " + tok.tagStrips + ")");
            }
        }

        /// <summary>Fix 1, the other side: a pawn already in WorldPawns is a SUCCESSFUL observed no-op, never a second pass.</summary>
        private static void AlreadyWorldPawnIsSuccess()
        {
            TestNet n = new TestNet(9410);
            NetworkActor a = Make(n, ContractorForm.Solo, "already");
            KnownCharacter c = Self(n, a);
            n.physical.failPlace = true;
            n.physical.onPlaceFailed = t => t.inWorldPawns = true; // a retained pawn that never left WorldPawns
            PhysicalEpisode e = Begin(n, a, new[] { c });
            n.physical.failPlace = false;
            n.physical.onPlaceFailed = null;
            T.Check(e.IsComplete, "RELEASE completes");
            T.Eq(0, n.physical.passCalls, "no PassToWorld (vanilla already holds it as a world pawn)");
            T.Eq(1, L(n).counters.passSkippedAlreadyWorld, "counted as an observed no-op");
            T.Check(AuthorityGate.CanSimulateAbstractly(c), "and the person is abstract again");
        }

        /// <summary>Fix 2: anonymous headcount is validated per tier over every request row; negative rows are refused.</summary>
        private static void DuplicateTierRows()
        {
            TestNet n = new TestNet(9420);
            NetworkActor org = Make(n, ContractorForm.Company, "tiers");
            OrganizationProfile p = org.Get<OrganizationProfile>();
            p.TierOf(Tier.Regular).healthy = 5;
            int committed = ReconciliationPlanner.PeekCommitted(p, Tier.Regular);
            LiveFingerprint before = Print(n);
            int nextId = n.ids.PeekNextId;
            EpisodeRequest twice = PhysicalRuntimeSuite.Request(org, null);
            twice.anonymous.Add(new TierCount(Tier.Regular, 4));
            twice.anonymous.Add(new TierCount(Tier.Regular, 4));
            PhysicalEpisode e;
            CommandResult r = L(n).Plan(twice, out e);
            T.Check(!r.ok && r.reasonKey == "Headcount", "Regular x4 + Regular x4 against 5 is refused (" + r + ")");
            T.Check(e == null && n.ctx.episodes.Count == 0, "no episode was created");
            T.Eq(5, FateRules.PeekHealthy(p, Tier.Regular), "no headcount changed");
            T.Eq(nextId, n.ids.PeekNextId, "no id was drawn");
            Same(before, Print(n), "the durable state is exactly unchanged");

            EpisodeRequest negative = PhysicalRuntimeSuite.Request(org, null);
            negative.anonymous.Add(new TierCount(Tier.Regular, -1));
            negative.anonymous.Add(new TierCount(Tier.Regular, 2));
            r = L(n).Plan(negative, out e);
            T.Check(!r.ok && r.reasonKey == "Headcount", "a negative row is malformed and refused, never clamped (" + r + ")");
            Same(before, Print(n), "and changes nothing");

            EpisodeRequest huge = PhysicalRuntimeSuite.Request(org, null);
            huge.anonymous.Add(new TierCount(Tier.Regular, int.MaxValue));
            huge.anonymous.Add(new TierCount(Tier.Regular, int.MaxValue));
            r = L(n).Plan(huge, out e);
            T.Check(!r.ok && r.reasonKey == "MemberCount", "rows that would overflow the sum are refused by the bound, never wrapped (" + r + ")");
            Same(before, Print(n), "and change nothing");

            EpisodeRequest fits = PhysicalRuntimeSuite.Request(org, null);
            fits.anonymous.Add(new TierCount(Tier.Regular, 2));
            fits.anonymous.Add(new TierCount(Tier.Regular, 3));
            r = L(n).Plan(fits, out e);
            T.Check(r.ok, "Regular x2 + Regular x3 against 5 fits (" + r + ")");
            L(n).Materialize(e);
            T.Eq(5, e.members.Count, "five anonymous members");
            T.Eq(0, FateRules.PeekHealthy(p, Tier.Regular), "all five checked out");
            T.Eq(committed + 5, ReconciliationPlanner.PeekCommitted(p, Tier.Regular), "into the checked-out headcount");
            foreach (EpisodeMember m in e.members) n.physical.ExitNormally(m.pawn, 41);
            L(n).Reconcile(e, "test");
            T.Check(e.IsComplete, "reconciled");
            T.Eq(5, FateRules.PeekHealthy(p, Tier.Regular), "all five back: counts conserved");
            T.Eq(committed, ReconciliationPlanner.PeekCommitted(p, Tier.Regular), "nothing left checked out");
        }

        /// <summary>
        /// Fix 3: the real Phase 2 state shape (an operation's abstract casualties already made the person Missing or Captured, then the
        /// operation went Troubled), a rescue episode, and a POSITIVE return: unhurt ⇒ Active, injured ⇒ Wounded with the bounded
        /// recovery, applied once, and the person is abstract only after RELEASE COMPLETE.
        /// </summary>
        private static void ReturnedMissingOrCaptured()
        {
            ReturnCase("missing, unhurt", Fate.Missing, 1f, CharacterStatus.Active, 0, 9430);
            ReturnCase("missing, injured", Fate.Missing, 0.5f, CharacterStatus.Wounded, 8, 9431);
            ReturnCase("captured, unhurt", Fate.Captured, 1f, CharacterStatus.Active, 0, 9432);
            ReturnCase("captured, injured", Fate.Captured, 0.3f, CharacterStatus.Wounded, 15, 9433);
        }

        private static void ReturnCase(string label, Fate prior, float health, CharacterStatus expected, int woundDays, int seed)
        {
            TestNet n = new TestNet(seed);
            NetworkActor org = Make(n, ContractorForm.Company, "return" + seed);
            Operation op = LiveOperation(n, org);
            KnownCharacter member = n.ctx.characters.Get(op.characters[op.characters.Count - 1]);
            CasualtyReport r = new CasualtyReport();
            r.fates.Add(new CharacterFate { character = member.id, fate = prior });
            n.ctx.Contractors.ApplyCasualties(org, r, ContractId.None, op.id, false);
            CharacterStatus shape = prior == Fate.Missing ? CharacterStatus.Missing : CharacterStatus.Captured;
            T.Eq(shape, member.status, label + ": the abstract fate is already applied (the real Troubled shape)");
            PhysicalEpisode e = Begin(n, org, new[] { member }, 0, op.id);
            n.physical.ExitNormally(member.pawn, 42, health);
            n.physical.ThrowOn("strip");
            L(n).Reconcile(e, "found");
            T.Check(e.consequencesApplied && !e.releaseApplied, label + ": committed, RELEASE held open");
            T.Eq(expected, member.status, label + ": the positive return resolves the story status");
            T.Eq(CustodyState.Stored, member.custody, label + ": custody Stored");
            T.Check(!AuthorityGate.CanSimulateAbstractly(member), label + ": still not abstract before RELEASE COMPLETE");
            if (expected == CharacterStatus.Wounded) T.Eq(e.committedTick + woundDays * Ticks.PerDay, member.woundedUntilTick, label + ": the bounded recovery (" + woundDays + " days)");
            int until = member.woundedUntilTick, statusTick = member.statusTick;
            L(n).FinishPending(e);
            T.Check(e.IsComplete, label + ": completes");
            T.Check(AuthorityGate.CanSimulateAbstractly(member) && !member.episode.IsValid, label + ": abstract only now");
            T.Eq(OpStatus.Resolved, op.status, label + ": the operation resolved as found");
            L(n).Reconcile(e, "again");
            L(n).FinishPending(e);
            T.Check(member.woundedUntilTick == until && member.statusTick == statusTick, label + ": no second recovery");
            if (expected == CharacterStatus.Wounded)
            {
                n.clock.Now = until + 1;
                n.ctx.Upkeep.UpkeepJob(new ScheduledJob { kind = ContractorService.UpkeepJob, target = org.id.Value });
                T.Eq(CharacterStatus.Active, member.status, label + ": abstract recovery heals it once, at its tick");
            }
            T.Check(member.IsAvailable, label + ": and the person can work again");
        }

        /// <summary>Fix 3, the limits: a return never revives the dead or the Lost; the rule itself and VALIDATE both refuse.</summary>
        private static void DeadAndLostImmutable()
        {
            KnownCharacter dead = new KnownCharacter { id = new CharacterId(3), status = CharacterStatus.Dead, diedTick = 5 };
            FateRules.ReturnedFree(dead, 100);
            T.Check(dead.status == CharacterStatus.Dead && dead.diedTick == 5, "the shared return rule leaves the dead dead");
            KnownCharacter lost = new KnownCharacter { id = new CharacterId(4), status = CharacterStatus.Lost };
            FateRules.ReturnedFree(lost, 100);
            T.Eq(CharacterStatus.Lost, lost.status, "and the Lost Lost");
            KnownCharacter active = new KnownCharacter { id = new CharacterId(5), status = CharacterStatus.Active, statusTick = 7 };
            FateRules.ReturnedFree(active, 100);
            T.Check(active.status == CharacterStatus.Active && active.statusTick == 7, "an Active person is untouched");

            foreach (CharacterStatus bad in new[] { CharacterStatus.Dead, CharacterStatus.Lost })
            {
                TestNet n = new TestNet(9440 + (int)bad);
                NetworkActor a = Make(n, ContractorForm.Solo, "immutable" + bad);
                KnownCharacter c = Self(n, a);
                PhysicalEpisode e = Begin(n, a, new[] { c });
                c.status = bad; // the record says so (an impossible state for a member, set from outside): no return may overwrite it
                n.physical.ExitNormally(c.pawn, 43, 0.5f);
                T.Check(!L(n).Reconcile(e, "test"), bad + ": nothing is committed");
                T.Eq(bad, c.status, bad + ": the status is unchanged");
                T.Check(!e.consequencesApplied, bad + ": no consequence applied");
                T.Check(e.lastError != null && (e.lastError.Contains("DeadTarget") || e.lastError.Contains("LostTarget")), bad + ": VALIDATE refused it (" + e.lastError + ")");
            }
            TestNet n2 = new TestNet(9450);
            NetworkActor solo = Make(n2, ContractorForm.Solo, "dead-plan");
            KnownCharacter d = Self(n2, solo);
            d.status = CharacterStatus.Dead;
            PhysicalEpisode none;
            T.Eq("NotAlive", L(n2).Plan(PhysicalRuntimeSuite.Request(solo, new[] { d }), out none).reasonKey, "and a dead person can never join an episode");
        }

        // ================================================================== FinalV3: projected succession eligibility

        private sealed class SuccessionWorld
        {
            public TestNet n;
            public NetworkActor org;
            public OrganizationProfile p;
            public Operation op;
            public KnownCharacter leader;
            public KnownCharacter b;
            public PhysicalEpisode e;
            public int successions;
            public int people;
        }

        /// <summary>
        /// The real state shape: an operation's abstract Troubled step leaves B Missing or Captured (<paramref name="prior"/>;
        /// Unharmed leaves B Active) and kills every other known person except the leader, who still leads. B is then the ONLY person
        /// who could succeed, and the headcount is stocked, so a plan that excluded B by a stale status would promote a Veteran.
        /// </summary>
        private static SuccessionWorld SuccessionSetup(int seed, Fate prior)
        {
            SuccessionWorld w = new SuccessionWorld { n = new TestNet(seed) };
            w.org = Make(w.n, ContractorForm.Company, "succession" + seed);
            w.p = w.org.Get<OrganizationProfile>();
            w.op = LiveOperation(w.n, w.org);
            w.leader = Leader(w.n, w.org);
            T.Check(w.leader != null && w.op.characters.Contains(w.leader.id), "setup: the leader is on the operation");
            foreach (CharacterId id in w.op.characters)
            {
                if (id == w.leader.id) continue;
                w.b = w.n.ctx.characters.Get(id);
                break;
            }
            T.Check(w.b != null, "setup: a second known person is on the operation");
            List<int> doomed = new List<int>();
            foreach (List<CharacterId> pool in new[] { w.p.lieutenants, w.p.knownMembers })
            {
                foreach (CharacterId id in pool)
                {
                    if (id != w.leader.id && id != w.b.id && !doomed.Contains(id.Value)) doomed.Add(id.Value);
                }
            }
            CasualtyReport r = new CasualtyReport();
            r.fates.Add(new CharacterFate { character = w.b.id, fate = prior });
            foreach (int id in doomed) r.fates.Add(new CharacterFate { character = new CharacterId(id), fate = Fate.Killed });
            w.n.ctx.Contractors.ApplyCasualties(w.org, r, ContractId.None, w.op.id, false);
            w.p.TierOf(Tier.Veteran).healthy = Math.Max(2, w.p.TierOf(Tier.Veteran).healthy);

            if (prior == Fate.Missing) T.Eq(CharacterStatus.Missing, w.b.status, "setup: B is Missing from the abstract Troubled step");
            else if (prior == Fate.Captured) T.Eq(CharacterStatus.Captured, w.b.status, "setup: B is Captured from the abstract Troubled step");
            else T.Eq(CharacterStatus.Active, w.b.status, "setup: B is Active");
            T.Eq(w.leader.id, w.p.leader, "setup: the leader still leads");
            foreach (int id in doomed) T.Check(!w.n.ctx.characters.Get(new CharacterId(id)).IsAlive, "setup: nobody else could lead");
            w.successions = w.p.succession.successions;
            w.people = w.n.ctx.characters.characters.Count;
            return w;
        }

        /// <summary>The leader observed Killed and B positively Returned in the SAME linked episode; RELEASE held open at its first strip.</summary>
        private static void LeaderKilledBReturns(SuccessionWorld w, float health)
        {
            w.e = Begin(w.n, w.org, new[] { w.leader, w.b }, 0, w.op.id);
            w.n.physical.Die(w.leader.pawn);
            w.n.physical.ExitNormally(w.b.pawn, 42, health);
            w.n.physical.ThrowOn("strip");
            L(w.n).Reconcile(w.e, "found");
        }

        /// <summary>B leads, from the plan that returned B: one succession, no promoted record, no dissolution, RELEASE unchanged, exactly once.</summary>
        private static void BLeadsExactlyOnce(SuccessionWorld w, string label)
        {
            T.Check(w.e.consequencesApplied && !w.e.releaseApplied, label + ": committed, RELEASE held open");
            T.Eq(CharacterStatus.Dead, w.leader.status, label + ": the leader is dead");
            T.Eq(w.b.id, w.p.leader, label + ": B is the new leader");
            T.Eq(CharacterRole.Leader, w.b.role, label + ": with the leader's role");
            T.Eq(w.successions + 1, w.p.succession.successions, label + ": exactly one succession");
            T.Eq(w.people, w.n.ctx.characters.characters.Count, label + ": no generic successor promoted, no record duplicated");
            T.Check(w.org.IsActive, label + ": the organization did not dissolve");
            T.Check(w.b.episode == w.e.id && !AuthorityGate.CanSimulateAbstractly(w.b), label + ": B stays physical until RELEASE COMPLETE");
            L(w.n).FinishPending(w.e);
            T.Check(w.e.IsComplete, label + ": completes");
            T.Check(AuthorityGate.CanSimulateAbstractly(w.b) && w.b.custody == CustodyState.Stored && !w.b.episode.IsValid, label + ": abstract only now");
            CharacterStatus status = w.b.status;
            L(w.n).Reconcile(w.e, "again");
            L(w.n).FinishPending(w.e);
            T.Check(w.p.leader == w.b.id && w.p.succession.successions == w.successions + 1 && w.n.ctx.characters.characters.Count == w.people && w.b.status == status,
                label + ": exactly once (no second succession, no second status write)");
        }

        /// <summary>Test A: a Missing member positively returned (unhurt) by the plan that kills the leader is a candidate, and leads.</summary>
        private static void ReturnedMissingSucceeds()
        {
            SuccessionWorld w = SuccessionSetup(9460, Fate.Missing);
            T.Check(ReconciliationPlanner.EligibleAfterPlan(w.b, MemberOutcome.Returned, 0), "Missing + unhurt return: projected eligible");
            LeaderKilledBReturns(w, 1f);
            T.Eq(CharacterStatus.Active, w.b.status, "Missing + unhurt return: B is Active");
            BLeadsExactlyOnce(w, "Missing");
        }

        /// <summary>Test B: the same for a Captured member.</summary>
        private static void ReturnedCapturedSucceeds()
        {
            SuccessionWorld w = SuccessionSetup(9461, Fate.Captured);
            T.Check(ReconciliationPlanner.EligibleAfterPlan(w.b, MemberOutcome.Returned, 0), "Captured + unhurt return: projected eligible");
            LeaderKilledBReturns(w, 1f);
            T.Eq(CharacterStatus.Active, w.b.status, "Captured + unhurt return: B is Active");
            BLeadsExactlyOnce(w, "Captured");
        }

        /// <summary>
        /// Test C: an injured return projects Wounded (not Active, not the stale Missing/Captured). The EXISTING abstract rule, shown
        /// in a twin world where the leader is killed and B wounded in one abstract step, lets a living Wounded person lead; the
        /// physical plan must reach the same answer.
        /// </summary>
        private static void InjuredReturnSucceeds()
        {
            foreach (Fate prior in new[] { Fate.Missing, Fate.Captured })
            {
                int seed = 9462 + (prior == Fate.Missing ? 0 : 1);
                string label = prior + " + injured return";

                SuccessionWorld twin = SuccessionSetup(seed, Fate.Unharmed);
                CasualtyReport both = new CasualtyReport();
                both.woundDays = 8;
                both.fates.Add(new CharacterFate { character = twin.leader.id, fate = Fate.Killed });
                both.fates.Add(new CharacterFate { character = twin.b.id, fate = Fate.Wounded });
                twin.n.ctx.Contractors.ApplyCasualties(twin.org, both, ContractId.None, twin.op.id, false);
                T.Eq(CharacterStatus.Wounded, twin.b.status, label + ": abstract twin, B wounded");
                bool abstractAdmitsWounded = twin.p.leader == twin.b.id;
                T.Check(abstractAdmitsWounded, label + ": the existing abstract rule lets a living Wounded person lead");

                SuccessionWorld w = SuccessionSetup(seed, prior);
                T.Eq(CharacterStatus.Wounded, ReconciliationPlanner.ProjectedStatus(w.b, MemberOutcome.Returned, 8), label + ": projected Wounded");
                T.Eq(abstractAdmitsWounded, ReconciliationPlanner.EligibleAfterPlan(w.b, MemberOutcome.Returned, 8), label + ": projected eligibility matches the abstract rule");
                int now = w.n.clock.Now;
                LeaderKilledBReturns(w, 0.5f);
                T.Eq(CharacterStatus.Wounded, w.b.status, label + ": B is Wounded");
                T.Check(w.b.woundedUntilTick > now && w.b.woundedUntilTick <= now + 60 * Ticks.PerDay, label + ": with a bounded recovery");
                T.Eq(abstractAdmitsWounded, w.p.leader == w.b.id, label + ": the physical plan agrees with the abstract rule");
                int until = w.b.woundedUntilTick;
                BLeadsExactlyOnce(w, label);
                T.Eq(until, w.b.woundedUntilTick, label + ": no second recovery");
            }
        }

        /// <summary>
        /// Test D: NeverPlaced is no return. B (Missing) is in the episode but never placed; the leader is placed and killed. B's
        /// status is not resolved, B is not a candidate, and the existing rule (nobody else alive) promotes a Veteran, unchanged.
        /// </summary>
        private static void NeverPlacedResolvesNothing()
        {
            SuccessionWorld w = SuccessionSetup(9464, Fate.Missing);
            T.Check(!ReconciliationPlanner.EligibleAfterPlan(w.b, MemberOutcome.NeverPlaced, 0), "projected: a NeverPlaced Missing person stays ineligible");
            w.e = Begin(w.n, w.org, new[] { w.b, w.leader }, 0, w.op.id, false);
            EpisodeMember mb = null, ml = null;
            foreach (EpisodeMember m in w.e.members)
            {
                if (m.character == w.b.id) mb = m;
                else if (m.character == w.leader.id) ml = m;
            }
            T.Check(mb != null && ml != null && w.e.members.IndexOf(mb) < w.e.members.IndexOf(ml), "setup: B is placed first");
            w.n.physical.ThrowOn("place"); // the first placement (B's) fails; the leader's succeeds
            L(w.n).Materialize(w.e);
            T.Check(mb.state != MemberState.Present && ml.state == MemberState.Present && w.e.state == EpisodeState.Open, "setup: the leader placed, B never placed");
            w.n.physical.Die(w.leader.pawn);
            L(w.n).Reconcile(w.e, "test");
            T.Check(w.e.consequencesApplied, "committed");
            T.Eq(MemberOutcome.NeverPlaced, mb.outcome, "B's outcome is NeverPlaced");
            T.Eq(CharacterStatus.Missing, w.b.status, "NeverPlaced resolves nothing: B stays Missing");
            T.Check(w.p.leader != w.b.id, "B does not lead");
            T.Eq(w.people + 1, w.n.ctx.characters.characters.Count, "nobody else could lead: the existing rule promotes a Veteran");
            KnownCharacter next = w.n.ctx.characters.Get(w.p.leader);
            T.Check(next != null && next.IsAlive && next.role == CharacterRole.Leader && w.org.IsActive, "the promoted Veteran leads; no dissolution");
            T.Eq(w.successions + 1, w.p.succession.successions, "exactly one succession");
        }

        /// <summary>
        /// Test E: the projection over every status and outcome. Dead and Lost are never eligible and never change; only a positive
        /// return resolves Missing/Captured; NeverPlaced keeps the pre-plan status; non-returning outcomes never lead; the status
        /// half is exactly the abstract rule's; computing a projection writes nothing.
        /// </summary>
        private static void ProjectedEligibilityMatrix()
        {
            foreach (CharacterStatus s in (CharacterStatus[])Enum.GetValues(typeof(CharacterStatus)))
            {
                KnownCharacter c = new KnownCharacter { id = new CharacterId(77), status = s, statusTick = 11 };
                bool alive = s != CharacterStatus.Dead && s != CharacterStatus.Lost;
                bool free = s != CharacterStatus.Captured && s != CharacterStatus.Missing;
                T.Eq(c.IsAlive && free, FateRules.MayLead(s), s + ": MayLead is the abstract rule's status half (alive, not captured, not missing)");

                T.Eq(!alive || free ? s : CharacterStatus.Active, ReconciliationPlanner.ProjectedStatus(c, MemberOutcome.Returned, 0), s + ": unhurt return");
                T.Eq(alive ? CharacterStatus.Wounded : s, ReconciliationPlanner.ProjectedStatus(c, MemberOutcome.Returned, 6), s + ": injured return");
                T.Eq(alive, ReconciliationPlanner.EligibleAfterPlan(c, MemberOutcome.Returned, 0), s + ": a returned person may lead iff alive");
                T.Eq(alive, ReconciliationPlanner.EligibleAfterPlan(c, MemberOutcome.Returned, 6), s + ": also when injured (living Wounded)");
                T.Eq(s, ReconciliationPlanner.ProjectedStatus(c, MemberOutcome.NeverPlaced, 0), s + ": NeverPlaced resolves nothing");
                T.Eq(alive && free, ReconciliationPlanner.EligibleAfterPlan(c, MemberOutcome.NeverPlaced, 0), s + ": NeverPlaced is judged by the pre-plan status");
                foreach (MemberOutcome o in (MemberOutcome[])Enum.GetValues(typeof(MemberOutcome)))
                {
                    if (o == MemberOutcome.Returned || o == MemberOutcome.NeverPlaced) continue;
                    T.Check(!ReconciliationPlanner.EligibleAfterPlan(c, o, 0) && !ReconciliationPlanner.EligibleAfterPlan(c, o, 6), s + " " + o + ": never a candidate");
                    T.Eq(s, ReconciliationPlanner.ProjectedStatus(c, o, 6), s + " " + o + ": no projected change");
                }
                if (!alive) T.Check(!ReconciliationPlanner.EligibleAfterPlan(c, MemberOutcome.Returned, 0) && !ReconciliationPlanner.EligibleAfterPlan(c, MemberOutcome.Returned, 6), s + ": never eligible");
                T.Check(c.status == s && c.statusTick == 11, s + ": projecting writes nothing");
            }
            T.Check(!ReconciliationPlanner.EligibleAfterPlan(null, MemberOutcome.Returned, 0), "no record, no candidate");
        }

        private static void SnapshotRestores()
        {
            OrganizationProfile org = new OrganizationProfile();
            org.tiers.Add(new TierCount(Tier.Regular, 3));
            org.knownMembers.Add(new CharacterId(7));
            TierCount regular = org.tiers[0];
            DurableSnapshot s = new DurableSnapshot().Capture(org);
            regular.healthy = 99;
            org.TierOf(Tier.Veteran).healthy = 4;
            org.knownMembers.Clear();
            org.leader = new CharacterId(8);
            s.Restore();
            T.Eq(3, regular.healthy, "a field restored on the SAME object");
            T.Eq(1, org.tiers.Count, "an added list entry removed");
            T.Check(ReferenceEquals(regular, org.tiers[0]), "and the list holds the same objects");
            T.Eq(1, org.knownMembers.Count, "a cleared list refilled");
            T.Check(!org.leader.IsValid, "a value-type field restored");
            T.Check(s.Covers(org) && s.Covers(org.tiers) && s.Covers(regular) && !s.Covers(new object()), "coverage is exact");
        }
    }
}
