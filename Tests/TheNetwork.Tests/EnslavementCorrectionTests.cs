using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Xml;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Physical;
using TheNetwork.Integration.Physical;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Settings;
using Verse;

namespace TheNetwork.Tests
{
    /// <summary>
    /// The owner found a dangling slaveFaction after synthetic RT-PHYX-022. These tests read the REAL vanilla IL, round-trip the REAL
    /// guest tracker through Scribe (including a failing reference as a negative control), exercise the real chooser/removal queue, and
    /// reconcile holder changes through the production domain over the fake port. Full TryEnslavePrisoner and map save/load need the owner.
    /// No new test patch is installed; Harmony's existing IL reader only reads instructions.
    /// </summary>
    public static class EnslavementCorrectionTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            EnslavementFixtureCleanupTests.Register(t);
            t.Add(new KeyValuePair<string, Action>("Enslavement.RuntimeUsesVanillaAndOwnWarden", RuntimePath));
            t.Add(new KeyValuePair<string, Action>("Enslavement.VanillaClearsHiddenDefBeforeCaching", VanillaSequence));
            t.Add(new KeyValuePair<string, Action>("Enslavement.GuestTrackerReferenceSaveLoadWithNegativeControl", GuestReferenceSaveLoad));
            t.Add(new KeyValuePair<string, Action>("Enslavement.SamePawnHolderOnlyUnavailableAndSaveLoad", HolderOnly));
            t.Add(new KeyValuePair<string, Action>("EncounterFaction.HiddenCapabilityMatrix", HiddenCapability));
            t.Add(new KeyValuePair<string, Action>("EncounterFaction.PreferredAndDeterministicSafeFallback", ChooseSafe));
            t.Add(new KeyValuePair<string, Action>("EncounterFaction.NoEligibleDefFailsClosed", FailClosed));
            t.Add(new KeyValuePair<string, Action>("EncounterFaction.ReleaseQueueIsIdempotentAndPermanentUntouched", ReleaseIdempotent));
        }

        private static TV Shell<TV>() { return (TV)FormatterServices.GetUninitializedObject(typeof(TV)); }

        private static string Code(string rel) { return PhysicalLifecycleTests.Code(PhysicalLifecycleTests.Src(rel)); }

        private static void RuntimePath()
        {
            string code = Code("Diagnostics/RuntimePhysicalTests/PhysicalCustodyScenarios.cs");
            int start = code.IndexOf("public sealed class Phyx022Enslavement", StringComparison.Ordinal);
            int end = code.IndexOf("public sealed class Phyx023Kidnapped", start, StringComparison.Ordinal);
            string run = code.Substring(start, end - start);
            T.Check(run.Contains("GenGuest.TryEnslavePrisoner(warden, p)"), "022 calls vanilla's exact public warden/prisoner API");
            T.Check(!run.Contains("SetGuestStatus(") && !run.Contains("SetFactionDirect("), "022 does not duplicate vanilla's faction/guest sequence");
            T.Check(run.Contains("TestFixtures.Disposable(PawnKindDefOf.Colonist, Faction.OfPlayer") && run.Contains("TestSite.IsTestMap(p.Map)"), "the warden is the run's own player fixture, on the guarded test map");
            T.Check(run.Contains("finally") && run.Contains("EnslavementFixtureCleanup.TryRemoveMessage") && run.Contains("TestFixtures.DisposeRefusal(warden, runId, ctx)") && run.Contains("Find.WorldPawns.PassToWorld(warden, PawnDiscardDecideMode.Discard)"), "the tagged unbound warden uses vanilla destroy/discard after message cleanup even if enslavement throws");
            T.Check(!run.Contains("TestFixtures.TryDispose(warden") && !run.Contains("warden.Discard("), "022 never uses the invalid despawn/direct-discard sequence");
            T.Check(run.Contains("p.SlaveFaction == null") && run.Contains("Contains(encounterFaction)") && run.Contains("EncounterFactions.Release(e.faction)"), "022 proves no cached shell reference before and after vanilla removes it through the existing RELEASE");
            T.Check(run.Contains("ReferenceEquals(c.pawn.pawn, p)") && run.Contains("Availability.Unavailable") && run.Contains("CharacterStatus.Captured"), "022 pins identity, old-NPC exclusion and Captured status");
            T.Check(!Regex.IsMatch(run, @"Scribe_\w+\.Look|IExposable|ExposeData"), "the scenario adds no persisted state");
        }

        private static List<CodeInstruction> IL(MethodBase method) { return PatchProcessor.GetOriginalInstructions(method); }

        private static int Call(List<CodeInstruction> il, Type type, string name)
        {
            return il.FindIndex(x => x.operand is MethodInfo m && m.DeclaringType == type && m.Name == name);
        }

        private static void VanillaSequence()
        {
            MethodInfo method = typeof(GenGuest).GetMethod("TryEnslavePrisoner", new[] { typeof(Pawn), typeof(Pawn) });
            T.Check(method != null && method.IsPublic && method.ReturnType == typeof(bool), "the supplied 1.6 assembly exposes the audited API");
            List<CodeInstruction> il = IL(method);
            int hidden = il.FindIndex(x => x.opcode == OpCodes.Ldfld && x.operand is FieldInfo f && f.DeclaringType == typeof(FactionDef) && f.Name == "hidden");
            int clear = Call(il, typeof(Thing), "SetFactionDirect");
            int guest = Call(il, typeof(Pawn_GuestTracker), "SetGuestStatus");
            T.Check(hidden >= 0 && hidden < clear && clear < guest, "vanilla checks the DEF, clears the old faction, THEN sets slave status");
            T.Check(clear > 0 && il[clear - 1].opcode == OpCodes.Ldnull, "the actual clearing call takes null");
            T.Check(Call(il, typeof(Faction), "get_Hidden") < 0, "the instance Hidden override is not the enslavement capability");
            T.Check(hidden >= 0 && il[hidden + 1].opcode.FlowControl == FlowControl.Cond_Branch, "the DEF-hidden check actually controls entry to the clearing path");
            T.Check(Call(il, typeof(Pawn), "get_IsSlave") >= 0 && Call(il, typeof(Pawn_CreepJoinerTracker), "DoAggressive") >= 0, "vanilla's existing-slave and creepjoiner handling stays inside the real API");
            List<CodeInstruction> tracker = IL(typeof(Pawn_GuestTracker).GetMethod("SetGuestStatus"));
            T.Check(tracker.Any(x => x.opcode == OpCodes.Stfld && x.operand is FieldInfo f && f.Name == "slaveFactionInt"), "the vanilla tracker caches a faction reference");
            int change = Call(tracker, typeof(Thing), "SetFaction");
            int cache = tracker.FindIndex(x => x.opcode == OpCodes.Stfld && x.operand is FieldInfo f && f.Name == "slaveFactionInt");
            T.Check(change >= 0 && change < cache, "the faction change can notify removal BEFORE the slave cache is assigned");
            List<CodeInstruction> setFaction = IL(typeof(Pawn).GetMethod("SetFaction"));
            T.Check(Call(setFaction, typeof(Pawn_GuestTracker), "SetGuestStatus") < Call(setFaction, typeof(FactionManager), "Notify_PawnLeftFaction"), "the real Pawn faction change clears guest state before the old-faction removal notification");
            List<CodeInstruction> tick = IL(typeof(FactionManager).GetMethod("FactionManagerTick"));
            T.Check(Call(tick, typeof(FactionManager), "Remove") >= 0 && Call(tick, typeof(FactionManager), "FactionCanBeRemoved") < 0, "a queued faction is removed without rechecking a later slave cache");
            List<CodeInstruction> remove = IL(AccessTools.Method(typeof(FactionManager), "Remove"));
            T.Check(Call(remove, typeof(Thing), "SetFaction") >= 0 && !remove.Any(x => x.operand is FieldInfo f && f.Name == "slaveFactionInt"), "removal nulls main factions; it does not clear the guest tracker's cache");
        }

        private static void GuestReferenceSaveLoad()
        {
            // Warm vanilla's type catalog before measuring Scribe. The supplied headless reference set lacks unrelated audio types.
            GenTypes.GetTypeInAnyAssembly(typeof(Pawn_GuestTracker).FullName);
            // Fixture INPUTS, not an emulation of enslavement: the audited hidden-def path leaves null; the old test cached the shell.
            // Real vanilla tracker serialization/reference resolution below demonstrates both the safe case and the exact failure.
            foreach (bool dangling in new[] { false, true })
            {
                Faction shell = new Faction { loadID = 19022, def = Shell<FactionDef>(), temporary = true };
                Pawn_GuestTracker tracker = new Pawn_GuestTracker { guestStatusInt = GuestStatus.Slave, joinStatus = JoinStatus.JoinAsSlave };
                AccessTools.Field(typeof(Pawn_GuestTracker), "slaveFactionInt").SetValue(tracker, dangling ? shell : null);
                string path = Path.Combine(Path.GetTempPath(), "thenetwork-slave-reference-" + Guid.NewGuid().ToString("N") + ".xml");
                int before = T.vanillaLog.Count;
                try
                {
                    Scribe.saver.InitSaving(path, "root");
                    try { Scribe_Deep.Look(ref tracker, "guest"); }
                    finally { Scribe.saver.FinalizeSaving(); }
                    XmlDocument xml = new XmlDocument();
                    xml.Load(path);
                    string saved = xml.SelectSingleNode("/root/guest/slaveFaction")?.InnerText;
                    T.Eq(dangling ? "Faction_19022" : "null", saved, "the REAL tracker writes the expected slaveFaction");
                    T.Eq(dangling, T.vanillaLog.Skip(before).Any(x => x.Contains("Faction_19022") && x.Contains("not deep-saved")), "save detects only the deliberately dangling reference");
                    Pawn_GuestTracker loaded = null;
                    Scribe.loader.InitLoading(path);
                    try { Scribe_Deep.Look(ref loaded, "guest"); }
                    finally { Scribe.loader.FinalizeLoading(); }
                    T.Eq(GuestStatus.Slave, loaded.GuestStatus, "slave status survives the REAL Scribe load");
                    T.Eq(dangling, T.vanillaLog.Skip(before).Any(x => x.Contains("Could not resolve reference") && x.Contains("Faction_19022")), "the real cross-reference resolver reproduces the bug only for the negative control");
                    if (!dangling) T.Check(loaded.SlaveFaction == null && T.vanillaLog.Count == before, "the cleared faction stays null across save/load with no vanilla warning or error");
                }
                finally { Scribe.ForceStop(); File.Delete(path); }
            }
        }

        private static void HolderOnly()
        {
            foreach (ContractorForm form in new[] { ContractorForm.Solo, ContractorForm.Crew })
            {
                TestNet n = new TestNet(9840 + (int)form);
                NetworkActor a = PhysicalLifecycleTests.Make(n, form, "slave-holder");
                OrganizationProfile org = a.Get<OrganizationProfile>();
                KnownCharacter c = org == null ? PhysicalLifecycleTests.Self(n, a) : n.ctx.characters.Get(org.leader);
                PhysicalEpisode episode = PhysicalLifecycleTests.Begin(n, a, new[] { c });
                Pawn pawn = new Pawn { thingIDNumber = c.pawn.thingIdNumber };
                c.pawn.pawn = episode.members[0].pawn.pawn = pawn;
                n.physical.Hold(c.pawn, ObservedKind.HeldByPlayer, HeldKind.PlayerPrisoner);
                n.ctx.Lifecycle.Reconcile(episode, "arrest");
                int events = n.recorder.events.Count, creates = n.physical.creates, custodyEpisodes = n.ctx.Lifecycle.counters.custodyEpisodes;
                int since = c.heldSinceTick;
                n.physical.Hold(c.pawn, ObservedKind.HeldByPlayer, HeldKind.PlayerSlave);
                for (int i = 0; i < 3; i++) n.ctx.Lifecycle.ReconcileHeld(c, "slave holder");
                T.Check(c.custody == CustodyState.OutOfCustody && c.heldBy == HeldKind.PlayerSlave && c.status == CharacterStatus.Captured, form + ": Captured, vanilla-held PlayerSlave");
                T.Check(ReferenceEquals(pawn, c.pawn.pawn), form + ": same bound Pawn");
                T.Eq(since, c.heldSinceTick, form + ": continuous holding timestamp");
                T.Eq(events, n.recorder.events.Count, form + ": no second capture/group casualty event");
                T.Eq(custodyEpisodes, n.ctx.Lifecycle.counters.custodyEpisodes, form + ": holder bookkeeping creates no Custody episode");
                T.Check(!AuthorityGate.CanSimulateAbstractly(c) && !c.IsAvailable, form + ": no old NPC person simulation or availability");
                T.Check(!n.ctx.Contractors.Checkout(a, new OperationId(n.ids.NextId()), 1f).characters.Contains(c.id), form + ": actual old-NPC checkout excludes the slave");
                if (form == ContractorForm.Solo) T.Eq(Availability.Unavailable, n.ctx.Contractors.AvailabilityOf(a), "old Solo unavailable");
                // The fake port's domain round-trip uses null pointers and durable binding ids. Physical pointer resolution remains owner work.
                c.pawn.pawn = episode.members[0].pawn.pawn = null;
                PhysicalLifecycleTests.SaveLoad(n);
                KnownCharacter loaded = n.ctx.characters.Get(c.id);
                T.Check(loaded.heldBy == HeldKind.PlayerSlave && loaded.status == CharacterStatus.Captured && loaded.pawn.thingIdNumber == pawn.thingIDNumber && !AuthorityGate.CanSimulateAbstractly(loaded), form + ": persisted binding id, holder, fate and authority survive load (" + loaded.custody + ", " + loaded.heldBy + ", " + loaded.status + ", " + loaded.pawn.thingIdNumber + ")");
                T.Eq(creates, n.physical.creates, form + ": no replacement pawn before or after load");
            }
        }

        private sealed class Defs : IDisposable
        {
            private readonly List<FactionDef> original = new List<FactionDef>(DefDatabase<FactionDef>.AllDefsListForReading);
            private readonly FactionDef preferred = FactionDefOf.OutlanderRefugee;
            private readonly List<PawnKindDef> kinds = new List<PawnKindDef>();

            public Defs() { DefDatabase<FactionDef>.AllDefsListForReading.Clear(); FactionDefOf.OutlanderRefugee = null; }

            public FactionDef Add(string name, bool hidden)
            {
                FactionDef d = Shell<FactionDef>();
                d.defName = name;
                d.hidden = hidden;
                d.humanlikeFaction = true;
                ThingDef race = Shell<ThingDef>();
                race.race = new RaceProperties { intelligence = Intelligence.Humanlike };
                PawnKindDef kind = Shell<PawnKindDef>();
                kind.defName = "EnslavementFixture_" + name;
                kind.race = race;
                kind.defaultFactionDef = d;
                d.basicMemberKind = kind;
                DefDatabase<PawnKindDef>.Add(kind);
                kinds.Add(kind);
                DefDatabase<FactionDef>.AllDefsListForReading.Add(d);
                return d;
            }

            public void Dispose()
            {
                foreach (PawnKindDef k in kinds) AccessTools.Method(typeof(DefDatabase<PawnKindDef>), "Remove").Invoke(null, new object[] { k });
                DefDatabase<FactionDef>.AllDefsListForReading.Clear();
                DefDatabase<FactionDef>.AllDefsListForReading.AddRange(original);
                FactionDefOf.OutlanderRefugee = preferred;
            }
        }

        private static void HiddenCapability()
        {
            using (Defs defs = new Defs())
            {
                FactionDef d = defs.Add("Safe", true);
                T.Check(EncounterFactions.Qualifies(d), "hidden generic humanlike def qualifies");
                d.hidden = false;
                Faction hiddenInstance = new Faction { def = d, hidden = true, temporary = true };
                T.Check(hiddenInstance.Hidden && !EncounterFactions.Qualifies(d), "hiding an INSTANCE cannot make a non-hidden def safe");
                d.hidden = true;
                d.isPlayer = true;
                T.Check(!EncounterFactions.Qualifies(d), "hidden player def still refused");
                d.isPlayer = false;
                d.permanentEnemy = true;
                T.Check(!EncounterFactions.Qualifies(d), "hidden permanent enemy still refused");
                d.permanentEnemy = false;
                d.basicMemberKind.isBoss = true;
                T.Check(!EncounterFactions.Qualifies(d), "hidden boss-only member pool still refused");
                T.Check(!EncounterFactions.Qualifies(null), "missing def refused");
            }
        }

        private static void ChooseSafe()
        {
            using (Defs defs = new Defs())
            {
                FactionDef unsafeDef = defs.Add("A_Unsafe", false), z = defs.Add("Z_Safe", true), b = defs.Add("B_Safe", true);
                // These are the minimal chooser facts of the vanilla XML, not a claim to load the whole game's def database headlessly.
                FactionDef preferred = defs.Add("OutlanderRefugee", true);
                FactionDefOf.OutlanderRefugee = preferred;
                T.Check(EncounterFactions.Qualifies(preferred) && ReferenceEquals(preferred, EncounterFactions.ChooseDef()), "vanilla's def-hidden preferred shape still wins over an earlier safe fallback");
                FactionDefOf.OutlanderRefugee = unsafeDef;
                T.Check(ReferenceEquals(b, EncounterFactions.ChooseDef()), "a patched unsafe preferred def is rejected just like an unsafe fallback");
                DefDatabase<FactionDef>.AllDefsListForReading.Reverse();
                T.Check(ReferenceEquals(b, EncounterFactions.ChooseDef()), "fallback stays ordinal and independent of database order");
                b.hidden = false;
                preferred.hidden = false;
                T.Check(ReferenceEquals(z, EncounterFactions.ChooseDef()), "the next safe candidate wins; unsafe earlier defs are never accepted");
            }
        }

        private static void FailClosed()
        {
            using (Defs defs = new Defs())
            {
                FactionDefOf.OutlanderRefugee = defs.Add("OnlyUnsafe", false);
                T.Check(EncounterFactions.ChooseDef() == null, "no def-hidden generic candidate fails closed");
                T.Throws(() => EncounterFactions.Ensure(new EpisodeId(1), "no-faction", null, 0), "Ensure stops before creating any faction when no safe def exists");
            }
            string code = Code("Integration/Physical/EncounterFactions.cs");
            T.Check(code.Contains("f.temporary = true;") && code.Contains("NewGeneratedFactionWithRelations(def, relations, true)"), "creation remains vanilla hidden and temporary, never a permanent Network faction");
        }

        private static void ReleaseIdempotent()
        {
            Game oldGame = Current.Game;
            World oldWorld = Current.CreatingWorld;
            try
            {
                Game game = Shell<Game>();
                AccessTools.Field(typeof(Game), "maps").SetValue(game, new List<Map>());
                game.questManager = new QuestManager();
                World world = Shell<World>();
                world.worldObjects = new WorldObjectsHolder();
                world.factionManager = new FactionManager();
                Current.Game = game;
                Current.CreatingWorld = world;
                Faction temporary = new Faction { loadID = 19022, def = Shell<FactionDef>(), temporary = true };
                Faction permanent = new Faction { loadID = 19023, def = Shell<FactionDef>() };
                world.factionManager.AllFactionsListForReading.AddRange(new[] { temporary, permanent });
                FactionRef reference = new FactionRef { loadId = temporary.loadID };
                T.Check(EncounterFactions.Release(reference) && EncounterFactions.Release(reference), "repeated RELEASE uses the REAL vanilla queue");
                List<Faction> queue = (List<Faction>)AccessTools.Field(typeof(FactionManager), "toRemove").GetValue(world.factionManager);
                T.Eq(1, queue.Count, "vanilla queued the shell only once");
                T.Check(!EncounterFactions.Release(new FactionRef { loadId = permanent.loadID }) && !queue.Contains(permanent), "a permanent faction is never queued");
                // Simulate the completed removal boundary; running the whole manager tick requires the game. The queue above is unmodified vanilla.
                world.factionManager.AllFactionsListForReading.Remove(temporary);
                T.Check(!EncounterFactions.Release(reference) && !EncounterFactions.Release(reference), "after removal, repeated RELEASE resolves nothing and changes nothing");
                T.Eq(1, queue.Count, "no second queue entry");
            }
            finally { Current.Game = oldGame; Current.CreatingWorld = oldWorld; }
        }
    }
}
