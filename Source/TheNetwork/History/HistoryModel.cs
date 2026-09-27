using System.Collections.Generic;
using RimWorld;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.History
{
    public enum AwarenessScope : byte
    {
        Public = 0,
        Regional = 1,
        Involved = 2,
        Secret = 3
    }

    public enum KnownLevel : byte
    {
        Unknown = 0,
        Suspected = 1,
        Confirmed = 2
    }

    /// <summary>Who knows that it happened (EVENTS_AND_HISTORY § 7). Phase 1: Public or Involved.</summary>
    public sealed class Awareness : IExposable
    {
        public AwarenessScope scope = AwarenessScope.Public;
        public List<ActorId> knowers = new List<ActorId>();
        public KnownLevel perpetratorKnown = KnownLevel.Confirmed;
        public KnownLevel principalKnown = KnownLevel.Confirmed;

        public void ExposeData()
        {
            NetScribe.LookEnum(ref scope, "scope", AwarenessScope.Public);
            NetScribe.LookIntList(ref knowers, "knowers", a => a.Value, v => new ActorId(v));
            NetScribe.LookEnum(ref perpetratorKnown, "perpetratorKnown", KnownLevel.Confirmed);
            NetScribe.LookEnum(ref principalKnown, "principalKnown", KnownLevel.Confirmed);
        }
    }

    public sealed class Participation : IExposable
    {
        public EntityRef entity;
        public string roleKey;

        /// <summary>Principal roles hold records alive through retention; bystanders do not.</summary>
        public bool principal;

        public void ExposeData()
        {
            NetScribe.Look(ref entity, "entity");
            Scribe_Values.Look(ref roleKey, "role");
            Scribe_Values.Look(ref principal, "principal", false);
        }
    }

    public sealed class RecordMagnitudes : IExposable
    {
        public int value;
        public int count;
        public int casualties;
        public int duration;

        public void ExposeData()
        {
            Scribe_Values.Look(ref value, "value", 0);
            Scribe_Values.Look(ref count, "count", 0);
            Scribe_Values.Look(ref casualties, "casualties", 0);
            Scribe_Values.Look(ref duration, "duration", 0);
        }
    }

    /// <summary>
    /// A remembered fact (DATA_MODEL § 13). Only Notable and above become records. Snapshot strings
    /// (<see cref="notes"/>) are captured once so the narrative still reads correctly after the
    /// entities involved are compacted or their mods removed.
    /// </summary>
    public sealed class HistoryRecord : IExposable
    {
        public HistoryRecordId id;
        public int tick;
        public string typeKey;
        public byte schema = 1;
        public Importance importance = Importance.Notable;
        public List<Participation> participants = new List<Participation>();
        public TileRef place;
        public string regionKey;
        public DefRef<ThingDef> subjectDef;
        public RecordMagnitudes magnitudes = new RecordMagnitudes();
        public string outcomeKey;
        public HistoryRecordId causedBy;
        public long sourceEventSeq;
        public Awareness awareness = new Awareness();
        public int narrativeSeed;

        /// <summary>Snapshot strings for the narrative, keyed "key=value" (source, holder, band …).</summary>
        public List<string> notes = new List<string>();

        public void ExposeData()
        {
            NetScribe.Look(ref id, "id");
            Scribe_Values.Look(ref tick, "tick", 0);
            Scribe_Values.Look(ref typeKey, "type");
            Scribe_Values.Look(ref schema, "schema", (byte)1);
            NetScribe.LookEnum(ref importance, "importance", Importance.Notable);
            LookParticipants();
            Scribe_Deep.Look(ref place, "place");
            if (Scribe.mode != LoadSaveMode.Saving || place == null || place.regionKey != regionKey) Scribe_Values.Look(ref regionKey, "region");
            Scribe_Deep.Look(ref subjectDef, "subject");
            Scribe_Deep.Look(ref magnitudes, "magnitudes");
            Scribe_Values.Look(ref outcomeKey, "outcome");
            NetScribe.Look(ref causedBy, "causedBy");
            Scribe_Values.Look(ref sourceEventSeq, "eventSeq", 0L);
            Scribe_Deep.Look(ref awareness, "awareness");
            // narrativeSeed is derived from (networkSeed, id) when the ledger is indexed; not persisted.
            NetScribe.LookStringList(ref notes, "notes");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (magnitudes == null) magnitudes = new RecordMagnitudes();
                if (awareness == null) awareness = new Awareness();
                if (regionKey == null && place != null) regionKey = place.regionKey;
            }
        }

        /// <summary>
        /// Participants persist compactly as "A17|source|p" strings (entity|role|principal flag): records
        /// are the bulk of a long save (EVENTS_AND_HISTORY § 11). Unparseable entries are dropped.
        /// </summary>
        private void LookParticipants()
        {
            List<string> raw = null;
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                raw = new List<string>(participants.Count);
                for (int i = 0; i < participants.Count; i++)
                {
                    Participation p = participants[i];
                    raw.Add(p.entity + "|" + (p.roleKey ?? "") + (p.principal ? "|p" : ""));
                }
            }
            NetScribe.LookStringList(ref raw, "participants");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                participants = new List<Participation>();
                for (int i = 0; i < raw.Count; i++)
                {
                    string[] parts = raw[i].Split('|');
                    EntityRef e = EntityRef.Parse(parts[0]);
                    if (!e.IsValid) continue;
                    participants.Add(new Participation { entity = e, roleKey = parts.Length > 1 ? parts[1] : null, principal = parts.Length > 2 && parts[2] == "p" });
                }
            }
            if (participants == null) participants = new List<Participation>();
        }

        public string Note(string key)
        {
            string prefix = key + "=";
            for (int i = 0; i < notes.Count; i++)
            {
                if (notes[i] != null && notes[i].StartsWith(prefix, System.StringComparison.Ordinal)) return notes[i].Substring(prefix.Length);
            }
            return null;
        }

        public void SetNote(string key, string value)
        {
            if (value == null) return;
            notes.Add(key + "=" + value);
        }

        public bool Involves(ActorId actor, bool principalOnly)
        {
            for (int i = 0; i < participants.Count; i++)
            {
                Participation p = participants[i];
                if (p.entity.Kind == EntityKind.Actor && p.entity.Id == actor.Value && (!principalOnly || p.principal)) return true;
            }
            return false;
        }
    }

    public sealed class HistoryLedger : IExposable
    {
        public List<HistoryRecord> records = new List<HistoryRecord>();

        /// <summary>Where the budgeted retention sweep resumes (a record id; 0 = start).</summary>
        public int sweepCursor;
        public long droppedCount;

        public void ExposeData()
        {
            NetScribe.LookListTolerant(ref records, "records", "history");
            Scribe_Values.Look(ref sweepCursor, "sweepCursor", 0);
            Scribe_Values.Look(ref droppedCount, "dropped", 0L);
        }
    }

    public sealed class DeedCounter : IExposable
    {
        public string key;
        public int lifetime;
        public float recent;
        public int recentTick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref key, "key");
            Scribe_Values.Look(ref lifetime, "lifetime", 0);
            Scribe_Values.Look(ref recent, "recent", 0f);
            Scribe_Values.Look(ref recentTick, "recentTick", 0);
        }
    }

    /// <summary>
    /// Incremental counters other systems read instead of the ledger (EVENTS_AND_HISTORY § 5).
    /// "recent" decays exponentially (half-life about one year), applied lazily on read and write.
    /// </summary>
    public sealed class ActorRecordSummary : IExposable
    {
        public const float HalfLifeTicks = Ticks.PerYear;

        public ActorId actor;
        public List<DeedCounter> deeds = new List<DeedCounter>();
        public int firstTick = -1;
        public int lastTick = -1;
        public HistoryRecordId bestRecord;

        public void ExposeData()
        {
            NetScribe.Look(ref actor, "actor");
            NetScribe.LookListTolerant(ref deeds, "deeds", "summaries.deeds");
            Scribe_Values.Look(ref firstTick, "firstTick", -1);
            Scribe_Values.Look(ref lastTick, "lastTick", -1);
            NetScribe.Look(ref bestRecord, "bestRecord");
        }

        public DeedCounter Find(string key)
        {
            for (int i = 0; i < deeds.Count; i++) if (deeds[i].key == key) return deeds[i];
            return null;
        }

        public void Add(string key, int delta, int now)
        {
            DeedCounter d = Find(key);
            if (d == null)
            {
                d = new DeedCounter { key = key, recentTick = now };
                deeds.Add(d);
            }
            d.recent = Decayed(d, now) + delta;
            d.recentTick = now;
            d.lifetime += delta;
            if (firstTick < 0) firstTick = now;
            lastTick = now;
        }

        public int Lifetime(string key)
        {
            DeedCounter d = Find(key);
            return d == null ? 0 : d.lifetime;
        }

        public float Recent(string key, int now)
        {
            DeedCounter d = Find(key);
            return d == null ? 0f : Decayed(d, now);
        }

        private static float Decayed(DeedCounter d, int now)
        {
            int dt = now - d.recentTick;
            if (dt <= 0 || d.recent == 0f) return d.recent;
            return d.recent * (float)System.Math.Pow(0.5, dt / (double)HalfLifeTicks);
        }
    }

    public sealed class SummaryStore : IExposable
    {
        public List<ActorRecordSummary> actors = new List<ActorRecordSummary>();

        private readonly Dictionary<int, ActorRecordSummary> byActor = new Dictionary<int, ActorRecordSummary>();

        public void ExposeData()
        {
            NetScribe.LookListTolerant(ref actors, "actors", "summaries");
        }

        public void RebuildIndex()
        {
            byActor.Clear();
            for (int i = 0; i < actors.Count; i++) if (actors[i] != null && actors[i].actor.IsValid) byActor[actors[i].actor.Value] = actors[i];
        }

        public ActorRecordSummary Get(ActorId id)
        {
            ActorRecordSummary s;
            return id.IsValid && byActor.TryGetValue(id.Value, out s) ? s : null;
        }

        public ActorRecordSummary GetOrCreate(ActorId id)
        {
            ActorRecordSummary s = Get(id);
            if (s == null && id.IsValid)
            {
                s = new ActorRecordSummary { actor = id };
                actors.Add(s);
                byActor[id.Value] = s;
            }
            return s;
        }
    }
}
