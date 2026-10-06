using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Physical;
using TheNetwork.Integration.Physical;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Tests
{
    /// <summary>Actual M1 predicates and Verse reference serialization; no generated map or vanilla world-tick claim.</summary>
    public static class Phase32bReservationTests
    {
        public static void Register(List<KeyValuePair<string, Action>> tests)
        {
            tests.Add(new KeyValuePair<string, Action>("GroupReserve.ActiveAnonymousAndPendingPeer", Active));
            tests.Add(new KeyValuePair<string, Action>("GroupReserve.ReleaseAndQuarantineBoundaries", Boundaries));
            tests.Add(new KeyValuePair<string, Action>("GroupReserve.EarlyThingIdBridgeThenPointerAuthority", Bridge));
            tests.Add(new KeyValuePair<string, Action>("GroupReserve.NoteRequiresDurableEpisodeBinding", Note));
            tests.Add(new KeyValuePair<string, Action>("GroupReserve.BadBindingsReportWithoutRepair", BadBindings));
            tests.Add(new KeyValuePair<string, Action>("GroupReserve.PromotionHandsOffSamePawnWithoutGap", Promotion));
            tests.Add(new KeyValuePair<string, Action>("GroupReserve.PromotionRollbackKeepsEpisodeProtection", Rollback));
            tests.Add(new KeyValuePair<string, Action>("GroupReserve.RemovalInertAndRebuild", Removal));
            tests.Add(new KeyValuePair<string, Action>("GroupReserve.RealScribeBridgeBeforeCrossReferences", ScribeBridge));
            tests.Add(new KeyValuePair<string, Action>("GroupReserve.SourceOneM1AndNoWorldScanOrDummyPerson", Source));
        }

        internal static TV Shell<TV>() { return (TV)FormatterServices.GetUninitializedObject(typeof(TV)); }

        private static Pawn Pawn(int id)
        {
            ThingDef def = Shell<ThingDef>();
            def.defName = "Human";
            return new Pawn { def = def, thingIDNumber = id };
        }

        private static PhysicalEpisode Episode(TestNet n, params Pawn[] pawns)
        {
            PhysicalEpisode e = new PhysicalEpisode { id = new EpisodeId(n.ids.NextId()), state = EpisodeState.Open };
            for (int i = 0; i < pawns.Length; i++)
                e.members.Add(new EpisodeMember
                {
                    slot = i, seatRole = OperationalRole.Rifleman, state = MemberState.Present,
                    pawn = new PawnRef { pawn = pawns[i], thingIdNumber = pawns[i].thingIDNumber, defName = "Human", boundTick = 1 }
                });
            n.ctx.episodes.Add(e);
            return e;
        }

        private static void Active()
        {
            TestNet n = new TestNet(9931);
            Pawn captured = Pawn(32001), pending = Pawn(32002), unrelated = Pawn(32003);
            PhysicalEpisode e = Episode(n, captured, pending);
            e.members[0].observed = ObservedKind.HeldByPlayer;
            // Terminal observation of one member does not close the Episode while its peer remains Pending.
            e.members[0].outcome = MemberOutcome.HeldByPlayer;
            RetainedPawnRegistry r = new RetainedPawnRegistry(n.ctx);
            RegistryLoadReport report = r.Rebuild();
            T.Check(r.Reserves(captured) && r.IsTemporaryReserved(captured), "captured anonymous slot is protected by its Episode");
            T.Check(r.Reserves(pending), "peer remains protected while Pending");
            T.Check(!r.Reserves(unrelated), "an unrelated anonymous Pawn is not protected");
            T.Eq(e.id, r.EpisodeOf(captured), "reservation retains durable Episode provenance");
            T.Eq(CharacterId.None, r.CharacterOf(captured), "reservation creates no person identity");
            T.Eq(0, n.ctx.characters.Count, "no dummy character");
            T.Eq(2, report.temporaryRequired, "two temporary bindings are required");
            T.Eq(2, report.temporaryCovered, "two temporary bindings are covered");
            T.Eq(0, report.namedCovered, "named category remains empty");
            T.Eq(0, report.FindingCount, "healthy Episode bindings have no findings");
            WorldPawnFacts facts = new WorldPawnFacts { retained = r.Reserves(captured), situation = WorldSituation.ReservedByQuest };
            T.Eq(ObservedKind.WorldFree, WorldPawnRules.KindOf(facts), "an owned anonymous vanilla exit is return evidence rather than perpetual WorldOther");
        }

        private static void Boundaries()
        {
            TestNet n = new TestNet(9932);
            Pawn p = Pawn(32101);
            PhysicalEpisode e = Episode(n, p);
            RetainedPawnRegistry r = new RetainedPawnRegistry(n.ctx);
            r.Rebuild();
            e.state = EpisodeState.Quarantined;
            T.Check(r.Reserves(p), "quarantine preserves the held physical Pawn");
            e.state = EpisodeState.Closed;
            e.consequencesApplied = true;
            e.members[0].outcome = MemberOutcome.Returned;
            T.Check(r.Reserves(p), "commit does not release temporary protection before RELEASE finishes");
            e.releaseApplied = true;
            T.Check(!r.Reserves(p) && !r.IsTemporaryReserved(p) && !r.EpisodeOf(p).IsValid, "RELEASE marker removes temporary ownership immediately");
            T.Eq(0, r.RetainedCount(), "no orphaned anonymous reservation");
            e.releaseApplied = false;
            e.members[0].outcome = MemberOutcome.Killed;
            T.Check(!r.Reserves(p), "positive Killed outcome does not reserve a corpse");
            e.members[0].outcome = MemberOutcome.Pending;
            e.members[0].observed = ObservedKind.Dead;
            T.Check(!r.Reserves(p), "positive death observation excludes corpse reservation");
            e.members[0].observed = ObservedKind.Unknown;
            T.Check(r.Reserves(p), "an unsupported observation preserves the bound living slot");
            p.health = Shell<Pawn_HealthTracker>();
            FieldInfo healthState = typeof(Pawn_HealthTracker).GetField("healthState", BindingFlags.Instance | BindingFlags.NonPublic);
            healthState.SetValue(p.health, Enum.Parse(healthState.FieldType, "Dead"));
            T.Check(!r.Reserves(p), "positive vanilla death state excludes a corpse even before the Episode records its outcome");
            T.Eq(0, r.DurableTemporaryCount(), "actual dead Pawn is not reported as needing living reservation");
            healthState.SetValue(p.health, Enum.Parse(healthState.FieldType, "Mobile"));
            e.members.Clear();
            T.Check(!r.Reserves(p), "stale derived index cannot own a member removed from the durable Episode");
        }

        private static void Bridge()
        {
            TestNet n = new TestNet(9933);
            Pawn p = Pawn(32201), twin = Pawn(32201);
            PhysicalEpisode e = Episode(n, p);
            PawnRef binding = e.members[0].pawn;
            binding.pawn = null;
            RetainedPawnRegistry r = new RetainedPawnRegistry(n.ctx);
            T.Eq(1, r.RebuildEarly(), "LoadingVars values alone build one binding id");
            T.Check(!r.pointersResolved && r.Reserves(p), "bridge protects before cross-reference resolution");
            T.Eq(1, r.DurableTemporaryCount(), "unresolved pointer never means nobody retained");
            binding.pawn = p;
            T.Check(r.Reserves(p) && !r.Reserves(twin), "a resolved pointer immediately contradicts a same-id twin");
            RegistryLoadReport report = r.ResolvePointers();
            T.Eq(1, report.episodeHealthy, "resolved Episode binding is healthy");
            T.Check(r.pointersResolved && r.Reserves(p) && !r.Reserves(twin), "pointer authority replaces bridge before the first world tick");
            T.Eq(1, r.EpisodeIndexCount, "one pointer indexed");
            T.Eq(0, n.ctx.characters.Count, "no load-time person was synthesized");
        }

        private static void Note()
        {
            TestNet n = new TestNet(9934);
            Pawn p = Pawn(32301);
            PhysicalEpisode e = Episode(n, p);
            RetainedPawnRegistry r = new RetainedPawnRegistry(n.ctx);
            r.Rebuild();
            Pawn fresh = Pawn(32302);
            r.NoteEpisode(fresh, e.id, 1);
            T.Check(!r.Reserves(fresh), "index note cannot manufacture a durable slot");
            e.members.Add(new EpisodeMember { slot = 1, state = MemberState.Created, pawn = new PawnRef { pawn = fresh, thingIdNumber = fresh.thingIDNumber } });
            T.Check(!r.Reserves(fresh), "new session binding is not silently discovered by a world/episode scan");
            r.NoteEpisode(fresh, e.id, 1);
            T.Check(r.Reserves(fresh), "explicit binding notification covers before Place");
            e.members[1].pawn.pawn = Pawn(32302);
            T.Check(!r.Reserves(fresh), "note is never stronger than the current durable pointer");
        }

        private static void BadBindings()
        {
            TestNet n = new TestNet(9935);
            Pawn missing = Pawn(32401), wrong = Pawn(32402), discarded = Pawn(32403);
            PhysicalEpisode e = Episode(n, missing, wrong, discarded);
            e.members[0].pawn.pawn = null;
            e.members[1].pawn.thingIdNumber = 32999;
            typeof(Thing).GetField("mapIndexOrState", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(discarded, (sbyte)-3);
            RetainedPawnRegistry r = new RetainedPawnRegistry(n.ctx);
            RegistryLoadReport report = r.Rebuild();
            T.Eq(3, report.episodeFindings.Count, "unresolved, mismatch and discarded slots are reported independently");
            T.Eq(0, report.findings.Count, "they are not fake named-person findings");
            T.Eq(0, report.covered, "bad pointers are never claimed healthy");
            T.Check(!r.Reserves(missing) && !r.Reserves(wrong) && !r.Reserves(discarded), "no bridge fallback after pointer phase");
            T.Eq(3, r.AuditTemporary().Count, "repeat audit is read-only");
            T.Check(e.members[0].pawn.pawn == null && e.members[1].pawn.thingIdNumber == 32999 && ReferenceEquals(e.members[2].pawn.pawn, discarded), "no repair, clearing or regeneration");
            T.Check(report.episodeFindings[0].ToString().Contains(e.id.ToString()) && report.episodeFindings[0].ToString().Contains("slot"), "finding identifies Episode slot provenance");
            T.Eq(0, n.physical.creates, "diagnostics generate no replacement");
        }

        private static KnownCharacter Promote(TestNet n, PhysicalEpisode e)
        {
            EpisodeMember m = e.members[0];
            KnownCharacter c = new KnownCharacter
            {
                id = new CharacterId(n.ids.NextId()), pawn = m.pawn.Copy(), custody = CustodyState.OutOfCustody,
                heldBy = HeldKind.PlayerPrisoner, episode = e.id, opRole = m.seatRole
            };
            n.ctx.characters.Add(c);
            m.character = c.id;
            return c;
        }

        private static void Promotion()
        {
            TestNet n = new TestNet(9936);
            Pawn p = Pawn(32501);
            PhysicalEpisode e = Episode(n, p);
            RetainedPawnRegistry r = new RetainedPawnRegistry(n.ctx);
            r.Rebuild();
            KnownCharacter c = Promote(n, e);
            T.Check(r.Reserves(p) && r.IsTemporaryReserved(p), "slot bridge remains valid after atomic identity assignment and before named-index refresh");
            T.Eq(1, r.DurableRetainedCount(), "named and Episode refer to one human, never double reservation count");
            r.Note(p, c.id);
            T.Check(r.Reserves(p) && !r.IsTemporaryReserved(p), "normal named M1 takes precedence without a protection gap");
            T.Eq(c.id, r.CharacterOf(p), "same Pawn resolves to the promoted identity");
            T.Eq(1, r.RetainedCount(), "overlapping handoff entries count one Pawn");
            T.Eq(1, r.NamedRetainedCount(), "named category owns it");
            T.Eq(0, r.TemporaryRetainedCount(), "temporary category has relinquished effective coverage");
            e.releaseApplied = true;
            T.Check(r.Reserves(p), "custody M1 remains after Episode release");
            T.Check(ReferenceEquals(p, c.pawn.pawn), "same real Pawn, no clone");
        }

        private static void Rollback()
        {
            TestNet n = new TestNet(9937);
            Pawn p = Pawn(32601);
            PhysicalEpisode e = Episode(n, p);
            RetainedPawnRegistry r = new RetainedPawnRegistry(n.ctx);
            r.Rebuild();
            DurableSnapshot snapshot = new DurableSnapshot().Capture(e);
            KnownCharacter c = Promote(n, e);
            r.Note(p, c.id);
            snapshot.Restore();
            n.ctx.characters.TruncateTo(0);
            T.Check(!e.members[0].IsNamed && n.ctx.characters.Count == 0, "in-place rollback removes temporary promotion identity");
            T.Check(r.Reserves(p) && r.IsTemporaryReserved(p), "stale named index falls through to the still-authoritative Episode");
            T.Eq(CharacterId.None, r.CharacterOf(p), "rolled-back named index cannot claim a deleted identity");
            T.Eq(1, r.RetainedCount(), "rollback keeps exactly one protected Pawn");
            T.Check(ReferenceEquals(p, e.members[0].pawn.pawn), "rollback never touches engine identity");
        }

        private static void Removal()
        {
            TestNet n = new TestNet(9938);
            Pawn p = Pawn(32701);
            PhysicalEpisode e = Episode(n, p);
            RetainedPawnRegistry r = new RetainedPawnRegistry(n.ctx);
            r.Rebuild();
            r.inert = true;
            T.Check(!r.Reserves(p) && !r.IsTemporaryReserved(p), "prepare-for-removal disables both categories");
            T.Eq(0, r.RetainedCount(), "inert registry reports no effective reservation");
            T.Check(ReferenceEquals(p, e.members[0].pawn.pawn) && n.ctx.characters.Count == 0, "removal predicate never destroys Pawn or manufactures person");
            r.inert = false;
            r.Rebuild();
            T.Check(r.Reserves(p), "resume reconstructs from existing unreleased Episode truth");
            e.releaseApplied = true;
            r.Rebuild();
            T.Check(!r.Reserves(p), "resume never resurrects completed temporary ownership");
        }

        internal static DomainContext loadingContext;
        internal static RetainedPawnRegistry loadingRegistry;
        internal static bool bridgeObserved;
        internal static bool postLoadObserved;

        private static void ScribeBridge()
        {
            TestNet n = new TestNet(9939);
            ReservationProbePawn pawn = new ReservationProbePawn { thingIDNumber = 32801 };
            PhysicalEpisode e = Episode(n, pawn);
            ReservationScribeRoot root = new ReservationScribeRoot { store = n.ctx.episodes };
            root.pawns.Add(pawn);
            string path = Path.Combine(Path.GetTempPath(), "thenetwork-episode-reservation-" + Guid.NewGuid().ToString("N") + ".xml");
            int logStart = T.vanillaLog.Count;
            try
            {
                Scribe.saver.InitSaving(path, "root");
                try { Scribe_Deep.Look(ref root, "fixture"); }
                finally { Scribe.saver.FinalizeSaving(); }
                T.Check(File.ReadAllText(path).Contains("Thing_Human32801"), "real PawnRef serialization carries the exact object reference");
                loadingContext = n.ctx;
                loadingRegistry = null;
                bridgeObserved = postLoadObserved = false;
                ReservationScribeRoot loaded = null;
                Scribe.loader.InitLoading(path);
                try { Scribe_Deep.Look(ref loaded, "fixture"); }
                finally { Scribe.loader.FinalizeLoading(); }
                T.Check(bridgeObserved, "actual LoadingVars has a null pointer but id bridge already reserves the loaded Pawn");
                T.Check(postLoadObserved, "actual PostLoadInit resolves/indexes the pointer before any world tick");
                T.Check(loaded != null && loaded.store.Get(e.id) != null && ReferenceEquals(loaded.pawns[0], loaded.store.Get(e.id).members[0].pawn.pawn), "real Verse cross-reference resolves to the exact loaded Pawn");
                T.Check(loadingRegistry != null && loadingRegistry.IsTemporaryReserved(loaded.pawns[0]), "loaded anonymous slot remains protected");
                T.Eq(0, n.ctx.characters.Count, "save/load creates no character");
                T.Eq(logStart, T.vanillaLog.Count, "roundtrip has no dangling-Pawn reference warning/error");
            }
            finally
            {
                loadingContext = null;
                Scribe.ForceStop();
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static string Code(string relative)
        {
            return File.ReadAllText(Path.Combine(Environment.GetEnvironmentVariable("THENETWORK_REPO"), "Source/TheNetwork", relative));
        }

        private static void Source()
        {
            string registry = Code("Integration/Physical/RetainedPawnRegistry.cs");
            string withoutComments = Regex.Replace(registry, @"//[^\r\n]*|/\*[\s\S]*?\*/", "");
            T.Check(!withoutComments.Contains("PawnsFinder") && !withoutComments.Contains("Find.WorldPawns") && !withoutComments.Contains("AllPawns"), "reservation never scans world Pawn populations");
            T.Check(!withoutComments.Contains("new KnownCharacter") && !withoutComments.Contains("characters.Add("), "reservation cannot manufacture temporary people");
            T.Eq(1, Regex.Matches(withoutComments, @"Quest\.MakeRaw\(").Count, "the existing single quest serves both categories");
            T.Check(Regex.IsMatch(withoutComments, @"public override void ExposeData\(\)\s*\{\s*base\.ExposeData\(\);\s*\}"), "quest part persists no independent Pawn list");
            int start = withoutComments.IndexOf("public int RebuildEarly()", StringComparison.Ordinal);
            int end = withoutComments.IndexOf("public RegistryLoadReport ResolvePointers()", start, StringComparison.Ordinal);
            string early = withoutComments.Substring(start, end - start);
            T.Check(!early.Contains(".pawn.pawn") && !early.Contains("Pawn p ="), "early stage uses durable ids rather than unresolved pointers");
            T.Check(withoutComments.Contains("ctx.episodes?.episodes") && withoutComments.Contains("RequiresEpisodeReservation(e, m)"), "temporary protection derives from Episode members only");
            string world = Code("Core/NetworkWorldComponent.cs");
            T.Check(world.Contains("LoadSaveMode.PostLoadInit) ResolveRetentionAfterLoad();"), "both categories resolve in existing before-first-tick load hook");
            T.Eq(2, Regex.Matches(world, @"EnsureOrganizationRoles\(\)").Count, "bootstrap and load each initialize write-once organization roles");
        }
    }

    /// <summary>Test-only Pawn serializer: engine identity/reference resolution is real; generation and health simulation are not.</summary>
    public sealed class ReservationProbePawn : Pawn
    {
        public ReservationProbePawn()
        {
            def = Phase32bReservationTests.Shell<ThingDef>();
            def.defName = "Human";
        }

        public override void ExposeData()
        {
            // The test executable is not a RunningMod; make this fixture self-describing for vanilla's normal Type.GetType fallback.
            if (Scribe.mode == LoadSaveMode.Saving) Scribe.saver.WriteAttribute("Class", typeof(ReservationProbePawn).AssemblyQualifiedName);
            Scribe_Values.Look(ref thingIDNumber, "id", 0);
        }
    }

    /// <summary>Test-only analogue of the production LoadingVars → FinalizeInit → PostLoadInit registry reconstruction.</summary>
    public sealed class ReservationScribeRoot : IExposable
    {
        public List<ReservationProbePawn> pawns = new List<ReservationProbePawn>();
        public EpisodeStore store = new EpisodeStore();

        public void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving) Scribe.saver.WriteAttribute("Class", typeof(ReservationScribeRoot).AssemblyQualifiedName);
            Scribe_Collections.Look(ref pawns, "pawns", LookMode.Deep);
            Scribe_Deep.Look(ref store, "episodes");
            if (Phase32bReservationTests.loadingContext == null) return;
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                store.RebuildIndex();
                Phase32bReservationTests.loadingContext.episodes = store;
                Phase32bReservationTests.loadingRegistry = new RetainedPawnRegistry(Phase32bReservationTests.loadingContext);
                Phase32bReservationTests.loadingRegistry.RebuildEarly();
                Phase32bReservationTests.bridgeObserved = store.episodes[0].members[0].pawn.pawn == null
                    && Phase32bReservationTests.loadingRegistry.Reserves(pawns[0]);
            }
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                RegistryLoadReport report = Phase32bReservationTests.loadingRegistry.ResolvePointers();
                Phase32bReservationTests.postLoadObserved = report.episodeHealthy == 1 && report.FindingCount == 0
                    && Phase32bReservationTests.loadingRegistry.Reserves(pawns[0]);
            }
        }
    }
}
