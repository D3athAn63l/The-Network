using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using RimWorld;
using TheNetwork.Domain.Physical;
using TheNetwork.Integration.Physical;
using UnityEngine;
using Verse;

namespace TheNetwork.Tests
{
    /// <summary>Pure policy and actual-assembly S27 reads; texture-only headless fixture stubs do not replace the production collector or GetConcerns.</summary>
    public static class Phase32bEvidenceTests
    {
        public static void Register(List<KeyValuePair<string, Action>> tests)
        {
            tests.Add(new KeyValuePair<string, Action>("Groups.Evidence_PureEndpointAndImpactShape", Shapes));
            tests.Add(new KeyValuePair<string, Action>("Groups.Evidence_AbsoluteWindowFailsClosed", Windows));
            tests.Add(new KeyValuePair<string, Action>("Groups.Evidence_DeliberateSubjectSeamOnly", Identification));
            tests.Add(new KeyValuePair<string, Action>("Groups.Evidence_RealCombatKindsAndCurrentPlayerSide", RealKinds));
            tests.Add(new KeyValuePair<string, Action>("Groups.Evidence_RangedImpactFalsePairsAndUnknownType", ImpactAndUnknown));
            tests.Add(new KeyValuePair<string, Action>("Groups.Evidence_BattleAndEntryBudgetsRemainAnonymous", Budgets));
            tests.Add(new KeyValuePair<string, Action>("Groups.Evidence_DirectRelationsBoundAndHosting", Relations));
            tests.Add(new KeyValuePair<string, Action>("Groups.Evidence_ActualFormerColonistRecordIndependentOfLogs", FormerColonist));
            tests.Add(new KeyValuePair<string, Action>("Groups.Evidence_NoProductionPlayLogReflectionOrWorldScan", SourceGate));
        }

        private static void Shapes()
        {
            object candidate = new object(), player = new object(), raider = new object();
            T.Check(CombatEvidenceRules.Matches(CombatEvidenceKind.Endpoints, candidate, candidate, player, null, 2, false, true), "candidate initiates player contact");
            T.Check(CombatEvidenceRules.Matches(CombatEvidenceKind.Endpoints, candidate, player, candidate, null, 2, true, false), "player initiates candidate contact");
            T.Check(!CombatEvidenceRules.Matches(CombatEvidenceKind.Endpoints, candidate, candidate, raider, null, 2, false, false), "third-party combat is insufficient");
            T.Check(!CombatEvidenceRules.Matches(CombatEvidenceKind.Endpoints, candidate, candidate, candidate, null, 2, false, true), "self contact cannot be player pair");
            T.Check(!CombatEvidenceRules.Matches(CombatEvidenceKind.Endpoints, candidate, candidate, player, raider, 3, false, true), "third concern invalidates ordinary endpoint kind");
            T.Check(!CombatEvidenceRules.Matches(CombatEvidenceKind.Endpoints, candidate, null, player, null, 2, false, true), "null endpoint fails closed");
            T.Check(CombatEvidenceRules.Matches(CombatEvidenceKind.RangedImpact, candidate, candidate, player, player, 3, false, true), "impact confirms actual equals original target");
            T.Check(!CombatEvidenceRules.Matches(CombatEvidenceKind.RangedImpact, candidate, candidate, raider, player, 3, false, true), "three-way impact cannot fabricate player combat");
            T.Check(!CombatEvidenceRules.Matches(CombatEvidenceKind.RangedImpact, candidate, candidate, player, null, 2, false, true), "two raw impact concerns can be turret victims");
            T.Check(!CombatEvidenceRules.Matches(CombatEvidenceKind.RangedImpact, candidate, candidate, candidate, player, 3, false, true), "self-hit with original player excluded");
            T.Check(!CombatEvidenceRules.Matches((CombatEvidenceKind)255, candidate, candidate, player, null, 2, false, true), "unknown semantics do not match");
        }

        private static void Windows()
        {
            int start, end;
            T.Check(CombatEvidenceRules.TryAbsoluteWindow(10, 20, 100000, out start, out end), "game origin converts Episode clock");
            T.Eq(100010, start, "absolute start"); T.Eq(100020, end, "absolute end");
            T.Check(CombatEvidenceRules.InWindow(start, start, end) && CombatEvidenceRules.InWindow(end, start, end), "inclusive actual Episode window");
            T.Check(!CombatEvidenceRules.InWindow(10, start, end), "relative tick cannot qualify absolute log");
            T.Check(!CombatEvidenceRules.InWindow(start - 1, start, end) && !CombatEvidenceRules.InWindow(end + 1, start, end), "prior and future entries rejected");
            T.Check(!CombatEvidenceRules.TryAbsoluteWindow(-1, 20, 100000, out start, out end), "old unknown origin rejected");
            T.Check(!CombatEvidenceRules.TryAbsoluteWindow(20, 10, 100000, out start, out end), "future Episode rejected");
            T.Check(!CombatEvidenceRules.TryAbsoluteWindow(0, 20, 0, out start, out end), "unset game absolute origin rejected");
            T.Check(!CombatEvidenceRules.TryAbsoluteWindow(1, 20, int.MaxValue, out start, out end), "wide overflow rejected before cast");
            T.Eq(-1, start, "unknown defaults remain unknown"); T.Eq(-1, end, "unknown end remains unknown");
        }

        private static void Identification()
        {
            object pawn = new object(), another = new object();
            T.Check(CombatEvidenceRules.DeliberatelyIdentifies(pawn, pawn, true), "explicit individual exact-Pawn seam");
            T.Check(!CombatEvidenceRules.DeliberatelyIdentifies(pawn, another, true), "wrong individual not identified");
            T.Check(!CombatEvidenceRules.DeliberatelyIdentifies(pawn, pawn, false), "aggregate/informal mention cannot identify");
            T.Check(!CombatEvidenceRules.DeliberatelyIdentifies<object>(null, null, true), "missing identity never a subject");
            T.Eq(ConcretizationEvidence.None, new PhysicalPromotionFacts().evidence, "absent optional facts have no evidence");
            T.Check(new PhysicalPromotionFacts().name == null, "no generated replacement name");
        }

        private static TV Shell<TV>() { return (TV)FormatterServices.GetUninitializedObject(typeof(TV)); }
        private static void Field(object value, string field, object data) { AccessTools.Field(value.GetType(), field).SetValue(value, data); }
        private static Pawn Pawn(int id, Faction faction = null, Faction host = null)
        {
            ThingDef def = Shell<ThingDef>(); def.defName = "Human"; def.category = ThingCategory.Pawn;
            def.race = Shell<RaceProperties>(); def.race.intelligence = Intelligence.Humanlike;
            Pawn pawn = new Pawn { def = def, thingIDNumber = id };
            Field(pawn, "factionInt", faction);
            if (host != null) { pawn.guest = Shell<Pawn_GuestTracker>(); Field(pawn.guest, "hostFactionInt", host); }
            return pawn;
        }
        private static Faction Player()
        {
            FactionDef def = Shell<FactionDef>(); def.isPlayer = true;
            return new Faction { def = def };
        }
        private static bool TextureStub(ref Texture2D __result) { __result = null; return false; }
        private static bool texturesStubbed;
        private static void Textures()
        {
            if (texturesStubbed) return;
            new Harmony("TheNetwork.Tests.Phase32bEvidencePresentation").Patch(AccessTools.Method(typeof(ContentFinder<Texture2D>), "Get"),
                prefix: new HarmonyMethod(typeof(Phase32bEvidenceTests), "TextureStub"));
            texturesStubbed = true;
        }
        private static LogEntry Entry(Type type, Pawn first, Pawn second, Pawn original = null, int timestamp = 1200)
        {
            Textures();
            LogEntry entry = (LogEntry)FormatterServices.GetUninitializedObject(type);
            Field(entry, "ticksAbs", timestamp);
            if (type == typeof(BattleLogEntry_MeleeCombat)) Field(entry, "initiator", first);
            else Field(entry, "initiatorPawn", first);
            Field(entry, "recipientPawn", second);
            if (type == typeof(BattleLogEntry_RangedImpact)) Field(entry, "originalTargetPawn", original);
            return entry;
        }
        private static Battle Battle(params LogEntry[] entries)
        {
            Battle battle = Shell<Battle>(); Field(battle, "entries", new List<LogEntry>(entries)); return battle;
        }
        private static ConcretizationEvidenceScan Scan(Pawn candidate, Faction player, params Battle[] battles)
        {
            return ConcretizationEvidenceCollector.Scan(candidate, 100, 250, 1000, player, battles);
        }
        private static bool Combat(ConcretizationEvidenceScan facts) { return (facts.evidence & ConcretizationEvidence.PlayerCombat) != 0; }

        private static void RealKinds()
        {
            Faction player = Player(); Pawn candidate = Pawn(900001), colonist = Pawn(900002, player), other = Pawn(900003), guest = Pawn(900004, null, player);
            foreach (Type type in new[] { typeof(BattleLogEntry_MeleeCombat), typeof(BattleLogEntry_RangedFire), typeof(BattleLogEntry_ExplosionImpact) })
            {
                T.Check(Combat(Scan(candidate, player, Battle(Entry(type, candidate, colonist)))), type.Name + " candidate attacks actual player-faction endpoint");
                T.Check(Combat(Scan(candidate, player, Battle(Entry(type, colonist, candidate)))), type.Name + " player attacks candidate endpoint");
                T.Check(Combat(Scan(candidate, player, Battle(Entry(type, candidate, guest)))), type.Name + " currently player-hosted endpoint");
                T.Check(!Combat(Scan(candidate, player, Battle(Entry(type, candidate, other)))), type.Name + " unrelated faction fight excluded");
                T.Check(!Combat(Scan(candidate, player, Battle(Entry(type, candidate, other), Entry(type, colonist, other)))), type.Name + " shared battle is not shared entry");
                T.Check(!Combat(Scan(candidate, player, Battle(Entry(type, candidate, colonist, null, 1099)))), type.Name + " before Episode absolute cutoff excluded");
                T.Check(!Combat(Scan(candidate, player, Battle(Entry(type, candidate, colonist, null, 1251)))), type.Name + " future timestamp excluded");
            }
            LogEntry contact = Entry(typeof(BattleLogEntry_RangedFire), candidate, colonist);
            Field(colonist, "factionInt", null);
            T.Check(!Combat(Scan(candidate, player, Battle(contact))), "current side is observed at reconciliation, old assumed side not invented");
            T.Check(!Combat(Scan(candidate, null, Battle(contact))), "unavailable player faction fails closed");
        }

        private class UnknownRangedFire : BattleLogEntry_RangedFire
        {
            public static int concernsCalls;
            public override IEnumerable<Thing> GetConcerns() { concernsCalls++; throw new InvalidOperationException("unknown entry must not be enumerated"); }
        }
        private static void ImpactAndUnknown()
        {
            Faction player = Player(); Pawn candidate = Pawn(910001), colonist = Pawn(910002, player), raider = Pawn(910003);
            T.Check(Combat(Scan(candidate, player, Battle(Entry(typeof(BattleLogEntry_RangedImpact), candidate, colonist, colonist)))), "exact impact with actual==original accepted");
            T.Check(Combat(Scan(candidate, player, Battle(Entry(typeof(BattleLogEntry_RangedImpact), colonist, candidate, candidate)))), "reverse confirmed impact endpoints accepted");
            T.Check(!Combat(Scan(candidate, player, Battle(Entry(typeof(BattleLogEntry_RangedImpact), null, candidate, colonist)))), "non-Pawn turret false pair rejected");
            T.Check(!Combat(Scan(candidate, player, Battle(Entry(typeof(BattleLogEntry_RangedImpact), candidate, raider, colonist)))), "three-party intended-player actual-raider rejected");
            T.Check(!Combat(Scan(candidate, player, Battle(Entry(typeof(BattleLogEntry_RangedImpact), candidate, candidate, colonist)))), "self-hit originalplayer rejected");
            T.Check(!Combat(Scan(candidate, player, Battle(Entry(typeof(BattleLogEntry_RangedImpact), candidate, colonist)))), "two yielded roles ambiguous and excluded");
            UnknownRangedFire.concernsCalls = 0;
            ConcretizationEvidenceScan unsupported = Scan(candidate, player, Battle(Entry(typeof(UnknownRangedFire), candidate, colonist)));
            T.Check(!Combat(unsupported), "unknown subclass cannot inherit strong evidence");
            T.Eq(0, UnknownRangedFire.concernsCalls, "unknown GetConcerns never invoked");
            T.Eq(1, unsupported.unsupportedEntries, "unsupported shape counted for diagnosis");
            foreach (Type type in new[] { typeof(BattleLogEntry_Event), typeof(BattleLogEntry_AbilityUsed), typeof(BattleLogEntry_ItemUsed), typeof(BattleLogEntry_DamageTaken), typeof(BattleLogEntry_StateTransition) })
            {
                LogEntry entry = (LogEntry)FormatterServices.GetUninitializedObject(type); Field(entry, "ticksAbs", 1200);
                T.Check(!Combat(Scan(candidate, player, Battle(entry))), type.Name + " not accepted solely for presence in combat log");
            }
        }

        private static void Budgets()
        {
            Faction player = Player(); Pawn candidate = Pawn(920001), colonist = Pawn(920002, player), raider = Pawn(920003), animal = Pawn(920004);
            LogEntry unrelated = Entry(typeof(BattleLogEntry_RangedFire), raider, animal), contact = Entry(typeof(BattleLogEntry_RangedFire), candidate, colonist);
            BattleLog history = new BattleLog();
            for (int i = 0; i < 40; i++)
            {
                Battle battle = Battle(); for (int j = 0; j < 1000; j++) battle.Entries.Add(unrelated); history.Battles.Add(battle);
            }
            ConcretizationEvidenceScan none = ConcretizationEvidenceCollector.Scan(candidate, 100, 250, 1000, player, history.Battles);
            T.Check(!Combat(none) && !none.collectionFailed, "large unrelated history remains anonymous");
            T.Eq(32, none.battlesExamined, "hard battle window"); T.Eq(4096, none.entriesExamined, "hard global entry checks per candidate");
            T.Check(none.truncatedBattles && none.truncatedEntries, "truncated windows are diagnosable");
            history.Battles[0].Entries[128] = contact; history.Battles[32].Entries[0] = contact;
            T.Check(!Combat(ConcretizationEvidenceCollector.Scan(candidate, 100, 250, 1000, player, history.Battles)), "evidence outside either window stays anonymous");
            history.Battles[31].Entries[127] = contact;
            ConcretizationEvidenceScan last = ConcretizationEvidenceCollector.Scan(candidate, 100, 250, 1000, player, history.Battles);
            T.Check(Combat(last), "last allowed combat entry accepted"); T.Eq(4096, last.entriesExamined, "valid edge has same bound");
            history.Battles[0].Entries[0] = Entry(typeof(BattleLogEntry_RangedFire), candidate, colonist, null, 900);
            history.Battles[0].Entries[1] = contact;
            ConcretizationEvidenceScan unordered = ConcretizationEvidenceCollector.Scan(candidate, 100, 250, 1000, player, history.Battles);
            T.Check(Combat(unordered), "old entry first does not early-break modded chronology"); T.Eq(2, unordered.entriesExamined, "early positive result stops bounded scan");
            T.Check(!Combat(ConcretizationEvidenceCollector.Scan(candidate, -1, 250, 1000, player, history.Battles)), "absent old-save creation tick no log identity");
            int total = 0;
            for (int i = 0; i < CombatEvidenceRules.MaxCandidates; i++) total += ConcretizationEvidenceCollector.Scan(Pawn(920010 + i), 100, 250, 1000, player, history.Battles).entriesExamined;
            T.Eq(32768, total, "eight negative candidates perform at most accepted total budget");
        }

        private static DirectPawnRelation Relation(Pawn other, PawnRelationDef def)
        {
            DirectPawnRelation relation = Shell<DirectPawnRelation>(); relation.otherPawn = other; relation.def = def; return relation;
        }
        private static void Relations()
        {
            Faction player = Player(); Pawn candidate = Pawn(930001), colonist = Pawn(930002, player), hosted = Pawn(930003, null, player), other = Pawn(930004);
            PawnRelationDef def = Shell<PawnRelationDef>(); def.defName = "Parent";
            candidate.relations = new Pawn_RelationsTracker(candidate);
            for (int i = 0; i < 128; i++) candidate.relations.DirectRelations.Add(Relation(other, def));
            candidate.relations.DirectRelations.Add(Relation(colonist, def));
            ConcretizationEvidenceScan outside = Scan(candidate, player);
            T.Check((outside.evidence & ConcretizationEvidence.PlayerRelation) == 0, "relation beyond first128 does not promote");
            T.Eq(128, outside.relationRecordsExamined, "bounded direct relation records"); T.Check(outside.truncatedRelations, "relationship truncation diagnosed");
            candidate.relations.DirectRelations[127] = Relation(hosted, def);
            ConcretizationEvidenceScan within = Scan(candidate, player);
            T.Check((within.evidence & ConcretizationEvidence.PlayerRelation) != 0, "actual player-hosted direct relation qualifies at bound");
            T.Eq(128, within.relationRecordsExamined, "last eligible relation inspected");
            candidate.relations.DirectRelations.Clear(); candidate.relations.DirectRelations.Add(null);
            candidate.relations.DirectRelations.Add(Relation(colonist, null)); candidate.relations.DirectRelations.Add(Relation(null, def)); candidate.relations.DirectRelations.Add(Relation(candidate, def));
            T.Check((Scan(candidate, player).evidence & ConcretizationEvidence.PlayerRelation) == 0, "null/undefined/self relation cannot create stake");
            candidate.relations.DirectRelations.Add(Relation(colonist, def));
            ConcretizationEvidenceScan noWindow = ConcretizationEvidenceCollector.Scan(candidate, -1, 250, 0, player, null);
            T.Check(noWindow.invalidWindow && (noWindow.evidence & ConcretizationEvidence.PlayerRelation) != 0, "strong relation independent of optional invalid log window");
            T.Check((Scan(candidate, null).evidence & ConcretizationEvidence.PlayerRelation) == 0, "missing player faction not equal to two null factions");
        }

        private static void FormerColonist()
        {
            Textures(); Pawn candidate = Pawn(940001); RecordDef previous = RecordDefOf.TimeAsColonistOrColonyAnimal;
            try
            {
                RecordDef record = Shell<RecordDef>(); record.defName = "TimeAsColonistOrColonyAnimal"; record.index = 0; record.type = RecordType.Time;
                RecordDefOf.TimeAsColonistOrColonyAnimal = record;
                Pawn_RecordsTracker tracker = Shell<Pawn_RecordsTracker>(); tracker.pawn = candidate;
                DefMap<RecordDef, float> map = Shell<DefMap<RecordDef, float>>(); Field(map, "values", new List<float> { 1f }); Field(tracker, "records", map); candidate.records = tracker;
                ConcretizationEvidenceScan positive = ConcretizationEvidenceCollector.Scan(candidate, -1, -1, 0, null, null);
                T.Check(positive.invalidWindow && (positive.evidence & ConcretizationEvidence.FormerColonist) != 0, "actual vanilla positive colony-time record promotes without any log or current player faction");
                map[record] = 0f;
                T.Check((Scan(candidate, null).evidence & ConcretizationEvidence.FormerColonist) == 0, "zero elapsed colony record cannot fabricate past stake");
                candidate.records = null;
                T.Check((Scan(candidate, null).evidence & ConcretizationEvidence.FormerColonist) == 0, "absent record no evidence");
            }
            finally { RecordDefOf.TimeAsColonistOrColonyAnimal = previous; }
        }

        private static void SourceGate()
        {
            string repo = Environment.GetEnvironmentVariable("THENETWORK_REPO");
            if (string.IsNullOrEmpty(repo)) repo = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
            string path = Path.Combine(repo, "Source/TheNetwork/Integration/Physical/ConcretizationEvidenceCollector.cs");
            string source = File.ReadAllText(path);
            foreach (string forbidden in new[] { "Find.PlayLog", "AnyEntryConcerns(", "GetCriticalPawnReason(", "AccumulatePawnGCDataImmediate(", ".RelatedPawns", ".PotentiallyRelatedPawns", "AllPawnsAlive", "AllPawnsAliveOrDead", "HarmonyLib", "System.Reflection", "FormatterServices" })
                T.Check(!source.Contains(forbidden), "collector excludes " + forbidden);
            T.Check(source.Contains("type == typeof(BattleLogEntry_MeleeCombat)") && source.Contains("type == typeof(BattleLogEntry_RangedFire)") && source.Contains("type == typeof(BattleLogEntry_ExplosionImpact)") && source.Contains("type == typeof(BattleLogEntry_RangedImpact)"), "exact-type audited allowlist cannot expand via subclass");
            string pure = File.ReadAllText(Path.Combine(repo, "Source/TheNetwork/Domain/Physical/ConcretizationEvidence.cs"));
            T.Check(!pure.Contains("using Verse") && !pure.Contains("using RimWorld") && !pure.Contains("UnityEngine"), "pure shape classifier has no engine API");
            T.Check(!source.Contains("DeliberateIdentification"), "collector invents no S2 producer");
        }
    }
}
