using System;
using System.Collections.Generic;
using TheNetwork.Domain.Knowledge;
using TheNetwork.Domain.Relations;
using TheNetwork.Kernel;

namespace TheNetwork.Tests
{
    public static class RelationsKnowledgeTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("Relations.LazyDecayAndDescriptor", RelationDecay));
            t.Add(new KeyValuePair<string, Action>("Relations.BoundedAndCompacted", RelationBounds));
            t.Add(new KeyValuePair<string, Action>("Knowledge.GainDecayPreparedness", KnowledgeGain));
            t.Add(new KeyValuePair<string, Action>("Knowledge.BoundedEntries", KnowledgeBounds));
        }

        private static void RelationDecay()
        {
            TestNet n = new TestNet();
            ActorId a = new ActorId(9001), b = new ActorId(9002);
            RelationView none = n.ctx.Relations.Get(a, b);
            T.Check(!none.exists && none.trust == 0.5f && none.standing == 0f, "unknown pairs read as neutral and are not stored");
            T.Eq(0, n.ctx.relations.Count, "reading creates nothing");
            n.ctx.Relations.Apply(a, b, new RelationDelta { standing = -40f, trust = -0.3f, defaults = 1 }, HistoryRecordId.None);
            RelationView now = n.ctx.Relations.Get(a, b);
            T.Check(Math.Abs(now.standing + 40f) < 0.01f && Math.Abs(now.trust - 0.2f) < 0.01f, "applied");
            T.Eq(1, n.ctx.relations.Count, "one directed edge");
            T.Check(!n.ctx.Relations.Get(b, a).exists, "edges are directed");
            n.clock.Now += Ticks.PerYear * 2;
            RelationView later = n.ctx.Relations.Get(a, b);
            T.Check(Math.Abs(later.standing + 20f) < 0.5f, "standing halves toward neutral over its half-life (" + later.standing + ")");
            T.Check(Math.Abs(later.trust - 0.35f) < 0.02f, "trust decays toward 0.5 (" + later.trust + ")");
            T.Eq(1, later.defaults, "counters do not decay");
            T.Check(n.ctx.relations.Find(a, b).updatedTick < n.clock.Now, "reading does not mutate the edge");
            n.ctx.Relations.Apply(a, b, new RelationDelta { standing = 0f }, HistoryRecordId.None);
            T.Check(Math.Abs(n.ctx.relations.Find(a, b).standing - later.standing) < 0.01f, "write applies the same decay first");
            T.Check(n.ctx.Relations.Descriptor(a, b) != "Unknown", "a descriptor, never a number");
        }

        private static void RelationBounds()
        {
            TestNet n = new TestNet();
            for (int i = 1; i <= RelationService.GlobalCap + 50; i++) n.ctx.Relations.Apply(new ActorId(100000 + i), new ActorId(200000), new RelationDelta { familiarity = 0.01f }, HistoryRecordId.None);
            T.Check(n.ctx.relations.Count <= RelationService.GlobalCap, "global cap enforced (" + n.ctx.relations.Count + ")");
            RelationEdge e = n.ctx.Relations.Apply(new ActorId(5), new ActorId(6), new RelationDelta { standing = 1f }, new HistoryRecordId(1));
            n.ctx.Relations.Apply(new ActorId(5), new ActorId(6), new RelationDelta(), new HistoryRecordId(2));
            n.ctx.Relations.Apply(new ActorId(5), new ActorId(6), new RelationDelta(), new HistoryRecordId(3));
            n.ctx.Relations.Apply(new ActorId(5), new ActorId(6), new RelationDelta(), new HistoryRecordId(4));
            T.Eq(RelationEdge.MaxSalient, e.salient.Count, "salient records bounded");
            T.Check(n.ctx.Relations.Compact() > 0, "edges of unknown (gone) actors are compacted away");
        }

        private static void KnowledgeGain()
        {
            TestNet n = new TestNet();
            ActorId a = new ActorId(77);
            T.Eq(0f, n.ctx.Knowledge.Proficiency(a, Topics.Thing("Steel")), "unknown topic: nothing");
            n.ctx.Knowledge.Gain(a, Topics.Thing("Steel"), 2f);
            float p1 = n.ctx.Knowledge.Proficiency(a, Topics.Thing("Steel"));
            n.ctx.Knowledge.Gain(a, Topics.Thing("Steel"), 4f);
            float p2 = n.ctx.Knowledge.Proficiency(a, Topics.Thing("Steel"));
            T.Check(p1 > 0f && p2 > p1 && p2 < 1f, "saturating proficiency (" + p1 + " → " + p2 + ")");
            n.clock.Now += Ticks.PerYear * 2;
            float p3 = n.ctx.Knowledge.Proficiency(a, Topics.Thing("Steel"));
            T.Check(p3 < p2, "decays lazily (" + p3 + ")");
            float prep = n.ctx.Knowledge.Preparedness(a, new List<string> { Topics.Thing("Steel"), Topics.Market });
            T.Check(prep > 0f && prep < p3, "preparedness weighs every topic (" + prep + ")");
        }

        private static void KnowledgeBounds()
        {
            TestNet n = new TestNet();
            ActorId a = new ActorId(78);
            for (int i = 0; i < KnowledgeService.CapPerActor + 30; i++) n.ctx.Knowledge.Gain(a, Topics.Thing("Item" + i), 1f + i * 0.01f);
            T.Eq(KnowledgeService.CapPerActor, n.ctx.knowledge.Get(a).entries.Count, "capped at 64 per actor");
            T.Check(n.ctx.knowledge.Get(a).Find(Topics.Thing("Item0")) == null, "the weakest topic was evicted");
            n.clock.Now += Ticks.PerYear * 20;
            T.Check(n.ctx.Knowledge.Compact() > 0, "long-faded topics are compacted");
        }
    }
}
