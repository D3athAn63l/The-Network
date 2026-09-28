using System;
using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using TheNetwork.Domain.Opportunities;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using Verse;

namespace TheNetwork.Integration
{
    /// <summary>
    /// Builds vanilla Sites for opportunities (RIMWORLD_INTEGRATION § 2.5, ADR-015): the vanilla
    /// <c>ItemStash</c> part carrying exactly the Things the Network committed, one vanilla threat part
    /// (or none), vanilla <c>TimeoutComp</c>, the injected <see cref="WorldObjectComp_NetworkSite"/>
    /// bound to the opportunity, and the quest tag <c>TheNetwork.Opp.&lt;id&gt;</c>. No Network Defs.
    /// Every vanilla generator runs under <c>Rand.PushState</c> and its result is committed at once.
    /// </summary>
    public sealed class SiteAdapter : ISiteAdapter
    {
        public const string TagPrefix = "TheNetwork.Opp.";
        public const int MaxStacksPerPayload = 40;

        private readonly Dictionary<int, WorldObject> resolved = new Dictionary<int, WorldObject>();

        public static string TagFor(OpportunityId id)
        {
            return TagPrefix + id.Value;
        }

        // ------------------------------------------------------------------ tiles

        public bool TryFindTile(int seed, int minDist, int maxDist, out TileRef tile)
        {
            tile = null;
            PlanetTile found;
            Rand.PushState(seed);
            try
            {
                // Surface only: orbital work is not Phase 1 (canBeSpace false).
                if (!TileFinder.TryFindNewSiteTile(out found, minDist, maxDist, false, null, 0f, true, TileFinderMode.Near, false, false, null, null)) return false;
            }
            catch (Exception ex)
            {
                NetLog.WarnOnce(LogCategory.Sites, "tilefinder", "Tile search failed: " + ex.Message);
                return false;
            }
            finally
            {
                Rand.PopState();
            }
            tile = TileRef.Of(found);
            return tile != null;
        }

        // ------------------------------------------------------------------ materialization

        public MaterializeResult Materialize(Opportunity opp)
        {
            MaterializeResult result = new MaterializeResult();
            if (opp.location == null || !opp.location.IsValidNow)
            {
                result.failureReason = "TileInvalid";
                return result;
            }
            PlanetTile tile = opp.location.Tile;

            // 1. The exact Things.
            List<Thing> things = new List<Thing>();
            for (int i = 0; i < opp.payload.Count; i++)
            {
                ItemPayload p = opp.payload[i] as ItemPayload;
                if (p == null || p.count <= 0) continue;
                string failure;
                if (!MakeThings(p, NetHash.Combine(opp.seed, "things." + i), things, out failure))
                {
                    result.thingCreationFailed = failure != "DefMissing";
                    result.failedDefName = p.thing?.defName;
                    result.failureReason = failure;
                    DiscardAll(things);
                    return result;
                }
            }

            // 2. Threat part and faction, with a vanilla-safe fallback chain.
            Faction faction = opp.threat.factionUsed?.Resolve();
            if (faction != null && (faction.defeated || !faction.HostileTo(Faction.OfPlayer))) faction = null;
            float points = Math.Max(0f, opp.threat.points);
            string profile;
            SitePartDef threatPart = ChooseThreatPart(opp.threat.profileKey, faction, points, tile, NetHash.Combine(opp.seed, "threatpart"), out profile, out faction);

            SitePartDef stash = DefDatabase<SitePartDef>.GetNamedSilentFail("ItemStash");
            if (stash == null)
            {
                result.failureReason = "ItemStashDefMissing";
                DiscardAll(things);
                return result;
            }
            List<SitePartDef> parts = new List<SitePartDef> { stash };
            if (threatPart != null) parts.Add(threatPart);

            Site site;
            Rand.PushState(NetHash.Combine(opp.seed, "site"));
            try
            {
                site = SiteMaker.MakeSite(parts, tile, faction, true, threatPart != null ? points : 0f);
            }
            catch (Exception ex)
            {
                result.failureReason = "SiteMakerFailed:" + ex.GetType().Name;
                NetLog.ErrorOnce(LogCategory.Sites, "sitemaker:" + profile, "Could not build a vanilla site (" + profile + "): " + ex);
                DiscardAll(things);
                return result;
            }
            finally
            {
                Rand.PopState();
            }

            // 3. Put the committed Things into the stash part (placed by vanilla GenStep_ItemStash).
            SitePart stashPart = null;
            for (int i = 0; i < site.parts.Count; i++) if (site.parts[i].def == stash) stashPart = site.parts[i];
            WorldObjectComp_NetworkSite comp = site.GetComponent<WorldObjectComp_NetworkSite>();
            TimeoutComp timeout = site.GetComponent<TimeoutComp>();
            if (stashPart == null || comp == null)
            {
                result.failureReason = comp == null ? "SiteCompMissing" : "StashPartMissing";
                NetLog.ErrorOnce(LogCategory.Sites, "sitecomp", "Network site comp or stash part missing on the vanilla Site def (" + result.failureReason + "); is the XML patch loaded?");
                DiscardAll(things);
                return result;
            }
            stashPart.things = new ThingOwner<Thing>(stashPart, false);
            stashPart.things.dontTickContents = true;
            stashPart.things.TryAddRangeOrTransfer(things, false);

            // 4. Window, binding, tag, label.
            int now = Find.TickManager.TicksGame;
            if (timeout != null && opp.expiresTick > now) timeout.StartTimeout(opp.expiresTick - now);
            comp.networkOpportunityId = opp.id.Value;
            QuestUtility.AddQuestTag(site, TagFor(opp.id));
            ItemPayload target = opp.Target;
            if (target != null) site.customLabel = "TheNetwork_SiteLabel".Translate(target.LabelSnapshot).Resolve();
            Find.WorldObjects.Add(site);

            result.ok = true;
            result.site = WorldObjectRef.Of(site);
            result.threatProfileUsed = profile;
            resolved[site.ID] = site;
            NetLog.Trace(LogCategory.Sites, "Materialized " + opp + " as site " + site.ID + " (" + profile + ", " + things.Count + " stacks) at " + opp.location);
            return result;
        }

        private static void DiscardAll(List<Thing> things)
        {
            for (int i = 0; i < things.Count; i++)
            {
                try
                {
                    if (!things[i].Destroyed) things[i].Destroy(DestroyMode.Vanish);
                }
                catch (Exception)
                {
                    // Unspawned and unheld: nothing else to clean up.
                }
            }
            things.Clear();
        }

        /// <summary>
        /// Creates the Things of one payload. A def that is missing now is "DefMissing" (a reference
        /// miss, handled by invalidation); a def that throws or yields an invalid Thing is a runtime
        /// catalog failure (COMPATIBILITY § 2.3, FailedToGenerate).
        /// </summary>
        internal static bool MakeThings(ItemPayload p, int seed, List<Thing> into, out string failure)
        {
            failure = null;
            ThingDef def = DefResolver<ThingDef>.Get(p.thing?.defName);
            if (def == null)
            {
                failure = "DefMissing";
                return false;
            }
            ThingDef stuff = p.stuff == null ? null : DefResolver<ThingDef>.Get(p.stuff.defName);
            if (def.MadeFromStuff && (stuff == null || !stuff.IsStuff)) stuff = GenStuff.DefaultStuffFor(def);
            if (!def.MadeFromStuff) stuff = null;
            int stackLimit = Math.Max(1, def.stackLimit);
            int left = p.count;
            int stacks = 0;
            Rand.PushState(seed);
            try
            {
                while (left > 0 && stacks < MaxStacksPerPayload)
                {
                    Thing t = ThingMaker.MakeThing(def, stuff);
                    if (t == null || t.def != def) throw new InvalidOperationException("ThingMaker returned " + (t == null ? "null" : t.def?.defName));
                    t.stackCount = Math.Min(left, stackLimit);
                    left -= t.stackCount;
                    if (p.qualityBand >= 0)
                    {
                        CompQuality q = t.TryGetComp<CompQuality>();
                        q?.SetQuality((QualityCategory)Math.Min(p.qualityBand, (int)QualityCategory.Legendary), ArtGenerationContext.Outsider);
                    }
                    if (def.category == ThingCategory.Building)
                    {
                        if (!def.Minifiable) throw new InvalidOperationException("building is not minifiable");
                        t = t.MakeMinified();
                    }
                    into.Add(t);
                    stacks++;
                }
                return true;
            }
            catch (Exception ex)
            {
                failure = "FailedToGenerate:" + ex.GetType().Name + ": " + NetScribe.Truncate(ex.Message, 160);
                NetLog.ErrorOnce(LogCategory.Catalog, "make:" + def.defName, "Could not create '" + def.defName + "' for a Network site: " + ex);
                return false;
            }
            finally
            {
                Rand.PopState();
            }
        }

        /// <summary>
        /// Maps a threat profile onto a vanilla SitePartDef that can actually be built here, falling back
        /// Outpost/BanditCamp/AmbushHidden/SleepingMechanoids → Manhunters → none. The profile really
        /// used is committed on the opportunity.
        /// </summary>
        private static SitePartDef ChooseThreatPart(string profileKey, Faction holder, float points, PlanetTile tile, int seed, out string used, out Faction siteFaction)
        {
            List<string> chain = new List<string>();
            switch (profileKey)
            {
                case ThreatProfiles.Outpost: chain.AddRange(new[] { ThreatProfiles.Outpost, ThreatProfiles.BanditCamp, ThreatProfiles.Manhunters }); break;
                case ThreatProfiles.BanditCamp: chain.AddRange(new[] { ThreatProfiles.BanditCamp, ThreatProfiles.Outpost, ThreatProfiles.Manhunters }); break;
                case ThreatProfiles.AmbushHidden: chain.AddRange(new[] { ThreatProfiles.AmbushHidden, ThreatProfiles.Manhunters }); break;
                case ThreatProfiles.SleepingMechanoids: chain.AddRange(new[] { ThreatProfiles.SleepingMechanoids, ThreatProfiles.Manhunters }); break;
                case ThreatProfiles.Manhunters: chain.Add(ThreatProfiles.Manhunters); break;
            }
            for (int i = 0; i < chain.Count; i++)
            {
                SitePartDef def = DefDatabase<SitePartDef>.GetNamedSilentFail(chain[i]);
                if (def == null) continue;
                Faction f = null;
                switch (chain[i])
                {
                    case ThreatProfiles.Outpost:
                    case ThreatProfiles.BanditCamp:
                        if (holder == null || holder.temporary || !def.FactionCanOwn(holder)) continue;
                        f = holder;
                        break;
                    case ThreatProfiles.AmbushHidden:
                        f = holder != null && def.FactionCanOwn(holder) ? holder : null;
                        break;
                    case ThreatProfiles.SleepingMechanoids:
                        if (Faction.OfMechanoids == null || !def.Worker.IsAvailable()) continue;
                        f = null;
                        break;
                    case ThreatProfiles.Manhunters:
                        PawnKindDef animal;
                        Rand.PushState(seed);
                        bool ok;
                        try
                        {
                            ok = ManhunterPackGenStepUtility.TryGetAnimalsKind(Math.Max(points, 100f), tile, out animal);
                        }
                        catch (Exception)
                        {
                            ok = false;
                        }
                        finally
                        {
                            Rand.PopState();
                        }
                        if (!ok) continue;
                        f = null;
                        break;
                }
                used = chain[i];
                siteFaction = f;
                return def;
            }
            used = ThreatProfiles.None;
            siteFaction = null;
            return null;
        }

        // ------------------------------------------------------------------ lookups

        public WorldObject Resolve(WorldObjectRef r)
        {
            if (r == null || r.id < 0) return null;
            WorldObject wo;
            if (resolved.TryGetValue(r.id, out wo) && wo != null && !wo.Destroyed && wo.ID == r.id) return wo;
            wo = r.Resolve();
            if (wo != null && !wo.Destroyed) resolved[r.id] = wo;
            else resolved.Remove(r.id);
            return wo != null && !wo.Destroyed ? wo : null;
        }

        public bool SiteExists(WorldObjectRef site)
        {
            return Resolve(site) != null;
        }

        public bool SiteHasMap(WorldObjectRef site)
        {
            MapParent mp = Resolve(site) as MapParent;
            return mp != null && mp.HasMap;
        }

        // ------------------------------------------------------------------ claim accounting

        public bool TrySampleRemaining(Opportunity opp, out int remaining)
        {
            remaining = 0;
            MapParent mp = Resolve(opp.site) as MapParent;
            Map map = mp?.Map;
            ThingDef def = DefResolver<ThingDef>.Get(opp.Target?.thing?.defName);
            if (map == null || def == null) return false;
            remaining = CountOnMap(map, def);
            NetLog.Trace(LogCategory.Sites, opp.id + ": " + remaining + " " + def.defName + " on the site map");
            return true;
        }

        public static int CountOnMap(Map map, ThingDef def)
        {
            int n = 0;
            List<Thing> buffer = new List<Thing>();
            if (def.category != ThingCategory.Building)
            {
                ThingOwnerUtility.GetAllThingsRecursively(map, ThingRequest.ForDef(def), buffer, true, null, true);
                for (int i = 0; i < buffer.Count; i++) n += buffer[i].stackCount;
                buffer.Clear();
            }
            ThingOwnerUtility.GetAllThingsRecursively(map, ThingRequest.ForGroup(ThingRequestGroup.MinifiedThing), buffer, true, null, true);
            for (int i = 0; i < buffer.Count; i++)
            {
                MinifiedThing m = buffer[i] as MinifiedThing;
                if (m?.InnerThing != null && m.InnerThing.def == def) n += m.stackCount;
            }
            return n;
        }

        public static int CountInHolder(IThingHolder holder, ThingDef def)
        {
            List<Thing> buffer = new List<Thing>();
            ThingOwnerUtility.GetAllThingsRecursively(holder, buffer, true, null);
            int n = 0;
            for (int i = 0; i < buffer.Count; i++)
            {
                Thing t = buffer[i];
                if (t.def == def) n += t.stackCount;
                else if (t is MinifiedThing m && m.InnerThing != null && m.InnerThing.def == def) n += m.stackCount;
            }
            return n;
        }

        public static int CountInCaravan(Caravan caravan, ThingDef def)
        {
            if (caravan == null || def == null) return 0;
            return CountInHolder(caravan, def);
        }

        // ------------------------------------------------------------------ release

        public void ReleaseSite(Opportunity opp, bool destroyIfNoMap)
        {
            WorldObject wo = Resolve(opp.site);
            if (wo == null) return;
            Unbind(wo, opp.id);
            MapParent mp = wo as MapParent;
            if (destroyIfNoMap && (mp == null || !mp.HasMap) && !wo.Destroyed) wo.Destroy();
        }

        /// <summary>Turns a Network site back into a plain vanilla site (comp inert, tag removed).</summary>
        public static void Unbind(WorldObject wo, OpportunityId id)
        {
            WorldObjectComp_NetworkSite comp = wo.GetComponent<WorldObjectComp_NetworkSite>();
            if (comp != null && comp.networkOpportunityId == id.Value) comp.networkOpportunityId = 0;
            wo.questTags?.Remove(TagFor(id));
        }
    }
}
