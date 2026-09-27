using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Xml;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Kernel
{
    public enum Importance : byte
    {
        Ephemeral = 0,
        Minor = 1,
        Notable = 2,
        Major = 3,
        Legendary = 4
    }

    /// <summary>
    /// A subsystem reacting to Network events (EVENTS_AND_HISTORY § 1.2). Consumers do not know each
    /// other; the bus calls them in a fixed global order.
    /// </summary>
    public interface IEventConsumer
    {
        string Name { get; }
        void Handle(TheNetwork.Persist.Events.NetworkEvent evt);
    }

    /// <summary>Global consumer order (EVENTS_AND_HISTORY § 1.2).</summary>
    public static class ConsumerOrder
    {
        public const int DomainOwners = 100;
        public const int History = 200;
        public const int Organizations = 300;
        public const int Relationships = 400;
        public const int Knowledge = 500;
        public const int Reputation = 600;
        public const int Consequences = 700;
        public const int Gossip = 800;
        public const int Presentation = 900;
    }

    /// <summary>The bounded audit trail of recent events (EVENTS_AND_HISTORY § 1.7). Never replayed.</summary>
    public sealed class EventJournal : IExposable
    {
        public const int KeepCount = 256;
        public const int KeepAgeTicks = Ticks.PerDay * 15;
        public const int HardCap = 1024;

        public List<TheNetwork.Persist.Events.NetworkEvent> entries = new List<TheNetwork.Persist.Events.NetworkEvent>();
        public long droppedCount;

        public void Append(TheNetwork.Persist.Events.NetworkEvent evt, int now)
        {
            entries.Add(evt);
            Prune(now);
        }

        public void Prune(int now)
        {
            int remove = 0;
            while (remove < entries.Count)
            {
                int remaining = entries.Count - remove;
                if (remaining <= KeepCount) break;
                TheNetwork.Persist.Events.NetworkEvent oldest = entries[remove];
                bool tooOld = now - oldest.tick > KeepAgeTicks;
                if (!tooOld && remaining <= HardCap) break;
                remove++;
            }
            if (remove > 0)
            {
                entries.RemoveRange(0, remove);
                droppedCount += remove;
            }
        }

        public void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                // An event class that no longer exists is dropped quietly (EVENTS_AND_HISTORY § 1.6):
                // the journal is an audit trail, not data anything depends on.
                entries = new List<TheNetwork.Persist.Events.NetworkEvent>();
                XmlNode node = Scribe.loader.curXmlParent?["entries"];
                int dropped = 0;
                if (node != null)
                {
                    foreach (XmlNode child in node.ChildNodes)
                    {
                        if (child.NodeType != XmlNodeType.Element) continue;
                        string reason;
                        TheNetwork.Persist.Events.NetworkEvent e = NetScribe.TryLoadElement<TheNetwork.Persist.Events.NetworkEvent>(child, out reason);
                        if (e != null) entries.Add(e);
                        else dropped++;
                    }
                }
                if (dropped > 0)
                {
                    NetLog.Info(LogCategory.Events, "Dropped " + dropped + " journal entries of unknown or broken event types.");
                }
            }
            else if (Scribe.mode == LoadSaveMode.Saving)
            {
                Scribe_Collections.Look(ref entries, "entries", LookMode.Deep);
            }
            Scribe_Values.Look(ref droppedCount, "dropped", 0L);
            if (entries == null) entries = new List<TheNetwork.Persist.Events.NetworkEvent>();
        }
    }

    /// <summary>
    /// Synchronous, ordered event dispatch (EVENTS_AND_HISTORY § 1.2–1.5, ADR-009).
    ///
    /// Publish appends to the bounded journal and dispatches to every consumer registered for the
    /// event's type key, in the fixed global order. Events published by consumers are queued and
    /// dispatched breadth-first after the current one. A consumer that throws is recorded once in
    /// diagnostics and the event is never re-dispatched.
    /// </summary>
    public sealed class NetworkEventBus
    {
        private sealed class Registration
        {
            public int order;
            public int registrationIndex;
            public IEventConsumer consumer;
            public HashSet<string> typeKeys; // null = every type
        }

        public const int CascadeCap = 64;

        private readonly IdAllocator ids;
        private readonly IClock clock;
        private readonly EventJournal journal;
        private readonly DiagnosticsState diagnostics;
        private readonly List<Registration> registrations = new List<Registration>();
        private readonly Dictionary<string, List<IEventConsumer>> table = new Dictionary<string, List<IEventConsumer>>();
        private readonly Queue<TheNetwork.Persist.Events.NetworkEvent> fifo = new Queue<TheNetwork.Persist.Events.NetworkEvent>();
        private bool dispatching;

        public NetworkEventBus(IdAllocator ids, IClock clock, EventJournal journal, DiagnosticsState diagnostics)
        {
            this.ids = ids;
            this.clock = clock;
            this.journal = journal;
            this.diagnostics = diagnostics;
        }

        public EventJournal Journal => journal;

        public void Register(int order, IEventConsumer consumer, params string[] typeKeys)
        {
            registrations.Add(new Registration
            {
                order = order,
                registrationIndex = registrations.Count,
                consumer = consumer,
                typeKeys = typeKeys == null || typeKeys.Length == 0 ? null : new HashSet<string>(typeKeys)
            });
            table.Clear();
        }

        public void Publish(TheNetwork.Persist.Events.NetworkEvent evt)
        {
            if (evt == null) return;
            evt.seq = ids.NextEventSeq();
            evt.tick = clock.Now;
            if (evt.importance != Importance.Ephemeral) journal.Append(evt, evt.tick);
            StateVersion.Bump();

            if (dispatching)
            {
                fifo.Enqueue(evt);
                return;
            }
            dispatching = true;
            try
            {
                Dispatch(evt);
                int cascade = 0;
                while (fifo.Count > 0)
                {
                    TheNetwork.Persist.Events.NetworkEvent next = fifo.Dequeue();
                    if (++cascade > CascadeCap)
                    {
                        NetLog.ErrorOnce(LogCategory.Events, "cascade:" + evt.typeKey,
                            "Event cascade from " + evt.typeKey + " exceeded " + CascadeCap + " events; the rest were journaled but not dispatched.");
                        fifo.Clear();
                        break;
                    }
                    Dispatch(next);
                }
            }
            finally
            {
                dispatching = false;
            }
        }

        private void Dispatch(TheNetwork.Persist.Events.NetworkEvent evt)
        {
            List<IEventConsumer> consumers = ConsumersFor(evt.typeKey);
            for (int i = 0; i < consumers.Count; i++)
            {
                IEventConsumer c = consumers[i];
                long t0 = Stopwatch.GetTimestamp();
                try
                {
                    c.Handle(evt);
                }
                catch (Exception ex)
                {
                    evt.hadErrors = true;
                    diagnostics.AddFailedConsumer(new FailedConsumerRecord
                    {
                        eventSeq = evt.seq,
                        typeKey = evt.typeKey,
                        consumer = c.Name,
                        message = NetScribe.Truncate(ex.GetType().Name + ": " + ex.Message, 400),
                        tick = evt.tick
                    });
                    NetLog.ErrorOnce(LogCategory.Events, "consumer:" + c.Name + ":" + evt.typeKey,
                        "Consumer " + c.Name + " failed on " + evt.typeKey + " #" + evt.seq + " (not re-dispatched): " + ex);
                }
                finally
                {
                    NetProfiler.Record("consumer:" + c.Name, Stopwatch.GetTimestamp() - t0);
                }
            }
        }

        private List<IEventConsumer> ConsumersFor(string typeKey)
        {
            List<IEventConsumer> list;
            if (table.TryGetValue(typeKey ?? "", out list)) return list;
            List<Registration> matches = new List<Registration>();
            for (int i = 0; i < registrations.Count; i++)
            {
                Registration r = registrations[i];
                if (r.typeKeys == null || (typeKey != null && r.typeKeys.Contains(typeKey))) matches.Add(r);
            }
            matches.Sort((a, b) => a.order != b.order ? a.order.CompareTo(b.order) : a.registrationIndex.CompareTo(b.registrationIndex));
            list = new List<IEventConsumer>(matches.Count);
            for (int i = 0; i < matches.Count; i++) list.Add(matches[i].consumer);
            table[typeKey ?? ""] = list;
            return list;
        }
    }
}

namespace TheNetwork.Persist.Events
{
    /// <summary>
    /// An immutable statement that something meaningful happened (EVENTS_AND_HISTORY § 1.1). The header
    /// shape is stable from Phase 1; payload classes are replaceable because the journal is bounded
    /// and never replayed. Persisted type names in this namespace are frozen (SAVE_AND_MIGRATION § 3).
    /// </summary>
    public abstract class NetworkEvent : IExposable
    {
        public long seq;
        public int tick;
        public string typeKey;
        public byte schema = 1;
        public Importance importance = Importance.Minor;
        public List<EntityRef> subjects = new List<EntityRef>();
        public TileRef place;
        public bool hadErrors;

        public virtual void ExposeData()
        {
            Scribe_Values.Look(ref seq, "seq", 0L);
            Scribe_Values.Look(ref tick, "tick", 0);
            Scribe_Values.Look(ref typeKey, "type");
            Scribe_Values.Look(ref schema, "schema", (byte)1);
            NetScribe.LookEnum(ref importance, "importance", Importance.Minor);
            NetScribe.LookEntityRefList(ref subjects, "subjects");
            Scribe_Deep.Look(ref place, "place");
            Scribe_Values.Look(ref hadErrors, "hadErrors", false);
        }

        public override string ToString()
        {
            return "#" + seq + " " + typeKey + " @" + tick;
        }
    }
}
