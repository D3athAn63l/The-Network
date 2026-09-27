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
