using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LudeonTK;
using RimWorld;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Catalog;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Domain.Ports;
using TheNetwork.Integration;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Settings;
using UnityEngine;
using Verse;

namespace TheNetwork.Diagnostics
{
    /// <summary>
    /// Dev actions (DEBUGGING § 3), in the vanilla debug menu under "The Network". No Harmony.
    /// Each logs what it did and bumps StateVersion. They exist so the owner never has to wait
    /// in-game days to test a flow.
    /// </summary>
    public static partial class NetworkDevActions
    {
        private const string Cat = "The Network";

        private static NetworkRuntime Rt
        {
            get
            {
                NetworkRuntime rt = NetworkRuntime.Current;
                if (rt == null) Messages.Message("[TheNetwork] No Network runtime in this game.", MessageTypeDefOf.RejectInput, false);
                return rt;
            }
        }

        /// <summary>
        /// The runtime for an action that changes Network state: started first if needed, and refused
        /// (nothing done) when start-up failed this session.
        /// </summary>
        private static NetworkRuntime Live
        {
            get
            {
                NetworkRuntime rt = Rt;
                if (rt == null) return null;
                if (!rt.EnsureStarted())
                {
                    Messages.Message("[TheNetwork] Start-up failed this session (" + (rt.Session.FailedStage ?? "?") + "); nothing was changed. See the log.", MessageTypeDefOf.RejectInput, false);
                    return null;
                }
                return rt;
            }
        }

        private static void Out(string text)
        {
            Log.Message(text);
            StateVersion.Bump();
        }

        // ================================================================== inspect

        [DebugAction(Cat, "Status (bootstrap/inspect)", allowedGameStates = AllowedGameStates.Playing)]
        public static void Status()
        {
            NetworkWorldComponent root = NetworkWorldComponent.Instance;
            NetworkRuntime rt = Rt;
            if (root == null || rt == null) return;
            rt.EnsureStarted();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[TheNetwork] Status");
            sb.AppendLine("  session: " + rt.Session.State + (rt.Session.IsFailed ? " during " + rt.Session.FailedStage + " (" + rt.Session.FailureMessage + ")" : ""));
            sb.AppendLine("  save format " + root.saveVersion + ", created with " + root.createdWithModVersion + ", bootstrapped " + root.bootstrapped + ", seed " + root.networkSeed + (root.preparedForRemoval ? ", PREPARED FOR REMOVAL" : ""));
            sb.AppendLine("  next id " + root.ids.PeekNextId + ", next event " + root.ids.PeekNextEventSeq + ", next job " + root.ids.PeekNextJobSeq + ", next due tick " + rt.Scheduler.NextDueTick + " (now " + rt.Clock.Now + ")");
            sb.Append(NetValidator.CountsVsCaps(rt));
            string reason;
            sb.AppendLine("  comms: " + (rt.Ctx.Intel.CommsOk(out reason) ? "usable" : "blocked (" + reason + ")") + (IntelDevOverrides.commsGateOverride ? " [dev override ON]" : "") + (IntelDevOverrides.waiveFees ? " [fees waived]" : ""));
            sb.AppendLine("  degraded subsystems: " + (rt.State.diagnostics.degradedSubsystems.Count == 0 ? "none" : string.Join(", ", rt.State.diagnostics.degradedSubsystems.ToArray())));
            Out(sb.ToString());
        }

        [DebugAction(Cat, "Inspect actor…", allowedGameStates = AllowedGameStates.Playing)]
        public static void InspectActor()
        {
            NetworkRuntime rt = Rt;
            if (rt == null) return;
            List<FloatMenuOption> opts = new List<FloatMenuOption>();
            foreach (NetworkActor a in rt.State.actors.actors)
            {
                NetworkActor captured = a;
                opts.Add(new FloatMenuOption(a.ToString(), () => Out(DescribeActor(rt, captured))));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        private static string DescribeActor(NetworkRuntime rt, NetworkActor a)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[TheNetwork] " + a + " status " + a.status + " fame " + a.reputation.fame + " (score " + a.reputation.score + ") provenance " + a.provenance.source + " template " + (a.provenance.templateId ?? "-"));
            if (a.bindings.faction != null) sb.AppendLine("  faction " + a.bindings.faction.NameSnapshot + " (load " + a.bindings.faction.loadId + ")");
            if (a.bindings.embodies.IsValid) sb.AppendLine("  embodies " + a.bindings.embodies + " '" + rt.State.characters.Get(a.bindings.embodies)?.name.Display + "'");
            foreach (ActorComponent c in a.components) sb.AppendLine("  component " + c.Key);
            IntelSourceProfile p = a.Get<IntelSourceProfile>();
            if (p != null) sb.AppendLine("  intel: speed " + p.speedBand + ", reliability " + p.reliabilityBand + ", fee " + p.feeBand + " (" + p.feePolicyKey + "), continuation " + p.continuationPolicyKey + (p.derived ? " [derived]" : ""));
            History.ActorRecordSummary sum = rt.State.summaries.Get(a.id);
            if (sum != null) foreach (History.DeedCounter d in sum.deeds) sb.AppendLine("  deed " + d.key + " = " + d.lifetime);
            return sb.ToString();
        }

        // ================================================================== catalog

        [DebugAction(Cat, "Rebuild item catalog", allowedGameStates = AllowedGameStates.Playing)]
        public static void RebuildCatalog()
        {
            CatalogCache.Reset();
            ItemCatalog c = CatalogCache.Get();
            Out("[TheNetwork] Catalog rebuilt: " + c.Count + " defs in " + c.BuildMs.ToString("0.0") + " ms.");
        }

        [DebugAction(Cat, "Catalog report (CSV)", allowedGameStates = AllowedGameStates.Playing)]
        public static void CatalogReport()
        {
            ItemCatalog c = CatalogCache.Get();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("defName,label,packageId,category,verdict,reasons,override,requestable,baseValue,tradeable,craftable,techLevel,stackLimit");
            foreach (CatalogEntry e in c.Entries)
            {
                string reason;
                bool ok = c.IsRequestable(e.DefName, out reason);
                sb.AppendLine(Csv(e.DefName) + "," + Csv(e.Label) + "," + Csv(e.facts.packageId) + "," + Csv(e.facts.category) + "," + e.Verdict + "," + Csv(string.Join(" ", e.classification.reasons.ToArray()))
                    + "," + c.OverrideOf(e.DefName) + "," + (ok ? "yes" : reason) + "," + e.facts.marketValue.ToString("0.##") + "," + e.facts.Tradeable + "," + e.facts.craftable + "," + e.facts.techLevel + "," + e.facts.stackLimit);
            }
            string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "TheNetwork");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "catalog.csv");
            File.WriteAllText(path, sb.ToString());
            Out("[TheNetwork] Catalog report written to " + path + " (" + c.Count + " rows).");
        }

        private static string Csv(string s)
        {
            if (s == null) return "";
            return s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        [DebugAction(Cat, "Explain item…", allowedGameStates = AllowedGameStates.Playing)]
        public static void ExplainItem()
        {
            Find.WindowStack.Add(new Dialog_DevTextInput("defName to explain", def =>
            {
                ItemCatalog c = CatalogCache.Get();
                CatalogEntry e = c.Get(def);
                if (e == null)
                {
                    Out("[TheNetwork] '" + def + "' is not a ThingDef in this game.");
                    return;
                }
                string reason;
                bool ok = c.IsRequestable(def, out reason);
                CatalogFacts f = e.facts;
                Out("[TheNetwork] " + def + " '" + e.Label + "' from " + (f.modName ?? "?") + " (" + (f.packageId ?? "?") + "): verdict " + e.Verdict
                    + (e.classification.reasons.Count > 0 ? " [" + string.Join(", ", e.classification.reasons.ToArray()) + "]" : "")
                    + "; override " + c.OverrideOf(def) + "; requestable " + (ok ? "yes" : "no (" + reason + ")")
                    + (c.RuntimeFailure(def) != null ? "; runtime failure: " + c.RuntimeFailure(def) : "")
                    + "\n  category " + f.category + ", class " + f.thingClassName + ", value " + f.marketValue.ToString("0.##") + ", stack " + f.stackLimit + ", tech " + f.techLevel
                    + ", tradeable " + f.Tradeable + ", craftable " + f.craftable + ", haulable " + f.everHaulable + ", minifiable " + f.minifiable);
            }));
        }

        // ================================================================== cast

        [DebugAction(Cat, "Global cast report (settings)", allowedGameStates = AllowedGameStates.Playing | AllowedGameStates.Entry)]
        public static void GlobalCastReport()
        {
            NetworkSettings s = NetworkMod.Settings;
            if (s == null) return;
            GlobalNetworkRoster r = s.roster;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[TheNetwork] Global cast (settings v" + s.settingsVersion + ", loaded v" + s.LoadedVersion + "; generator v" + r.generation.generatorVersion + ", seed " + r.generation.castSeed + ", at " + r.generation.lastGeneratedAt + ")");
            sb.AppendLine("  contractors " + r.contractorTemplates.Count + " (" + r.CountActive(true) + " active), fixers " + r.fixerTemplates.Count + " (" + r.CountActive(false) + " active), quarantined/unreadable " + r.CountQuarantined());
            Dictionary<string, int> forms = new Dictionary<string, int>();
            int issuers = 0, famous = 0, custom = 0;
            foreach (ContractorTemplate t in r.contractorTemplates)
            {
                string k = t.form.ToString();
                int n;
                forms.TryGetValue(k, out n);
                forms[k] = n + 1;
                if (t.canIssueWork) issuers++;
                if (t.startingFame >= Domain.FameBand.Famous) famous++;
                if (t.provenance == TemplateProvenance.Custom) custom++;
            }
            foreach (KeyValuePair<string, int> kv in forms) sb.AppendLine("  form " + kv.Key + ": " + kv.Value);
            sb.AppendLine("  canIssueWork: " + issuers + ", famous or legendary: " + famous + ", custom: " + custom);
            foreach (FixerTemplate f in r.fixerTemplates) sb.AppendLine("  fixer " + f.DisplayLabel + " [" + f.provenance + "] fee " + f.feeBand + " speed " + f.speedBand + " reliability " + f.reliabilityBand + " style " + f.intelStyle + (f.IsQuarantined ? " QUARANTINED " + f.quarantined.reasonKey : "") + " id " + f.templateId);
            for (int i = 0; i < s.LoadReport.Count; i++) sb.AppendLine("  load: " + s.LoadReport[i]);
            Out(sb.ToString());
        }

        [DebugAction(Cat, "World cast snapshot report", allowedGameStates = AllowedGameStates.Playing)]
        public static void WorldCastReport()
        {
            NetworkRuntime rt = Rt;
            if (rt == null) return;
            WorldCastSnapshot c = rt.State.cast;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[TheNetwork] World cast snapshot: imported " + c.imported + " at tick " + c.importedTick + " (settings v" + c.settingsVersionAtImport + "); contractors " + c.Count(CastEntryKind.Contractor)
                + " (instantiated " + c.CountInstantiated(CastEntryKind.Contractor) + "), fixers " + c.Count(CastEntryKind.Fixer) + " (instantiated " + c.CountInstantiated(CastEntryKind.Fixer) + ")");
            foreach (CastEntry e in c.entries)
            {
                if (e.kind == CastEntryKind.Fixer) sb.AppendLine("  fixer " + e.DisplayName + " template " + e.templateId + " → actor " + e.actor);
            }
            Out(sb.ToString());
        }

        [DebugAction(Cat, "Regenerate generated cast (dev; keeps custom)", allowedGameStates = AllowedGameStates.Playing | AllowedGameStates.Entry)]
        public static void RegenerateCast()
        {
            NetworkStartup.Regenerate(NetworkMod.Settings);
            Out("[TheNetwork] Regenerated. Existing worlds keep their snapshots.");
        }

        // ================================================================== intel

        [DebugAction(Cat, "Toggle comms-gate override", allowedGameStates = AllowedGameStates.Playing)]
        public static void ToggleComms()
        {
            IntelDevOverrides.commsGateOverride = !IntelDevOverrides.commsGateOverride;
            Out("[TheNetwork] Comms-gate override " + (IntelDevOverrides.commsGateOverride ? "ON" : "OFF") + " (dev only).");
        }

        [DebugAction(Cat, "Toggle fee waiver", allowedGameStates = AllowedGameStates.Playing)]
        public static void ToggleFees()
        {
            IntelDevOverrides.waiveFees = !IntelDevOverrides.waiveFees;
            Out("[TheNetwork] Fee waiver " + (IntelDevOverrides.waiveFees ? "ON" : "OFF") + " (dev only; fees still frozen in terms).");
        }

        [DebugAction(Cat, "Submit intel…", allowedGameStates = AllowedGameStates.Playing)]
        public static void SubmitIntel()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            List<FloatMenuOption> opts = new List<FloatMenuOption>();
            foreach (UI.ContactView c in rt.Read.Contacts())
            {
                UI.ContactView captured = c;
                opts.Add(new FloatMenuOption(c.name + " (" + c.kindLabel + ")", () =>
                {
                    Find.WindowStack.Add(new Dialog_DevTextInput("defName to ask about (no quantity)", def =>
                    {
                        CommandResult res = rt.Commands.SubmitIntel(captured.key, def);
                        Out("[TheNetwork] Submit " + def + " to " + captured.name + ": " + res);
                    }));
                }));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        [DebugAction(Cat, "Run the next search round now…", allowedGameStates = AllowedGameStates.Playing)]
        public static void RunRoundNow()
        {
            PickRequest(true, r => r.state == IntelState.Searching, (rt, r) =>
            {
                bool ok = rt.Ctx.Intel.ForceRoundNow(r.id);
                Out("[TheNetwork] Forced round " + r.round + " of " + r.id + ": " + (ok ? "resolved → " + r.state : "not searching"));
            });
        }

        [DebugAction(Cat, "Force lead on next round", allowedGameStates = AllowedGameStates.Playing)]
        public static void ForceLead()
        {
            IntelDevOverrides.forceLead = true;
            IntelDevOverrides.forceNoLead = false;
            Out("[TheNetwork] The next round delivers a lead (if a credible source exists).");
        }

        [DebugAction(Cat, "Force no-lead on next round", allowedGameStates = AllowedGameStates.Playing)]
        public static void ForceNoLead()
        {
            IntelDevOverrides.forceNoLead = true;
            IntelDevOverrides.forceLead = false;
            Out("[TheNetwork] The next round finds nothing.");
        }

        [DebugAction(Cat, "Force divergence class…", allowedGameStates = AllowedGameStates.Playing)]
        public static void ForceDivergence()
        {
            List<FloatMenuOption> opts = new List<FloatMenuOption>();
            foreach (LeadDivergence d in new[] { LeadDivergence.Accurate, LeadDivergence.Partial, LeadDivergence.Outdated, LeadDivergence.Bad, LeadDivergence.Jackpot, LeadDivergence.Complication, LeadDivergence.Trap })
            {
                LeadDivergence captured = d;
                opts.Add(new FloatMenuOption(d.ToString(), () => { IntelDevOverrides.forceDivergence = captured; Out("[TheNetwork] Next round divergence: " + captured); }));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        [DebugAction(Cat, "Force source kind for next round…", allowedGameStates = AllowedGameStates.Playing)]
        public static void ForceSourceKind()
        {
            List<FloatMenuOption> opts = new List<FloatMenuOption>();
            foreach (SourceKind k in Enum.GetValues(typeof(SourceKind)))
            {
                SourceKind captured = k;
                opts.Add(new FloatMenuOption(k.ToString(), () => { IntelDevOverrides.forceSourceKind = captured; Out("[TheNetwork] Next round source kind (if a candidate exists): " + captured); }));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        [DebugAction(Cat, "Reroll the current round (nonce++)", allowedGameStates = AllowedGameStates.Playing)]
        public static void Reroll()
        {
            PickRequest(true, r => r.state == IntelState.Searching, (rt, r) =>
            {
                r.rerollNonce++;
                Out("[TheNetwork] " + r.id + " reroll nonce now " + r.rerollNonce + " (dev only).");
            });
        }

        [DebugAction(Cat, "Explain source resolution for item…", allowedGameStates = AllowedGameStates.Playing)]
        public static void ExplainSource()
        {
            NetworkRuntime rt = Rt;
            if (rt == null) return;
            Find.WindowStack.Add(new Dialog_DevTextInput("defName", def =>
            {
                ItemFacts facts = rt.Ctx.catalog.Facts(def);
                if (facts == null)
                {
                    Out("[TheNetwork] '" + def + "' not in catalog.");
                    return;
                }
                List<string> skipped = new List<string>();
                List<SourceCandidate> cands = SourceResolver.BuildCandidates(facts, rt.Ctx.world.LiveFactions(), skipped);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("[TheNetwork] Source resolution for " + def + " (package " + (facts.packageId ?? "?") + (facts.isLudeon ? ", Ludeon" : "") + ", tech " + facts.techLevel + ")");
                float total = 0f;
                foreach (SourceCandidate c in cands) total += c.weight;
                foreach (SourceCandidate c in cands) sb.AppendLine("  " + c + "  (" + (c.weight / Math.Max(0.001f, total)).ToString("P0") + " of the draw)");
                foreach (string s in skipped) sb.AppendLine("  skipped: " + s);
                Out(sb.ToString());
            }));
        }

        [DebugAction(Cat, "Dump intel request…", allowedGameStates = AllowedGameStates.Playing)]
        public static void DumpIntel()
        {
            PickRequest(false, r => true, (rt, r) =>
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("[TheNetwork] " + r + " source " + r.source + " '" + r.terms.sourceName + "' seed " + r.seed + " nonce " + r.rerollNonce);
                sb.AppendLine("  terms: fee " + r.terms.initialFee + " (" + r.terms.feePolicyKey + "), continuation " + r.terms.continuationPolicyKey + " fee " + r.terms.continuationFee + ", rounds/segment " + r.terms.roundsPerSegment + ", max " + r.terms.maxRounds);
                sb.AppendLine("  round " + r.round + " (segment from " + r.segmentStartRound + "), started " + r.roundStartedTick + ", due " + r.nextRoundDueTick + ", ended " + r.endedTick + " " + r.endReasonKey);
                foreach (MoneyRecord m in r.fees) sb.AppendLine("  money " + m.direction + " " + m.silver + " " + m.noteKey + " r" + m.round + (m.pending ? " PENDING" : ""));
                foreach (LeadId id in r.leads)
                {
                    Lead l = rt.State.intel.Get(id);
                    if (l != null) sb.AppendLine("  lead " + l.id + " r" + l.round + " " + l.state + " divergence(hidden) " + l.divergence + " → " + l.opportunity);
                }
                Out(sb.ToString());
            });
        }

        private static void PickRequest(bool mutates, Predicate<IntelRequest> filter, Action<NetworkRuntime, IntelRequest> act)
        {
            NetworkRuntime rt = mutates ? Live : Rt;
            if (rt == null) return;
            List<FloatMenuOption> opts = new List<FloatMenuOption>();
            foreach (IntelRequest r in rt.State.intel.requests)
            {
                if (!filter(r)) continue;
                IntelRequest captured = r;
                opts.Add(new FloatMenuOption(r.ToString(), () => act(rt, captured)));
            }
            if (opts.Count == 0) opts.Add(new FloatMenuOption("(none)", null));
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        // ================================================================== opportunities

        [DebugAction(Cat, "Create representative opportunity…", allowedGameStates = AllowedGameStates.Playing)]
        public static void CreateOpportunity()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            Find.WindowStack.Add(new Dialog_DevTextInput("defName for the cache", def =>
            {
                ItemFacts facts = rt.Ctx.catalog.Facts(def);
                if (facts == null)
                {
                    Out("[TheNetwork] '" + def + "' not in catalog.");
                    return;
                }
                string failure;
                SourceKind? kind = IntelDevOverrides.forceSourceKind;
                IntelDevOverrides.forceSourceKind = null;
                Opportunity o = rt.Ctx.Opportunities.CreateDebug(facts, IntelDevOverrides.forceDivergence ?? LeadDivergence.Accurate, kind, out failure);
                IntelDevOverrides.forceDivergence = null;
                Out("[TheNetwork] Debug opportunity: " + (o == null ? "none (" + failure + ")" : o + " at " + o.location + " threat " + o.threat.profileKey + " " + o.threat.points + (failure != null ? " FAILED " + failure : "")));
            }));
        }

        [DebugAction(Cat, "Dump opportunity…", allowedGameStates = AllowedGameStates.Playing)]
        public static void DumpOpportunity()
        {
            PickOpportunity(false, o => true, (rt, o) =>
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("[TheNetwork] " + o + " origin " + o.origin + " seed " + o.seed + " committed " + o.committedAtTick + " expires " + o.expiresTick);
                sb.AppendLine("  source " + o.sourceContext.kind + " holder " + (o.sourceContext.holder?.NameSnapshot ?? "-") + " stance " + o.sourceContext.holderStance + " evidence {" + string.Join(",", o.sourceContext.evidence.ToArray()) + "}");
                sb.AppendLine("  threat " + o.threat.profileKey + " " + o.threat.points + " faction " + (o.threat.factionUsed?.NameSnapshot ?? "-") + "; site " + (o.site == null ? "-" : o.site.id.ToString()) + (o.site != null && rt.SiteAdapter.SiteExists(o.site) ? " (exists" + (rt.SiteAdapter.SiteHasMap(o.site) ? ", map)" : ")") : " (gone)"));
                foreach (OpportunityPayload p in o.payload)
                {
                    ItemPayload ip = p as ItemPayload;
                    if (ip != null) sb.AppendLine("  payload " + ip.role + " " + ip.count + "x " + ip.thing?.defName + (ip.stuff != null ? " (" + ip.stuff.defName + ")" : "") + (ip.qualityBand >= 0 ? " q" + ip.qualityBand : ""));
                }
                Engagement e = o.engagement;
                sb.AppendLine("  engagement: first " + e.firstEngagedTick + ", initial " + e.initialOnMap + ", last remaining " + e.lastRemaining + " @" + e.lastSampleTick + ", caravans " + e.caravanDepartures + " (carried " + e.caravanTally + ", own cargo included), recovered " + e.recovered + " " + e.recoveredBand + (e.settled ? ", settled" : ""));
                Out(sb.ToString());
            });
        }

        [DebugAction(Cat, "Sample opportunity now…", allowedGameStates = AllowedGameStates.Playing)]
        public static void SampleNow()
        {
            PickOpportunity(false, o => o.state == OpportunityState.Engaged, (rt, o) =>
            {
                int remaining;
                bool ok = rt.SiteAdapter.TrySampleRemaining(o, out remaining);
                Out("[TheNetwork] " + o.id + " sample: " + (ok ? remaining + " of the target still on the map" : "no map"));
            });
        }

        [DebugAction(Cat, "Expire opportunity now…", allowedGameStates = AllowedGameStates.Playing)]
        public static void ExpireNow()
        {
            PickOpportunity(true, o => o.state == OpportunityState.Materialized, (rt, o) =>
            {
                rt.Ctx.Opportunities.DevExpire(o);
                Out("[TheNetwork] " + o.id + " expired (dev). The vanilla site keeps its own timeout.");
            });
        }

        [DebugAction(Cat, "Force claim (half)…", allowedGameStates = AllowedGameStates.Playing)]
        public static void ForceClaim()
        {
            PickOpportunity(true, o => o.state == OpportunityState.Materialized || o.state == OpportunityState.Engaged, (rt, o) =>
            {
                rt.Ctx.Opportunities.DevForceResolve(o, 0.5f);
                Out("[TheNetwork] " + o.id + " resolved (dev): " + o.state + " " + o.engagement.recoveredBand);
            });
        }

        private static void PickOpportunity(bool mutates, Predicate<Opportunity> filter, Action<NetworkRuntime, Opportunity> act)
        {
            NetworkRuntime rt = mutates ? Live : Rt;
            if (rt == null) return;
            List<FloatMenuOption> opts = new List<FloatMenuOption>();
            foreach (Opportunity o in rt.State.opportunities.opportunities)
            {
                if (!filter(o)) continue;
                Opportunity captured = o;
                opts.Add(new FloatMenuOption(o.ToString(), () => act(rt, captured)));
            }
            if (opts.Count == 0) opts.Add(new FloatMenuOption("(none)", null));
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        // ================================================================== validate / performance / save

        [DebugAction(Cat, "Validate all", allowedGameStates = AllowedGameStates.Playing)]
        public static void ValidateAll()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            ValidationReport r = NetValidator.Run(rt, ValidationMode.Full);
            Messages.Message("[TheNetwork] " + r.Summary() + " (see log)", MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction(Cat, "Quarantine and failed-consumer report", allowedGameStates = AllowedGameStates.Playing)]
        public static void QuarantineReport()
        {
            NetworkRuntime rt = Rt;
            if (rt == null) return;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[TheNetwork] Quarantine: " + rt.State.diagnostics.quarantine.Count);
            foreach (QuarantineRecord q in rt.State.diagnostics.quarantine) sb.AppendLine("  " + q + (q.rawXml != null ? " (raw XML kept, " + q.rawXml.Length + " chars)" : ""));
            sb.AppendLine("Failed consumers: " + rt.State.diagnostics.failedConsumers.Count);
            foreach (FailedConsumerRecord f in rt.State.diagnostics.failedConsumers) sb.AppendLine("  #" + f.eventSeq + " " + f.typeKey + " " + f.consumer + ": " + f.message);
            sb.AppendLine("Failed migrations: " + rt.State.diagnostics.failedMigrations.Count);
            Out(sb.ToString());
        }

        [DebugAction(Cat, "Print timing report", allowedGameStates = AllowedGameStates.Playing)]
        public static void TimingReport()
        {
            NetworkRuntime rt = Rt;
            Out(NetProfiler.Report() + (rt != null ? NetValidator.CountsVsCaps(rt) : ""));
        }

        [DebugAction(Cat, "Reset timing counters", allowedGameStates = AllowedGameStates.Playing)]
        public static void ResetTiming()
        {
            NetProfiler.Reset();
            Out("[TheNetwork] Timing counters reset (profiling is " + (NetProfiler.Enabled ? "on" : "off; enable it in Mod Settings") + ").");
        }

        [DebugAction(Cat, "S18 performance harness (synthetic)", allowedGameStates = AllowedGameStates.Playing | AllowedGameStates.Entry)]
        public static void S18()
        {
            PerfHarness.Result r = PerfHarness.Run(10000, 5000);
            Out(r.text);
        }

        [DebugAction(Cat, "Save-size report (Network node)", allowedGameStates = AllowedGameStates.Playing)]
        public static void SaveSize()
        {
            NetworkWorldComponent root = NetworkWorldComponent.Instance;
            if (root == null) return;
            string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "TheNetwork");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "network-node.xml");
            Scribe.saver.InitSaving(path, "TheNetwork");
            try
            {
                NetworkWorldComponent tmp = root;
                Scribe_Deep.Look(ref tmp, "component", root.world);
            }
            finally
            {
                Scribe.saver.FinalizeSaving();
            }
            Out("[TheNetwork] Network node written to " + path + ": " + new FileInfo(path).Length / 1024 + " KB.");
        }

        [DebugAction(Cat, "Prepare save for removal", allowedGameStates = AllowedGameStates.Playing)]
        public static void PrepareRemoval()
        {
            NetworkRuntime rt = Live;
            if (rt == null) return;
            Out(RemovalPreparer.Prepare(rt));
        }
    }

    /// <summary>A minimal text prompt for dev actions.</summary>
    public sealed class Dialog_DevTextInput : Window
    {
        private readonly string prompt;
        private readonly Action<string> onOk;
        private string text = "";
        private bool focused;

        public Dialog_DevTextInput(string prompt, Action<string> onOk)
        {
            this.prompt = prompt;
            this.onOk = onOk;
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
        }

        public override Vector2 InitialSize => new Vector2(420f, 150f);

        public override void DoWindowContents(Rect inRect)
        {
            Widgets.Label(new Rect(0f, 0f, inRect.width, 24f), prompt);
            GUI.SetNextControlName("NetworkDevText");
            text = Widgets.TextField(new Rect(0f, 30f, inRect.width, 28f), text);
            if (!focused)
            {
                GUI.FocusControl("NetworkDevText");
                focused = true;
            }
            if (Widgets.ButtonText(new Rect(inRect.width - 100f, inRect.height - 32f, 100f, 30f), "OK") || (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return))
            {
                string value = (text ?? "").Trim();
                Close();
                if (value.Length > 0) onOk(value);
            }
        }
    }
}
