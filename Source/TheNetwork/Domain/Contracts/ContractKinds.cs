using System.Collections.Generic;
using Verse;

namespace TheNetwork
{
    /// <summary>
    /// Per-kind contract rules (DATA_MODEL § 9). Phase 2 ships exactly one: Procurement. Contracts persist
    /// the kind as a string, so a missing def never breaks a save (the built-in rules apply).
    /// </summary>
    public class NetworkContractKindDef : Def
    {
        public string kindKey;
        public List<string> objectives = new List<string>();
        public float biddingWindowDaysOpen = 1.5f;
        public float biddingWindowDaysDirect = 0.75f;
        public float unfilledExpiryDays = 10f;
        public float worseThanExpectedGraceDays = 3f;
        public float partialResultGraceDays = 5f;
        public float awaitingPaymentGraceDays = 7f;
        public float deliveryHoldMaxDays = 15f;
        public int deliveryRetries = 3;
        public float troubledDeadlineDays = 10f;
        public int maxOffers = 3;
        public int maxQuantityStacks = 40;
    }
}

namespace TheNetwork.Domain.Contracts
{
    /// <summary>The kind rules as plain data (headless-testable). Defaults are the Procurement rules.</summary>
    public sealed class ContractKindRules
    {
        public string kindKey = Contractors.ContractKinds.Procurement;
        public float biddingWindowDaysOpen = 1.5f;
        public float biddingWindowDaysDirect = 0.75f;
        public float unfilledExpiryDays = 10f;
        public float worseThanExpectedGraceDays = 3f;
        public float partialResultGraceDays = 5f;
        public float awaitingPaymentGraceDays = 7f;
        public float deliveryHoldMaxDays = 15f;
        public int deliveryRetries = 3;
        public float troubledDeadlineDays = 10f;
        public int maxOffers = 3;
        public int maxQuantityStacks = 40;

        public static ContractKindRules From(NetworkContractKindDef d)
        {
            return new ContractKindRules
            {
                kindKey = string.IsNullOrEmpty(d.kindKey) ? Contractors.ContractKinds.Procurement : d.kindKey,
                biddingWindowDaysOpen = d.biddingWindowDaysOpen,
                biddingWindowDaysDirect = d.biddingWindowDaysDirect,
                unfilledExpiryDays = d.unfilledExpiryDays,
                worseThanExpectedGraceDays = d.worseThanExpectedGraceDays,
                partialResultGraceDays = d.partialResultGraceDays,
                awaitingPaymentGraceDays = d.awaitingPaymentGraceDays,
                deliveryHoldMaxDays = d.deliveryHoldMaxDays,
                deliveryRetries = d.deliveryRetries,
                troubledDeadlineDays = d.troubledDeadlineDays,
                maxOffers = d.maxOffers,
                maxQuantityStacks = d.maxQuantityStacks
            };
        }
    }

    /// <summary>
    /// Kind rules by key. Built-in defaults, replaced by the XML def when the runtime registers it. An
    /// unknown kind key is not executable (its contracts are voided with a refund by the validator).
    /// </summary>
    public static class ContractKindRegistry
    {
        private static readonly Dictionary<string, ContractKindRules> rules = new Dictionary<string, ContractKindRules>
        {
            { Contractors.ContractKinds.Procurement, new ContractKindRules() }
        };

        public static ContractKindRules Get(string kindKey)
        {
            ContractKindRules r;
            return kindKey != null && rules.TryGetValue(kindKey, out r) ? r : null;
        }

        public static bool Knows(string kindKey) => Get(kindKey) != null;

        public static void Register(ContractKindRules r)
        {
            if (r != null && !string.IsNullOrEmpty(r.kindKey)) rules[r.kindKey] = r;
        }
    }
}
