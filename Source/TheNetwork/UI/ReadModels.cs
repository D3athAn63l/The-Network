using System;
using System.Collections.Generic;
using RimWorld.Planet;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Catalog;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Domain.Ports;
using TheNetwork.History;
using TheNetwork.Integration;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;
using Verse;

namespace TheNetwork.UI
{
    // Read models (ARCHITECTURE § 9): immutable views with ids and pre-formatted strings. No live
    // Pawn, Faction or Site objects; look targets are resolved at click time. Rebuilt only when the
    // global StateVersion changes.

    public sealed class ContactView
    {
        public SourceKey key;
        public string name;
        public string kindLabel;
        public string descriptors;
        public bool usable;
        public string reasonKey;
        public int sortGroup;
    }

    public sealed class CatalogRowView
    {
        public string defName;
        public string label;
        public string labelLower;
        public string modName;
        public string category;
        public float marketValue;
        public CatalogVerdict verdict;
        public string reasons;
        public ItemOverride over;
        public bool requestable;
        public string blockReason;
        public bool technical;
    }

    public sealed class LeadView
    {
        public LeadId id;
        public OpportunityId opportunity;
        public int round;
        public string title;
        public string summary;
        public string stateLabel;
        public bool canLookAt;
    }

    public sealed class IntelRowView
    {
        public IntelRequestId id;
        public string itemLabel;
        public string sourceName;
        public IntelState state;
        public string stateLabel;
        public string timing;
        public string terms;
        public string money;
        public List<LeadView> leads = new List<LeadView>();
        public bool active;
    }

    public sealed class HistoryEntryView
    {
        public int tick;
        public string date;
        public string line;
        public Importance importance;
        public bool fromJournal;
    }

    public sealed class NetworkReadModels
    {
        private readonly NetworkRuntime runtime;

        private int contactsVersion = -1;
        private int contactsHour = -1;
        private List<ContactView> contacts;
        private int catalogVersion = -1;
        private List<CatalogRowView> catalogRows;
        private int intelVersion = -1;
        private int intelSlot = -1;
        private List<IntelRowView> intelRows;
        private int historyVersion = -1;
        private List<HistoryEntryView> historyRows;

        public NetworkReadModels(NetworkRuntime runtime)
        {
            this.runtime = runtime;
        }

        private DomainContext Ctx => runtime.Ctx;

        public bool CommsUsable(out string reasonKey)
        {
            return Ctx.Intel.CommsOk(out reasonKey);
        }

        // ------------------------------------------------------------------ contacts

        public List<ContactView> Contacts()
        {
            int v = StateVersion.Current;
            int hour = Ctx.Now / Ticks.PerHour;
            if (contacts != null && contactsVersion == v && contactsHour == hour) return contacts;
            List<ContactView> list = new List<ContactView>();
            HashSet<int> proxiedFactions = new HashSet<int>();
            List<NetworkActor> sources = Ctx.Actors.IntelSources();
            for (int i = 0; i < sources.Count; i++)
            {
                NetworkActor a = sources[i];
                IntelSourceProfile p = a.Get<IntelSourceProfile>();
                string reason;
                bool usable = Ctx.Actors.IsUsableSource(a, out reason);
                if (a.kind == ActorKind.FactionProxy && a.bindings.faction != null)
                {
                    proxiedFactions.Add(a.bindings.faction.loadId);
                    if (!usable) continue; // a faction that turned hostile is simply not listed as a contact
                }
                list.Add(new ContactView
                {
                    key = SourceKey.ForActor(a.id),
                    name = a.name.Display,
                    kindLabel = KindLabel(a),
                    descriptors = Narrative.SourceDescriptors(a, p, runtime.State.summaries.Get(a.id)),
                    usable = usable,
                    reasonKey = reason,
                    sortGroup = a.kind == ActorKind.Individual ? 0 : (a.kind == ActorKind.Institution ? 1 : 2)
                });
            }
            List<FactionFacts> factions = Ctx.world.LiveFactions();
            for (int i = 0; i < factions.Count; i++)
            {
                FactionFacts f = factions[i];
                string reason;
                if (proxiedFactions.Contains(f.loadId) || !SourcePolicies.FactionCanBeContact(f, out reason)) continue;
                IntelSourceProfile p = SourcePolicies.ForFaction(f);
                list.Add(new ContactView
                {
                    key = SourceKey.ForFaction(f.loadId),
                    name = f.name,
                    kindLabel = "TheNetwork_Kind_Faction".Translate().Resolve(),
                    descriptors = ("TheNetwork_Fee_" + p.feeBand).Translate().Resolve() + ", " + ("TheNetwork_Speed_" + p.speedBand).Translate().Resolve() + ", " + "TheNetwork_Reliability_Untested".Translate().Resolve(),
                    usable = true,
                    sortGroup = 2
                });
            }
            list.Sort((a, b) => a.sortGroup != b.sortGroup ? a.sortGroup.CompareTo(b.sortGroup) : string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            contacts = list;
            contactsVersion = v;
            contactsHour = hour;
            return list;
        }

        private static string KindLabel(NetworkActor a)
        {
            if (a.Has<ContractorProfile>() && !a.Has<FixerProfile>()) return "TheNetwork_Kind_Contractor".Translate().Resolve();
            switch (a.kind)
            {
                case ActorKind.Individual: return "TheNetwork_Kind_Fixer".Translate().Resolve();
                case ActorKind.Institution: return "TheNetwork_Kind_Institution".Translate().Resolve();
                case ActorKind.FactionProxy: return "TheNetwork_Kind_Faction".Translate().Resolve();
                default: return a.kind.ToString();
            }
        }

        // ------------------------------------------------------------------ catalog

        public List<CatalogRowView> CatalogRows()
        {
            int v = StateVersion.Current;
            if (catalogRows != null && catalogVersion == v) return catalogRows;
            ItemCatalog cat = CatalogCache.Get();
            List<CatalogRowView> list = new List<CatalogRowView>(cat.Count);
            IReadOnlyList<CatalogEntry> entries = cat.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                CatalogEntry e = entries[i];
                if (e.facts.category != "Item" && e.facts.category != "Building") continue;
                string reason;
                bool ok = cat.IsRequestable(e.DefName, out reason);
                list.Add(new CatalogRowView
                {
                    defName = e.DefName,
                    label = e.Label,
                    labelLower = e.Label.ToLowerInvariant(),
                    modName = e.facts.modName ?? "?",
                    category = e.facts.topCategoryLabel ?? "",
                    marketValue = e.facts.marketValue,
                    verdict = e.Verdict,
                    reasons = e.classification.reasons.Count == 0 ? "" : string.Join(", ", e.classification.reasons.ToArray()),
                    over = cat.OverrideOf(e.DefName),
                    requestable = ok,
                    blockReason = reason,
                    technical = e.classification.HasTechnicalExclusion
                });
            }
            catalogRows = list;
            catalogVersion = v;
            return list;
        }

        // ------------------------------------------------------------------ intel

        public List<IntelRowView> IntelRows()
        {
            int v = StateVersion.Current;
            int now = Ctx.Now;
            int slot = now / 250;
            if (intelRows != null && intelVersion == v && intelSlot == slot) return intelRows;
            List<IntelRowView> list = new List<IntelRowView>();
            List<IntelRequest> requests = runtime.State.intel.requests;
            for (int i = requests.Count - 1; i >= 0; i--)
            {
                IntelRequest r = requests[i];
                if (r.state == IntelState.Closed && r.endedTick >= 0 && now - r.endedTick > Ticks.PerDay * 30) continue;
                list.Add(BuildIntelRow(r, now));
            }
            list.Sort((a, b) => a.active != b.active ? (a.active ? -1 : 1) : b.id.Value.CompareTo(a.id.Value));
            intelRows = list;
            intelVersion = v;
            intelSlot = slot;
            return list;
        }

        private IntelRowView BuildIntelRow(IntelRequest r, int now)
        {
            IntelRowView row = new IntelRowView
            {
                id = r.id,
                itemLabel = r.topic.Label,
                sourceName = r.terms.sourceName ?? Ctx.actors.NameOf(r.source),
                state = r.state,
                stateLabel = ("TheNetwork_IntelState_" + r.state).Translate().Resolve() + (r.quarantinedReason != null ? " [" + "TheNetwork_Quarantined".Translate().Resolve() + "]" : ""),
                active = r.IsActive,
                terms = Narrative.ContinuationPolicy(r.terms)
            };
            if (r.state == IntelState.Searching)
            {
                row.timing = "TheNetwork_Timing_Searching".Translate(r.round, Narrative.Elapsed(now - r.submittedTick), Narrative.Estimate(r.terms.speedBand)).Resolve();
            }
            else if (r.endedTick >= 0)
            {
                row.timing = "TheNetwork_Timing_Ended".Translate(("TheNetwork_EndReason_" + (r.endReasonKey ?? "Unknown")).Translate(), Narrative.Elapsed(r.endedTick - r.submittedTick)).Resolve();
            }
            else
            {
                row.timing = "TheNetwork_Timing_Waiting".Translate(r.round, Narrative.Elapsed(now - r.submittedTick)).Resolve();
            }
            int paid = r.TotalPaid();
            int refunded = r.TotalRefunded();
            row.money = "TheNetwork_Money".Translate(paid).Resolve() + (refunded > 0 ? " · " + "TheNetwork_MoneyRefunded".Translate(refunded).Resolve() : "")
                + (Ctx.Intel.HasPendingRefund(r) ? " · " + "TheNetwork_RefundPending".Translate().Resolve() : "");
            for (int k = 0; k < r.leads.Count; k++)
            {
                Lead lead = Ctx.intel.Get(r.leads[k]);
                if (lead == null) continue;
                Opportunity opp = Ctx.opportunities.Get(lead.opportunity);
                row.leads.Add(new LeadView
                {
                    id = lead.id,
                    opportunity = lead.opportunity,
                    round = lead.round,
                    title = "TheNetwork_LeadTitle".Translate(k + 1, lead.round).Resolve(),
                    summary = Narrative.LeadSummary(lead, Ctx),
                    stateLabel = LeadStateLabel(lead, opp),
                    canLookAt = opp != null && opp.site != null && runtime.SiteAdapter.SiteExists(opp.site)
                });
            }
            return row;
        }

        private static string LeadStateLabel(Lead lead, Opportunity opp)
        {
            if (opp == null) return "TheNetwork_LeadState_Archived".Translate().Resolve();
            switch (opp.state)
            {
                case OpportunityState.Materialized: return "TheNetwork_LeadState_Open".Translate().Resolve();
                case OpportunityState.Engaged: return "TheNetwork_LeadState_Engaged".Translate().Resolve();
                case OpportunityState.Claimed: return "TheNetwork_LeadState_Claimed".Translate(("TheNetwork_Recovered_" + opp.engagement.recoveredBand).Translate()).Resolve();
                case OpportunityState.Abandoned: return "TheNetwork_LeadState_Abandoned".Translate().Resolve();
                case OpportunityState.Expired: return "TheNetwork_LeadState_Expired".Translate().Resolve();
                case OpportunityState.Closed:
                    return opp.engagement.recoveredBand != RecoveredBand.None
                        ? "TheNetwork_LeadState_Claimed".Translate(("TheNetwork_Recovered_" + opp.engagement.recoveredBand).Translate()).Resolve()
                        : "TheNetwork_LeadState_Closed".Translate().Resolve();
                default: return ("TheNetwork_OppState_" + opp.state).Translate().Resolve();
            }
        }

        /// <summary>Resolved at click time; never stored in a view.</summary>
        public void LookAt(OpportunityId id)
        {
            Opportunity opp = Ctx.opportunities.Get(id);
            WorldObject wo = opp?.site == null ? null : runtime.SiteAdapter.Resolve(opp.site);
            if (wo == null) return;
            Find.WindowStack.TryRemove(typeof(MainTabWindow_Network), false);
            CameraJumper.TryJumpAndSelect(new GlobalTargetInfo(wo));
        }

        // ------------------------------------------------------------------ history

        public List<HistoryEntryView> HistoryRows()
        {
            int v = StateVersion.Current;
            if (historyRows != null && historyVersion == v) return historyRows;
            List<HistoryEntryView> list = new List<HistoryEntryView>();
            List<HistoryRecord> records = runtime.State.history.records;
            for (int i = 0; i < records.Count; i++)
            {
                HistoryRecord r = records[i];
                list.Add(new HistoryEntryView { tick = r.tick, line = Narrative.RecordLine(r), importance = r.importance });
            }
            List<NetworkEvent> journal = runtime.State.journal.entries;
            for (int i = 0; i < journal.Count; i++)
            {
                string line = Narrative.JournalLine(journal[i]);
                if (line != null) list.Add(new HistoryEntryView { tick = journal[i].tick, line = line, importance = journal[i].importance, fromJournal = true });
            }
            list.Sort((a, b) => b.tick.CompareTo(a.tick));
            historyRows = list;
            historyVersion = v;
            return list;
        }

        public string PlayerSummaryLine()
        {
            ActorRecordSummary s = runtime.State.summaries.Get(runtime.State.actors.PlayerProxyId);
            if (s == null) return "TheNetwork_History_NoneYet".Translate().Resolve();
            return "TheNetwork_History_Summary".Translate(
                s.Lifetime("intel.requested"), s.Lifetime("intel.leads"), s.Lifetime("opp.claimed.full") + s.Lifetime("opp.claimed.partial"),
                s.Lifetime("opp.claimed.partial"), s.Lifetime("opp.abandoned")).Resolve();
        }
    }
}
