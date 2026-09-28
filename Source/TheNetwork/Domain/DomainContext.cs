using System;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;

namespace TheNetwork.Domain
{
    /// <summary>Scheduler job kinds used in Phase 1 (SIMULATION § 2). The strings are persisted.</summary>
    public static class JobKinds
    {
        public const string IntelRound = "intel.round";
        public const string IntelClose = "intel.close";
        public const string OppSample = "opp.sample";
        public const string OppWarn = "opp.warn";
        public const string OppClose = "opp.close";
        public const string HistorySweep = "history.sweep";
        public const string CompactSweep = "compact.sweep";
        public const string RefundRetry = "payment.refund";

        // Phase 2 (SIMULATION § 2).
        public const string ContractorUpkeep = ContractorService.UpkeepJob;
        public const string PopulationWeekly = UpkeepService.PopulationJob;
        public const string ContractBidding = Contracts.ProcurementService.BiddingJob;
        public const string ContractOffers = Contracts.ProcurementService.OffersJob;
        public const string ContractExpire = Contracts.ProcurementService.ExpireJob;
        public const string ContractDecision = Contracts.ProcurementService.DecisionJob;
        public const string ContractDelivery = Contracts.ProcurementService.DeliveryJob;
        public const string ContractRefund = Contracts.ProcurementService.RefundJob;
        public const string OperationCheckpoint = Operations.OperationService.CheckpointJob;
        public const string OperationTroubled = Operations.OperationService.TroubledJob;
        public const string ConsequenceFollowUp = Consequences.ConsequenceEngine.FollowUpJob;

        public const int SamplePeriod = 2500;
        public const int SweepPeriod = Ticks.PerQuadrum;
    }

    /// <summary>The outcome of a player command (ARCHITECTURE § 9): ok, or a reason key the UI can show.</summary>
    public struct CommandResult
    {
        public bool ok;
        public string reasonKey;
        public string detail;

        public static CommandResult Ok => new CommandResult { ok = true };

        public static CommandResult Fail(string reasonKey, string detail = null)
        {
            return new CommandResult { ok = false, reasonKey = reasonKey, detail = detail };
        }

        public override string ToString()
        {
            return ok ? "ok" : reasonKey + (detail != null ? " (" + detail + ")" : "");
        }
    }

    /// <summary>
    /// Everything a Domain service may touch: its stores, kernel services and adapter ports. Built by
    /// the root runtime, or directly by headless tests with fakes and a manual clock.
    /// </summary>
    public sealed class DomainContext
    {
        public int networkSeed;
        public IdAllocator ids;
        public IClock clock;
        public NetScheduler scheduler;
        public NetworkEventBus bus;
        public DiagnosticsState diagnostics;

        public WorldCastSnapshot cast;
        public ActorStore actors;
        public CharacterStore characters;
        public IntelStore intel;
        public OpportunityStore opportunities;
        public History.SummaryStore summaries;
        public History.HistoryLedger ledger;
        public Relations.RelationStore relations;
        public Knowledge.KnowledgeStore knowledge;
        public Contracts.ContractStore contracts;
        public Operations.OperationStore operations;
        public Consequences.ConsequenceStore consequences;
        public NetworkTuning tuning = new NetworkTuning();

        public ICatalog catalog;
        public ICommsAccess comms;
        public IPayment payment;
        public IWorldFacts world;
        public ISiteAdapter sites;
        public IDelivery delivery;

        public ActorService Actors;
        public IntelService Intel;
        public OpportunityService Opportunities;
        public ContractorService Contractors;
        public UpkeepService Upkeep;
        public Relations.RelationService Relations;
        public Knowledge.KnowledgeService Knowledge;
        public Contracts.ProcurementService Procurement;
        public Operations.OperationService Operations;
        public Consequences.ConsequenceEngine Consequences;

        public int Now => clock.Now;
    }

    /// <summary>Tuning read from Mod Settings (master § 75). Values, never formulas.</summary>
    public sealed class NetworkTuning
    {
        public Func<int> targetProvider;
        private int target = 100;

        /// <summary>The contractor population scale (default about 100 lightweight actors).</summary>
        public int targetContractorCount
        {
            get { return targetProvider != null ? targetProvider() : target; }
            set { target = value; }
        }
    }

    /// <summary>Service switches read from Mod Settings (ARCHITECTURE § 9). Runtime only.</summary>
    public static class ServiceToggles
    {
        public static bool IntelEnabled = true;
        public static bool ProcurementEnabled = true;
    }

    /// <summary>
    /// Dev-mode overrides for contracts and operations (DEBUGGING § 3). Runtime only, never saved, consumed
    /// by the next matching decision. They replace a draw; they never skip a state or its bookkeeping.
    /// </summary>
    public static class ProcurementDevOverrides
    {
        public static Operations.OutcomeBand? forceBand;
        public static int? forceSecured;
        public static int? forceDelayTicks;
        public static int forceDeliveryFailures;
        public static string forceTroubled;
        public static bool forceWorseThanExpected;
        public static bool forceFollowUp;
        public static bool forceNewcomer;

        public static void Clear()
        {
            forceBand = null;
            forceSecured = null;
            forceDelayTicks = null;
            forceDeliveryFailures = 0;
            forceTroubled = null;
            forceWorseThanExpected = false;
            forceFollowUp = false;
            forceNewcomer = false;
        }
    }

    /// <summary>
    /// Dev-mode overrides consumed by the next Intel round (DEBUGGING § 3). Runtime only, never saved,
    /// never reachable by a player without dev mode.
    /// </summary>
    public static class IntelDevOverrides
    {
        public static bool forceLead;
        public static bool forceNoLead;
        public static LeadDivergence? forceDivergence;
        public static SourceKind? forceSourceKind;
        public static bool commsGateOverride;

        /// <summary>Dev only: fees are frozen as usual but not charged (recorded as waived).</summary>
        public static bool waiveFees;

        public static void Clear()
        {
            forceLead = false;
            forceNoLead = false;
            forceDivergence = null;
            forceSourceKind = null;
        }
    }
}
