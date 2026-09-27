using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Opportunities;
using TheNetwork.History;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;
using TheNetwork.UI;
using Verse;

namespace TheNetwork.Tests
{
    public static class IntelTests
    {
        public static void Register(List<KeyValuePair<string, Action>> t)
        {
            t.Add(new KeyValuePair<string, Action>("Intel.SubmitGateAndCharge", SubmitGate));
            t.Add(new KeyValuePair<string, Action>("Intel.SubmitRefusals", SubmitRefusals));
            t.Add(new KeyValuePair<string, Action>("Intel.MultiLeadAskFree", MultiLead));
            t.Add(new KeyValuePair<string, Action>("Intel.AutoContinuationAndRoundLimit", AutoContinuation));
            t.Add(new KeyValuePair<string, Action>("Intel.ContinuationFees", ContinuationFees));
            t.Add(new KeyValuePair<string, Action>("Intel.NothingCredibleConcludes", NothingCredible));
            t.Add(new KeyValuePair<string, Action>("Intel.ExchangeSingleLead", ExchangeSingle));
            t.Add(new KeyValuePair<string, Action>("Intel.CancelRefundPolicy", CancelRefund));
            t.Add(new KeyValuePair<string, Action>("Intel.InvalidateTopicMissing", InvalidateTopicMissing));
            t.Add(new KeyValuePair<string, Action>("Intel.ThingCreationFailureFullRefund", ThingFailure));
            t.Add(new KeyValuePair<string, Action>("Intel.CommsLossDoesNotStopSearch", CommsLoss));
            t.Add(new KeyValuePair<string, Action>("Intel.DeterministicAcrossSaveLoad", Determinism));
            t.Add(new KeyValuePair<string, Action>("Intel.StaleJobIgnored", StaleJob));
            t.Add(new KeyValuePair<string, Action>("Intel.ClosesAfterLeadsClose", Closing));
            t.Add(new KeyValuePair<string, Action>("Intel.FactionContactLazyProxy", FactionContact));
            t.Add(new KeyValuePair<string, Action>("Intel.NoQuantityAnywhere", NoQuantity));
        }

        public static void ForceLead(SourceKind kind = SourceKind.AbandonedCache)
        {
            IntelDevOverrides.forceLead = true;
            IntelDevOverrides.forceSourceKind = kind;
        }

        private static IntelRequest Submit(TestNet n, NetworkActor source, string def = "TestSteel")
        {
            CommandResult r = n.ctx.Intel.Submit(SourceKey.ForActor(source.id), def);
            T.Check(r.ok, "submit ok (" + r + ")");
            return n.Only();
        }

        private static void SubmitGate()
        {
            TestNet n = new TestNet();
            NetworkActor fixer = n.AddFixer("Thorough");
            n.comms.usable = false;
            CommandResult refused = n.ctx.Intel.Submit(SourceKey.ForActor(fixer.id), "TestSteel");
            T.Check(!refused.ok && refused.reasonKey == "NoUsableCommsConsole", "no usable console → refused with its reason");
            T.Eq(0, n.pay.charged, "nothing charged when refused");
            T.Eq(0, n.ctx.intel.requests.Count, "nothing created when refused");
            n.comms.usable = true;
            SearchTerms preview = n.ctx.Intel.PreviewTerms(SourceKey.ForActor(fixer.id), "TestSteel");
            IntelRequest r = Submit(n, fixer);
            T.Eq(IntelState.Searching, r.state, "Submitted → Searching in the same tick");
            T.Eq(1, r.round, "round 1");
            T.Eq(preview.initialFee, n.pay.charged, "the source's fee was charged");
            T.Eq(preview.initialFee, r.TotalPaid(), "fee recorded in the ledger in the same step");
            T.Check(n.scheduler.Find(JobKinds.IntelRound, r.id.Value) != null, "round job scheduled");
            T.Check(r.nextRoundDueTick > n.clock.Now + Ticks.PerDay / 2, "a round takes in-game time");
            T.Eq(1, n.recorder.Count(EventKeys.IntelRequested), "Intel.Requested published");
            T.Eq("TestSteel", r.topic.DefName, "topic is the item only");
            T.Eq(SourcePolicies.ContAskFree, r.terms.continuationPolicyKey, "terms frozen from the source");
        }

        private static void SubmitRefusals()
        {
            TestNet n = new TestNet();
            NetworkActor fixer = n.AddFixer("Thorough", fee: Band.VeryHigh);
            n.pay.silver = 1;
            CommandResult r = n.ctx.Intel.Submit(SourceKey.ForActor(fixer.id), "TestRifle");
            T.Check(!r.ok && r.reasonKey == "NotEnoughBeaconSilver", "cannot afford → refused (" + r + ")");
            n.pay.silver = 100000;
            n.cat.notRequestable.Add("TestRifle");
            T.Eq("Ineligible", n.ctx.Intel.Submit(SourceKey.ForActor(fixer.id), "TestRifle").reasonKey, "ineligible topic refused");
            T.Eq("NotInCatalog", n.ctx.Intel.Submit(SourceKey.ForActor(fixer.id), "Nope").reasonKey, "unknown topic refused");
            T.Eq("SourceMissing", n.ctx.Intel.Submit(SourceKey.ForActor(new ActorId(9999)), "TestSteel").reasonKey, "unknown source refused");
            n.world.factions.Add(FakeWorld.Faction(50, "Hostile Band", "ludeon.rimworld", 4, true, ludeon: true));
            T.Eq("FactionHostile", n.ctx.Intel.Submit(SourceKey.ForFaction(50), "TestSteel").reasonKey, "a hostile faction is not a contact");
            T.Eq(0, n.ctx.intel.requests.Count, "no request created by any refusal");
            T.Eq(0, n.pay.charged, "nothing charged by any refusal");
        }

        private static void MultiLead()
        {
            TestNet n = new TestNet();
            NetworkActor fixer = n.AddFixer("Thorough");
            IntelRequest r = Submit(n, fixer);
            ForceLead();
            n.ResolveRound(r);
            T.Eq(IntelState.AwaitingDecision, r.state, "lead → the source asks the player");
            T.Eq(1, r.leads.Count, "lead A delivered");
            Lead a = n.ctx.intel.Get(r.leads[0]);
            Opportunity oa = n.ctx.opportunities.Get(a.opportunity);
            T.Check(oa != null && oa.state == OpportunityState.Materialized && oa.site != null, "lead A's opportunity materialized as a site");
            T.Check(oa.intel == r.id && oa.lead == a.id, "lead and opportunity are separate, linked entities");
            T.Eq(1, n.recorder.Count(EventKeys.IntelLeadDelivered), "Intel.LeadDelivered");
            T.Check(n.scheduler.Find(JobKinds.IntelRound, r.id.Value) == null, "no timer while waiting for the player");

            int chargedBefore = n.pay.charged;
            T.Check(n.ctx.Intel.Continue(r.id).ok, "ContinueIntel");
            T.Eq(chargedBefore, n.pay.charged, "askFree: continuing costs nothing");
            T.Eq(IntelState.Searching, r.state, "back to Searching");
            T.Eq(2, r.round, "round 2");

            // The player pursues lead A meanwhile: engaging its site does not stop the search.
            n.sites.maps.Add(oa.site.id);
            n.ctx.Opportunities.OnMapGenerated(oa.id);
            T.Eq(OpportunityState.Engaged, oa.state, "lead A engaged");
            T.Eq(LeadState.Pursued, a.state, "lead A pursued");
            T.Eq(IntelState.Searching, r.state, "pursuing lead A does not end the search");

            ForceLead();
            n.ResolveRound(r);
            T.Eq(2, r.leads.Count, "lead B from the same request");
            Lead b = n.ctx.intel.Get(r.leads[1]);
            T.Check(b.opportunity != a.opportunity, "lead B points at a different opportunity");
            T.Eq(2, b.round, "lead B came from round 2");
            T.Check(n.ctx.Intel.End(r.id).ok, "EndIntel");
            T.Eq(IntelState.Concluded, r.state, "Concluded");
            T.Eq(2, r.leads.Count, "ending the search keeps both leads");
            T.Check(n.ctx.intel.Get(r.leads[0]) != null && n.ctx.intel.Get(r.leads[1]) != null, "both leads still exist");
            T.Check(n.ledger.records.Exists(h => h.typeKey == EventKeys.IntelLeadDelivered), "history recorded the lead");
        }

        private static void AutoContinuation()
        {
            TestNet n = new TestNet();
            NetworkActor fixer = n.AddFixer("Patient");
            IntelRequest r = Submit(n, fixer);
            ForceLead();
            n.ResolveRound(r);
            T.Eq(IntelState.Searching, r.state, "auto policy keeps searching after a lead without asking");
            T.Eq(2, r.round, "next round started");
            T.Eq(2, r.segmentStartRound, "a new (free) segment started after the lead");
            int guard = 0;
            while (r.state == IntelState.Searching && guard++ < 40)
            {
                ForceLead();
                n.ResolveRound(r);
            }
            T.Eq(IntelState.Concluded, r.state, "the search ends at the source's round limit");
            T.Eq("RoundLimit", r.endReasonKey, "reason: round limit");
            T.Eq(r.terms.maxRounds, r.round, "exactly maxRounds rounds ran");
            T.Eq(r.terms.maxRounds, r.leads.Count, "one lead per round when every round finds one");
        }

        private static void ContinuationFees()
        {
            TestNet n = new TestNet();
            NetworkActor perRound = n.AddFixer("PayPerRound");
            IntelRequest r = Submit(n, perRound);
            ForceLead();
            n.ResolveRound(r);
            int before = n.pay.charged;
            T.Check(n.ctx.Intel.Continue(r.id).ok, "continue");
            T.Eq(r.terms.initialFee, n.pay.charged - before, "pay-per-round charges the fee again");
            T.Eq(r.terms.initialFee, r.terms.continuationFee, "frozen continuation fee");

            n.ctx.Intel.End(r.id);
            T.Check(n.ctx.Intel.Cancel(r.id).ok || r.state != IntelState.Searching, "only one search running below");
            NetworkActor discount = n.AddFixer("Discount");
            IntelRequest d = Submit(n, discount);
            ForceLead();
            n.ResolveRound(d);
            before = n.pay.charged;
            T.Check(n.ctx.Intel.Continue(d.id).ok, "continue (reduced)");
            int paid = n.pay.charged - before;
            T.Check(paid > 0 && paid < d.terms.initialFee, "reduced continuation fee (" + paid + " < " + d.terms.initialFee + ")");

            n.pay.silver = 0;
            ForceLead();
            n.ResolveRound(d);
            CommandResult cannot = n.ctx.Intel.CanContinue(d.id);
            T.Check(!cannot.ok && cannot.reasonKey == "NotEnoughBeaconSilver", "continuing needs the fee");
            T.Eq(IntelState.AwaitingDecision, d.state, "the search simply waits");
        }

        private static void NothingCredible()
        {
            TestNet n = new TestNet();
            NetworkActor fixer = n.AddFixer("Thorough");
            IntelRequest r = Submit(n, fixer);
            int paid = n.pay.charged;
            int guard = 0;
            while (r.state == IntelState.Searching && guard++ < 20)
            {
                IntelDevOverrides.forceNoLead = true;
                n.ResolveRound(r);
            }
            T.Eq(IntelState.Concluded, r.state, "an unproductive segment concludes");
            T.Eq("NothingCredible", r.endReasonKey, "no lead at all is a real outcome");
            T.Eq(r.terms.roundsPerSegment, r.round, "every round of the paid segment ran");
            T.Eq(0, n.pay.refunded, "fees are kept for rounds that ran");
            T.Eq(paid, n.pay.charged, "no extra charge for automatic rounds");
            IntelEvent concluded = (IntelEvent)n.recorder.events.FindLast(e => e.typeKey == EventKeys.IntelConcluded);
            T.Check(concluded != null && concluded.importance == Importance.Notable, "search with no lead at all is Notable");
            T.Check(n.ledger.records.Exists(h => h.typeKey == EventKeys.IntelConcluded && h.magnitudes.count == 0), "history: nothing credible found");
            T.Eq(1, n.summaries.Get(n.ctx.actors.PlayerProxyId).Lifetime("intel.nothing"), "player summary counter");
        }

        private static void ExchangeSingle()
        {
            TestNet n = new TestNet();
            NetworkActor exchange = n.ctx.actors.Exchange;
            T.Check(exchange != null && exchange.kind == ActorKind.Institution && exchange.Has<IntelSourceProfile>(), "the Exchange is an Institution Intel source");
            IntelRequest r = Submit(n, exchange);
            ForceLead();
            n.ResolveRound(r);
            T.Eq(IntelState.Concluded, r.state, "single-lead policy concludes after a lead");
            T.Eq("SourceDone", r.endReasonKey, "reason");
            T.Eq(1, r.leads.Count, "the lead is kept");
        }

        private static void CancelRefund()
        {
            TestNet n = new TestNet();
            NetworkActor fixer = n.AddFixer("Thorough");
            IntelRequest early = Submit(n, fixer);
            n.Advance((early.nextRoundDueTick - n.clock.Now) / 4);
            T.Check(n.ctx.Intel.Cancel(early.id).ok, "cancel early");
            T.Eq(IntelState.Cancelled, early.state, "Cancelled");
            T.Eq(early.terms.initialFee * early.terms.cancelRefundPercent / 100, n.pay.refunded, "early cancel refunds the policy's share");
            T.Check(n.scheduler.Find(JobKinds.IntelRound, early.id.Value) == null, "running round job removed");
            int refundedBefore = n.pay.refunded;
            IntelRequest late = Submit(n, fixer);
            n.Advance((late.nextRoundDueTick - n.clock.Now) * 3 / 4);
            T.Check(n.ctx.Intel.Cancel(late.id).ok, "cancel late");
            T.Eq(refundedBefore, n.pay.refunded, "late cancel refunds nothing");
            T.Check(!n.ctx.Intel.CanCancel(late.id).ok, "cannot cancel twice");
        }

        private static void InvalidateTopicMissing()
        {
            TestNet n = new TestNet();
            NetworkActor fixer = n.AddFixer("Thorough");
            IntelRequest r = Submit(n, fixer, "ModX_Weirdium");
            n.cat.items.Remove("ModX_Weirdium"); // the item's mod was removed
            n.ResolveRound(r);
            T.Eq(IntelState.Invalidated, r.state, "missing topic → Invalidated");
            T.Eq("TopicMissing", r.endReasonKey, "reason");
            T.Eq(r.terms.initialFee, n.pay.refunded, "the running round's fee is refunded in full");
            IntelEvent e = (IntelEvent)n.recorder.events.FindLast(x => x.typeKey == EventKeys.IntelInvalidated);
            T.Check(e != null && e.silver == r.terms.initialFee, "Intel.Invalidated carries the refund (letter)");
            T.Check(n.ledger.records.Exists(h => h.typeKey == EventKeys.IntelInvalidated), "history keeps the snapshot story");
        }

        private static void ThingFailure()
        {
            TestNet n = new TestNet();
            NetworkActor fixer = n.AddFixer("Thorough");
            IntelRequest r = Submit(n, fixer, "ModX_Weirdium");
            n.sites.failDefs.Add("ModX_Weirdium");
            ForceLead();
            n.ResolveRound(r);
            T.Eq(IntelState.Invalidated, r.state, "Thing creation failure → Invalidated");
            T.Eq("ItemCannotBeProduced", r.endReasonKey, "reason");
            T.Eq(r.TotalPaid(), n.pay.refunded, "full refund");
            T.Check(n.cat.unusable.Contains("ModX_Weirdium"), "def marked unusable for the session");
            string reason;
            T.Check(!n.cat.IsRequestable("ModX_Weirdium", out reason), "and no longer requestable");
            T.Eq(0, r.leads.Count, "no lead delivered");
        }

        private static void CommsLoss()
        {
            TestNet n = new TestNet();
            NetworkActor fixer = n.AddFixer("Thorough");
            IntelRequest r = Submit(n, fixer);
            n.comms.usable = false; // solar flare / power loss
            ForceLead();
            n.ResolveRound(r);
            T.Eq(1, r.leads.Count, "the lead still arrives without a console");
            T.Eq(IntelState.AwaitingDecision, r.state, "the search waits");
            int charged = n.pay.charged;
            CommandResult c = n.ctx.Intel.Continue(r.id);
            T.Check(!c.ok && c.reasonKey == "NoUsableCommsConsole", "continue refused without comms");
            T.Check(!n.ctx.Intel.End(r.id).ok, "end refused without comms");
            T.Eq(charged, n.pay.charged, "nothing charged");
            T.Eq(IntelState.AwaitingDecision, r.state, "nothing lost or changed");
            n.comms.usable = true;
            T.Check(n.ctx.Intel.Continue(r.id).ok, "works again when comms return");
        }

        private static void Determinism()
        {
            // Two identical worlds; in the second, the request is saved and reloaded through Scribe before
            // the round resolves. Both must produce the same round (SIMULATION § 6.4; Spike S8 logic).
            TestNet a = new TestNet(777);
            TestNet b = new TestNet(777);
            a.world.factions.Add(FakeWorld.Faction(20, "Red Hands", "someone.weirdmod", 5, true));
            b.world.factions.Add(FakeWorld.Faction(20, "Red Hands", "someone.weirdmod", 5, true));
            NetworkActor fa = a.AddFixer("Thorough");
            NetworkActor fb = b.AddFixer("Thorough");
            IntelRequest ra = Submit(a, fa, "ModX_Weirdium");
            IntelRequest rb = Submit(b, fb, "ModX_Weirdium");
            T.Eq(ra.seed, rb.seed, "same seed");
            T.Eq(ra.nextRoundDueTick, rb.nextRoundDueTick, "same committed round duration");

            IntelRequest reloaded = RoundTrip(rb);
            b.ctx.intel.Remove(rb);
            b.ctx.intel.Add(reloaded);
            rb = reloaded;

            // Resolve several rounds without forcing anything: the draws decide.
            for (int i = 0; i < 6; i++)
            {
                if (ra.state == IntelState.AwaitingDecision) { a.ctx.Intel.Continue(ra.id); b.ctx.Intel.Continue(rb.id); }
                if (ra.state != IntelState.Searching) break;
                a.ResolveRound(ra);
                b.ResolveRound(rb);
                if (i == 1)
                {
                    IntelRequest again = RoundTrip(rb);
                    b.ctx.intel.Remove(rb);
                    b.ctx.intel.Add(again);
                    rb = again;
                }
            }
            T.Eq(ra.state, rb.state, "same state");
            T.Eq(ra.round, rb.round, "same round");
            T.Eq(ra.leads.Count, rb.leads.Count, "same number of leads");
            for (int i = 0; i < Math.Min(ra.leads.Count, rb.leads.Count); i++)
            {
                Lead la = a.ctx.intel.Get(ra.leads[i]);
                Lead lb = b.ctx.intel.Get(rb.leads[i]);
                Opportunity oa = a.ctx.opportunities.Get(la.opportunity);
                Opportunity ob = b.ctx.opportunities.Get(lb.opportunity);
                T.Eq(la.divergence, lb.divergence, "same hidden divergence, lead " + i);
                T.Eq(la.reported.estimateLow + "-" + la.reported.estimateHigh, lb.reported.estimateLow + "-" + lb.reported.estimateHigh, "same reported amount");
                T.Eq(la.reported.threatBand, lb.reported.threatBand, "same reported threat");
                T.Eq(oa.TargetCount, ob.TargetCount, "same true amount");
                T.Eq(oa.sourceContext.kind, ob.sourceContext.kind, "same source context");
                T.Eq(oa.threat.points, ob.threat.points, "same threat points");
                T.Eq(oa.location.tileId, ob.location.tileId, "same tile");
                T.Eq(oa.payload.Count, ob.payload.Count, "same cargo lines");
            }
            T.Check(ra.leads.Count > 0 || ra.state == IntelState.Concluded, "the comparison exercised at least one outcome");
        }

        public static IntelRequest RoundTrip(IntelRequest r)
        {
            string path = Path.Combine(Path.GetTempPath(), "thenetwork-intel-" + Guid.NewGuid().ToString("N") + ".xml");
            Scribe.saver.InitSaving(path, "request");
            try { r.ExposeData(); } finally { Scribe.saver.FinalizeSaving(); }
            IntelRequest copy = new IntelRequest();
            Scribe.loader.InitLoading(path);
            try { copy.ExposeData(); } finally { Scribe.loader.FinalizeLoading(); }
            File.Delete(path);
            return copy;
        }

        private static void StaleJob()
        {
            TestNet n = new TestNet();
            NetworkActor fixer = n.AddFixer("Thorough");
            IntelRequest r = Submit(n, fixer);
            n.ctx.Intel.RunRound(new ScheduledJob { kind = JobKinds.IntelRound, target = r.id.Value, arg = 7 });
            T.Eq(1, r.round, "a job for another round does nothing");
            T.Eq(IntelState.Searching, r.state, "state unchanged");
            n.ctx.Intel.RunRound(new ScheduledJob { kind = JobKinds.IntelRound, target = 99999, arg = 1 });
            T.Check(true, "a job for a missing request does nothing");
        }

        private static void Closing()
        {
            TestNet n = new TestNet();
            NetworkActor fixer = n.AddFixer("Thorough");
            IntelRequest r = Submit(n, fixer);
            ForceLead();
            n.ResolveRound(r);
            n.ctx.Intel.End(r.id);
            Lead l = n.ctx.intel.Get(r.leads[0]);
            Opportunity o = n.ctx.opportunities.Get(l.opportunity);
            n.Advance(Ticks.PerDay * 3);
            T.Eq(IntelState.Concluded, r.state, "not closed while its lead is open");
            n.ctx.Opportunities.DevExpire(o);
            n.Advance(Ticks.PerDay * 3);
            T.Eq(OpportunityState.Closed, o.state, "opportunity closed a day after its end");
            T.Eq(LeadState.Closed, l.state, "lead closed with it");
            T.Eq(IntelState.Closed, r.state, "request closed after every lead closed");
        }

        private static void FactionContact()
        {
            TestNet n = new TestNet();
            n.world.factions.Add(FakeWorld.Faction(30, "Friendly Traders", "ludeon.rimworld", 4, false, ludeon: true));
            int actorsBefore = n.ctx.actors.actors.Count;
            SearchTerms preview = n.ctx.Intel.PreviewTerms(SourceKey.ForFaction(30), "TestSteel");
            T.Check(preview != null && preview.initialFee > 0, "a faction contact has derived terms");
            T.Eq(actorsBefore, n.ctx.actors.actors.Count, "previewing creates no proxy");
            CommandResult r = n.ctx.Intel.Submit(SourceKey.ForFaction(30), "TestSteel");
            T.Check(r.ok, "submit to a faction contact (" + r + ")");
            NetworkActor proxy = n.ctx.actors.ProxyForFaction(30);
            T.Check(proxy != null && proxy.kind == ActorKind.FactionProxy && proxy.Get<IntelSourceProfile>().derived, "proxy created lazily with a derived profile");
            IntelRequest req = n.Only();
            n.world.factions.Clear(); // the faction vanished
            n.ResolveRound(req);
            T.Eq(IntelState.Invalidated, req.state, "a vanished source invalidates its search");
            T.Eq("SourceGone", req.endReasonKey, "reason");
            T.Eq(1, n.ctx.Actors.ReconcileFactionProxies(), "proxy ends (FactionVanished)");
            T.Eq(ActorStatus.Destroyed, proxy.status, "degrades safely");
        }

        private static readonly string[] QuantityWords = { "quantity", "amount", "count", "stack", "minimum", "howmany", "requested" };

        private static void CheckNames(Type type, string what)
        {
            foreach (FieldInfo f in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (f.Name.Contains("<")) continue;
                foreach (string w in QuantityWords) T.Check(f.Name.ToLowerInvariant().IndexOf(w, StringComparison.Ordinal) < 0, what + " field '" + f.Name + "' looks like a quantity");
            }
            foreach (PropertyInfo p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                foreach (string w in QuantityWords) T.Check(p.Name.ToLowerInvariant().IndexOf(w, StringComparison.Ordinal) < 0, what + " property '" + p.Name + "' looks like a quantity");
            }
        }

        private static void CheckParams(Type type, string what)
        {
            foreach (MethodInfo m in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                foreach (ParameterInfo p in m.GetParameters())
                {
                    T.Check(p.ParameterType != typeof(int) || p.Name == "ticks" || p.Name == "now" || p.Name == "round",
                        what + "." + m.Name + " takes an int '" + p.Name + "' (Intel commands take no numbers)");
                    foreach (string w in QuantityWords) T.Check(p.Name.ToLowerInvariant().IndexOf(w, StringComparison.Ordinal) < 0, what + "." + m.Name + " parameter '" + p.Name + "' looks like a quantity");
                }
            }
        }

        private static void NoQuantity()
        {
            // The request, its topic and frozen terms, the command surface and the Intel events carry no
            // quantity of any kind (DATA_MODEL § 8). Money is "silver"/"fee"; the lead's report of how much
            // the SOURCE thinks is there is perception of the opportunity, not an input.
            CheckNames(typeof(IntelRequest), "IntelRequest");
            CheckNames(typeof(IntelTopic), "IntelTopic");
            CheckNames(typeof(SearchTerms), "SearchTerms");
            CheckNames(typeof(MoneyRecord), "MoneyRecord");
            CheckNames(typeof(SourceKey), "SourceKey");
            CheckNames(typeof(IntelEvent), "IntelEvent");
            foreach (string m in new[] { "Submit", "CanSubmit", "Continue", "CanContinue", "End", "CanEnd", "Cancel", "CanCancel", "PreviewTerms" })
            {
                MethodInfo mi = typeof(IntelService).GetMethod(m);
                T.Check(mi != null, "IntelService." + m + " exists");
                foreach (ParameterInfo p in mi.GetParameters())
                {
                    T.Check(p.ParameterType == typeof(SourceKey) || p.ParameterType == typeof(string) || p.ParameterType == typeof(IntelRequestId), "IntelService." + m + " parameter '" + p.Name + "' is " + p.ParameterType.Name);
                }
            }
            CheckParams(typeof(NetworkCommands), "NetworkCommands");
            T.Eq(2, typeof(IntelService).GetMethod("Submit").GetParameters().Length, "SubmitIntel(source, topic): two parameters");
            // Fee and duration policies read unit-level item facts only.
            foreach (MethodInfo mi in typeof(SourcePolicies).GetMethods(BindingFlags.Static | BindingFlags.Public))
            {
                foreach (ParameterInfo p in mi.GetParameters())
                {
                    foreach (string w in QuantityWords) T.Check(p.Name.ToLowerInvariant().IndexOf(w, StringComparison.Ordinal) < 0, "SourcePolicies." + mi.Name + " parameter '" + p.Name + "'");
                }
            }
        }
    }
}
