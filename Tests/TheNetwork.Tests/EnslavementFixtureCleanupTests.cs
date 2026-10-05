using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using TheNetwork.Diagnostics.RuntimePhysicalTests;
using Verse;

namespace TheNetwork.Tests
{
    /// <summary>Real archive/Scribe/IL checks. Map-dependent generation, despawn and pawn destruction still need the owner rerun.</summary>
    public static class EnslavementFixtureCleanupTests
    {
        public static void Register(List<KeyValuePair<string, Action>> tests)
        {
            tests.Add(new KeyValuePair<string, Action>("EnslavementFixture.OnlyOwnNewMessageRemovedAndLiveTargetsDetached", OwnMessage));
            tests.Add(new KeyValuePair<string, Action>("EnslavementFixture.UnexpectedOrAmbiguousReferencePreservesAllMessages", RefuseUnexpected));
            tests.Add(new KeyValuePair<string, Action>("EnslavementFixture.NoMessageAndMissingPrerequisites", NoMessage));
            tests.Add(new KeyValuePair<string, Action>("EnslavementFixture.RealArchiveSaveLoadWithDanglingWardenNegativeControl", ArchiveSaveLoad));
            tests.Add(new KeyValuePair<string, Action>("EnslavementFixture.VanillaMessagePersistenceAndDestroyDiscardOrder", VanillaAudit));
            tests.Add(new KeyValuePair<string, Action>("EnslavementFixture.RealWorldDiscardModeNeverRetainsAndGuardsReentry", WorldDiscard));
            tests.Add(new KeyValuePair<string, Action>("EnslavementFixture.RuntimeCleanupOrderAndIsolation", RuntimeGate));
        }

        private static TV Shell<TV>() { return (TV)FormatterServices.GetUninitializedObject(typeof(TV)); }

        private static Pawn Pawn(int id)
        {
            ThingDef def = Shell<ThingDef>();
            def.defName = "Human";
            return new Pawn { def = def, thingIDNumber = id };
        }

        private static Message Message(int id, Pawn prisoner = null, Pawn warden = null)
        {
            Message message = Shell<Message>();
            AccessTools.Field(typeof(Message), "ID").SetValue(message, id);
            message.def = MessageTypeDefOf.NeutralEvent;
            // Same text deliberately: identity/target evidence, not translation, must select the fixture message.
            message.text = "fixture enslavement";
            message.lookTargets = prisoner == null ? new LookTargets() : new LookTargets(prisoner, warden);
            return message;
        }

        private static void OwnMessage()
        {
            Archive archive = new Archive();
            Pawn prisoner = Pawn(55841), warden = Pawn(55842), otherWarden = Pawn(55843);
            Message old = Message(1, prisoner, otherWarden);
            archive.Add(old);
            archive.Pin(old);
            HashSet<IArchivable> before = new HashSet<IArchivable>(archive.ArchivablesListForReading);
            Message unrelated = Message(2, prisoner, otherWarden), owned = Message(3, prisoner, warden);
            archive.Add(unrelated);
            archive.Pin(unrelated);
            archive.Add(owned);
            archive.Pin(owned);
            Message liveAlias = owned;
            LookTargets unrelatedTargets = unrelated.lookTargets;
            int removed;
            string refusal;
            T.Check(EnslavementFixtureCleanup.TryRemoveMessage(archive, before, prisoner, warden, out removed, out refusal), "the exact new neutral-event message is removable");
            T.Eq(1, removed, "only one message removed");
            T.Check(!archive.Contains(owned) && !archive.IsPinned(owned), "real Archive.Remove removes the entry and its pin");
            T.Check(archive.Contains(old) && archive.IsPinned(old) && archive.Contains(unrelated) && archive.IsPinned(unrelated), "pre-existing and newly added unrelated messages/pins survive");
            T.Check(ReferenceEquals(unrelatedTargets, unrelated.lookTargets) && unrelated.lookTargets.targets.Count == 2, "unrelated targets are untouched");
            T.Check(liveAlias.lookTargets != null && !liveAlias.lookTargets.Any, "the same live Message object has no discarded-warden target");
            T.Check(EnslavementFixtureCleanup.TryRemoveMessage(archive, before, prisoner, warden, out removed, out refusal) && removed == 0, "cleanup is idempotent");
        }

        private static void RefuseUnexpected()
        {
            foreach (string reason in new[] { "pre-existing", "reversed", "extra-target", "wrong-type", "duplicate" })
            {
                Archive archive = new Archive();
                Pawn prisoner = Pawn(55841), warden = Pawn(55842);
                Message owned = Message(1, prisoner, warden);
                archive.Add(owned);
                HashSet<IArchivable> before = new HashSet<IArchivable>();
                if (reason == "pre-existing") before.Add(owned);
                if (reason == "reversed") owned.lookTargets = new LookTargets(warden, prisoner);
                if (reason == "extra-target") owned.lookTargets.targets.Add(Pawn(55843));
                if (reason == "wrong-type") owned.def = new MessageTypeDef();
                if (reason == "duplicate") archive.Add(Message(2, prisoner, warden));
                int count = archive.ArchivablesListForReading.Count, removed;
                LookTargets original = owned.lookTargets;
                string refusal;
                T.Check(!EnslavementFixtureCleanup.TryRemoveMessage(archive, before, prisoner, warden, out removed, out refusal), reason + ": refuse unsafe cleanup");
                T.Check(refusal != null && removed == 0 && archive.ArchivablesListForReading.Count == count && ReferenceEquals(original, owned.lookTargets), reason + ": fail before any archive or target mutation");
            }
        }

        private static void NoMessage()
        {
            Archive archive = new Archive();
            Message unrelated = Message(1);
            archive.Add(unrelated);
            Pawn prisoner = Pawn(55841), warden = Pawn(55842);
            int removed;
            string refusal;
            HashSet<IArchivable> before = new HashSet<IArchivable>(archive.ArchivablesListForReading);
            T.Check(EnslavementFixtureCleanup.TryRemoveMessage(archive, before, prisoner, warden, out removed, out refusal) && removed == 0 && archive.Contains(unrelated), "rejected/early-throwing enslavement needs no message removal");
            T.Check(!EnslavementFixtureCleanup.TryRemoveMessage(null, before, prisoner, warden, out removed, out refusal), "missing archive fails closed");
            T.Check(!EnslavementFixtureCleanup.TryRemoveMessage(archive, null, prisoner, warden, out removed, out refusal), "missing baseline fails closed");
            T.Check(!EnslavementFixtureCleanup.TryRemoveMessage(archive, before, prisoner, prisoner, out removed, out refusal), "the contractor cannot be its own disposable warden");
        }

        private static void ArchiveSaveLoad()
        {
            GenTypes.GetTypeInAnyAssembly(typeof(Archive).FullName);
            foreach (bool dangling in new[] { true, false })
            {
                Archive archive = new Archive();
                Message unrelated = Message(1);
                archive.Add(unrelated);
                HashSet<IArchivable> before = new HashSet<IArchivable>(archive.ArchivablesListForReading);
                Pawn prisoner = Pawn(55841), warden = Pawn(55842);
                Message owned = Message(2, prisoner, warden);
                archive.Add(owned);
                int removed;
                string refusal;
                if (!dangling) T.Check(EnslavementFixtureCleanup.TryRemoveMessage(archive, before, prisoner, warden, out removed, out refusal) && removed == 1, "fixture cleanup precedes the real archive save");
                string path = Path.Combine(Path.GetTempPath(), "thenetwork-warden-archive-" + Guid.NewGuid().ToString("N") + ".xml");
                int logStart = T.vanillaLog.Count;
                try
                {
                    Scribe.saver.InitSaving(path, "root");
                    try { Scribe_Deep.Look(ref archive, "archive"); }
                    finally { Scribe.saver.FinalizeSaving(); }
                    string xml = File.ReadAllText(path);
                    T.Eq(dangling, xml.Contains("Thing_Human55842"), "only the negative control serializes the missing warden reference");
                    Archive loaded = null;
                    Scribe.loader.InitLoading(path);
                    try { Scribe_Deep.Look(ref loaded, "archive"); }
                    finally { Scribe.loader.FinalizeLoading(); }
                    T.Eq(dangling ? 2 : 1, loaded.ArchivablesListForReading.Count, "unrelated archived message survives real save/load");
                    string[] logs = T.vanillaLog.Skip(logStart).ToArray();
                    T.Eq(dangling, logs.Any(x => x.Contains("Could not resolve reference") && x.Contains("Thing_Human55842") && x.Contains("targets/1")), "real Scribe reproduces exactly the warden's LookTargets /targets/1 error only without cleanup");
                    if (!dangling) T.Eq(0, logs.Length, "cleaned archive saves and loads without vanilla errors or warnings");
                }
                finally { Scribe.ForceStop(); File.Delete(path); }
            }
        }

        private static List<CodeInstruction> IL(MethodBase method) { return PatchProcessor.GetOriginalInstructions(method); }
        private static int Call(List<CodeInstruction> il, Type type, string name)
        {
            return il.FindIndex(x => x.operand is MethodInfo m && m.DeclaringType == type && m.Name == name);
        }

        private static void VanillaAudit()
        {
            List<CodeInstruction> enslave = IL(typeof(GenGuest).GetMethod("TryEnslavePrisoner"));
            T.Check(enslave.Any(x => x.operand is ConstructorInfo c && c.DeclaringType == typeof(LookTargets)) && Call(enslave, typeof(Messages), "Message") >= 0, "real enslavement constructs the vanilla message's LookTargets");
            MethodInfo overload = typeof(Messages).GetMethod("Message", new[] { typeof(string), typeof(LookTargets), typeof(MessageTypeDef), typeof(bool) });
            T.Eq(true, overload.GetParameters()[3].DefaultValue, "enslavement's omitted historical argument defaults to true");
            T.Check(Call(IL(typeof(Messages).GetMethod("Message", new[] { typeof(Message), typeof(bool) })), typeof(Archive), "Add") >= 0, "the live Message object is also archived");
            T.Check(Call(IL(typeof(Archive).GetMethod("ExposeData")), typeof(Scribe_Collections), "Look") >= 0 &&
                Call(IL(typeof(Message).GetMethod("ExposeData")), typeof(Scribe_Deep), "Look") >= 0 &&
                Call(IL(typeof(LookTargets).GetMethod("ExposeData")), typeof(Scribe_Collections), "Look") >= 0, "archive/message/LookTargets really expose their persisted data");
            T.Check(typeof(Archive).GetMethod("Remove", new[] { typeof(IArchivable) }).IsPublic, "single-archivable removal is a supported public API");
            T.Check(!typeof(Messages).GetMethods(BindingFlags.Public | BindingFlags.Static).Any(m => m.Name == "Remove"), "there is no public single-live-message removal API");
            List<CodeInstruction> discard = IL(AccessTools.Method(typeof(WorldPawns), "DiscardPawn"));
            int destroyCall = Call(discard, typeof(Thing), "Destroy");
            int discardCall = Call(discard, typeof(Thing), "Discard");
            T.Check(destroyCall >= 0 && discardCall > destroyCall, "vanilla explicit discard destroys the pawn before discarding it");
            T.Check(Call(IL(typeof(Pawn).GetMethod("Destroy")), typeof(WorldPawns), "IsBeingDiscarded") >= 0, "Pawn.Destroy respects vanilla's discard re-entry guard");
            Thing thing = new Thing();
            int logStart = T.vanillaLog.Count;
            thing.Discard(true);
            T.Check(!thing.Discarded && T.vanillaLog.Skip(logStart).Any(x => x.Contains("Tried to discard") && x.Contains("state is -1")), "real Thing.Discard reproduces the invalid despawned-state warning");
            AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(thing, (sbyte)-2);
            logStart = T.vanillaLog.Count;
            thing.Discard(true);
            T.Check(thing.Discarded && T.vanillaLog.Count == logStart, "real Thing.Discard accepts destroyed state -2 and moves to discarded -3 silently");
        }

        // Only the map-dependent virtual pawn operations are probes. The orchestrator and its re-entry guard are real WorldPawns.
        private sealed class DiscardProbe : Pawn
        {
            public WorldPawns world;
            public string calls = "";
            public override void Destroy(DestroyMode mode = DestroyMode.Vanish)
            {
                T.Check(world.IsBeingDiscarded(this), "vanilla installs its guard before pawn destruction");
                calls += "Destroy;";
                AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(this, (sbyte)-2);
            }
            public override void Discard(bool silentlyRemoveReferences = false)
            {
                T.Check(Destroyed && !world.Contains(this), "vanilla calls Discard only after destruction and outside world retention");
                calls += "Discard;";
                AccessTools.Field(typeof(Thing), "mapIndexOrState").SetValue(this, (sbyte)-3);
            }
        }

        private static void WorldDiscard()
        {
            WorldPawns world = new WorldPawns();
            DiscardProbe probe = new DiscardProbe { world = world, def = Pawn(55842).def, thingIDNumber = 55842 };
            world.PassToWorld(probe, PawnDiscardDecideMode.Discard);
            T.Eq("Destroy;Discard;", probe.calls, "real explicit discard executes the safe state order");
            T.Check(probe.Discarded && !world.Contains(probe) && !world.IsBeingDiscarded(probe), "no world pawn retained, guard unwound");
        }

        private static void RuntimeGate()
        {
            string code = PhysicalLifecycleTests.Code(PhysicalLifecycleTests.Src("Diagnostics/RuntimePhysicalTests/PhysicalCustodyScenarios.cs"));
            int start = code.IndexOf("public sealed class Phyx022Enslavement", StringComparison.Ordinal);
            string run = code.Substring(start, code.IndexOf("public sealed class Phyx023Kidnapped", start, StringComparison.Ordinal) - start);
            int snapshot = run.IndexOf("new HashSet<IArchivable>(archive.ArchivablesListForReading)", StringComparison.Ordinal);
            int enslave = run.IndexOf("GenGuest.TryEnslavePrisoner(warden, p)", StringComparison.Ordinal);
            int clean = run.IndexOf("EnslavementFixtureCleanup.TryRemoveMessage", StringComparison.Ordinal);
            int despawn = run.IndexOf("warden.DeSpawn()", StringComparison.Ordinal);
            int guard = run.IndexOf("TestFixtures.DisposeRefusal(warden, runId, ctx)", StringComparison.Ordinal);
            int discard = run.IndexOf("Find.WorldPawns.PassToWorld(warden, PawnDiscardDecideMode.Discard)", StringComparison.Ordinal);
            T.Check(snapshot >= 0 && snapshot < enslave && enslave < clean && clean < despawn && despawn < guard && guard < discard, "022 snapshots, runs real API, cleans the message, then ownership-guards vanilla destruction/discard");
            T.Check(run.Contains("if (cleaned)") && run.Contains("warden preserved on the test map") && run.Contains("removed == 1"), "cleanup failure preserves a saveable fixture; success checks exactly one created message");
            T.Check(!run.Contains("PawnDiscardDecideMode.KeepForever") && !run.Contains("warden.Discard(") && !run.Contains("TestFixtures.TryDispose(warden"), "no fixture retention or invalid direct-discard fallback");
            string helper = PhysicalLifecycleTests.Code(PhysicalLifecycleTests.Src("Diagnostics/RuntimePhysicalTests/EnslavementFixtureCleanup.cs"));
            T.Check(helper.Contains("archive.Remove(owned)") && helper.Contains("before.Contains(message)") && helper.Contains("targets.Count != 2") && helper.Contains("ReferenceEquals(targets[0].Thing, prisoner)") && helper.Contains("ReferenceEquals(targets[1].Thing, warden)"), "the one removal has baseline and exact target-identity gates");
            T.Check(!run.Contains("Messages.Clear") && !helper.Contains(".Clear(") && !helper.Contains("RemoveAll(") && !helper.Contains("Reflection") && !helper.Contains("Scribe_"), "no broad message removal, reflection hack or new persisted state");
            T.Check(run.Contains("TestFixtures.Disposable(PawnKindDefOf.Colonist, Faction.OfPlayer") && !run.Contains("Colonists") && !run.Contains("HomeMap"), "a fresh tagged warden, never a real colony pawn");
        }
    }
}
