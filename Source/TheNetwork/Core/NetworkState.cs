using System;
using System.Collections.Generic;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Opportunities;
using TheNetwork.History;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Core
{
    /// <summary>
    /// Every persisted store, in the fixed save order (DATA_MODEL § 3, SAVE_AND_MIGRATION § 2). Stores
    /// that later phases fill are reserved as empty nodes so the layout never shifts. Tests construct
    /// this directly.
    /// </summary>
    public sealed class NetworkState
    {
        public WorldCastSnapshot cast = new WorldCastSnapshot();
        public ActorStore actors = new ActorStore();
        public CharacterStore characters = new CharacterStore();
        public Domain.Knowledge.KnowledgeStore knowledge = new Domain.Knowledge.KnowledgeStore();
        public Domain.Relations.RelationStore relations = new Domain.Relations.RelationStore();
        public ReservedStore obligations = new ReservedStore();
        public ReservedStore contacts = new ReservedStore();
        public IntelStore intel = new IntelStore();
        public OpportunityStore opportunities = new OpportunityStore();
        public Domain.Contracts.ContractStore contracts = new Domain.Contracts.ContractStore();
        public Domain.Operations.OperationStore operations = new Domain.Operations.OperationStore();
        public ReservedStore deployments = new ReservedStore();
        public ReservedStore leases = new ReservedStore();
        public HistoryLedger history = new HistoryLedger();
        public SummaryStore summaries = new SummaryStore();
        public ReservedStore legends = new ReservedStore();
        public ReservedStore beliefs = new ReservedStore();
        public Domain.Consequences.ConsequenceStore consequences = new Domain.Consequences.ConsequenceStore();
        public SchedulerState scheduler = new SchedulerState();
        public EventJournal journal = new EventJournal();
        public DiagnosticsState diagnostics = new DiagnosticsState();

        /// <summary>
        /// Scribes every store in order. Each store loads independently: a store that throws is
        /// replaced by an empty one, recorded, and its subsystem marked degraded, so one failure never
        /// destroys another store's data (SAVE_AND_MIGRATION § 1.4).
        /// </summary>
        public void ExposeStores(List<string> failures)
        {
            Store(ref cast, "cast", "cast", failures);
            Store(ref actors, "actors", "actors", failures);
            Store(ref characters, "characters", "actors", failures);
            Store(ref knowledge, "knowledge", "knowledge", failures);
            Store(ref relations, "relations", "relations", failures);
            Store(ref obligations, "obligations", null, failures);
            Store(ref contacts, "contacts", null, failures);
            Store(ref intel, "intel", "intel", failures);
            Store(ref opportunities, "opportunities", "opportunities", failures);
            Store(ref contracts, "contracts", "contracts", failures);
            Store(ref operations, "operations", "operations", failures);
            Store(ref deployments, "deployments", null, failures);
            Store(ref leases, "leases", null, failures);
            Store(ref history, "history", "history", failures);
            Store(ref summaries, "summaries", "history", failures);
            Store(ref legends, "legends", null, failures);
            Store(ref beliefs, "beliefs", null, failures);
            Store(ref consequences, "consequences", "consequences", failures);
            Store(ref scheduler, "scheduler", "scheduler", failures);
            Store(ref journal, "journal", null, failures);
            Store(ref diagnostics, "diagnostics", null, failures);
        }

        private static void Store<T>(ref T store, string label, string subsystem, List<string> failures) where T : class, IExposable, new()
        {
            try
            {
                Scribe_Deep.Look(ref store, label);
            }
            catch (Exception ex)
            {
                failures?.Add((subsystem ?? label) + "|" + label + ": " + ex.GetType().Name + ": " + ex.Message);
                store = null;
            }
            if (store == null) store = new T();
        }

        /// <summary>
        /// The highest id in use by any entity that draws from the shared allocator (<c>ids.NextId()</c>):
        /// actors, known characters, intel requests, leads, opportunities, history records, contracts,
        /// offers and operations. A new entity kind that draws an id must be added here (a test checks
        /// every kind).
        /// </summary>
        public int MaxEntityId()
        {
            int max = 0;
            for (int i = 0; i < actors.actors.Count; i++) max = Math.Max(max, actors.actors[i].id.Value);
            for (int i = 0; i < characters.characters.Count; i++) max = Math.Max(max, characters.characters[i].id.Value);
            for (int i = 0; i < intel.requests.Count; i++) max = Math.Max(max, intel.requests[i].id.Value);
            for (int i = 0; i < intel.leads.Count; i++) max = Math.Max(max, intel.leads[i].id.Value);
            for (int i = 0; i < opportunities.opportunities.Count; i++) max = Math.Max(max, opportunities.opportunities[i].id.Value);
            for (int i = 0; i < history.records.Count; i++) max = Math.Max(max, history.records[i].id.Value);
            for (int i = 0; i < contracts.contracts.Count; i++) max = Math.Max(max, contracts.contracts[i].id.Value);
            for (int i = 0; i < contracts.offers.Count; i++) max = Math.Max(max, contracts.offers[i].id.Value);
            for (int i = 0; i < operations.operations.Count; i++) max = Math.Max(max, operations.operations[i].id.Value);
            return max;
        }

        /// <summary>
        /// IDs must stay above every id in use, even after a hand-edited or partial save: raises the entity
        /// counter above <see cref="MaxEntityId"/> and the event counter above the journal. Returns true
        /// when the entity counter had to move.
        /// </summary>
        public bool RepairIdCounters(IdAllocator ids)
        {
            int max = MaxEntityId();
            bool raised = ids.EnsureAbove(max);
            if (raised) NetLog.Warn(LogCategory.Kernel, "ID counter was behind the data; raised above " + max + ".");
            for (int i = 0; i < journal.entries.Count; i++) ids.EnsureEventSeqAbove(journal.entries[i].seq);
            return raised;
        }

        public void RebuildIndexes()
        {
            actors.RebuildIndex();
            characters.RebuildIndex();
            knowledge.RebuildIndex();
            relations.RebuildIndex();
            intel.RebuildIndex();
            opportunities.RebuildIndex();
            contracts.RebuildIndex();
            operations.RebuildIndex();
            summaries.RebuildIndex();
        }
    }
}
