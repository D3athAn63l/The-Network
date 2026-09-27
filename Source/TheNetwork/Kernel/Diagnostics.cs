using System.Collections.Generic;
using Verse;

namespace TheNetwork.Kernel
{
    /// <summary>
    /// One quarantined item: an entity that failed to load or validate (SAVE_AND_MIGRATION § 7).
    /// Either it still exists as an object (<see cref="entity"/> is set and the entity carries a
    /// quarantine flag), or it could not be constructed and only its raw XML survives here, so the
    /// data is never silently discarded.
    /// </summary>
    public sealed class QuarantineRecord : IExposable
    {
        public string storeKey;
        public EntityRef entity;
        public string reasonKey;
        public string rawXml;
        public int tick = -1;

        public static QuarantineRecord ForRawXml(string storeKey, string reason, string xml)
        {
            return new QuarantineRecord
            {
                storeKey = storeKey,
                reasonKey = reason,
                rawXml = NetScribe.Truncate(xml, NetScribe.MaxRawXmlChars)
            };
        }

        public static QuarantineRecord ForEntity(string storeKey, EntityRef entity, string reason, int tick)
        {
            return new QuarantineRecord { storeKey = storeKey, entity = entity, reasonKey = reason, tick = tick };
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref storeKey, "store");
            NetScribe.Look(ref entity, "entity");
            Scribe_Values.Look(ref reasonKey, "reason");
            Scribe_Values.Look(ref rawXml, "rawXml");
            Scribe_Values.Look(ref tick, "tick", -1);
        }

        public override string ToString()
        {
            return storeKey + " " + (entity.IsValid ? entity.ToString() : "(raw)") + ": " + reasonKey;
        }
    }

    public sealed class FailedConsumerRecord : IExposable
    {
        public long eventSeq;
        public string typeKey;
        public string consumer;
        public string message;
        public int tick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref eventSeq, "eventSeq", 0L);
            Scribe_Values.Look(ref typeKey, "type");
            Scribe_Values.Look(ref consumer, "consumer");
            Scribe_Values.Look(ref message, "message");
            Scribe_Values.Look(ref tick, "tick", 0);
        }
    }

    public sealed class FailedMigrationRecord : IExposable
    {
        public string name;
        public int from;
        public int to;
        public int tick;
        public string message;

        public void ExposeData()
        {
            Scribe_Values.Look(ref name, "name");
            Scribe_Values.Look(ref from, "from", 0);
            Scribe_Values.Look(ref to, "to", 0);
            Scribe_Values.Look(ref tick, "tick", 0);
            Scribe_Values.Look(ref message, "message");
        }
    }

    /// <summary>The persisted diagnostics store (always the last store in the save layout).</summary>
    public sealed class DiagnosticsState : IExposable
    {
        public const int MaxQuarantine = 200;
        public const int MaxFailedConsumers = 100;

        public List<QuarantineRecord> quarantine = new List<QuarantineRecord>();
        public List<FailedConsumerRecord> failedConsumers = new List<FailedConsumerRecord>();
        public List<FailedMigrationRecord> failedMigrations = new List<FailedMigrationRecord>();
        public List<string> oneTimeWarnings = new List<string>();
        public List<string> degradedSubsystems = new List<string>();
        public int downgradedFrom;

        public void AddQuarantine(QuarantineRecord record)
        {
            quarantine.Add(record);
            while (quarantine.Count > MaxQuarantine) quarantine.RemoveAt(0);
        }

        public void AddFailedConsumer(FailedConsumerRecord record)
        {
            failedConsumers.Add(record);
            while (failedConsumers.Count > MaxFailedConsumers) failedConsumers.RemoveAt(0);
        }

        /// <summary>True the first time a key is seen in this save (per-save one-time warnings).</summary>
        public bool FirstTime(string key)
        {
            if (oneTimeWarnings.Contains(key)) return false;
            oneTimeWarnings.Add(key);
            return true;
        }

        public bool IsDegraded(string subsystem)
        {
            return degradedSubsystems.Contains(subsystem);
        }

        public void ExposeData()
        {
            NetScribe.LookListTolerant(ref quarantine, "quarantine", "diagnostics.quarantine");
            NetScribe.LookListTolerant(ref failedConsumers, "failedConsumers", "diagnostics.failedConsumers");
            NetScribe.LookListTolerant(ref failedMigrations, "failedMigrations", "diagnostics.failedMigrations");
            NetScribe.LookStringList(ref oneTimeWarnings, "oneTimeWarnings");
            NetScribe.LookStringList(ref degradedSubsystems, "degradedSubsystems");
            Scribe_Values.Look(ref downgradedFrom, "downgradedFrom", 0);
        }
    }
}
