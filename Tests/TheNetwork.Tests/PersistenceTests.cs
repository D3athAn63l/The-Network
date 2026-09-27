using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using Verse;

namespace TheNetwork.Tests
{
    public static class PersistenceTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("Persist.TolerantLoaderQuarantinesAndContinues", Tolerant));
            t.Add(new KeyValuePair<string, Action>("Persist.StoreLayoutFixedOrder", Layout));
            t.Add(new KeyValuePair<string, Action>("Persist.StateRoundTrip", StateRoundTrip));
            t.Add(new KeyValuePair<string, Action>("Persist.ReferencesAreStrings", ReferencesAreStrings));
        }

        private static void Tolerant()
        {
            string path = SettingsTests.Fixture("save/v1_intel_store_corrupted.xml");
            NetScribe.PendingQuarantine.Clear();
            int vanillaBefore = T.vanillaLog.Count;
            IntelStore store = new IntelStore();
            string marker = null;
            Scribe.loader.InitLoading(path);
            try
            {
                Scribe_Deep.Look(ref store, "intel");
                Scribe.EnterNode("after");
                Scribe_Values.Look(ref marker, "marker");
                Scribe.ExitNode();
            }
            finally
            {
                Scribe.loader.FinalizeLoading();
            }
            store.RebuildIndex();
            T.Eq(3, store.requests.Count, "siblings of the unconstructible element loaded");
            T.Eq(1, NetScribe.PendingQuarantine.Count, "the unknown class is quarantined, not dropped");
            T.Check(NetScribe.PendingQuarantine.Count == 1 && NetScribe.PendingQuarantine[0].reasonKey.StartsWith("UnknownClass") && NetScribe.PendingQuarantine[0].rawXml.Contains("IntelRequestFromTheFuture"),
                "quarantine keeps the reason and the raw XML");
            IntelRequest ok = store.Get(new IntelRequestId(11));
            T.Check(ok != null && ok.state == IntelState.Searching && ok.round == 2 && ok.leads.Count == 1 && ok.topic.DefName == "TestSteel", "a good element loads completely");
            IntelRequest badEnum = store.Get(new IntelRequestId(13));
            T.Check(badEnum != null && badEnum.quarantinedReason == "MalformedEnum:state", "an unreadable state is quarantined in place, never guessed");
            IntelRequest badNested = store.Get(new IntelRequestId(14));
            T.Check(badNested != null && badNested.topic != null && badNested.topic.DefName == null, "a corrupted nested value leaves an empty topic (the validator invalidates it)");
            T.Eq("still-readable", marker, "Scribe state intact after the corrupted elements");
            for (int i = vanillaBefore; i < T.vanillaLog.Count; i++) Console.WriteLine("    vanilla: " + T.vanillaLog[i]);
            T.Check(T.vanillaLog.Count - vanillaBefore <= 1, "at most one vanilla line (the corrupted nested class), none from the list loader");
            NetScribe.PendingQuarantine.Clear();
        }

        private static readonly string[] ExpectedOrder =
        {
            "saveVersion", "createdWithModVersion", "lastSavedWithModVersion", "networkSeed", "bootstrapped", "preparedForRemoval", "ids",
            "cast", "actors", "characters", "knowledge", "relations", "obligations", "contacts", "intel", "opportunities", "contracts",
            "operations", "deployments", "leases", "history", "summaries", "legends", "beliefs", "consequences", "scheduler", "journal", "diagnostics"
        };

        private static string SaveState(NetworkState state, int saveVersion)
        {
            string path = Path.Combine(Path.GetTempPath(), "thenetwork-state-" + Guid.NewGuid().ToString("N") + ".xml");
            Scribe.saver.InitSaving(path, "component");
            try
            {
                int v = saveVersion;
                string created = "0.1.0", last = "0.1.0";
                int seed = 5;
                bool boot = true, prepared = false;
                IdAllocator ids = new IdAllocator();
                Scribe_Values.Look(ref v, "saveVersion", 0, true);
                Scribe_Values.Look(ref created, "createdWithModVersion");
                Scribe_Values.Look(ref last, "lastSavedWithModVersion");
                Scribe_Values.Look(ref seed, "networkSeed", 0, true);
                Scribe_Values.Look(ref boot, "bootstrapped", false, true);
                Scribe_Values.Look(ref prepared, "preparedForRemoval", false, true);
                Scribe_Deep.Look(ref ids, "ids");
                state.ExposeStores(null);
            }
            finally
            {
                Scribe.saver.FinalizeSaving();
            }
            return path;
        }

        private static void Layout()
        {
            NetworkState state = new NetworkState();
            string path = SaveState(state, 1);
            XmlDocument doc = new XmlDocument();
            doc.Load(path);
            List<string> names = new List<string>();
            foreach (XmlNode n in doc.DocumentElement.ChildNodes) if (n.NodeType == XmlNodeType.Element) names.Add(n.Name);
            T.Eq("saveVersion", names.Count > 0 ? names[0] : null, "saveVersion is written first");
            int first = names.IndexOf("cast");
            List<string> stores = first < 0 ? new List<string>() : names.GetRange(first, names.Count - first);
            List<string> expected = new List<string>(ExpectedOrder);
            expected = expected.GetRange(expected.IndexOf("cast"), expected.Count - expected.IndexOf("cast"));
            T.Eq(string.Join(",", expected.ToArray()), string.Join(",", stores.ToArray()), "every store slot saved, in the fixed order, even when empty");
            File.Delete(path);
        }

        private static void StateRoundTrip()
        {
            TestNet n = new TestNet(31337);
            NetworkActor fixer = n.AddFixer("Thorough");
            n.ctx.Intel.Submit(Domain.Intel.SourceKey.ForActor(fixer.id), "ModX_Weirdium");
            IntelRequest r = n.Only();
            IntelTests.ForceLead();
            n.ResolveRound(r);
            NetworkState state = new NetworkState
            {
                cast = n.ctx.cast, actors = n.ctx.actors, characters = n.ctx.characters, intel = n.ctx.intel, opportunities = n.ctx.opportunities,
                history = n.ledger, summaries = n.summaries, journal = n.journal, diagnostics = n.diag
            };
            n.scheduler.WriteTo(state.scheduler);
            string path = SaveState(state, 1);

            NetworkState loaded = new NetworkState();
            Scribe.loader.InitLoading(path);
            try
            {
                List<string> failures = new List<string>();
                loaded.ExposeStores(failures);
                T.Eq(0, failures.Count, "no store failed");
            }
            finally
            {
                Scribe.loader.FinalizeLoading();
            }
            loaded.RebuildIndexes();
            File.Delete(path);

            T.Eq(state.actors.actors.Count, loaded.actors.actors.Count, "actors");
            T.Check(loaded.actors.PlayerProxy != null && loaded.actors.Exchange != null, "pseudo-actors reindexed");
            NetworkActor f2 = loaded.actors.Get(fixer.id);
            T.Check(f2 != null && f2.Has<FixerProfile>() && f2.Has<IntelSourceProfile>(), "polymorphic components round-trip (frozen type names)");
            T.Eq(state.cast.entries.Count, loaded.cast.entries.Count, "cast snapshot");
            IntelRequest r2 = loaded.intel.Get(r.id);
            T.Check(r2 != null && r2.state == r.state && r2.round == r.round && r2.seed == r.seed && r2.leads.Count == r.leads.Count, "request state, round, seed, leads");
            T.Eq(r.terms.initialFee, r2.terms.initialFee, "frozen terms");
            Lead l2 = loaded.intel.Get(r.leads[0]);
            Lead l1 = n.ctx.intel.Get(r.leads[0]);
            T.Check(l2 != null && l2.divergence == l1.divergence && l2.reported.estimateLow == l1.reported.estimateLow && l2.reported.threatBand == l1.reported.threatBand, "lead report and hidden divergence");
            Opportunity o1 = n.ctx.opportunities.Get(l1.opportunity);
            Opportunity o2 = loaded.opportunities.Get(l1.opportunity);
            T.Check(o2 != null && o2.TargetCount == o1.TargetCount && o2.payload.Count == o1.payload.Count && o2.location.tileId == o1.location.tileId && o2.seed == o1.seed, "opportunity truth (payload, tile, seed)");
            T.Check(o2.Target.thing.defName == "ModX_Weirdium" && o2.Target.thing.packageId == "someone.weirdmod", "payload def stored as a string with its package snapshot");
            T.Eq(state.history.records.Count, loaded.history.records.Count, "history records");
            T.Eq(state.scheduler.jobs.Count, loaded.scheduler.jobs.Count, "scheduler jobs");
            T.Eq(state.journal.entries.Count, loaded.journal.entries.Count, "journal (polymorphic event classes)");
        }

        private static void ReferencesAreStrings()
        {
            // A save that references a def and a faction which no longer exist still loads: references are
            // strings and ids, never Scribe_Defs/Scribe_References (DATA_MODEL § 2).
            DefRef<Verse.ThingDef> d = new DefRef<Verse.ThingDef> { defName = "Gone_ModItem", label = "gone thing", packageId = "gone.mod" };
            string path = Path.Combine(Path.GetTempPath(), "thenetwork-ref-" + Guid.NewGuid().ToString("N") + ".xml");
            Scribe.saver.InitSaving(path, "ref");
            try { d.ExposeData(); } finally { Scribe.saver.FinalizeSaving(); }
            int before = T.vanillaLog.Count;
            DefRef<Verse.ThingDef> back = new DefRef<Verse.ThingDef>();
            Scribe.loader.InitLoading(path);
            try { back.ExposeData(); } finally { Scribe.loader.FinalizeLoading(); }
            File.Delete(path);
            T.Eq("Gone_ModItem", back.defName, "defName kept");
            T.Eq("gone thing", back.LabelSnapshot, "label snapshot readable after removal");
            T.Check(back.Resolve() == null, "a missing def resolves silently to null");
            T.Eq(before, T.vanillaLog.Count, "no vanilla error for a missing def");
        }
    }
}
