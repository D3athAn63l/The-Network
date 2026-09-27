using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Persist.Events;

namespace TheNetwork.Domain.Opportunities
{
    public sealed class IntelRoundResult
    {
        public Lead lead;
        public Opportunity opportunity;
        public bool thingCreationFailed;
        public string reason;
        public SourceDecision decision;
    }

    /// <summary>
    /// Owns world truth (ARCHITECTURE § 6.14, STATE_MACHINES § 2.2): generation, materialization, the
    /// site lifecycle through the injected comp's callbacks, claim accounting and closing. No
    /// transition waits for defenders to die: taking part of the cache and leaving is a Claim.
    /// </summary>
    public sealed class OpportunityService
    {
        public const int TileMinDist = 4;
        public const int TileMaxDist = 16;

        private readonly DomainContext ctx;

        public OpportunityService(DomainContext ctx)
        {
            this.ctx = ctx;
        }

        // ================================================================== generation

        /// <summary>
        /// One Intel round found something to report: resolve the source context, commit the truth,
        /// create the vanilla site and the lead. Returns no lead when there is no credible source
        /// (a real result) or no usable tile.
        /// </summary>
        public IntelRoundResult GenerateForIntel(IntelRequest r, ItemFacts facts, LeadDivergence divergence, int seed, SourceKind? forcedKind)
        {
            IntelRoundResult result = new IntelRoundResult();
            GenerationInput input = new GenerationInput
            {
                item = facts,
                divergence = divergence,
                baseThreatPoints = r.roundThreatBasis > 0f ? r.roundThreatBasis : ctx.world.BaseThreatPoints(),
                factions = ctx.world.LiveFactions(),
                extraCargoPool = ctx.catalog.ExtraCargoPool(Math.Max(facts.techLevel, 4)),
                seed = seed,
                forcedKind = forcedKind
            };
            OpportunityDraft draft = OpportunityGenerator.Generate(input);
            result.decision = draft.decision;
            if (draft.NoCredibleLead)
            {
                result.reason = "NoCredibleSource";
                return result;
            }

            TileRef tile;
            if (!ctx.sites.TryFindTile(NetHash.Combine(seed, "tile"), TileMinDist, TileMaxDist, out tile))
            {
                result.reason = "NoSiteTile";
                return result;
            }

            Opportunity opp = Commit(draft, facts, tile, seed, OpportunityOrigin.IntelResolution, r.id.Ref);
            opp.intel = r.id;
            result.opportunity = opp;

            MaterializeResult mat = ctx.sites.Materialize(opp);
            if (!mat.ok)
            {
                if (mat.thingCreationFailed)
                {
                    result.thingCreationFailed = true;
                    result.reason = mat.failureReason;
                    if (mat.failedDefName != null && mat.failedDefName != facts.defName)
                    {
                        // An extra-cargo def failed, not the topic: only that def becomes unusable.
                        ctx.catalog.MarkUnusable(mat.failedDefName, mat.failureReason);
                        result.thingCreationFailed = false;
                    }
                }
                Invalidate(opp, "MaterializeFailed:" + (mat.failureReason ?? "?"), sendLetter: false);
                if (result.reason == null) result.reason = "SiteFailed";
                return result;
            }
            opp.site = mat.site;
            if (!string.IsNullOrEmpty(mat.threatProfileUsed)) opp.threat.profileKey = mat.threatProfileUsed;

            Lead lead = new Lead
            {
                id = new LeadId(ctx.ids.NextId()),
                intel = r.id,
                opportunity = opp.id,
                reportedBy = r.source,
                reportedTick = ctx.Now,
                reliability = SourcePolicies.Reliability(r.terms.reliabilityBand),
                divergence = divergence,
                round = r.round,
                state = LeadState.Active,
                stateTick = ctx.Now
            };
            lead.reported = LeadReporter.Report(opp, draft, divergence, r.terms.reliabilityBand, NetHash.Combine(seed, "report"), HolderText(opp));
            lead.reported.travelTicks = ctx.world.TravelTicksFromPlayerHome(opp.location);
            ctx.intel.Add(lead);
            opp.lead = lead.id;
            Transition(opp, OpportunityState.Revealed);
            Transition(opp, OpportunityState.Materialized);
            PublishOpp(EventKeys.OpportunityMaterialized, Importance.Minor, opp, null);
            ScheduleTimers(opp);
            result.lead = lead;
            return result;
        }

        /// <summary>Commits the truth as a Latent opportunity (Opportunity.Generated).</summary>
        public Opportunity Commit(OpportunityDraft draft, ItemFacts facts, TileRef tile, int seed, OpportunityOrigin origin, EntityRef originRef)
        {
            Opportunity opp = new Opportunity
            {
                id = new OpportunityId(ctx.ids.NextId()),
                archetypeKey = Archetypes.GuardedCache,
                origin = origin,
                originRef = originRef,
                location = tile,
                seed = seed,
                committedAtTick = ctx.Now,
                stateTick = ctx.Now,
                state = OpportunityState.Latent,
                expiresTick = ctx.Now + draft.windowDays * Ticks.PerDay
            };
            SourceDecision d = draft.decision;
            opp.sourceContext.kind = d.kind;
            opp.sourceContext.holderStance = d.holderStance;
            opp.sourceContext.evidence.AddRange(d.evidence);
            if (d.holder != null)
            {
                opp.sourceContext.holder = ActorService.RefFrom(d.holder);
                // A proxy so history can name the holder; never an Intel contact by this path.
                NetworkActor proxy = ctx.Actors.GetOrCreateFactionProxy(d.holder, false);
                opp.sourceContext.holderActor = proxy?.id ?? ActorId.None;
            }
            opp.threat.points = draft.threatPoints;
            opp.threat.profileKey = d.threatProfileKey;
            opp.threat.factionUsed = d.kind == SourceKind.Mechanoids ? null : opp.sourceContext.holder?.Copy();

            ItemPayload target = new ItemPayload
            {
                thing = new DefRef<ThingDef> { defName = facts.defName, label = facts.label, packageId = facts.packageId, modName = facts.modName },
                count = draft.targetCount,
                qualityBand = draft.targetQualityBand,
                role = PayloadRole.Target
            };
            if (facts.madeFromStuff)
            {
                string stuff = ctx.world.PickStuff(facts.defName, NetHash.Combine(seed, "stuff"));
                if (stuff != null) target.stuff = new DefRef<ThingDef> { defName = stuff, label = ctx.catalog.Facts(stuff)?.label ?? stuff };
            }
            opp.payload.Add(target);
            for (int i = 0; i < draft.extras.Count; i++)
            {
                DraftCargo c = draft.extras[i];
                ItemFacts cf = ctx.catalog.Facts(c.defName);
                ItemPayload extra = new ItemPayload
                {
                    thing = new DefRef<ThingDef> { defName = c.defName, label = c.label, packageId = cf?.packageId, modName = cf?.modName },
                    count = c.count,
                    qualityBand = -1,
                    role = PayloadRole.Extra
                };
                if (cf != null && cf.madeFromStuff)
                {
                    string stuff = ctx.world.PickStuff(c.defName, NetHash.Combine(seed, "stuff." + i));
                    if (stuff != null) extra.stuff = new DefRef<ThingDef> { defName = stuff, label = stuff };
                }
                opp.payload.Add(extra);
            }
            ctx.opportunities.Add(opp);
            PublishOpp(EventKeys.OpportunityGenerated, Importance.Minor, opp, null);
            return opp;
        }

        private void ScheduleTimers(Opportunity opp)
        {
            int warnAt = opp.expiresTick - Ticks.PerDay;
            if (warnAt > ctx.Now) ctx.scheduler.Schedule(JobKinds.OppWarn, warnAt, opp.id.Value);
            // Reconciliation after the window: catches a site that vanished without a callback.
            ctx.scheduler.Schedule(JobKinds.OppSample, opp.expiresTick + 2 * Ticks.PerDay, opp.id.Value);
        }

        public string HolderText(Opportunity opp)
        {
            SourceContext sc = opp.sourceContext;
            switch (sc.kind)
            {
                case SourceKind.Mechanoids: return "dormant mechanoids";
                case SourceKind.AncientSite: return "an old ruin with wildlife about";
                case SourceKind.AbandonedCache: return "an abandoned cache";
                default: return sc.holder != null ? sc.holder.NameSnapshot : "unknown hands";
            }
        }

        // ================================================================== transitions

        private void Transition(Opportunity opp, OpportunityState to)
        {
            opp.state = to;
            opp.stateTick = ctx.Now;
            StateVersion.Bump();
        }

        private void Finish(Opportunity opp, OpportunityState to, string outcomeKey, string eventKey, Importance importance)
        {
            if (opp.IsTerminal) return;
            ctx.scheduler.Cancel(JobKinds.OppSample, opp.id.Value);
            ctx.scheduler.Cancel(JobKinds.OppWarn, opp.id.Value);
            Transition(opp, to);
            opp.endedTick = ctx.Now;
            opp.outcomeKey = outcomeKey;
            Lead lead = ctx.intel.Get(opp.lead);
            if (lead != null && lead.state == LeadState.Active && to != OpportunityState.Claimed && to != OpportunityState.Abandoned)
            {
                lead.state = LeadState.Stale;
                lead.stateTick = ctx.Now;
            }
            if (eventKey != null) PublishOpp(eventKey, importance, opp, outcomeKey);
            opp.closeDueTick = ctx.Now + Ticks.PerDay;
            ctx.scheduler.Schedule(JobKinds.OppClose, opp.closeDueTick, opp.id.Value);
        }

        /// <summary>Any non-terminal state → Invalidated. The site, if any, is left for vanilla to time out.</summary>
        public void Invalidate(Opportunity opp, string reasonKey, bool sendLetter = true)
        {
            if (opp == null || opp.IsTerminal) return;
            Finish(opp, OpportunityState.Invalidated, reasonKey, sendLetter ? EventKeys.OpportunityInvalidated : null, Importance.Minor);
            NetLog.Info(LogCategory.Opportunities, "Opportunity " + opp.id + " invalidated: " + reasonKey);
        }

        // ================================================================== site callbacks (from the comp)

        public void OnMapGenerated(OpportunityId id)
        {
            Opportunity opp = ctx.opportunities.Get(id);
            if (opp == null || opp.state != OpportunityState.Materialized) return;
            Transition(opp, OpportunityState.Engaged);
            opp.engagement.firstEngagedTick = ctx.Now;
            opp.engagement.claimedBy = ctx.actors.PlayerProxyId;
            int remaining;
            if (ctx.sites.TrySampleRemaining(opp, out remaining))
            {
                opp.engagement.initialOnMap = remaining;
                opp.engagement.lastRemaining = remaining;
                opp.engagement.lastSampleTick = ctx.Now;
            }
            Lead lead = ctx.intel.Get(opp.lead);
            if (lead != null && lead.state == LeadState.Active)
            {
                lead.state = LeadState.Pursued;
                lead.stateTick = ctx.Now;
            }
            ctx.scheduler.Cancel(JobKinds.OppWarn, opp.id.Value);
            ctx.scheduler.Schedule(JobKinds.OppSample, ctx.Now + JobKinds.SamplePeriod, opp.id.Value);
            PublishOpp(EventKeys.OpportunityEngaged, Importance.Minor, opp, null);
        }

        /// <summary>
        /// A caravan left the site map (its pawns have already exited): re-sample what is left. The
        /// caravan's own count is kept for diagnostics only; it includes the player's own cargo and is
        /// never used for recovery.
        /// </summary>
        public void OnCaravanFormed(OpportunityId id, int targetInCaravan)
        {
            Opportunity opp = ctx.opportunities.Get(id);
            if (opp == null || opp.state != OpportunityState.Engaged) return;
            opp.engagement.caravanDepartures++;
            opp.engagement.caravanTally += Math.Max(0, targetInCaravan);
            Sample(opp);
            StateVersion.Bump();
        }

        /// <summary>
        /// Vanilla is about to remove the site map in this same tick (the last player pawns have gone,
        /// by caravan, pods or shuttle): the final sample. Whatever left in transporters is simply no
        /// longer on the map, so pods need no separate count.
        /// </summary>
        public void OnMapAboutToBeRemoved(OpportunityId id)
        {
            Opportunity opp = ctx.opportunities.Get(id);
            if (opp == null || opp.state != OpportunityState.Engaged) return;
            Sample(opp);
        }

        public void OnMapRemoved(OpportunityId id)
        {
            Opportunity opp = ctx.opportunities.Get(id);
            if (opp == null || opp.state != OpportunityState.Engaged) return;
            ResolveEngagement(opp);
        }

        /// <summary>The player settled the site: everything still there is theirs (Claimed, All).</summary>
        public void OnMapSettled(OpportunityId id)
        {
            Opportunity opp = ctx.opportunities.Get(id);
            if (opp == null || opp.IsTerminal) return;
            if (opp.state == OpportunityState.Materialized) OnMapGenerated(id);
            if (opp.state != OpportunityState.Engaged) return;
            opp.engagement.settled = true;
            int initial = RecoveryBasis(opp.engagement, opp.TargetCount);
            opp.engagement.recovered = initial;
            opp.engagement.recoveredBand = initial > 0 ? RecoveredBand.All : RecoveredBand.None;
            if (initial > 0) Finish(opp, OpportunityState.Claimed, "Settled", EventKeys.OpportunityClaimed, ClaimImportance(opp));
            else Finish(opp, OpportunityState.Abandoned, "SettledNothingThere", EventKeys.OpportunityAbandoned, Importance.Minor);
        }

        public void OnSiteDestroyed(OpportunityId id)
        {
            Opportunity opp = ctx.opportunities.Get(id);
            if (opp == null || opp.IsTerminal) return;
            if (opp.state == OpportunityState.Engaged)
            {
                if (ctx.sites.SiteHasMap(opp.site)) return; // settled or converted; the map callbacks decide
                ResolveEngagement(opp);
                return;
            }
            if (ctx.Now >= opp.expiresTick - JobKinds.SamplePeriod) Finish(opp, OpportunityState.Expired, "TimedOut", EventKeys.OpportunityExpired, Importance.Minor);
            else Finish(opp, OpportunityState.Destroyed, "SiteDestroyed", EventKeys.OpportunityDestroyed, Importance.Minor);
        }

        /// <summary>
        /// Re-counts the target def on the site map. Never sets the initial count: a later sample may
        /// include what the player brought, so only the map-generation sample may define the site's stock.
        /// </summary>
        private void Sample(Opportunity opp)
        {
            int remaining;
            if (ctx.sites.TrySampleRemaining(opp, out remaining))
            {
                opp.engagement.lastRemaining = remaining;
                opp.engagement.lastSampleTick = ctx.Now;
            }
        }

        /// <summary>
        /// Map gone: recovered ≈ site stock at map generation − what the last sample still found on the
        /// map, capped at the committed target. Coarse by design; the band is what history reports.
        /// </summary>
        private void ResolveEngagement(Opportunity opp)
        {
            opp.engagement.recovered = RecoveredEstimate(opp.engagement, opp.TargetCount);
            int initial = RecoveryBasis(opp.engagement, opp.TargetCount);
            opp.engagement.recoveredBand = BandUtility.RecoveredBandFor(opp.engagement.recovered, initial);
            if (opp.engagement.recovered > 0)
            {
                Finish(opp, OpportunityState.Claimed, opp.engagement.recoveredBand == RecoveredBand.All ? "FullRecovery" : "PartialRecovery", EventKeys.OpportunityClaimed, ClaimImportance(opp));
            }
            else
            {
                Finish(opp, OpportunityState.Abandoned, initial <= 0 ? "NothingThere" : "LeftEmptyHanded", EventKeys.OpportunityAbandoned, Importance.Minor);
            }
        }

        /// <summary>
        /// What the recovery is measured against: the committed target count, or less if fewer were
        /// found on the map. Same-def items the holder happened to own never count as the cache.
        /// </summary>
        public static int RecoveryBasis(Engagement e, int targetCount)
        {
            if (targetCount <= 0) return 0;
            int initial = e.initialOnMap >= 0 ? e.initialOnMap : targetCount;
            return Math.Max(0, Math.Min(initial, targetCount));
        }

        /// <summary>
        /// Provenance rule (S19): recovery is measured only as the depletion of the site's own stock.
        /// The initial count is taken at map generation, before any player pawn, caravan or pod cargo
        /// arrives; later samples count everything on the map, so the player's own copies of the target
        /// def can only raise the remainder (never the recovery), and whatever the player carries away,
        /// by caravan or pods, is counted by its absence from the map, once. Nothing the player brought
        /// is ever added.
        /// </summary>
        public static int RecoveredEstimate(Engagement e, int targetCount)
        {
            int basis = RecoveryBasis(e, targetCount);
            if (basis <= 0) return 0;
            int initial = e.initialOnMap >= 0 ? e.initialOnMap : targetCount;
            int remaining = e.lastRemaining >= 0 ? e.lastRemaining : initial;
            return BandUtility.Clamp(initial - remaining, 0, basis);
        }

        private Importance ClaimImportance(Opportunity opp)
        {
            ItemFacts f = ctx.catalog.Facts(opp.Target?.thing?.defName);
            float value = f == null ? 0f : f.marketValue * opp.engagement.recovered;
            return value >= 5000f ? Importance.Major : Importance.Notable;
        }

        // ================================================================== jobs

        /// <summary>
        /// opp.sample: every 2,500 ticks while the site map exists (claim fallback), and once after the
        /// window as reconciliation for a site that vanished without a callback.
        /// </summary>
        public void SampleJob(ScheduledJob job)
        {
            Opportunity opp = ctx.opportunities.Get(new OpportunityId(job.target));
            if (opp == null || opp.IsTerminal || opp.quarantinedReason != null) return;
            if (opp.state == OpportunityState.Engaged)
            {
                if (ctx.sites.SiteHasMap(opp.site))
                {
                    Sample(opp);
                    ctx.scheduler.Schedule(JobKinds.OppSample, ctx.Now + JobKinds.SamplePeriod, opp.id.Value);
                }
                else
                {
                    // Map gone without a callback (the site may or may not remain): resolve from the last sample.
                    ResolveEngagement(opp);
                }
                return;
            }
            if (opp.state == OpportunityState.Materialized)
            {
                if (!ctx.sites.SiteExists(opp.site))
                {
                    NetLog.Info(LogCategory.Opportunities, "Opportunity " + opp.id + ": site vanished without a callback; treated as destroyed.");
                    Finish(opp, OpportunityState.Vanished, "SiteVanished", EventKeys.OpportunityDestroyed, Importance.Minor);
                }
                else if (ctx.Now > opp.expiresTick)
                {
                    // Vanilla's timeout has not removed it yet (it only does so without a map): check again later.
                    ctx.scheduler.Schedule(JobKinds.OppSample, ctx.Now + Ticks.PerDay, opp.id.Value);
                }
            }
        }

        public void WarnJob(ScheduledJob job)
        {
            Opportunity opp = ctx.opportunities.Get(new OpportunityId(job.target));
            if (opp == null || opp.state != OpportunityState.Materialized || opp.expiryWarned) return;
            opp.expiryWarned = true;
            PublishOpp(EventKeys.OpportunityExpiringSoon, Importance.Minor, opp, null);
        }

        /// <summary>opp.close: terminal → Closed; the lead closes; the request may close; a leftover map-less site is released.</summary>
        public void CloseJob(ScheduledJob job)
        {
            Opportunity opp = ctx.opportunities.Get(new OpportunityId(job.target));
            if (opp == null || !opp.IsTerminal || opp.state == OpportunityState.Closed) return;
            if (opp.site != null && opp.state != OpportunityState.Invalidated)
            {
                ctx.sites.ReleaseSite(opp, destroyIfNoMap: opp.state == OpportunityState.Claimed || opp.state == OpportunityState.Abandoned);
            }
            Transition(opp, OpportunityState.Closed);
            opp.closeDueTick = -1;
            Lead lead = ctx.intel.Get(opp.lead);
            if (lead != null && lead.state != LeadState.Closed)
            {
                lead.state = LeadState.Closed;
                lead.stateTick = ctx.Now;
                ctx.Intel.OnLeadClosed(lead);
            }
        }

        // ================================================================== dev

        /// <summary>Dev: a representative opportunity without Intel (origin Debug), materialized now.</summary>
        public Opportunity CreateDebug(ItemFacts facts, LeadDivergence divergence, SourceKind? forcedKind, out string failure)
        {
            failure = null;
            int seed = NetHash.Combine(NetHash.Combine(ctx.networkSeed, ctx.ids.PeekNextId), "debug");
            GenerationInput input = new GenerationInput
            {
                item = facts,
                divergence = divergence,
                baseThreatPoints = ctx.world.BaseThreatPoints(),
                factions = ctx.world.LiveFactions(),
                extraCargoPool = ctx.catalog.ExtraCargoPool(Math.Max(facts.techLevel, 4)),
                seed = seed,
                forcedKind = forcedKind
            };
            OpportunityDraft draft = OpportunityGenerator.Generate(input);
            if (draft.NoCredibleLead)
            {
                failure = "NoCredibleSource";
                return null;
            }
            TileRef tile;
            if (!ctx.sites.TryFindTile(NetHash.Combine(seed, "tile"), TileMinDist, TileMaxDist, out tile))
            {
                failure = "NoSiteTile";
                return null;
            }
            Opportunity opp = Commit(draft, facts, tile, seed, OpportunityOrigin.Debug, EntityRef.None);
            MaterializeResult mat = ctx.sites.Materialize(opp);
            if (!mat.ok)
            {
                failure = mat.failureReason;
                if (mat.thingCreationFailed) ctx.catalog.MarkUnusable(mat.failedDefName ?? facts.defName, mat.failureReason);
                Invalidate(opp, "MaterializeFailed", sendLetter: false);
                return opp;
            }
            opp.site = mat.site;
            if (!string.IsNullOrEmpty(mat.threatProfileUsed)) opp.threat.profileKey = mat.threatProfileUsed;
            Transition(opp, OpportunityState.Materialized);
            PublishOpp(EventKeys.OpportunityMaterialized, Importance.Minor, opp, null);
            ScheduleTimers(opp);
            return opp;
        }

        /// <summary>
        /// Consequence Engine v0: a last known location, through the same generation and site machinery
        /// as an Intel lead. The target payload is what the contractor had secured (the committed stuff and
        /// quality when known), possibly nothing; extra cargo and the threat come from the generator, with
        /// the threat profile optionally taken from what stopped them. No lead: the letter reports it.
        /// </summary>
        public Opportunity GenerateFollowUp(ItemFacts facts, int lostCount, ItemPayload lostCargo, int seed, EntityRef originRef, int depth, string threatProfile, out string failure)
        {
            failure = null;
            GenerationInput input = new GenerationInput
            {
                item = facts,
                divergence = LeadDivergence.Accurate,
                baseThreatPoints = ctx.world.BaseThreatPoints(),
                factions = ctx.world.LiveFactions(),
                extraCargoPool = ctx.catalog.ExtraCargoPool(Math.Max(facts.techLevel, 4)),
                seed = seed
            };
            OpportunityDraft draft = OpportunityGenerator.Generate(input);
            if (draft.NoCredibleLead)
            {
                failure = "NoCredibleSource";
                return null;
            }
            draft.targetCount = Math.Max(0, lostCount);
            TileRef tile;
            if (!ctx.sites.TryFindTile(NetHash.Combine(seed, "tile"), TileMinDist, TileMaxDist, out tile))
            {
                failure = "NoSiteTile";
                return null;
            }
            Opportunity opp = Commit(draft, facts, tile, seed, OpportunityOrigin.ConsequenceRule, originRef);
            opp.lineageDepth = depth;
            ItemPayload target = opp.Target;
            if (target != null && lostCargo != null)
            {
                if (lostCargo.stuff != null) target.stuff = lostCargo.stuff.Copy();
                target.qualityBand = lostCargo.qualityBand;
            }
            if (!string.IsNullOrEmpty(threatProfile) && opp.threat.factionUsed != null) opp.threat.profileKey = threatProfile;
            MaterializeResult mat = ctx.sites.Materialize(opp);
            if (!mat.ok)
            {
                failure = mat.failureReason ?? "SiteFailed";
                if (mat.thingCreationFailed && mat.failedDefName != null) ctx.catalog.MarkUnusable(mat.failedDefName, mat.failureReason);
                Invalidate(opp, "MaterializeFailed", sendLetter: false);
                return null;
            }
            opp.site = mat.site;
            if (!string.IsNullOrEmpty(mat.threatProfileUsed)) opp.threat.profileKey = mat.threatProfileUsed;
            Transition(opp, OpportunityState.Revealed);
            Transition(opp, OpportunityState.Materialized);
            PublishOpp(EventKeys.OpportunityMaterialized, Importance.Minor, opp, null);
            ScheduleTimers(opp);
            return opp;
        }

        /// <summary>Dev: resolve an engaged or materialized opportunity as if the player left with a share of the target.</summary>
        public void DevForceResolve(Opportunity opp, float share)
        {
            if (opp == null || opp.IsTerminal) return;
            if (opp.state != OpportunityState.Engaged)
            {
                Transition(opp, OpportunityState.Engaged);
                opp.engagement.firstEngagedTick = ctx.Now;
            }
            int initial = Math.Max(0, opp.TargetCount);
            opp.engagement.initialOnMap = initial;
            opp.engagement.lastRemaining = initial - (int)Math.Round(initial * BandUtility.Clamp01(share));
            ResolveEngagement(opp);
        }

        public void DevExpire(Opportunity opp)
        {
            if (opp == null || opp.IsTerminal) return;
            Finish(opp, OpportunityState.Expired, "DevExpired", EventKeys.OpportunityExpired, Importance.Minor);
        }

        // ================================================================== events

        private void PublishOpp(string key, Importance importance, Opportunity opp, string reasonKey)
        {
            Lead lead = ctx.intel.Get(opp.lead);
            OpportunityEvent e = EventFactory.Make<OpportunityEvent>(key, importance, opp.id.Ref, opp.lead.Ref, opp.intel.Ref, opp.sourceContext.holderActor.Ref);
            e.opportunity = opp.id;
            e.lead = opp.lead;
            e.request = opp.intel;
            e.source = lead?.reportedBy ?? ActorId.None;
            if (e.source.IsValid) e.subjects.Add(e.source.Ref);
            e.holderActor = opp.sourceContext.holderActor;
            e.holderName = HolderText(opp);
            e.sourceKindKey = opp.sourceContext.kind.ToString();
            ItemPayload t = opp.Target;
            e.targetDefName = t?.thing?.defName;
            e.targetLabel = t?.LabelSnapshot;
            e.targetCount = t?.count ?? 0;
            e.recoveredCount = opp.engagement.recovered;
            e.recoveredBand = opp.engagement.recoveredBand;
            ItemFacts f = ctx.catalog.Facts(e.targetDefName);
            e.marketValue = f == null ? 0 : (int)(f.marketValue * Math.Max(0, opp.engagement.recovered));
            e.reasonKey = reasonKey;
            e.reportHeld = lead != null && LeadReporter.ReportHeld(lead.divergence);
            e.place = opp.location?.Copy();
            ctx.bus.Publish(e);
        }

        public List<Opportunity> Live()
        {
            List<Opportunity> list = new List<Opportunity>();
            for (int i = 0; i < ctx.opportunities.opportunities.Count; i++)
            {
                if (!ctx.opportunities.opportunities[i].IsTerminal) list.Add(ctx.opportunities.opportunities[i]);
            }
            return list;
        }
    }
}
