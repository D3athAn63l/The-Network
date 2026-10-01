using System.Collections.Generic;
using System.Text;
using RimWorld;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Contractors;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Opportunities;
using TheNetwork.History;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using Verse;

namespace TheNetwork.Core
{
    public enum ValidationMode
    {
        OnLoad,
        Full
    }

    public sealed class ValidationReport
    {
        public readonly List<string> Findings = new List<string>();
        public int Repairs;
        public readonly Dictionary<string, int> MissingDefs = new Dictionary<string, int>();

        public void Add(string finding, bool repaired)
        {
            Findings.Add(finding);
            if (repaired) Repairs++;
        }

        public string Summary()
        {
            return Findings.Count == 0 ? "validation clean" : Findings.Count + " validation findings (" + Repairs + " repaired)";
        }

        public string Full()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[TheNetwork] Validation: " + Summary());
            for (int i = 0; i < Findings.Count; i++) sb.AppendLine("  " + Findings[i]);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Validators (DEBUGGING § 4): ID uniqueness, orphans, external references, scheduler/entity
    /// agreement, state sanity and caps. Runs on the first tick after a load (world changes such as
    /// refunds happen then, inside the game tick) and on demand from a dev action. Repairs only where
    /// the repair is safe; everything else is reported.
    /// </summary>
    public static class NetValidator
    {
        public static ValidationReport Run(NetworkRuntime rt, ValidationMode mode)
        {
            ValidationReport report = new ValidationReport();
            NetworkState s = rt.State;
            DomainContext ctx = rt.Ctx;
            CheckIds(rt, report);
            CheckExternalRefs(rt, report);
            CheckOrphansAndStates(rt, report);
            CheckScheduler(rt, report);
            CheckSpatial(rt, report);
            CheckCareers(rt, report);
            int endedProxies = ctx.Actors.ReconcileFactionProxies();
            if (endedProxies > 0) report.Add(endedProxies + " faction proxies ended (FactionVanished).", true);
            CheckCaps(rt, report);
            foreach (KeyValuePair<string, int> kv in report.MissingDefs)
            {
                NetLog.Info(LogCategory.Compat, kv.Value + " references to missing ThingDef '" + kv.Key + "' were invalidated.");
            }
            if (mode == ValidationMode.Full || report.Findings.Count > 0) NetLog.Info(LogCategory.Kernel, mode == ValidationMode.Full ? report.Full() : report.Summary());
            StateVersion.Bump();
            return report;
        }

        private static void CheckIds(NetworkRuntime rt, ValidationReport report)
        {
            NetworkState s = rt.State;
            HashSet<int> seen = new HashSet<int>();
            int next = rt.Root.ids.PeekNextId;
            for (int i = 0; i < s.actors.actors.Count; i++) Check(s.actors.actors[i].id.Value, "actor", seen, next, report, e => s.actors.actors[e].quarantinedReason = "DuplicateId", i);
            for (int i = 0; i < s.characters.characters.Count; i++) Check(s.characters.characters[i].id.Value, "character", seen, next, report, e => s.characters.characters[e].quarantinedReason = "DuplicateId", i);
            for (int i = 0; i < s.intel.requests.Count; i++) Check(s.intel.requests[i].id.Value, "intel", seen, next, report, e => s.intel.requests[e].quarantinedReason = "DuplicateId", i);
            for (int i = 0; i < s.intel.leads.Count; i++) Check(s.intel.leads[i].id.Value, "lead", seen, next, report, e => s.intel.leads[e].quarantinedReason = "DuplicateId", i);
            for (int i = 0; i < s.opportunities.opportunities.Count; i++) Check(s.opportunities.opportunities[i].id.Value, "opportunity", seen, next, report, e => s.opportunities.opportunities[e].quarantinedReason = "DuplicateId", i);
            for (int i = 0; i < s.contracts.contracts.Count; i++) Check(s.contracts.contracts[i].id.Value, "contract", seen, next, report, e => s.contracts.contracts[e].quarantinedReason = "DuplicateId", i);
            for (int i = 0; i < s.contracts.offers.Count; i++) Check(s.contracts.offers[i].id.Value, "offer", seen, next, report, e => s.contracts.offers[e].quarantinedReason = "DuplicateId", i);
            for (int i = 0; i < s.operations.operations.Count; i++) Check(s.operations.operations[i].id.Value, "operation", seen, next, report, e => s.operations.operations[e].quarantinedReason = "DuplicateId", i);
        }

        private static void Check(int id, string kind, HashSet<int> seen, int next, ValidationReport report, System.Action<int> quarantine, int index)
        {
            if (id <= 0)
            {
                report.Add(kind + " with no id at index " + index, false);
                return;
            }
            if (!seen.Add(id))
            {
                quarantine(index);
                report.Add("Duplicate id " + id + " (" + kind + "): quarantined.", true);
            }
            if (id >= next) report.Add("Id " + id + " (" + kind + ") is not below nextId " + next + ".", false);
        }

        private static void CheckExternalRefs(NetworkRuntime rt, ValidationReport report)
        {
            NetworkState s = rt.State;
            DomainContext ctx = rt.Ctx;
            List<IntelRequest> requests = new List<IntelRequest>(s.intel.requests);
            for (int i = 0; i < requests.Count; i++)
            {
                IntelRequest r = requests[i];
                if (!r.IsActive || r.quarantinedReason != null) continue;
                string def = r.topic.DefName;
                if (DefResolver<ThingDef>.Get(def) == null)
                {
                    Count(report, def);
                    ctx.Intel.Invalidate(r, "TopicMissing", false);
                    report.Add("Intel " + r.id + ": topic '" + def + "' missing; invalidated with refund.", true);
                }
                else if (!ctx.Actors.SourceStillExists(r.source))
                {
                    ctx.Intel.Invalidate(r, "SourceGone", false);
                    report.Add("Intel " + r.id + ": source " + r.source + " gone; invalidated with refund.", true);
                }
            }
            List<string> contractFindings = new List<string>();
            ctx.Procurement.InvalidateMissing(def =>
            {
                bool ok = DefResolver<ThingDef>.Get(def) != null;
                if (!ok) Count(report, def);
                return ok;
            }, contractFindings);
            for (int i = 0; i < contractFindings.Count; i++) report.Add(contractFindings[i], true);
            List<Opportunity> opps = new List<Opportunity>(s.opportunities.opportunities);
            for (int i = 0; i < opps.Count; i++)
            {
                Opportunity o = opps[i];
                if (o.IsTerminal || o.quarantinedReason != null) continue;
                ItemPayload t = o.Target;
                if (t != null && DefResolver<ThingDef>.Get(t.thing?.defName) == null)
                {
                    Count(report, t.thing?.defName);
                    ctx.Opportunities.Invalidate(o, "DefMissing");
                    report.Add("Opportunity " + o.id + ": target def missing; invalidated.", true);
                    continue;
                }
                if (o.location != null && !o.location.IsValidNow)
                {
                    ctx.Opportunities.Invalidate(o, "TileLayerMissing");
                    report.Add("Opportunity " + o.id + ": tile layer missing; invalidated.", true);
                    continue;
                }
                if (o.state == OpportunityState.Materialized && !rt.SiteAdapter.SiteExists(o.site))
                {
                    // Reconciliation: vanished while the mod was away or without a callback.
                    rt.Scheduler.Schedule(JobKinds.OppSample, rt.Clock.Now + 1, o.id.Value);
                    report.Add("Opportunity " + o.id + ": site missing; reconciliation scheduled.", true);
                }
                else if (o.state == OpportunityState.Engaged && !rt.SiteAdapter.SiteHasMap(o.site))
                {
                    rt.Scheduler.Schedule(JobKinds.OppSample, rt.Clock.Now + 1, o.id.Value);
                    report.Add("Opportunity " + o.id + ": engaged but no map; resolution scheduled.", true);
                }
            }
        }

        private static void Count(ValidationReport report, string def)
        {
            if (def == null) def = "(null)";
            int n;
            report.MissingDefs.TryGetValue(def, out n);
            report.MissingDefs[def] = n + 1;
        }

        private static void CheckOrphansAndStates(NetworkRuntime rt, ValidationReport report)
        {
            NetworkState s = rt.State;
            int now = rt.Clock.Now;
            for (int i = 0; i < s.intel.leads.Count; i++)
            {
                Lead l = s.intel.leads[i];
                if (s.intel.Get(l.intel) == null) report.Add("Lead " + l.id + " has no request " + l.intel + ".", false);
                if (l.state != LeadState.Closed && s.opportunities.Get(l.opportunity) == null)
                {
                    l.state = LeadState.Closed;
                    l.stateTick = now;
                    report.Add("Lead " + l.id + ": opportunity archived or missing; lead closed.", true);
                }
            }
            for (int i = 0; i < s.intel.requests.Count; i++)
            {
                IntelRequest r = s.intel.requests[i];
                for (int k = r.leads.Count - 1; k >= 0; k--)
                {
                    if (s.intel.Get(r.leads[k]) == null)
                    {
                        report.Add("Intel " + r.id + ": lead " + r.leads[k] + " missing (kept as archived).", false);
                    }
                }
                if (r.IsTerminal && r.endedTick < 0)
                {
                    r.endedTick = now;
                    report.Add("Intel " + r.id + ": terminal without end tick; set.", true);
                }
                if (s.actors.Get(r.source) == null) report.Add("Intel " + r.id + ": source actor " + r.source + " missing.", false);
            }
            for (int i = 0; i < s.opportunities.opportunities.Count; i++)
            {
                Opportunity o = s.opportunities.opportunities[i];
                if (o.lead.IsValid && s.intel.Get(o.lead) == null && o.state != OpportunityState.Closed) report.Add("Opportunity " + o.id + ": lead " + o.lead + " missing.", false);
                if (o.IsTerminal && o.endedTick < 0)
                {
                    o.endedTick = now;
                    report.Add("Opportunity " + o.id + ": terminal without end tick; set.", true);
                }
            }
            for (int i = 0; i < s.actors.actors.Count; i++)
            {
                NetworkActor a = s.actors.actors[i];
                if (a.bindings.embodies.IsValid && s.characters.Get(a.bindings.embodies) == null) report.Add("Actor " + a.id + ": embodied character missing.", false);
            }
            int players = 0;
            for (int i = 0; i < s.actors.actors.Count; i++) if (s.actors.actors[i].kind == ActorKind.PlayerProxy) players++;
            if (players != 1) report.Add("Expected exactly one PlayerProxy, found " + players + ".", false);
        }

        private static void CheckScheduler(NetworkRuntime rt, ValidationReport report)
        {
            NetworkState s = rt.State;
            NetScheduler sch = rt.Scheduler;
            int now = rt.Clock.Now;
            for (int i = 0; i < s.intel.requests.Count; i++)
            {
                IntelRequest r = s.intel.requests[i];
                if (r.quarantinedReason != null) continue;
                if (r.state == IntelState.Searching)
                {
                    ScheduledJob j = sch.Find(JobKinds.IntelRound, r.id.Value);
                    if (j == null || j.arg != r.round)
                    {
                        int due = r.nextRoundDueTick > now ? r.nextRoundDueTick : now + 1;
                        sch.Schedule(JobKinds.IntelRound, due, r.id.Value, r.round);
                        r.nextRoundDueTick = due;
                        report.Add("Intel " + r.id + ": round job missing; recreated for round " + r.round + ".", true);
                    }
                }
                else if (r.IsTerminal && r.state != IntelState.Closed && r.closeDueTick > 0 && !sch.Has(JobKinds.IntelClose, r.id.Value))
                {
                    sch.Schedule(JobKinds.IntelClose, r.closeDueTick > now ? r.closeDueTick : now + 1, r.id.Value);
                    report.Add("Intel " + r.id + ": close job missing; recreated.", true);
                }
                else if (r.IsTerminal && r.state != IntelState.Closed && r.closeDueTick <= 0)
                {
                    rt.Ctx.Intel.TryScheduleClose(r);
                }
                if (rt.Ctx.Intel.HasPendingRefund(r) && !sch.Has(JobKinds.RefundRetry, r.id.Value))
                {
                    sch.Schedule(JobKinds.RefundRetry, now + 1, r.id.Value);
                    report.Add("Intel " + r.id + ": pending refund had no retry job; recreated.", true);
                }
            }
            for (int i = 0; i < s.opportunities.opportunities.Count; i++)
            {
                Opportunity o = s.opportunities.opportunities[i];
                if (o.quarantinedReason != null) continue;
                if ((o.state == OpportunityState.Engaged || o.state == OpportunityState.Materialized) && !sch.Has(JobKinds.OppSample, o.id.Value))
                {
                    int due = o.state == OpportunityState.Engaged ? now + JobKinds.SamplePeriod : o.expiresTick + 2 * Ticks.PerDay;
                    sch.Schedule(JobKinds.OppSample, due > now ? due : now + 1, o.id.Value);
                    report.Add("Opportunity " + o.id + ": sample job missing; recreated.", true);
                }
                if (o.IsTerminal && o.state != OpportunityState.Closed && !sch.Has(JobKinds.OppClose, o.id.Value))
                {
                    sch.Schedule(JobKinds.OppClose, o.closeDueTick > now ? o.closeDueTick : now + 1, o.id.Value);
                    report.Add("Opportunity " + o.id + ": close job missing; recreated.", true);
                }
            }
            List<string> contractJobs = new List<string>();
            rt.Ctx.Procurement.EnsureJobs(contractJobs);
            for (int i = 0; i < contractJobs.Count; i++) report.Add(contractJobs[i], true);
            List<string> upkeepJobs = new List<string>();
            rt.Ctx.Upkeep.EnsureUpkeepJobs(upkeepJobs);
            for (int i = 0; i < upkeepJobs.Count; i++) report.Add(upkeepJobs[i], true);
            // Jobs whose target is gone.
            List<ScheduledJob> orphans = new List<ScheduledJob>();
            foreach (ScheduledJob j in sch.AllJobs)
            {
                if (!sch.IsKnownKind(j.kind)) continue;
                bool live = true;
                if (j.kind.StartsWith("intel.", System.StringComparison.Ordinal) || j.kind == JobKinds.RefundRetry) live = s.intel.Get(new IntelRequestId(j.target)) != null;
                else if (j.kind.StartsWith("opp.", System.StringComparison.Ordinal)) live = s.opportunities.Get(new OpportunityId(j.target)) != null;
                else if (j.kind.StartsWith("contract.", System.StringComparison.Ordinal) || j.kind.StartsWith("operation.", System.StringComparison.Ordinal) || j.kind.StartsWith("consequence.", System.StringComparison.Ordinal)) live = rt.Ctx.Procurement.JobTargetExists(j);
                else if (j.kind == ContractorService.UpkeepJob)
                {
                    NetworkActor a = s.actors.Get(new ActorId(j.target));
                    live = a != null && a.status == ActorStatus.Active;
                }
                if (!live) orphans.Add(j);
            }
            for (int i = 0; i < orphans.Count; i++)
            {
                sch.Cancel(orphans[i].kind, orphans[i].target);
                report.Add("Job " + orphans[i] + ": target gone; removed.", true);
            }
        }

        /// <summary>Spatial reconciliation (SPATIAL § 9): anchors, destinations and operation bindings.</summary>
        private static void CheckSpatial(NetworkRuntime rt, ValidationReport report)
        {
            if (rt.Ctx.Spatial == null) return;
            List<string> findings = new List<string>();
            rt.Ctx.Spatial.Validate(findings);
            for (int i = 0; i < findings.Count; i++) report.Add(findings[i], true);
        }

        /// <summary>Career reconciliation (ADR-046): score and band agree, bounds hold, no finished operation missed its career result.</summary>
        private static void CheckCareers(NetworkRuntime rt, ValidationReport report)
        {
            if (rt.Ctx.Career == null) return;
            List<string> findings = new List<string>();
            rt.Ctx.Career.Validate(findings);
            for (int i = 0; i < findings.Count; i++) report.Add(findings[i], true);
        }

        private static void CheckCaps(NetworkRuntime rt, ValidationReport report)
        {
            NetworkState s = rt.State;
            int notable = 0, major = 0;
            for (int i = 0; i < s.history.records.Count; i++)
            {
                if (s.history.records[i].importance == Importance.Notable) notable++;
                else if (s.history.records[i].importance >= Importance.Major) major++;
            }
            if (notable > RetentionPolicy.NotableGlobalCap || major > RetentionPolicy.MajorGlobalCap)
            {
                rt.History.EnforceGlobalCaps();
                report.Add("History over its caps; oldest records dropped.", true);
            }
            if (s.journal.entries.Count > EventJournal.HardCap)
            {
                s.journal.Prune(rt.Clock.Now);
                report.Add("Journal over its hard cap; pruned.", true);
            }
        }

        public static string CountsVsCaps(NetworkRuntime rt)
        {
            NetworkState s = rt.State;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[TheNetwork] Counts vs caps");
            sb.AppendLine("  actors: " + s.actors.actors.Count + ", characters: " + s.characters.characters.Count + ", cast entries: " + s.cast.entries.Count);
            sb.AppendLine("  intel requests: " + s.intel.requests.Count + ", leads: " + s.intel.leads.Count + ", opportunities: " + s.opportunities.opportunities.Count);
            sb.AppendLine("  contracts: " + s.contracts.contracts.Count + ", offers: " + s.contracts.offers.Count + ", operations: " + s.operations.operations.Count + ", pending consequences: " + s.consequences.pending.Count);
            sb.AppendLine("  relations: " + s.relations.Count + " / " + Domain.Relations.RelationService.GlobalCap + ", knowledge books: " + s.knowledge.books.Count);
            sb.AppendLine("  history records: " + s.history.records.Count + " (Notable cap " + RetentionPolicy.NotableGlobalCap + ", Major cap " + RetentionPolicy.MajorGlobalCap + "), dropped so far " + s.history.droppedCount);
            sb.AppendLine("  journal: " + s.journal.entries.Count + " / " + EventJournal.HardCap + " (dropped " + s.journal.droppedCount + ")");
            sb.AppendLine("  scheduler jobs: " + rt.Scheduler.Count + ", quarantine: " + s.diagnostics.quarantine.Count + ", failed consumers: " + s.diagnostics.failedConsumers.Count);
            return sb.ToString();
        }
    }
}
