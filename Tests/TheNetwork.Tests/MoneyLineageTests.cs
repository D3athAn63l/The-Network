using System;
using System.Collections.Generic;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contracts;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Operations;
using TheNetwork.Kernel;
using TheNetwork.Persist.Events;

namespace TheNetwork.Tests
{
    /// <summary>
    /// Replacement money (DATA_MODEL § 16, ADR-038): a replacement moves the parent's funding to the child
    /// as a TransferOut/TransferIn pair. A transfer is never a charge or a refund; no silver is created or
    /// destroyed anywhere in a lineage.
    /// </summary>
    public static class MoneyLineageTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("Money.A_ReplacementThenVoidRefundsOnce", ReplacementVoid));
            t.Add(new KeyValuePair<string, Action>("Money.B_ReplacementThenSuccessChargesBalanceOnly", ReplacementSuccess));
            t.Add(new KeyValuePair<string, Action>("Money.C_ReplacementThenCancelUsesCarriedFunding", ReplacementCancel));
            t.Add(new KeyValuePair<string, Action>("Money.D_ReplacementThenInsuredFailurePaysOnce", ReplacementInsuredFailure));
            t.Add(new KeyValuePair<string, Action>("Money.E_ReplacementThenPartialProRatesCarriedDeposit", ReplacementPartial));
            t.Add(new KeyValuePair<string, Action>("Money.F_ReplacementThenHandoverCountsCarriedDeposit", ReplacementHandover));
            t.Add(new KeyValuePair<string, Action>("Money.G_ChainedReplacementConserves", ChainedReplacement));
            t.Add(new KeyValuePair<string, Action>("Money.H_TransferSurvivesSaveLoadWithoutDuplication", ReplacementSaveLoad));
        }

        // ================================================================== helpers

        /// <summary>
        /// Posts to one reliable team, accepts its quote, then loses that team before the work starts, so
        /// the broker hands the job to another reliable team on a NEW linked contract.
        /// </summary>
        private static Contract Replaced(TestNet n, NetworkActor fixer, out Contract parent, bool insure = false, int count = 150, int premium = 0)
        {
            NetworkActor first = ProcurementTests.Reliable(n);
            ProcurementTests.Reliable(n);
            parent = ProcurementTests.Post(n, fixer, "TestSteel", count, first, premium);
            Offer o = ProcurementTests.Bid(n, parent);
            T.Check(o != null, "the first team bids (" + ProcurementTests.Refusals(parent) + ")");
            if (o == null) return null;
            T.Check(n.ctx.Procurement.Accept(o.id, insure).ok, "accepted");
            return LoseContractor(n, parent);
        }

        /// <summary>Ends the contract's contractor before the work and returns the replacement contract.</summary>
        private static Contract LoseContractor(TestNet n, Contract c)
        {
            n.ctx.Contractors.EndActor(n.ctx.actors.Get(c.parties.contractor), "Test");
            ProcurementTests.RunUntil(n, () => c.IsTerminal, 20);
            T.Eq(Causes.PreWorkLoss, c.outcome?.causeKey, "the contractor was lost before the work");
            T.Eq(1, c.lineage.children.Count, "a replacement was found");
            if (c.lineage.children.Count == 0) return null;
            Contract child = n.ctx.contracts.Get(c.lineage.children[0]);
            T.Check(child != null && child.lineage.relationKey == ContractLineage.Replacement && child.IsUnderway, "the replacement is under way");
            return child;
        }

        public static List<Contract> Lineage(TestNet n, Contract root)
        {
            List<Contract> all = new List<Contract>();
            foreach (Contract c in n.ctx.contracts.contracts) if (c.id == root.id || c.lineage.root == root.id) all.Add(c);
            return all;
        }

        /// <summary>
        /// The lineage invariant: external charges minus external refunds equal the player's real net
        /// silver; internal transfers sum to zero (in total and per purpose), every TransferIn is matched
        /// by a TransferOut of the same amount and purpose on its counterpart, and no contract has returned
        /// more than it was funded with.
        /// </summary>
        public static void AssertLineage(TestNet n, Contract root, int charged0, int refunded0, string label)
        {
            List<Contract> lineage = Lineage(n, root);
            int external = 0, transfers = 0;
            Dictionary<MoneyPurpose, int> perPurpose = new Dictionary<MoneyPurpose, int>();
            foreach (Contract c in lineage)
            {
                external += c.ExternalCharged() - c.ExternalRefunded();
                transfers += c.TransferredIn() - c.TransferredOut();
                T.Check(c.TotalFunding() >= 0 && c.ExternalRefunded() <= c.TotalFunding(), label + ": " + c.id + " never returns more than it holds (" + c.ExternalRefunded() + " of " + c.TotalFunding() + ")");
                foreach (MoneyRecord m in c.ledger)
                {
                    T.Check(m.silver > 0, label + ": no empty or negative money record");
                    T.Check(!m.pending, label + ": nothing pending");
                    if (m.IsExternal) continue;
                    int sign = m.direction == MoneyDirection.TransferIn ? 1 : -1;
                    int v;
                    perPurpose.TryGetValue(m.purpose, out v);
                    perPurpose[m.purpose] = v + sign * m.silver;
                    Contract other = n.ctx.contracts.Get(new ContractId(m.linkedContract));
                    MoneyDirection mirror = m.direction == MoneyDirection.TransferIn ? MoneyDirection.TransferOut : MoneyDirection.TransferIn;
                    bool matched = false;
                    if (other != null) foreach (MoneyRecord x in other.ledger) if (x.direction == mirror && x.purpose == m.purpose && x.silver == m.silver && x.linkedContract == c.id.Value) matched = true;
                    T.Check(matched, label + ": " + c.id + " " + m.direction + " " + m.purpose + " " + m.silver + " is mirrored on " + m.linkedContract);
                }
            }
            T.Eq((n.pay.charged - charged0) - (n.pay.refunded - refunded0), external, label + ": external charges − external refunds = the player's real net silver");
            T.Eq(0, transfers, label + ": internal transfers sum to zero across the lineage");
            foreach (KeyValuePair<MoneyPurpose, int> kv in perPurpose) T.Eq(0, kv.Value, label + ": transfers of " + kv.Key + " sum to zero");
        }

        private static int Count(TestNet n, Contract root, MoneyDirection dir, MoneyPurpose purpose)
        {
            int k = 0;
            foreach (Contract c in Lineage(n, root)) foreach (MoneyRecord m in c.ledger) if (m.direction == dir && m.purpose == purpose) k++;
            return k;
        }

        // ================================================================== A–H

        /// <summary>A: replacement, then a technical invalidation: every silver paid comes back exactly once.</summary>
        private static void ReplacementVoid()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n, "Premium", "Basic");
            int charged0 = n.pay.charged, refunded0 = n.pay.refunded;
            Contract parent;
            Contract child = Replaced(n, fixer, out parent, true, 150, 120);
            if (child == null) return;
            int paid = n.pay.charged - charged0;
            T.Check(parent.Funding(MoneyPurpose.InsurancePremium) == 0 && child.Funding(MoneyPurpose.InsurancePremium) > 0, "the insurance premium moved with the insurance terms");
            T.Eq(0, parent.NetFunding(), "the parent holds nothing refundable after the transfer");
            T.Eq(paid, child.NetFunding(), "the child holds everything the player paid (deposit, premium, insurance)");
            T.Eq(0, child.ExternalCharged(), "no charge on the replacement");
            T.Eq(0, n.pay.refunded - refunded0, "the replacement refunded nothing");
            n.ctx.Procurement.Void(parent, Causes.DefMissing);
            n.ctx.Procurement.Void(child, Causes.DefMissing);
            n.ctx.Procurement.Void(child, Causes.DefMissing);
            T.Eq(ContractStatus.Voided, child.status, "the replacement is Voided");
            T.Eq(paid, n.pay.refunded - refunded0, "refunded exactly once: every silver paid, no more");
            T.Eq(1, Count(n, parent, MoneyDirection.PlayerRefunded, MoneyPurpose.Refund), "one refund record in the lineage");
            AssertLineage(n, parent, charged0, refunded0, "A");
        }

        /// <summary>B: replacement, then success: only the remaining balance is charged, once.</summary>
        private static void ReplacementSuccess()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n, "Premium");
            int charged0 = n.pay.charged, refunded0 = n.pay.refunded, calls0 = n.pay.chargeCalls;
            Contract parent;
            Contract child = Replaced(n, fixer, out parent, false, 150, 80);
            if (child == null) return;
            int deposit = parent.ExternalCharged() - 80;
            T.Eq(parent.terms.deposit, deposit, "the parent charged the deposit (plus the premium)");
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            ProcurementTests.RunUntil(n, () => child.IsTerminal);
            T.Eq(ContractStatus.Fulfilled, child.status, "the replacement delivers");
            T.Eq(child.terms.price - deposit, child.ExternalCharged(), "only the remaining balance is charged on the replacement");
            T.Eq(child.terms.price + 80, n.pay.charged - charged0, "the lineage charged the price and the premium, once");
            T.Eq(2, n.pay.chargeCalls - calls0, "two charges in total: at award and at delivery");
            T.Eq(0, n.pay.refunded - refunded0, "nothing refunded");
            AssertLineage(n, parent, charged0, refunded0, "B");
        }

        /// <summary>C: replacement, then the client cancels: the policy applies to the carried funding, once.</summary>
        private static void ReplacementCancel()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n);
            int charged0 = n.pay.charged, refunded0 = n.pay.refunded;
            Contract parent;
            Contract child = Replaced(n, fixer, out parent, false, 150, 60);
            if (child == null) return;
            T.Eq("AlreadyClosed", n.ctx.Procurement.Cancel(parent.id).reasonKey, "the replaced parent cannot be cancelled");
            int funded = child.Funding(MoneyPurpose.Deposit) + child.Funding(MoneyPurpose.Premium);
            T.Eq(parent.terms.deposit + 60, funded, "the carried deposit and premium count on the replacement");
            float share = FixerPolicies.CancelRefundShare(child.terms.refundPolicyKey, true);
            T.Check(Op(n, child).phase == OpPhase.Preparing, "still preparing");
            T.Check(n.ctx.Procurement.Cancel(child.id).ok, "cancel the replacement");
            T.Eq((int)Math.Round(funded * share), n.pay.refunded - refunded0, "the refund policy applied once, to the carried funding");
            T.Check(n.pay.refunded - refunded0 > 0, "and it is not nothing");
            n.ctx.Procurement.Void(parent, Causes.DefMissing);
            T.Eq((int)Math.Round(funded * share), n.pay.refunded - refunded0, "no second refund from the parent");
            AssertLineage(n, parent, charged0, refunded0, "C");
        }

        /// <summary>D: replacement, then an insured failure: the inherited insurance pays once, on the carried deposit.</summary>
        private static void ReplacementInsuredFailure()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n, "Premium", "Basic");
            int charged0 = n.pay.charged, refunded0 = n.pay.refunded;
            Contract parent;
            Contract child = Replaced(n, fixer, out parent, true);
            if (child == null) return;
            T.Check(child.terms.insurance != null && child.terms.insurance.coverage == parent.terms.insurance.coverage, "the insurance terms are inherited");
            ProcurementDevOverrides.forceBand = OutcomeBand.Failure;
            ProcurementDevOverrides.forceSecured = 0;
            ProcurementTests.RunUntil(n, () => child.IsTerminal);
            T.Eq(ContractStatus.Failed, child.status, "the replacement fails");
            int expected = (int)Math.Round(parent.terms.deposit * child.terms.insurance.coverage);
            T.Check(expected > 0, "a covered loss");
            T.Eq(expected, n.pay.refunded - refunded0, "the payout is the coverage share of the carried deposit, once");
            T.Eq(1, Count(n, parent, MoneyDirection.PlayerRefunded, MoneyPurpose.InsurancePayout), "one payout in the lineage");
            T.Eq(0, parent.ExternalRefunded(), "the parent paid out nothing");
            AssertLineage(n, parent, charged0, refunded0, "D");
        }

        /// <summary>E: replacement, then a partial result: the pro-rated balance counts the carried deposit.</summary>
        private static void ReplacementPartial()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n, "Premium");
            int charged0 = n.pay.charged, refunded0 = n.pay.refunded;
            Contract parent;
            Contract child = Replaced(n, fixer, out parent, false, 200);
            if (child == null) return;
            ProcurementDevOverrides.forceBand = OutcomeBand.Partial;
            ProcurementTests.RunUntil(n, () => child.status == ContractStatus.Renegotiating || child.IsTerminal);
            T.Eq(SubStatus.PartialResult, child.subStatus, "a partial result");
            int secured = child.Acquire.secured;
            T.Check(n.ctx.Procurement.RespondPartial(child.id, PartialChoice.AcceptPartial).ok, "accept the partial result");
            int prorated = (int)Math.Round(child.terms.price * secured / 200f);
            int expected = Math.Max(0, prorated - parent.terms.deposit);
            T.Eq(expected, child.ExternalCharged(), "the pro-rated balance is reduced by the carried deposit (" + secured + " of 200)");
            T.Eq(secured, child.Acquire.delivered, "the secured part is delivered");
            AssertLineage(n, parent, charged0, refunded0, "E");
        }

        /// <summary>F: replacement, then the client cannot pay: the handover covers what the carried deposit paid for.</summary>
        private static void ReplacementHandover()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n, "Premium");
            int charged0 = n.pay.charged, refunded0 = n.pay.refunded;
            Contract parent;
            Contract child = Replaced(n, fixer, out parent, false, 100);
            if (child == null) return;
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            ProcurementTests.RunUntil(n, () => Op(n, child).outcome != null || child.IsTerminal);
            n.pay.silver = 0;
            ProcurementTests.RunUntil(n, () => child.IsTerminal, 15);
            T.Eq(ContractStatus.PartiallyFulfilled, child.status, "partial handover");
            T.Eq(Causes.PartialHandover, child.outcome?.causeKey, "PartialHandover");
            int deposit = parent.terms.deposit;
            int expected = (int)Math.Floor(100 * deposit / (float)(deposit + child.terms.balance));
            T.Check(expected > 0, "the carried deposit covers some goods");
            T.Eq(expected, child.Acquire.delivered, "the goods the carried deposit paid for");
            T.Eq(0, child.ExternalCharged(), "no charge on the replacement");
            AssertLineage(n, parent, charged0, refunded0, "F");
        }

        /// <summary>G: A → B → C: the funding moves twice, is charged once, and the final balance once.</summary>
        private static void ChainedReplacement()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n, "Premium", "Basic");
            ProcurementTests.Reliable(n);
            int charged0 = n.pay.charged, refunded0 = n.pay.refunded, calls0 = n.pay.chargeCalls;
            Contract a;
            Contract b = Replaced(n, fixer, out a, true, 150, 50);
            if (b == null) return;
            int paid = n.pay.charged - charged0;
            Contract c = LoseContractor(n, b);
            if (c == null) return;
            T.Check(c.lineage.root == a.id && c.lineage.parent == b.id && c.lineage.inheritedFrom == b.id && c.lineage.depth == 2, "A → B → C lineage");
            T.Eq(0, a.NetFunding(), "A holds nothing");
            T.Eq(0, b.NetFunding(), "B holds nothing");
            T.Eq(paid, c.NetFunding(), "C holds everything the player paid");
            T.Eq(a.terms.deposit, c.Funding(MoneyPurpose.Deposit), "the deposit arrived intact");
            T.Eq(paid, n.pay.charged - charged0, "no repeated charge");
            AssertLineage(n, a, charged0, refunded0, "G (before delivery)");
            ProcurementDevOverrides.forceBand = OutcomeBand.Triumph;
            ProcurementTests.RunUntil(n, () => c.IsTerminal);
            T.Eq(ContractStatus.Fulfilled, c.status, "C delivers");
            T.Eq(c.terms.balance, c.ExternalCharged(), "C charges only the balance");
            T.Eq(2, n.pay.chargeCalls - calls0, "two charges for the whole lineage");
            T.Eq(0, n.pay.refunded - refunded0, "nothing refunded");
            AssertLineage(n, a, charged0, refunded0, "G");
        }

        /// <summary>H: save and load after a transfer: the records come back once, and money still reconciles.</summary>
        private static void ReplacementSaveLoad()
        {
            TestNet n = ProcurementTests.World(0);
            NetworkActor fixer = ProcurementTests.Fixer(n, "Premium", "Basic");
            int charged0 = n.pay.charged, refunded0 = n.pay.refunded;
            Contract parent;
            Contract child = Replaced(n, fixer, out parent, true, 150, 40);
            if (child == null) return;
            int paid = n.pay.charged - charged0;

            TheNetwork.Core.NetworkState state = new TheNetwork.Core.NetworkState { contracts = n.ctx.contracts, operations = n.ctx.operations, consequences = n.ctx.consequences, characters = n.ctx.characters, actors = n.ctx.actors };
            string path = PersistenceTests.SaveState(state, 2);
            TheNetwork.Core.NetworkState loaded = new TheNetwork.Core.NetworkState();
            Verse.Scribe.loader.InitLoading(path);
            try
            {
                List<string> failures = new List<string>();
                loaded.ExposeStores(failures);
                T.Eq(0, failures.Count, "no store failed (" + string.Join("; ", failures.ToArray()) + ")");
            }
            finally
            {
                Verse.Scribe.loader.FinalizeLoading();
            }
            loaded.RebuildIndexes();
            System.IO.File.Delete(path);

            Contract lp = loaded.contracts.Get(parent.id), lc = loaded.contracts.Get(child.id);
            T.Check(lp != null && lc != null, "both contracts load");
            if (lp == null || lc == null) return;
            T.Check(ProcurementTests.SameLedger(parent, lp) && ProcurementTests.SameLedger(child, lc), "every record (direction, purpose, amount, counterpart) survives, none duplicated");
            T.Eq(0, lp.NetFunding(), "the loaded parent holds nothing");
            T.Eq(paid, lc.NetFunding(), "the loaded child holds everything");

            n.ctx.contracts = loaded.contracts;
            n.ctx.operations = loaded.operations;
            T.Eq(0, n.ctx.Procurement.EnsureJobs(null), "no job to recreate: loading does not re-run the transfer");
            n.ctx.Procurement.Void(lc, Causes.DefMissing);
            T.Eq(paid, n.pay.refunded - refunded0, "voided after loading: refunded exactly once");
            AssertLineage(n, lp, charged0, refunded0, "H");
        }

        private static Operation Op(TestNet n, Contract c)
        {
            return n.ctx.Procurement.CurrentOperation(c);
        }
    }
}
