using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using RimWorld;
using TheNetwork.Diagnostics.RuntimePhysicalTests;
using TheNetwork.Diagnostics.RuntimeTests;
using TheNetwork.Diagnostics.RuntimeTests.Suites;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Physical;
using TheNetwork.Integration.Physical;
using TheNetwork.Kernel;
using TheNetwork.Settings;
using TheNetwork.Core;
using TheNetwork;
using TheNetwork.Persist;
using Verse;

namespace TheNetwork.Tests
{
    /// <summary>
    /// The Phase 3.1 RUNTIME-QA correction pass (PR #10, after the owner's first real physical-suite run on build 29f31dd), proven headlessly.
    ///
    /// <list type="bullet">
    /// <item>Fix 1, the retained registry survives a load: <c>World.FinalizeInit</c> (which builds the registry) runs BEFORE the load's
    /// cross-references resolve, so a pointer-built index was empty and every retained person was an ordinary Free world pawn on the first
    /// tick. Stage 1 now indexes by the persisted thing id; stage 2 (PostLoadInit) builds the validated pointer index.</item>
    /// <item>Fix 2, a first projection draws only from the encounter faction's generic member pool.</item>
    /// <item>Fix 3, an embodied individual's operational role is stored from origin facts before it can ever materialize.</item>
    /// <item>Fixes 4 to 6 are harness assertions and labels, pinned here by the production policies they must agree with.</item>
    /// <item>Fix 7, a living bound person whose binding does not resolve, is discarded or disagrees with its thing id is reported, never repaired.</item>
    /// </list>
    ///
    /// The real adapter cannot run headlessly (it needs a RimWorld game): the registry's index, its stages and its audit run over REAL
    /// <c>Pawn</c> objects (never spawned), and vanilla's own steps are the one simulated part. The owner's physical tier (RT-PHYX-*) remains the runtime proof,
    /// which this pass does NOT claim.
    /// </summary>
    public static class Phase31QaTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix1_LoadOrderIsAudited_FinalizeInitPrecedesCrossReferences", LoadOrderScan));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix1_BindingRulesMatrix", BindingRulesMatrix));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix1_Stage1_ThingIdBridgeCoversBeforeAnyPointerResolves", Stage1Bridge));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix1_Stage2_PointerIndexAfterCrossRefsBecomesTheAuthority", Stage2Pointers));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix1_PointerAndPersistedIdMustAgree_MismatchFailsClosed", MismatchFailsClosed));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix1_UnresolvedAndDiscardedBindingsAreReportedNeverRepaired", UnresolvedAndDiscarded));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix1_OnlyLivingDeployedStoredOrHeldPeopleAreRetained", CustodyMatrix));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix1_FifteenBoundPeopleNoneIsEverActuallyFree", FifteenBound));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix1_RealScribeLoad_StageOneRegistryFromTheLoadedStore", ScribeStages));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix1_Scan_QuestRestoredFromDurableStateAtPostLoadInit", ScanQuestRestore));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix1_Scan_LoadNeverGeneratesSpawnsOrRerolls", ScanLoadNeverGenerates));
            t.Add(new KeyValuePair<string, Action>("Phys31Qa.Fix7_Scan_ValidationReportsTheBoundDiscardedDiagnostic", ScanValidation));
        }

        // ================================================================== helpers

        private static NetworkActor Solo(TestNet n, string id) { return PhysicalLifecycleTests.Make(n, ContractorForm.Solo, id); }

        private static KnownCharacter Self(TestNet n, NetworkActor a) { return PhysicalLifecycleTests.Self(n, a); }

        private static string Code(string rel) { return PhysicalLifecycleTests.Code(PhysicalLifecycleTests.Src(rel)); }

        private static string Body(string code, string from, string to)
        {
            int a = code.IndexOf(from, StringComparison.Ordinal);
            T.Check(a >= 0, "found " + from);
            if (a < 0) return "";
            int b = code.IndexOf(to, a + from.Length, StringComparison.Ordinal);
            return b < 0 ? code.Substring(a) : code.Substring(a, b - a);
        }

        /// <summary>A real Pawn object (never spawned) with a thing id, as vanilla assigns one at creation.</summary>
        internal static Pawn MakePawn(int thingId)
        {
            Pawn p = new Pawn();
            p.thingIDNumber = thingId;
            return p;
        }

        internal static void Discard(Pawn p)
        {
            FieldInfo f = typeof(Thing).GetField("mapIndexOrState", BindingFlags.NonPublic | BindingFlags.Instance);
            T.Check(f != null, "Thing.mapIndexOrState exists (the discarded marker)");
            if (f != null) f.SetValue(p, (sbyte)-3);
        }

        /// <summary>
        /// Vanilla's WorldPawns.GetSituation for a retained pawn, as the registry quest part answers it: QuestPartReserves asks THIS registry
        /// (scan-checked), and a reserved pawn is ReservedByQuest, an unreserved one an ordinary Free world pawn.
        /// </summary>
        private static ObservedKind VanillaSees(RetainedPawnRegistry r, Pawn p, bool retainedPerson)
        {
            WorldPawnFacts facts = new WorldPawnFacts { retained = retainedPerson, situation = r.Reserves(p) ? WorldSituation.ReservedByQuest : WorldSituation.Free };
            return WorldPawnRules.KindOf(facts);
        }

        private static void NeverFree(RetainedPawnRegistry r, IList<Pawn> pawns, string stage)
        {
            for (int i = 0; i < pawns.Count; i++)
            {
                ObservedKind k = VanillaSees(r, pawns[i], true);
                T.Check(k == ObservedKind.WorldFree, stage + ": retained pawn #" + pawns[i].thingIDNumber + " is ReservedByQuest by the Network's own registry, never an actual Free pawn (" + k + ")");
            }
        }

        // ================================================================== Fix 1: the load order, audited

        private static void LoadOrderScan()
        {
            // The audit's two facts, pinned in the SOURCE so a refactor cannot silently undo them. (1) The registry is built in FinalizeInit, which
            // vanilla runs before cross-references resolve; so the port's constructor may only do the durable stage. (2) The pointer stage is the
            // world component's PostLoadInit, which vanilla runs inside FinalizeLoading, after every cross-reference and before the first tick.
            string port = Code("Integration/Physical/RimWorldPhysicalWorldPort.cs");
            string ctor = Body(port, "public RimWorldPhysicalWorldPort(DomainContext ctx)", "public bool Available");
            T.Check(ctor.Contains("Registry.RebuildEarly();") && !Regex.IsMatch(ctor, @"Registry\.(Rebuild|ResolvePointers)\("), "the port's constructor (FinalizeInit) builds ONLY the durable thing-id stage");
            string registry = Code("Integration/Physical/RetainedPawnRegistry.cs");
            string early = Body(registry, "public int RebuildEarly()", "public RegistryLoadReport ResolvePointers()");
            T.Check(!early.Contains(".pawn.pawn") && !early.Contains("PawnRef.pawn") && early.Contains("thingIdNumber"), "stage 1 reads persisted thing ids and never a pawn pointer");
            string world = Code("Core/NetworkWorldComponent.cs");
            T.Check(Regex.IsMatch(world, @"if \(Scribe\.mode == LoadSaveMode\.PostLoadInit\)\s*ResolveRetentionAfterLoad\(\);"), "stage 2 runs in the world component's PostLoadInit (after every cross-reference)");
            T.Check(world.Contains("runtime.PhysicalWorld?.OnReferencesResolved();"), "stage 2 is the port's OnReferencesResolved");
            T.Check(!Regex.IsMatch(Body(world, "private void BuildRuntime()", "private void EnsureNetworkSeed()"), @"ResolvePointers|OnReferencesResolved"), "BuildRuntime (FinalizeInit) never builds the pointer index");
            T.Check(Regex.IsMatch(world, @"if \(!fromLoad \|\| !bootstrapped\) runtime\.PhysicalWorld\?\.Registry\.ResolvePointers\(\);"), "a new game has nothing to wait for");
            // The bridge is the narrow rule, and the pointer stays authoritative once it exists.
            T.Check(registry.Contains("BindingRules.BridgeCovers(") && registry.Contains("if (pointersResolved || p.thingIDNumber <= 0"), "the thing-id bridge is consulted only before the pointer index exists");
            T.Check(registry.Contains("ReferenceEquals(c.pawn.pawn, p)"), "once built, the predicate is reference equality plus retained custody");
            string quest = Code("Integration/Physical/RetainedPawnRegistry.cs");
            T.Check(Regex.IsMatch(quest, @"public override bool QuestPartReserves\(Pawn p\)\s*\{\s*RetainedPawnRegistry r = RetainedPawnRegistry\.Active;\s*return r != null && r\.Reserves\(p\);"),
                "vanilla's reservation query asks THIS registry: a retained pawn is ReservedByQuest exactly when Reserves is true");
        }

        private static void BindingRulesMatrix()
        {
            Action<bool, int, bool, int, bool, BindingIntegrity, string> row = (bound, persisted, resolved, ptrId, disc, want, what) =>
                T.Eq(want, BindingRules.Judge(bound, persisted, resolved, ptrId, disc), what);
            row(false, 0, false, 0, false, BindingIntegrity.NotBound, "no binding");
            row(true, 4242, true, 4242, false, BindingIntegrity.Healthy, "resolved, same id, alive");
            row(true, 4242, false, 0, false, BindingIntegrity.Unresolved, "persisted but the pointer did not resolve");
            row(true, 4242, true, 4242, true, BindingIntegrity.Discarded, "resolved to a discarded pawn");
            row(true, 4242, true, 777, false, BindingIntegrity.IdMismatch, "resolved, but another thing id");
            row(true, 4242, true, 777, true, BindingIntegrity.IdMismatch, "a mismatch outranks a discard (identity in doubt)");
            row(true, 0, true, 4242, false, BindingIntegrity.IdMismatch, "a binding with no persisted id cannot be attested");
            // The bridge: an id match, and no contradicting resolved pointer.
            T.Check(BindingRules.BridgeCovers(4242, 4242, false, false), "bridge: id matches, pointer unresolved");
            T.Check(BindingRules.BridgeCovers(4242, 4242, true, true), "bridge: id matches, the resolved pointer IS this pawn");
            T.Check(!BindingRules.BridgeCovers(4242, 4242, true, false), "bridge: a RESOLVED pointer to another object contradicts it");
            T.Check(!BindingRules.BridgeCovers(4243, 4242, false, false), "bridge: another thing id is never covered");
            T.Check(!BindingRules.BridgeCovers(0, 0, false, false) && !BindingRules.BridgeCovers(-1, -1, false, false), "bridge: no id, no coverage");
            // Exhaustive: Healthy only when every clause holds.
            for (int persisted = 0; persisted <= 1; persisted++)
                for (int resolved = 0; resolved <= 1; resolved++)
                    for (int same = 0; same <= 1; same++)
                        for (int disc = 0; disc <= 1; disc++)
                        {
                            BindingIntegrity v = BindingRules.Judge(true, persisted == 1 ? 4242 : 0, resolved == 1, resolved == 1 ? (same == 1 ? 4242 : 1) : 0, disc == 1);
                            bool healthy = persisted == 1 && resolved == 1 && same == 1 && disc == 0;
                            T.Check((v == BindingIntegrity.Healthy) == healthy, "Healthy iff persisted id, resolved, same id and not discarded (" + persisted + resolved + same + disc + " -> " + v + ")");
                        }
        }

        private static void Stage1Bridge()
        {
            TestNet n = new TestNet(9701);
            NetworkActor a = Solo(n, "stage1");
            KnownCharacter c = Self(n, a);
            Pawn real = MakePawn(4242), twin = MakePawn(4242), other = MakePawn(4243);
            // What the world's LoadingVars pass leaves behind at FinalizeInit: the persisted thing id and custody, the pointer NOT yet resolved.
            c.pawn = new PawnRef { pawn = null, thingIdNumber = 4242, defName = "Human", boundTick = 1, agedThroughTick = 1 };
            c.custody = CustodyState.Deployed;
            RetainedPawnRegistry r = new RetainedPawnRegistry(n.ctx);
            T.Eq(1, r.RebuildEarly(), "stage 1 indexed the one durable binding by its thing id");
            T.Check(!r.pointersResolved && r.IndexCount == 0, "no pointer index exists yet: the pointer index is EMPTY, which is what the first runtime exposed");
            int naive = 0;
            foreach (KnownCharacter k in n.ctx.characters.characters) if (k.pawn != null && k.pawn.pawn != null) naive++;
            T.Eq(0, naive, "an index built from POINTERS at this stage would cover nobody: the defect (15 bound, 0 retained)");
            T.Eq(1, r.DurableRetainedCount(), "the durable state says one person MUST be reserved");
            T.Eq(1, r.RetainedCount(), "and the registry covers them (it never reads 'nobody retained' off an unresolved pointer)");
            T.Check(r.Reserves(real), "Deployed: reserved by the thing-id bridge before any pointer resolves");
            c.custody = CustodyState.Stored;
            T.Check(r.Reserves(real), "Stored: reserved by the bridge too");
            T.Check(!r.Reserves(other), "another thing id is never covered");
            T.Check(!r.Reserves(MakePawn(0)) && !r.Reserves(MakePawn(-1)) && !r.Reserves(null), "no id and null are never covered");
            NeverFree(r, new[] { real }, "stage 1");
            // Between the two stages the pointer resolves (ResolveAllCrossReferences) but the pointer index is not built yet (PostLoadInit):
            // a RESOLVED pointer is authoritative, so the bridge never covers a different object that merely shares the id.
            c.pawn.pawn = real;
            T.Check(r.Reserves(real), "pointer resolved, index not yet built: the same object is still covered");
            T.Check(!r.Reserves(twin), "pointer resolved, index not yet built: a twin with the same thing id is NOT covered (the persisted PawnRef stays authoritative)");
            NeverFree(r, new[] { real }, "between the stages");
            T.Eq(0, n.physical.creates, "loading never creates a pawn");
        }

        private static void Stage2Pointers()
        {
            TestNet n = new TestNet(9702);
            NetworkActor a = Solo(n, "stage2");
            KnownCharacter c = Self(n, a);
            Pawn real = MakePawn(4242), twin = MakePawn(4242);
            c.pawn = new PawnRef { pawn = null, thingIdNumber = 4242, defName = "Human", boundTick = 1, agedThroughTick = 1 };
            c.custody = CustodyState.Stored;
            RetainedPawnRegistry r = new RetainedPawnRegistry(n.ctx);
            r.RebuildEarly();
            c.pawn.pawn = real; // cross-references resolved
            PawnRef before = c.pawn;
            RegistryLoadReport rep = r.ResolvePointers();
            T.Eq(1, rep.bound, "one binding");
            T.Eq(1, rep.healthy, "it resolved to a live pawn with the persisted thing id");
            T.Eq(1, rep.durableRetained, "one person must be reserved");
            T.Eq(1, rep.covered, "and is covered");
            T.Eq(0, rep.findings.Count, "no integrity finding");
            T.Check(r.pointersResolved && r.IndexCount == 1, "the pointer index is built");
            T.Check(r.Reserves(real), "the resolved pointer is reserved");
            T.Check(!r.Reserves(twin), "after resolution the pointer is the AUTHORITY: a twin with the same thing id is not covered (the bridge is closed)");
            T.Eq(c.id, r.CharacterOf(real), "the pointer maps to the same character");
            T.Check(!r.CharacterOf(twin).IsValid, "and a twin maps to nobody");
            NeverFree(r, new[] { real }, "stage 2");
            T.Check(ReferenceEquals(before, c.pawn) && ReferenceEquals(c.pawn.pawn, real) && c.pawn.thingIdNumber == 4242 && c.custody == CustodyState.Stored, "resolving changed nothing the Network stores");
            RegistryLoadReport again = r.ResolvePointers();
            T.Check(again.covered == 1 && again.findings.Count == 0 && r.IndexCount == 1, "stage 2 is idempotent");
            // A binding made after the load (a new first materialization) is covered through Note, before the lifecycle even writes it.
            NetworkActor b = Solo(n, "after");
            KnownCharacter c2 = Self(n, b);
            Pawn fresh = MakePawn(5000);
            r.Note(fresh, c2.id);
            c2.pawn = new PawnRef { pawn = fresh, thingIdNumber = 5000 };
            c2.custody = CustodyState.Deployed;
            T.Check(r.Reserves(fresh), "a person bound after the load is covered from its binding on (M1)");
        }

        private static void MismatchFailsClosed()
        {
            TestNet n = new TestNet(9703);
            NetworkActor a = Solo(n, "mismatch");
            KnownCharacter c = Self(n, a);
            Pawn wrong = MakePawn(777), persistedId = MakePawn(4242);
            c.pawn = new PawnRef { pawn = wrong, thingIdNumber = 4242, defName = "Human", boundTick = 1, agedThroughTick = 1 };
            c.custody = CustodyState.Deployed;
            RetainedPawnRegistry r = new RetainedPawnRegistry(n.ctx);
            r.RebuildEarly();
            PawnRef binding = c.pawn;
            RegistryLoadReport rep = r.ResolvePointers();
            T.Eq(0, rep.healthy, "a pointer whose pawn disagrees with the persisted thing id is not healthy");
            T.Eq(1, rep.findings.Count, "it is reported");
            T.Eq(BindingIntegrity.IdMismatch, rep.findings[0].kind, "as an id mismatch");
            T.Check(rep.findings[0].ToString().Contains("PHYSICAL INTEGRITY") && rep.findings[0].ToString().Contains("disagrees"), "loudly, in plain words: " + rep.findings[0]);
            T.Eq(1, rep.durableRetained, "the durable state still says this person must be reserved");
            T.Eq(0, rep.covered, "but the registry cannot attest the identity, so it covers nobody here: a reservation GAP the report states");
            T.Check(!r.Reserves(wrong), "the mismatching pawn is not claimed");
            T.Check(!r.Reserves(persistedId), "and neither is a pawn that merely has the persisted id (the pointer disagrees with it)");
            T.Eq(1, r.Audit().Count, "the read-only audit says the same");
            // Fail closed means: nothing regenerated, nothing cleared, nobody marked healthy, abstract authority stays closed.
            T.Check(ReferenceEquals(binding, c.pawn) && ReferenceEquals(c.pawn.pawn, wrong) && c.pawn.thingIdNumber == 4242 && c.pawn.defName == "Human", "the durable binding is untouched");
            T.Check(c.custody == CustodyState.Deployed && !AuthorityGate.CanSimulateAbstractly(c), "custody unchanged and abstract authority still closed");
            T.Eq(0, n.physical.creates, "no pawn was generated");
            T.Check(r.ResolvePointers().findings.Count == 1 && r.Audit().Count == 1, "asking again changes nothing: there is no quiet healing");
        }

        private static void UnresolvedAndDiscarded()
        {
            TestNet n = new TestNet(9704);
            NetworkActor a = Solo(n, "unres"), b = Solo(n, "disc"), d = Solo(n, "dead");
            KnownCharacter cu = Self(n, a), cd = Self(n, b), cx = Self(n, d);
            Pawn gone = MakePawn(6002);
            Discard(gone);
            T.Check(gone.Discarded, "the fixture pawn is discarded");
            cu.pawn = new PawnRef { pawn = null, thingIdNumber = 6001, defName = "Human", boundTick = 1 };
            cd.pawn = new PawnRef { pawn = gone, thingIdNumber = 6002, defName = "Human", boundTick = 1 };
            cx.pawn = new PawnRef { pawn = null, thingIdNumber = 6003, defName = "Human", boundTick = 1 };
            cu.custody = CustodyState.Stored;
            cd.custody = CustodyState.Stored;
            cx.custody = CustodyState.Stored;
            cx.status = CharacterStatus.Dead; // a dead person's pawn may legitimately not resolve: no finding
            RetainedPawnRegistry r = new RetainedPawnRegistry(n.ctx);
            r.RebuildEarly();
            T.Eq(2, r.DurableRetainedCount(), "two LIVING Stored people must be reserved (the dead one is not retained)");
            RegistryLoadReport rep = r.ResolvePointers();
            T.Eq(2, rep.findings.Count, "the unresolved and the discarded living binding are reported; the dead person's is not");
            BindingIntegrity ku = BindingIntegrity.NotBound, kd = BindingIntegrity.NotBound;
            foreach (BindingFinding f in rep.findings)
            {
                if (f.id == cu.id) ku = f.kind;
                if (f.id == cd.id) kd = f.kind;
            }
            T.Eq(BindingIntegrity.Unresolved, ku, "an unresolved binding after the cross-references");
            T.Eq(BindingIntegrity.Discarded, kd, "a binding to a pawn vanilla discarded");
            T.Check(rep.findings[0].ToString().Contains("DISCARDED") || rep.findings[1].ToString().Contains("DISCARDED"), "the discard is stated in words (positive bad physical evidence)");
            T.Eq(0, rep.covered, "neither can be covered: there is no live pawn to protect");
            T.Check(!r.Reserves(gone), "a discarded pawn is never claimed");
            // Read-only: nothing regenerated, cleared or marked healthy.
            T.Check(cu.pawn.thingIdNumber == 6001 && cu.pawn.pawn == null && cd.pawn.thingIdNumber == 6002 && ReferenceEquals(cd.pawn.pawn, gone), "both bindings are exactly as persisted");
            T.Check(cu.custody == CustodyState.Stored && cd.custody == CustodyState.Stored && cu.IsAlive && cd.IsAlive, "custody and status untouched");
            T.Eq(0, n.physical.creates, "no replacement pawn was generated");
            T.Eq(2, r.Audit().Count, "the audit reports them every time");
            // A person with NO episode: the finding is the whole response. With an open episode the lifecycle decides from its own observation (Gone ⇒ Lost).
            T.Check(!cu.episode.IsValid && !cd.episode.IsValid, "no episode was opened or changed by the diagnostic");
        }

        private static void CustodyMatrix()
        {
            TestNet n = new TestNet(9705);
            int expected = 0, id = 7000;
            List<Pawn> retained = new List<Pawn>();
            foreach (CharacterStatus status in new[] { CharacterStatus.Active, CharacterStatus.Dead, CharacterStatus.Lost })
            {
                foreach (CustodyState custody in Enum.GetValues(typeof(CustodyState)))
                {
                    NetworkActor a = Solo(n, "m" + id);
                    KnownCharacter c = Self(n, a);
                    Pawn p = MakePawn(id);
                    c.pawn = new PawnRef { pawn = null, thingIdNumber = id, defName = "Human", boundTick = 1 };
                    c.custody = custody;
                    c.status = status;
                    // Phase 3.2A (ADR-056) extends M1 "from the binding on" to a person vanilla holds: OutOfCustody is retained too.
                    bool want = c.IsAlive && (custody == CustodyState.Deployed || custody == CustodyState.Stored || custody == CustodyState.OutOfCustody);
                    T.Eq(want, RetainedPawnRegistry.RetainedCustody(c), "retained iff alive and Deployed, Stored or OutOfCustody (" + status + ", " + custody + ")");
                    if (want)
                    {
                        expected++;
                        retained.Add(p);
                    }
                    else
                    {
                        // never accidentally retained, in either stage
                        RetainedPawnRegistry only = new RetainedPawnRegistry(n.ctx);
                        only.RebuildEarly();
                        T.Check(!only.Reserves(p), "stage 1 does not reserve " + status + "/" + custody);
                        c.pawn.pawn = p;
                        only.ResolvePointers();
                        T.Check(!only.Reserves(p), "stage 2 does not reserve " + status + "/" + custody);
                        c.pawn.pawn = null;
                    }
                    id++;
                }
            }
            RetainedPawnRegistry r = new RetainedPawnRegistry(n.ctx);
            r.RebuildEarly();
            T.Eq(expected, r.DurableRetainedCount(), "the durable count is exactly the living Deployed, Stored or held people (" + expected + ")");
            T.Eq(3, expected, "only Deployed, Stored and OutOfCustody, while alive (3 of " + id + " fixtures)");
            for (int i = 0; i < retained.Count; i++) T.Check(r.Reserves(retained[i]), "stage 1 reserves retained #" + retained[i].thingIDNumber);
            foreach (KnownCharacter c in n.ctx.characters.characters) if (c.pawn != null && c.pawn.IsBound) c.pawn.pawn = MakeLike(c.pawn.thingIdNumber, retained);
            r.ResolvePointers();
            T.Eq(expected, r.RetainedCount(), "stage 2 covers exactly the same people");
        }

        private static Pawn MakeLike(int thingId, List<Pawn> pool)
        {
            for (int i = 0; i < pool.Count; i++) if (pool[i].thingIDNumber == thingId) return pool[i];
            return MakePawn(thingId);
        }

        private static void FifteenBound()
        {
            // The owner's runtime log: "15 bound pawn(s), 0 retained", then "(14 not)". Fifteen bound people in the mix a real save holds.
            TestNet n = new TestNet(9706);
            CustodyState[] pattern = { CustodyState.Stored, CustodyState.Stored, CustodyState.Deployed, CustodyState.Stored, CustodyState.Unmaterialized };
            List<Pawn> pawns = new List<Pawn>();
            List<KnownCharacter> people = new List<KnownCharacter>();
            int retainedWanted = 0;
            for (int i = 0; i < 15; i++)
            {
                NetworkActor a = Solo(n, "f" + i);
                KnownCharacter c = Self(n, a);
                Pawn p = MakePawn(8000 + i);
                c.pawn = new PawnRef { pawn = null, thingIdNumber = 8000 + i, defName = "Human", boundTick = 1, agedThroughTick = 1 };
                c.custody = pattern[i % pattern.Length];
                if (i == 14) c.status = CharacterStatus.Dead;
                if (RetainedPawnRegistry.RetainedCustody(c)) retainedWanted++;
                pawns.Add(p);
                people.Add(c);
            }
            RetainedPawnRegistry r = new RetainedPawnRegistry(n.ctx); // FinalizeInit: the pointers do not exist yet
            T.Eq(15, r.RebuildEarly(), "stage 1: all 15 bindings are indexed by thing id");
            T.Eq(retainedWanted, r.RetainedCount(), "stage 1: every Deployed or Stored living person counts as retained (" + retainedWanted + "), never 0");
            for (int i = 0; i < 15; i++)
            {
                bool retained = RetainedPawnRegistry.RetainedCustody(people[i]);
                T.Eq(retained, r.Reserves(pawns[i]), "stage 1: person " + i + " (" + people[i].custody + ", " + people[i].status + ") reservation");
                if (retained) T.Check(VanillaSees(r, pawns[i], true) != ObservedKind.ReservationBroken, "stage 1: person " + i + " is never an actual Free pawn on the first tick");
            }
            for (int i = 0; i < 15; i++) people[i].pawn.pawn = pawns[i]; // ResolveAllCrossReferences
            RegistryLoadReport rep = r.ResolvePointers();
            T.Eq(15, rep.bound, "stage 2: 15 bound");
            T.Eq(15, rep.healthy, "stage 2: all 15 resolve to their persisted thing ids");
            T.Eq(retainedWanted, rep.durableRetained, "stage 2: the durable count");
            T.Eq(retainedWanted, rep.covered, "stage 2: every one covered (no gap)");
            T.Eq(0, rep.findings.Count, "stage 2: no integrity finding");
            for (int i = 0; i < 15; i++)
            {
                bool retained = RetainedPawnRegistry.RetainedCustody(people[i]);
                T.Eq(retained, r.Reserves(pawns[i]), "stage 2: person " + i + " reservation");
                if (retained) T.Check(VanillaSees(r, pawns[i], true) == ObservedKind.WorldFree, "stage 2: person " + i + " is ReservedByQuest by the registry, never Free");
            }
            T.Eq(0, n.physical.creates, "the whole load generated nothing");
        }

        private static void ScribeStages()
        {
            // The REAL Scribe: a bound PawnRef written by a save and read back through the real LoadingVars pass. What FinalizeInit sees is the
            // PERSISTED thing id and custody: those are plain values, loaded in LoadingVars. (The pawn POINTER is resolved by vanilla's later
            // cross-reference pass, which a headless run cannot reproduce: that ordering is the audited fact pinned by LoadOrderScan, and the
            // pointer is therefore saved as null here, exactly as an unresolved one would be.) A stage-1 registry built from the LOADED store, never
            // from hand-built data, must already cover the pawn.
            TestNet n = new TestNet(9707);
            NetworkActor a = Solo(n, "scribe");
            KnownCharacter c = Self(n, a);
            c.pawn = new PawnRef { pawn = null, thingIdNumber = 4242, defName = "Human", boundTick = 1, agedThroughTick = 1 };
            c.custody = CustodyState.Stored;
            NetworkState state = new NetworkState { actors = n.ctx.actors, characters = n.ctx.characters, knowledge = n.ctx.knowledge, contracts = n.ctx.contracts, operations = n.ctx.operations, consequences = n.ctx.consequences, deployments = n.ctx.episodes };
            string path = PersistenceTests.SaveState(state, SaveMigrations.Current);
            NetworkState loaded = new NetworkState();
            bool ranStage1 = false;
            try
            {
                Scribe.loader.InitLoading(path);
                try
                {
                    List<string> failures = new List<string>();
                    loaded.ExposeStores(failures);
                    T.Eq(0, failures.Count, "no store failed to load");
                    // ---- STAGE 1 (World.FinalizeInit): after LoadingVars, BEFORE FinalizeLoading. BuildRuntime's first act is the same index rebuild.
                    loaded.RebuildIndexes();
                    KnownCharacter lc = loaded.characters.Get(c.id);
                    T.Check(lc != null && lc.pawn != null, "the person loaded");
                    if (lc != null && lc.pawn != null)
                    {
                        T.Eq(4242, lc.pawn.thingIdNumber, "the persisted thing id IS loaded by LoadingVars");
                        T.Eq(CustodyState.Stored, lc.custody, "and so is custody");
                        T.Check(lc.pawn.IsBound, "the binding counts as bound without any pointer");
                        DomainContext view = new DomainContext { characters = loaded.characters };
                        RetainedPawnRegistry r = new RetainedPawnRegistry(view);
                        T.Eq(1, r.RebuildEarly(), "stage 1 indexed the loaded binding by its thing id");
                        T.Eq(1, r.DurableRetainedCount(), "the registry knows one person must be reserved, from the loaded durable state alone");
                        T.Check(r.Reserves(MakePawn(4242)), "and covers that pawn before any pointer resolves");
                        T.Check(!r.Reserves(MakePawn(4243)), "and no other");
                        ranStage1 = true;
                    }
                }
                finally
                {
                    Scribe.loader.FinalizeLoading();
                }
            }
            finally
            {
                File.Delete(path);
            }
            T.Check(ranStage1, "the staged load was exercised");
        }

        // ================================================================== Fix 1 / 7: scans

        private static void ScanQuestRestore()
        {
            string port = Code("Integration/Physical/RimWorldPhysicalWorldPort.cs");
            string stage2 = Body(port, "public RegistryLoadReport OnReferencesResolved()", "public string OnLoaded()");
            T.Check(Regex.IsMatch(stage2, @"report\.durableRetained > 0\)\s*\{\s*try\s*\{\s*Quest q = Registry\.EnsureQuest\(\);"), "the registry quest is ensured at stage 2 whenever the DURABLE state says anyone is retained");
            T.Check(stage2.Contains("Registry.ResolvePointers();") && !stage2.Contains("RetainedCount() > 0"), "the decision reads durable state, never an index that may be empty");
            string load = Body(port, "public string OnLoaded()", "public string PrepareForRemoval()");
            T.Check(load.Contains("Registry.DurableRetainedCount()") && Regex.IsMatch(load, @"if \(durable > 0\)"), "the first-tick pass re-ensures it from durable state (idempotent)");
            string registry = Code("Integration/Physical/RetainedPawnRegistry.cs");
            string find = Body(registry, "public Quest FindQuest()", "public Quest EnsureQuest()");
            T.Check(find.Contains("q.State != QuestState.Ongoing") && find.Contains("cachedQuest.State == QuestState.Ongoing"), "only an ONGOING registry quest counts as live (vanilla's QuestReserves answers only for Ongoing)");
            string ensure = Body(registry, "public Quest EnsureQuest()", "public int Release()");
            T.Check(ensure.Contains("q.SetInitiallyAccepted();") && ensure.Contains("Find.QuestManager.Add(q);") && ensure.Contains("QuestPart_NetworkRetainedPawns"), "a missing quest is recreated hidden, accepted, with the Network part");
            string resume = Body(registry, "public void Resume()", "public void OnPawnEvent");
            T.Check(resume.Contains("DurableRetainedCount() > 0"), "a resume recreates it from durable state too");
            string rawWorld = PhysicalLifecycleTests.Src("Core/NetworkWorldComponent.cs");
            T.Check(rawWorld.Contains("PostLoadInit runs inside Scribe.loader.FinalizeLoading, AFTER every cross-reference"), "documented: the stage-2 hook is the narrow supported point (vanilla's PostLoadInit)");
        }

        private static void ScanLoadNeverGenerates()
        {
            string[] forbidden = { "PawnGenerator", "GeneratePawn", "GenSpawn", "SpawnSetup", ".Spawn(", "ThingMaker", ".Discard(", ".Destroy(", "PassToWorld(", "AddHediff", "SetFaction", "PawnProjection", "Create(" };
            string registry = Code("Integration/Physical/RetainedPawnRegistry.cs");
            string port = Code("Integration/Physical/RimWorldPhysicalWorldPort.cs");
            string world = Code("Core/NetworkWorldComponent.cs");
            Dictionary<string, string> loadCode = new Dictionary<string, string>
            {
                { "RetainedPawnRegistry.RebuildEarly", Body(registry, "public int RebuildEarly()", "public RegistryLoadReport ResolvePointers()") },
                { "RetainedPawnRegistry.ResolvePointers", Body(registry, "public RegistryLoadReport ResolvePointers()", "public RegistryLoadReport Rebuild()") },
                { "RetainedPawnRegistry.Audit", Body(registry, "public List<BindingFinding> Audit()", "private static BindingFinding Finding") },
                { "OnReferencesResolved", Body(port, "public RegistryLoadReport OnReferencesResolved()", "public string OnLoaded()") },
                { "OnLoaded", Body(port, "public string OnLoaded()", "public string PrepareForRemoval()") },
                { "ResolveRetentionAfterLoad", Body(world, "private void ResolveRetentionAfterLoad()", "public override void FinalizeInit") }
            };
            foreach (KeyValuePair<string, string> kv in loadCode)
            {
                T.Check(kv.Value.Length > 40, "found " + kv.Key);
                foreach (string f in forbidden) T.Check(!kv.Value.Contains(f), "the load path " + kv.Key + " never calls " + f + " (nothing generated, spawned, rerolled, passed, destroyed or discarded at load)");
                T.Check(!Regex.IsMatch(kv.Value, @"\.pawn\s*=[^=]") && !Regex.IsMatch(kv.Value, @"thingIdNumber\s*=[^=]"), "the load path " + kv.Key + " never rewrites a binding");
            }
            // No production Network code deliberately destroys or discards a pawn (the physical tier disposes only its own disposable test pawns).
            foreach (string f in PhysicalLifecycleTests.AllSources())
            {
                string rel = f.Replace('\\', '/');
                if (rel.Contains("/Diagnostics/RuntimePhysicalTests/")) continue;
                string code = PhysicalLifecycleTests.Code(File.ReadAllText(f));
                T.Check(!Regex.IsMatch(code, @"\bp(awn)?\.(Destroy|Discard)\("), "production code never destroys or discards a pawn (" + Path.GetFileName(f) + ")");
            }
            // No per-tick global pawn or world-pawn scan was added: the registry and the new files never touch a global pawn list.
            foreach (string rel in new[] { "Integration/Physical/RetainedPawnRegistry.cs", "Integration/Physical/FactionMemberKinds.cs", "Domain/Physical/BindingRules.cs" })
            {
                string code = Code(rel);
                foreach (string g in new[] { "PawnsFinder", "AllPawnsAlive", "AllPawns", "GetPawnsBySituation", "WorldPawnsTick", "DefDatabase<PawnKindDef>.AllDefs" })
                    T.Check(!code.Contains(g), rel + " never reads a global pawn list (" + g + ")");
            }
            T.Check(!Code("Integration/Physical/RetainedPawnRegistry.cs").Contains("Scribe") && !Code("Domain/Physical/BindingRules.cs").Contains("Scribe"), "no persisted field was added by the load fix (save format unchanged)");
            T.Eq(5, SaveMigrations.Current, "save format stays 5");
        }

        private static void ScanValidation()
        {
            string v = Code("Core/NetValidator.cs");
            string body = Body(v, "private static void CheckBindings(NetworkRuntime rt, ValidationReport report)", "private static void CheckEpisodes");
            T.Check(v.Contains("CheckEpisodes(rt, report);\n            CheckBindings(rt, report);") || Regex.IsMatch(v, @"CheckEpisodes\(rt, report\);\s*CheckBindings\(rt, report\);"), "the normal post-load validation runs the binding audit");
            T.Check(body.Contains("registry.Audit()") && body.Contains("report.Add(findings[i].ToString(), false)"), "every finding is REPORTED, never repaired");
            T.Check(body.Contains("covered < durable") && body.Contains("reservation gap"), "and a reservation gap is stated");
            foreach (string f in new[] { "Create(", "PawnGenerator", "Spawn", "Discard", "Destroy", "pawn = ", "EnsureQuest", "Resume", "Release" })
                T.Check(!body.Contains(f), "the integrity check never calls " + f + ": nothing regenerated, cleared, repaired or marked healthy");
            string verifier = Body(Code("Diagnostics/RuntimePhysicalTests/PhysicalScenarios.cs"), "public sealed class Phyx010VerifyAfterLoad", "private void Verify()");
            T.Check(verifier.Length > 100, "found the read-only verifier");
            string verify = Body(Code("Diagnostics/RuntimePhysicalTests/PhysicalScenarios.cs"), "private void Verify()", "RT-PHYX-011");
            T.Check(verify.Contains("port.Registry.Audit()") && verify.Contains("v.Fail(integrity[i].ToString())"), "the owner's read-only verifier fails on every binding-integrity finding");
        }
    }
}
