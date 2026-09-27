using System;
using System.Collections.Generic;
using TheNetwork.Domain.Actors;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Domain.Knowledge
{
    // Knowledge v1 (DATA_MODEL § 7, ARCHITECTURE § 6.9): per-actor experience over string topic keys,
    // capped per actor, decaying lazily. There is no world information map.

    public sealed class KnowledgeEntry : IExposable
    {
        public string topic;
        public float exp;
        public int lastTick;

        public void ExposeData()
        {
            Scribe_Values.Look(ref topic, "topic");
            Scribe_Values.Look(ref exp, "exp", 0f);
            Scribe_Values.Look(ref lastTick, "tick", 0);
        }
    }

    public sealed class KnowledgeBook : IExposable
    {
        public ActorId actor;
        public List<KnowledgeEntry> entries = new List<KnowledgeEntry>();

        public KnowledgeEntry Find(string topic)
        {
            for (int i = 0; i < entries.Count; i++) if (entries[i].topic == topic) return entries[i];
            return null;
        }

        public void ExposeData()
        {
            NetScribe.Look(ref actor, "actor");
            NetScribe.LookListTolerant(ref entries, "entries", "knowledge.entries");
        }
    }

    public sealed class KnowledgeStore : IExposable
    {
        public List<KnowledgeBook> books = new List<KnowledgeBook>();

        private readonly Dictionary<int, KnowledgeBook> byActor = new Dictionary<int, KnowledgeBook>();

        public void ExposeData()
        {
            NetScribe.LookListTolerant(ref books, "books", "knowledge");
        }

        public void RebuildIndex()
        {
            byActor.Clear();
            for (int i = 0; i < books.Count; i++) if (books[i] != null && books[i].actor.IsValid) byActor[books[i].actor.Value] = books[i];
        }

        public KnowledgeBook Get(ActorId id)
        {
            KnowledgeBook b;
            return id.IsValid && byActor.TryGetValue(id.Value, out b) ? b : null;
        }

        public KnowledgeBook GetOrCreate(ActorId id)
        {
            KnowledgeBook b = Get(id);
            if (b == null && id.IsValid)
            {
                b = new KnowledgeBook { actor = id };
                books.Add(b);
                byActor[id.Value] = b;
            }
            return b;
        }

        public void Remove(KnowledgeBook b)
        {
            books.Remove(b);
            byActor.Remove(b.actor.Value);
        }
    }

    /// <summary>Topic keys (DATA_MODEL § 7): strings, so they outlive the content they name.</summary>
    public static class Topics
    {
        public static string Thing(string defName) => "thing:" + defName;
        public static string Arch(string key) => "arch:" + key;
        public static string Threat(string tag) => "threat:" + tag;
        public static string Region(string regionKey) => "region:" + regionKey;
        public static string FactionDef(string defName) => "factiondef:" + defName;
        public const string Market = "arch:Market";
    }

    /// <summary>A committed knowledge gain (persisted on an operation's outcome, applied once).</summary>
    public sealed class TopicGain : IExposable
    {
        public string topic;
        public float amount;

        public void ExposeData()
        {
            Scribe_Values.Look(ref topic, "topic");
            Scribe_Values.Look(ref amount, "amount", 0f);
        }
    }

    /// <summary>
    /// Learning from what an actor actually did. Proficiency is read lazily with decay; gains evict the
    /// least valuable topic when a book is full. Preparedness for the resolver reads it.
    /// </summary>
    public sealed class KnowledgeService
    {
        public const int CapPerActor = 64;
        public static readonly double HalfLifeTicks = Ticks.PerYear * 2.0;

        private readonly DomainContext ctx;

        public KnowledgeService(DomainContext ctx)
        {
            this.ctx = ctx;
        }

        private static float Decayed(KnowledgeEntry e, int now)
        {
            int dt = now - e.lastTick;
            if (dt <= 0) return e.exp;
            return e.exp * (float)Math.Pow(0.5, dt / HalfLifeTicks);
        }

        public float Exp(ActorId actor, string topic)
        {
            KnowledgeEntry e = ctx.knowledge.Get(actor)?.Find(topic);
            return e == null ? 0f : Decayed(e, ctx.Now);
        }

        /// <summary>0..1, saturating: a few operations make an actor competent, many make it expert.</summary>
        public float Proficiency(ActorId actor, string topic)
        {
            float x = Exp(actor, topic);
            return x <= 0f ? 0f : 1f - (float)Math.Exp(-x / 3.0);
        }

        /// <summary>Preparedness (SIMULATION § 3.2): weighted proficiency over the operation's topics, 0..1.</summary>
        public float Preparedness(ActorId actor, IList<string> topics)
        {
            if (topics == null || topics.Count == 0) return 0f;
            float sum = 0f, weight = 0f;
            for (int i = 0; i < topics.Count; i++)
            {
                float w = i == 0 ? 2f : 1f;
                sum += Proficiency(actor, topics[i]) * w;
                weight += w;
            }
            return weight <= 0f ? 0f : sum / weight;
        }

        public void Gain(ActorId actor, string topic, float amount)
        {
            if (!actor.IsValid || string.IsNullOrEmpty(topic) || amount <= 0f) return;
            int now = ctx.Now;
            KnowledgeBook book = ctx.knowledge.GetOrCreate(actor);
            KnowledgeEntry e = book.Find(topic);
            if (e == null)
            {
                if (book.entries.Count >= CapPerActor) EvictOne(book, now);
                e = new KnowledgeEntry { topic = topic, exp = 0f, lastTick = now };
                book.entries.Add(e);
            }
            e.exp = Decayed(e, now) + amount;
            e.lastTick = now;
        }

        public void Apply(ActorId actor, List<TopicGain> gains)
        {
            if (gains == null) return;
            for (int i = 0; i < gains.Count; i++) Gain(actor, gains[i].topic, gains[i].amount);
        }

        private static void EvictOne(KnowledgeBook book, int now)
        {
            int worst = -1;
            float worstScore = float.MaxValue;
            for (int i = 0; i < book.entries.Count; i++)
            {
                float score = Decayed(book.entries[i], now);
                if (score < worstScore)
                {
                    worstScore = score;
                    worst = i;
                }
            }
            if (worst >= 0) book.entries.RemoveAt(worst);
        }

        /// <summary>Compaction: faded topics are dropped; books of actors ended long ago go.</summary>
        public int Compact()
        {
            int removed = 0;
            int now = ctx.Now;
            List<KnowledgeBook> books = ctx.knowledge.books;
            for (int b = books.Count - 1; b >= 0; b--)
            {
                KnowledgeBook book = books[b];
                NetworkActor a = ctx.actors.Get(book.actor);
                if (a == null || (a.status != ActorStatus.Active && a.endedTick >= 0 && now - a.endedTick > 2 * Ticks.PerYear))
                {
                    ctx.knowledge.Remove(book);
                    removed++;
                    continue;
                }
                removed += book.entries.RemoveAll(e => Decayed(e, now) < 0.05f);
            }
            return removed;
        }
    }
}
