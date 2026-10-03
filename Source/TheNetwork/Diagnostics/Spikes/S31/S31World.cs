using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace TheNetwork.Diagnostics.Spikes.S31
{
    /// <summary>
    /// The RimWorld side of spike S31 (dev only, armed, dedicated test map). It is the ONLY place in the mod that creates a pawn, a Lord,
    /// a faction, a quest or a map, and it exists only to answer S31. It never touches a pawn, map, faction, quest or world object that
    /// does not carry an S31 marker, never calls PassToWorld (vanilla performs every pass), and implements no production physical port.
    /// The reservation fixture is VANILLA: a hidden, accepted quest rooted on a vanilla utility QuestScriptDef, holding a vanilla
    /// QuestPart_ReservePawns (so the save names no S31 type). It does not settle S9r's registry design.
    /// </summary>
    public static class S31World
    {
        public const int MapSize = 100;

        public static int Tick => Find.TickManager?.TicksGame ?? 0;

        public static void Log(string line)
        {
            Verse.Log.Message(S31Ids.LogPrefix + line);
        }

        public static void Warn(string line)
        {
            Verse.Log.Warning(S31Ids.LogPrefix + line);
        }

        // ================================================================== observation (read-only)

        /// <summary>The vanilla reservation query itself (the one GetSituation, the GC and quest generation use).</summary>
        public static bool Reserved(Pawn p)
        {
            return p != null && QuestUtility.IsReservedByQuestOrQuestBeingGenerated(p);
        }

        public static string Nick(Pawn p)
        {
            if (p?.Name is NameTriple t) return t.Nick;
            return p?.Name?.ToStringShort;
        }

        /// <summary>A read-only snapshot. Nothing here writes to the pawn, the world or the Network.</summary>
        public static S31Snap Snap(Pawn p, string step)
        {
            S31Snap s = new S31Snap { tick = Tick, step = step };
            if (p == null) return s;
            s.thingId = p.thingIDNumber;
            s.nick = Nick(p);
            s.kind = p.kindDef?.defName;
            s.dead = p.Dead;
            s.destroyed = p.Destroyed;
            s.discarded = p.Discarded;
            s.spawned = p.Spawned;
            s.mapId = p.MapHeld?.uniqueID ?? -1;
            s.holder = p.ParentHolder?.GetType().Name;
            s.inWorld = Find.WorldPawns.Contains(p);
            s.situation = Find.WorldPawns.GetSituation(p).ToString();
            s.reserved = Reserved(p);
            s.suspended = p.Suspended;
            Faction f = p.Faction;
            s.factionId = f?.loadID ?? -1;
            s.factionName = f?.Name;
            s.factionTemporary = f != null && f.temporary;
            if (p.Spawned)
            {
                s.job = p.CurJobDef?.defName;
                s.lord = p.GetLord()?.LordJob?.GetType().Name;
                s.x = p.Position.x;
                s.z = p.Position.z;
            }
            s.bioTicks = p.ageTracker?.AgeBiologicalTicks ?? -1;
            s.food = p.needs?.food?.CurLevel ?? -1f;
            s.rest = p.needs?.rest?.CurLevel ?? -1f;
            List<Hediff> hediffs = p.health?.hediffSet?.hediffs;
            if (hediffs != null)
            {
                s.hediffs = hediffs.Count;
                s.hediffSig = string.Join(",", hediffs.Select(h => h.def.defName + "@" + (h.Part?.def.defName ?? "body")).OrderBy(x => x).ToArray());
                for (int i = 0; i < hediffs.Count; i++) s.maxHediffAge = Math.Max(s.maxHediffAge, hediffs[i].ageTicks);
            }
            List<Apparel> worn = p.apparel?.WornApparel;
            if (worn != null)
            {
                s.apparel = worn.Count;
                s.apparelSig = string.Join(",", worn.Select(a => a.def.defName).OrderBy(x => x).ToArray());
            }
            return s;
        }

        /// <summary>Every pawn object (maps, world, caravans) with this thing id: 1 means no twin and nothing missing.</summary>
        public static int CountById(int thingId)
        {
            int n = 0;
            foreach (Pawn p in PawnsFinder.All_AliveOrDead) if (p != null && p.thingIDNumber == thingId) n++;
            return n;
        }

        public static int CountByNick(string nick)
        {
            int n = 0;
            foreach (Pawn p in PawnsFinder.All_AliveOrDead) if (p != null && Nick(p) == nick) n++;
            return n;
        }

        /// <summary>Every pawn that carries an S31 marker, wherever it is. The ONLY enumeration the cleanup acts on.</summary>
        public static List<Pawn> MarkedPawns()
        {
            List<Pawn> r = new List<Pawn>();
            foreach (Pawn p in PawnsFinder.All_AliveOrDead)
            {
                if (p != null && S31Ids.CarriesMarker(p.questTags) && !r.Contains(p)) r.Add(p);
            }
            return r;
        }

        // ================================================================== the log ("already here")

        public sealed class LogMark
        {
            public readonly Dictionary<LogMessage, int> seen = new Dictionary<LogMessage, int>();
        }

        public static LogMark MarkLog()
        {
            LogMark m = new LogMark();
            try
            {
                foreach (LogMessage msg in Verse.Log.Messages) if (msg != null) m.seen[msg] = msg.repeats;
            }
            catch (Exception)
            {
                // The log is read only for evidence; a concurrent write simply leaves this mark partial.
            }
            return m;
        }

        /// <summary>Log lines written (or repeated) since the mark that contain the text.</summary>
        public static List<string> LinesSince(LogMark mark, string contains)
        {
            List<string> r = new List<string>();
            try
            {
                foreach (LogMessage msg in Verse.Log.Messages)
                {
                    if (msg?.text == null || msg.text.IndexOf(contains, StringComparison.Ordinal) < 0) continue;
                    int before;
                    if (mark != null && mark.seen.TryGetValue(msg, out before) && before == msg.repeats) continue;
                    r.Add(msg.text.Length > 300 ? msg.text.Substring(0, 300) : msg.text);
                }
            }
            catch (Exception)
            {
            }
            return r;
        }

        // ================================================================== the M1 reservation fixture (a vanilla hidden quest)

        public static Quest FindManifest()
        {
            List<Quest> all = Find.QuestManager.QuestsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                Quest q = all[i];
                if (q != null && !q.Historical && q.tags != null && q.tags.Contains(S31Ids.ManifestTag)) return q;
            }
            return null;
        }

        public static Quest FindPoolQuest()
        {
            List<Quest> all = Find.QuestManager.QuestsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                Quest q = all[i];
                if (q != null && !q.Historical && q.tags != null && q.tags.Contains(S31Ids.PoolQuestTag)) return q;
            }
            return null;
        }

        /// <summary>
        /// The reservation anchor: Quest.MakeRaw, a vanilla root (QuestScriptDefOf.Util_GetDefaultRewardValueFromPoints: a utility script
        /// no storyteller or dialog compares against; a root is mandatory, QuestManager drops root-less quests on load), hidden, accepted
        /// (Ongoing, so QuestReserves answers), one vanilla QuestPart_ReservePawns.
        /// </summary>
        public static Quest EnsureManifest(out string error)
        {
            error = null;
            Quest q = FindManifest();
            if (q != null) return q;
            QuestScriptDef root = QuestScriptDefOf.Util_GetDefaultRewardValueFromPoints;
            if (root == null)
            {
                error = "the vanilla QuestScriptDef Util_GetDefaultRewardValueFromPoints is missing; no reservation quest can be rooted";
                return null;
            }
            q = Quest.MakeRaw();
            q.root = root;
            q.hidden = true;
            q.hiddenInUI = true;
            q.name = S31Ids.QuestName;
            QuestUtility.AddQuestTag(ref q.tags, S31Ids.ManifestTag);
            q.AddPart(new QuestPart_ReservePawns());
            q.SetInitiallyAccepted();
            Find.QuestManager.Add(q);
            Log("reservation fixture created: quest id " + q.id + " (hidden, " + q.State + ", root " + root.defName + ", part QuestPart_ReservePawns)");
            return q;
        }

        public static QuestPart_ReservePawns ReservePart(Quest q)
        {
            return q?.GetFirstPartOfType<QuestPart_ReservePawns>();
        }

        /// <summary>M1: the pawn joins the reservation while it is still SPAWNED, before any exit.</summary>
        public static bool Reserve(Pawn p, out string error)
        {
            Quest q = EnsureManifest(out error);
            QuestPart_ReservePawns part = ReservePart(q);
            if (part == null)
            {
                error = error ?? "the reservation quest has no QuestPart_ReservePawns";
                return false;
            }
            if (!part.pawns.Contains(p)) part.pawns.Add(p);
            return true;
        }

        public static void AddManifestTag(string tag)
        {
            string error;
            Quest q = EnsureManifest(out error);
            if (q != null && !q.tags.Contains(tag)) q.tags.Add(tag);
        }

        public static List<string> ManifestTags(string prefix)
        {
            List<string> r = new List<string>();
            Quest q = FindManifest();
            if (q?.tags != null) foreach (string t in q.tags) if (t != null && t.StartsWith(prefix, StringComparison.Ordinal)) r.Add(t);
            return r;
        }

        // ================================================================== the dedicated test map

        public static WorldObjectDef TestMapDef => DefDatabase<WorldObjectDef>.GetNamedSilentFail(S31Ids.TestMapDef);

        /// <summary>The narrowness check, made at runtime on the def actually loaded: plain MapParent, no comps, not a home, no incidents.</summary>
        public static bool TestMapDefIsNarrow(WorldObjectDef def, out string why)
        {
            why = null;
            if (def == null) why = "the spike-only WorldObjectDef " + S31Ids.TestMapDef + " is not loaded (is this the spike build?)";
            else if (def.worldObjectClass != typeof(MapParent)) why = def.defName + " is a " + def.worldObjectClass + ", not exactly MapParent";
            else if (def.comps != null && def.comps.Count > 0) why = def.defName + " has " + def.comps.Count + " comps (a timeout, raid or abandon comp would disturb the test)";
            else if (def.canBePlayerHome) why = def.defName + " can be a player home";
            else if (def.IncidentTargetTags != null && def.IncidentTargetTags.Count > 0) why = def.defName + " is an incident target";
            return why == null;
        }

        public static MapParent FindTestMapParent()
        {
            WorldObjectDef def = TestMapDef;
            if (def == null) return null;
            foreach (WorldObject w in Find.WorldObjects.AllWorldObjects) if (w is MapParent mp && w.def == def && !w.Destroyed) return mp;
            return null;
        }

        public static Map TestMap
        {
            get
            {
                MapParent p = FindTestMapParent();
                return p != null && p.HasMap ? p.Map : null;
            }
        }

        /// <summary>Creates (or regenerates the map of) the spike-owned MapParent on an empty, temperate tile. Never the colony map.</summary>
        public static Map EnsureTestMap(out string report)
        {
            WorldObjectDef def = TestMapDef;
            string why;
            if (!TestMapDefIsNarrow(def, out why))
            {
                report = "refused: " + why;
                return null;
            }
            string error;
            if (EnsureManifest(out error) == null)
            {
                report = "refused: " + error;
                return null;
            }
            MapParent parent = FindTestMapParent();
            if (parent == null)
            {
                PlanetTile tile = TileFinder.RandomSettlementTileFor(Faction.OfPlayer, false, t => !Find.WorldObjects.AnyWorldObjectAt(t)
                    && Find.World.tileTemperatures.SeasonAndOutdoorTemperatureAcceptableFor(t, ThingDefOf.Human));
                if (!tile.Valid)
                {
                    report = "refused: no empty, temperate tile was found for the test map";
                    return null;
                }
                parent = (MapParent)WorldObjectMaker.MakeWorldObject(def);
                parent.Tile = tile;
                Find.WorldObjects.Add(parent);
                AddManifestTag(S31Ids.IntTag(S31Ids.MapPrefix, parent.ID));
                Log("test map world object created: id " + parent.ID + ", def " + def.defName + " (MapParent, no comps, no faction), tile " + tile);
            }
            if (!parent.HasMap)
            {
                Map m = GetOrGenerateMapUtility.GetOrGenerateMap(parent.Tile, new IntVec3(MapSize, 1, MapSize), def);
                m.fogGrid.ClearAllFog();
                Log("test map generated: map " + m.uniqueID + " (" + MapSize + "x" + MapSize + ", generator " + parent.MapGeneratorDef.defName + ") on world object " + parent.ID);
            }
            report = "test map ready: world object " + parent.ID + ", map " + parent.Map.uniqueID + ", tile " + parent.Tile;
            return parent.Map;
        }

        // ================================================================== the temporary faction (a fixture; it does not settle S10)

        public static Faction FindFaction(int loadId)
        {
            foreach (Faction f in Find.FactionManager.AllFactionsListForReading) if (f.loadID == loadId) return f;
            return null;
        }

        /// <summary>
        /// One temporary hidden faction per scenario, as PHYSICAL_LIFECYCLE § 13.2 designs it (created like vanilla's refugee and beggar
        /// quests: NewGeneratedFactionWithRelations(hidden), temporary = true, FactionManager.Add). Hidden means no settlement is
        /// generated. Its leader pawn is tagged so the cleanup can find it.
        /// </summary>
        public static Faction NewTempFaction(string label, out string report)
        {
            FactionDef def = FactionDefOf.OutlanderRefugee ?? FactionDefOf.OutlanderCivil;
            if (def == null)
            {
                report = "refused: no humanlike outlander FactionDef is loaded";
                return null;
            }
            List<FactionRelation> relations = new List<FactionRelation>();
            foreach (Faction other in Find.FactionManager.AllFactionsListForReading)
            {
                if (!other.def.PermanentlyHostileTo(def)) relations.Add(new FactionRelation(other, FactionRelationKind.Neutral));
            }
            Faction f = FactionGenerator.NewGeneratedFactionWithRelations(def, relations, true);
            f.temporary = true;
            f.Name = S31Ids.FactionName + label;
            Find.FactionManager.Add(f);
            AddManifestTag(S31Ids.IntTag(S31Ids.FactionPrefix, f.loadID));
            if (f.leader != null) QuestUtility.AddQuestTag(ref f.leader.questTags, S31Ids.LeaderTag);
            report = "temporary faction " + f.loadID + " \"" + f.Name + "\" (def " + def.defName + ", hidden, temporary, leader " + (f.leader != null ? "#" + f.leader.thingIDNumber : "none") + ")";
            Log(report);
            return f;
        }

        // ================================================================== probes (spike pawns; not a contractor projection)

        /// <summary>
        /// One unmistakable S31 pawn: a vanilla Villager of the fixture faction, freshly generated (forceGenerateNewPawn: it is never a
        /// redressed world pawn), no generated relations, named S31-Probe-n / S31-Decoy-n, carrying an S31 quest tag, spawned on the test
        /// map near the centre. It never enters WorldPawns before its exit.
        /// </summary>
        public static Pawn NewPawn(Map map, Faction f, string nick, string tag, out string error)
        {
            error = null;
            PawnKindDef kind = PawnKindDefOf.Villager;
            PawnGenerationRequest req = new PawnGenerationRequest(kind, f, PawnGenerationContext.NonPlayer, map.Tile, forceGenerateNewPawn: true,
                allowDead: false, allowDowned: false, canGeneratePawnRelations: false, mustBeCapableOfViolence: false, colonistRelationChanceFactor: 0f,
                allowPregnant: false, allowAddictions: false, developmentalStages: DevelopmentalStage.Adult);
            Pawn p = PawnGenerator.GeneratePawn(req);
            if (p == null)
            {
                error = "PawnGenerator returned null";
                return null;
            }
            p.Name = new NameTriple("S31", nick, "Probe");
            QuestUtility.AddQuestTag(ref p.questTags, tag);
            IntVec3 cell = CellFinder.RandomClosewalkCellNear(map.Center, map, 6);
            GenSpawn.Spawn(p, cell, map);
            return p;
        }

        /// <summary>A bounded, non-bleeding injury for scenario C: one vanilla Bruise (severity 3) on a leg.</summary>
        public static string Injure(Pawn p)
        {
            HediffDef def = DefDatabase<HediffDef>.GetNamedSilentFail("Bruise") ?? HediffDefOf.Cut;
            BodyPartRecord part = p.health.hediffSet.GetNotMissingParts().FirstOrDefault(x => x.def.defName == "Leg")
                ?? p.health.hediffSet.GetNotMissingParts().FirstOrDefault(x => x.depth == BodyPartDepth.Outside && x.parent != null);
            Hediff h = HediffMaker.MakeHediff(def, p, part);
            h.Severity = 3f;
            p.health.AddHediff(h, part);
            return def.defName + " (severity 3) on " + (part?.Label ?? "body");
        }

        // ================================================================== Lords (vanilla AI only)

        public static void HoldLord(Map map, Faction f, List<Pawn> pawns)
        {
            LordMaker.MakeNewLord(f, new LordJob_DefendPoint(map.Center), map, pawns);
        }

        /// <summary>The vanilla exit: LordJob_ExitMapBest. The pawns walk to the edge and vanilla's ExitMap passes them.</summary>
        public static void ExitLord(Map map, Faction f, List<Pawn> pawns)
        {
            HashSet<Lord> old = new HashSet<Lord>();
            foreach (Pawn p in pawns)
            {
                Lord l = p.GetLord();
                if (l != null) old.Add(l);
            }
            foreach (Lord l in old)
            {
                l.RemoveAllPawns();
                map.lordManager.RemoveLord(l);
            }
            LordMaker.MakeNewLord(f, new LordJob_ExitMapBest(LocomotionUrgency.Walk, false, true), map, pawns);
        }

        // ================================================================== disposal (spike-owned pawns only)

        /// <summary>Removes a spike-owned pawn the way WorldPawns' own discard does (destroy, then discard). Never called on an unmarked pawn.</summary>
        public static void Dispose(Pawn p)
        {
            if (p == null) return;
            if (Find.WorldPawns.Contains(p)) Find.WorldPawns.RemovePawn(p);
            if (!p.Destroyed) p.Destroy(DestroyMode.Vanish);
            if (!p.Discarded) p.Discard(true);
        }
    }

    /// <summary>One S31 quest-target signal, observed synchronously when vanilla sent it.</summary>
    public sealed class S31SignalEvent
    {
        public int tick;
        public string tag;
        public string part;
        public int thingId = -1;
        public S31Snap snap;
    }

    /// <summary>
    /// M2 OBSERVABILITY ONLY. A runtime-only signal receiver (registered for one game, never saved): vanilla sends "tag.LeftMap" for an S31
    /// pawn synchronously inside Pawn.ExitMap, after PassToWorld and before FactionManager.Notify_PawnLeftMap. The receiver records what
    /// it sees at that instant and logs it. It changes nothing: no reservation, no custody, no faction.
    /// </summary>
    public sealed class S31SignalObserver : ISignalReceiver
    {
        public readonly List<S31SignalEvent> events = new List<S31SignalEvent>();

        public void Notify_SignalReceived(Signal signal)
        {
            try
            {
                if (!S31Ids.IsSignal(signal.tag)) return;
                Pawn subject;
                signal.args.TryGetArg("SUBJECT", out subject);
                S31SignalEvent e = new S31SignalEvent { tick = S31World.Tick, tag = signal.tag, part = S31Ids.SignalPart(signal.tag) };
                if (subject != null)
                {
                    e.thingId = subject.thingIDNumber;
                    e.snap = S31World.Snap(subject, "signal " + e.part);
                }
                if (events.Count < 2000) events.Add(e);
                S31World.Log("vanilla signal " + signal.tag + (e.snap != null ? ": " + e.snap.Line() : ""));
            }
            catch (Exception ex)
            {
                S31World.Warn("signal observer error (observation only, nothing changed): " + ex.Message);
            }
        }

        public S31Snap First(string part, int thingId, int sinceTick)
        {
            for (int i = 0; i < events.Count; i++)
            {
                S31SignalEvent e = events[i];
                if (e.part == part && e.thingId == thingId && e.tick >= sinceTick) return e.snap;
            }
            return null;
        }
    }
}
