using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Physical;
using TheNetwork.Integration.Physical;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Tests
{
    /// <summary>Measured headless registry/reference-serialization costs only: no generation, full game save, world tick or TPS claim.</summary>
    public static class Phase32bRetentionScaleTests
    {
        public static void Register(List<KeyValuePair<string, Action>> tests)
        {
            tests.Add(new KeyValuePair<string, Action>("Groups.Retention_150And300PlusTemporaryCoverageAndLookupObservation", Coverage));
            tests.Add(new KeyValuePair<string, Action>("Groups.Retention_Format5ReferenceFixture150And300RoundtripObservation", References));
        }

        internal static TV Shell<TV>() { return (TV)FormatterServices.GetUninitializedObject(typeof(TV)); }

        private sealed class Fixture
        {
            public readonly DomainContext context;
            public readonly NetworkState state;
            public readonly List<RetentionScaleProbePawn> pawns = new List<RetentionScaleProbePawn>();
            public readonly List<CharacterId> identities = new List<CharacterId>();
            public readonly int named;
            public readonly int temporary;

            public Fixture(int namedCount, int temporaryCount)
            {
                named = namedCount; temporary = temporaryCount;
                state = new NetworkState();
                context = new DomainContext { characters = state.characters, episodes = state.deployments, actors = state.actors };
                for (int i = 0; i < namedCount + temporaryCount; i++)
                {
                    RetentionScaleProbePawn pawn = new RetentionScaleProbePawn { thingIDNumber = 960000 + namedCount * 10 + i };
                    pawns.Add(pawn);
                    if (i >= namedCount) continue;
                    CharacterId id = new CharacterId(20000 + i);
                    identities.Add(id);
                    context.characters.Add(new KnownCharacter
                    {
                        id = id, status = CharacterStatus.Active, custody = CustodyState.Stored,
                        name = NameSnapshot.Person("Scale", "Identity" + i, "Fixture"),
                        opRole = OperationalRole.Rifleman,
                        pawn = new PawnRef { pawn = pawn, thingIdNumber = pawn.thingIDNumber, defName = "Human", boundTick = 1, agedThroughTick = 1 }
                    });
                }
                if (temporaryCount == 0) return;
                PhysicalEpisode episode = new PhysicalEpisode
                {
                    id = new EpisodeId(30000), state = EpisodeState.Open, createdTick = 1, openedTick = 1,
                    cause = new EpisodeCause { devKey = "RetentionScale.HeadlessFixture" }
                };
                for (int i = 0; i < temporaryCount; i++)
                {
                    Pawn pawn = pawns[namedCount + i];
                    episode.members.Add(new EpisodeMember
                    {
                        slot = i, state = MemberState.Present, seatRole = OperationalRole.Rifleman,
                        pawn = new PawnRef { pawn = pawn, thingIdNumber = pawn.thingIDNumber, defName = "Human", boundTick = 1 }
                    });
                }
                context.episodes.Add(episode);
            }
        }

        private static string Ms(double value) { return value.ToString("F3", CultureInfo.InvariantCulture); }
        private static void Coverage()
        {
            foreach (int count in new[] { 150, 300 })
            {
                Fixture fixture = new Fixture(count, count == 300 ? 8 : 0);
                RetainedPawnRegistry registry = new RetainedPawnRegistry(fixture.context);
                registry.Rebuild(); // warm managed code before observations; does not alter durable identity
                Stopwatch watch = Stopwatch.StartNew();
                int early = registry.RebuildEarly(); watch.Stop(); double earlyMs = watch.Elapsed.TotalMilliseconds;
                T.Eq(fixture.pawns.Count, early, "all durable named and temporary ids indexed above soft retention region");
                for (int i = 0; i < fixture.pawns.Count; i++) T.Check(registry.Reserves(fixture.pawns[i]), "early bridge covers scale Pawn " + i);
                watch.Restart(); RegistryLoadReport report = registry.ResolvePointers(); watch.Stop(); double resolveMs = watch.Elapsed.TotalMilliseconds;
                T.Eq(count, report.bound, "all named bindings audited"); T.Eq(count, report.healthy, "all named pointers healthy");
                T.Eq(fixture.temporary, report.episodeBound, "temporary Episode pointers audited separately");
                T.Eq(fixture.temporary, report.episodeHealthy, "temporary pointers healthy");
                T.Eq(fixture.pawns.Count, report.covered, "full coverage has no 150 hard cap");
                T.Eq(count, report.namedCovered, "all living named identities retained");
                T.Eq(fixture.temporary, report.temporaryCovered, "at most eight temporary slots retained");
                T.Eq(0, report.FindingCount, "no integrity findings at scale");
                long queryStart = registry.queries, hitStart = registry.hits;
                int misses = 0;
                watch.Restart();
                for (int i = 0; i < 100000; i++) if (!registry.Reserves(fixture.pawns[i % fixture.pawns.Count])) misses++;
                watch.Stop(); double lookupMs = watch.Elapsed.TotalMilliseconds;
                T.Eq(0, misses, "100000 lookups never reject a real retained identity");
                T.Eq(100000L, registry.queries - queryStart, "actual registry lookup count");
                T.Eq(100000L, registry.hits - hitStart, "every named/temporary actual lookup hits");
                T.Eq(count, fixture.context.characters.Count, "benchmark creates no additional or dummy identities");
                for (int i = 0; i < count; i++)
                {
                    KnownCharacter character = fixture.context.characters.Get(fixture.identities[i]);
                    T.Check(character != null && ReferenceEquals(character.pawn.pawn, fixture.pawns[i]), "registry rebuilding preserves exact bound Pawn " + i);
                    T.Eq(CustodyState.Stored, character.custody, "registry does not abstract or discard scale identity " + i);
                }
                for (int i = count; i < fixture.pawns.Count; i++) T.Check(registry.IsTemporaryReserved(fixture.pawns[i]) && !registry.CharacterOf(fixture.pawns[i]).IsValid, "temporary slot is protected without person " + i);
                T.Eq(0, registry.Audit().Count + registry.AuditTemporary().Count, "read-only scale audit healthy");
                Console.WriteLine("    HEADLESS registry microbenchmark named=" + count + " temporary=" + fixture.temporary
                    + " rebuild_early_ms=" + Ms(earlyMs) + " resolve_pointers_ms=" + Ms(resolveMs)
                    + " lookup_count=100000 lookup_total_ms=" + Ms(lookupMs) + " (observation only; no TPS/generation/full-save claim)");
            }
        }

        private static void References()
        {
            long firstBytes = -1;
            foreach (int count in new[] { 150, 300 })
            {
                Fixture fixture = new Fixture(count, count == 300 ? 8 : 0);
                RetentionScaleScribeRoot root = new RetentionScaleScribeRoot { state = fixture.state, pawns = fixture.pawns, named = count, temporary = fixture.temporary };
                string path = Path.Combine(Path.GetTempPath(), "thenetwork-retention-scale-" + Guid.NewGuid().ToString("N") + ".xml");
                int logStart = T.vanillaLog.Count;
                try
                {
                    Stopwatch watch = Stopwatch.StartNew();
                    Scribe.saver.InitSaving(path, "root");
                    try { Scribe_Deep.Look(ref root, "fixture"); }
                    finally { Scribe.saver.FinalizeSaving(); }
                    watch.Stop(); double saveMs = watch.Elapsed.TotalMilliseconds;
                    long bytes = new FileInfo(path).Length;
                    T.Check(bytes > 0 && File.ReadAllText(path).Contains("<saveVersion>5</saveVersion>"), "actual Scribe fixture uses format5 state stores");
                    RetentionScaleScribeRoot loaded = null;
                    watch.Restart();
                    Scribe.loader.InitLoading(path);
                    try { Scribe_Deep.Look(ref loaded, "fixture"); }
                    finally { Scribe.loader.FinalizeLoading(); }
                    watch.Stop(); double loadMs = watch.Elapsed.TotalMilliseconds;
                    T.Check(loaded != null && loaded.saveVersion == SaveMigrations.Current && loaded.saveVersion == 5, "no save format bump");
                    T.Eq(count + fixture.temporary, loaded.pawns.Count, "all test-owned identity probes loaded");
                    T.Eq(count, loaded.state.characters.Count, "all real CharacterStore records roundtrip");
                    T.Check(loaded.earlyBridgeObserved && loaded.postLoadResolved, "actual LoadingVars bridge then PostLoad pointers protect both categories before any world tick");
                    T.Check(loaded.registry != null, "reconstructed actual registry available");
                    T.Eq(0, loaded.storeFailures.Count, "production store serializer reports no degraded store");
                    for (int i = 0; i < count; i++)
                    {
                        KnownCharacter character = loaded.state.characters.Get(fixture.identities[i]);
                        T.Check(character != null && ReferenceEquals(character.pawn.pawn, loaded.pawns[i]) && loaded.registry.Reserves(loaded.pawns[i]), "loaded exact named cross-reference and retention " + i);
                        T.Eq(fixture.pawns[i].thingIDNumber, character.pawn.thingIdNumber, "durable scale identity preserved " + i);
                    }
                    if (fixture.temporary > 0)
                    {
                        PhysicalEpisode episode = loaded.state.deployments.Get(new EpisodeId(30000));
                        T.Check(episode != null && !episode.releaseApplied, "pending scale Episode stays owned");
                        for (int i = 0; i < fixture.temporary; i++)
                        {
                            Pawn pawn = loaded.pawns[count + i];
                            T.Check(ReferenceEquals(episode.members[i].pawn.pawn, pawn) && loaded.registry.IsTemporaryReserved(pawn), "loaded temporary exact reference and reservation " + i);
                            T.Check(!episode.members[i].IsNamed && !loaded.registry.CharacterOf(pawn).IsValid, "Scribe did not synthesize a dummy identity " + i);
                        }
                    }
                    T.Eq(0, loaded.registry.Audit().Count + loaded.registry.AuditTemporary().Count, "zero loaded integrity findings");
                    T.Eq(logStart, T.vanillaLog.Count, "real scale roundtrip has no unresolved-Pawn or Scribe warning/error");
                    Console.WriteLine("    HEADLESS reference fixture named=" + count + " temporary=" + fixture.temporary + " format=5 fixture_xml_bytes=" + bytes
                        + " fixture_bytes_delta_from150=" + (firstBytes < 0 ? 0 : bytes - firstBytes)
                        + " serialize_ms=" + Ms(saveMs) + " load_with_crossrefs_ms=" + Ms(loadMs)
                        + " early_index_ms=" + Ms(loaded.earlyMilliseconds) + " resolve_pointers_ms=" + Ms(loaded.resolveMilliseconds)
                        + " (NetworkState + identity-only Pawn probe XML; NOT RimWorld full-save size/time)");
                    if (firstBytes < 0) firstBytes = bytes;
                }
                finally
                {
                    Scribe.ForceStop();
                    if (File.Exists(path)) File.Delete(path);
                }
            }
        }
    }

    /// <summary>Test-only identity holder. Actual Pawn/GetUniqueLoadID and Verse cross-references; excludes the full Unity Pawn serializer.</summary>
    public sealed class RetentionScaleProbePawn : Pawn
    {
        public RetentionScaleProbePawn()
        {
            def = Phase32bRetentionScaleTests.Shell<ThingDef>(); def.defName = "Human";
        }
        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving) Scribe.saver.WriteAttribute("Class", typeof(RetentionScaleProbePawn).AssemblyQualifiedName);
            Scribe_Values.Look(ref thingIDNumber, "id", 0);
        }
    }

    /// <summary>Test-only format5 production-store wrapper, observing actual LoadingVars and PostLoadInit reconstruction.</summary>
    public sealed class RetentionScaleScribeRoot : IExposable
    {
        public int saveVersion = 5;
        public int named;
        public int temporary;
        public List<RetentionScaleProbePawn> pawns = new List<RetentionScaleProbePawn>();
        public NetworkState state = new NetworkState();
        public readonly List<string> storeFailures = new List<string>();
        public RetainedPawnRegistry registry;
        public bool earlyBridgeObserved;
        public bool postLoadResolved;
        public double earlyMilliseconds;
        public double resolveMilliseconds;

        public void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving) Scribe.saver.WriteAttribute("Class", typeof(RetentionScaleScribeRoot).AssemblyQualifiedName);
            Scribe_Values.Look(ref saveVersion, "saveVersion", 0, true);
            Scribe_Values.Look(ref named, "named", 0, true);
            Scribe_Values.Look(ref temporary, "temporary", 0, true);
            Scribe_Collections.Look(ref pawns, "identityProbes", LookMode.Deep);
            state.ExposeStores(storeFailures);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                state.RebuildIndexes();
                DomainContext context = new DomainContext { characters = state.characters, episodes = state.deployments, actors = state.actors };
                registry = new RetainedPawnRegistry(context);
                Stopwatch watch = Stopwatch.StartNew(); registry.RebuildEarly(); watch.Stop(); earlyMilliseconds = watch.Elapsed.TotalMilliseconds;
                bool unknownPointers = true, bridgeCoverage = true;
                for (int i = 0; i < state.characters.characters.Count; i++) unknownPointers &= state.characters.characters[i].pawn.pawn == null;
                foreach (PhysicalEpisode episode in state.deployments.episodes) foreach (EpisodeMember member in episode.members) unknownPointers &= member.pawn.pawn == null;
                for (int i = 0; i < pawns.Count; i++) bridgeCoverage &= registry.Reserves(pawns[i]);
                earlyBridgeObserved = unknownPointers && bridgeCoverage && pawns.Count == named + temporary;
            }
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                Stopwatch watch = Stopwatch.StartNew(); RegistryLoadReport report = registry.ResolvePointers(); watch.Stop(); resolveMilliseconds = watch.Elapsed.TotalMilliseconds;
                postLoadResolved = report.FindingCount == 0 && report.namedCovered == named && report.temporaryCovered == temporary && report.covered == pawns.Count;
            }
        }
    }
}
