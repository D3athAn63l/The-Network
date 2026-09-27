using System.Collections.Generic;
using TheNetwork.Domain.Intel;
using TheNetwork.Domain.Ports;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using TheNetwork.Settings;

namespace TheNetwork.Domain.Actors
{
    /// <summary>
    /// Actor registry service (ARCHITECTURE § 6.6): the Phase 1 pseudo-actors, the world cast snapshot,
    /// Fixer instantiation and lazy faction proxies. No contractor actors exist in Phase 1.
    /// </summary>
    public sealed class ActorService
    {
        public const string ExchangeKey = "institution:Exchange";

        private readonly DomainContext ctx;

        public ActorService(DomainContext ctx)
        {
            this.ctx = ctx;
        }

        private int SeedFor(ActorId id)
        {
            return NetHash.Combine(NetHash.Combine(ctx.networkSeed, id.Value), "actor");
        }

        // ------------------------------------------------------------------ bootstrap actors

        /// <summary>Exactly one PlayerProxy, with IssuerProfile only (no ContractorProfile until Phase 4).</summary>
        public NetworkActor EnsurePlayerProxy(FactionFacts playerFaction)
        {
            NetworkActor p = ctx.actors.PlayerProxy;
            if (p == null)
            {
                p = new NetworkActor
                {
                    id = new ActorId(ctx.ids.NextId()),
                    kind = ActorKind.PlayerProxy,
                    foundedTick = ctx.Now,
                    name = NameSnapshot.Org(playerFaction?.name ?? "the colony"),
                    provenance = new Provenance { source = ProvenanceSource.Bootstrap, importedTick = ctx.Now }
                };
                p.seed = SeedFor(p.id);
                p.Add(new IssuerProfile());
                ctx.actors.Add(p);
            }
            if (playerFaction != null)
            {
                // Rebind if the player's faction changed (for example another mod replaced it).
                FactionRef current = p.bindings.faction;
                if (current == null || current.loadId != playerFaction.loadId)
                {
                    p.bindings.faction = RefFrom(playerFaction);
                    p.bindings.faction.wasPlayer = true;
                    p.name = NameSnapshot.Org(playerFaction.name);
                }
            }
            return p;
        }

        /// <summary>"The Exchange": the generic information network (master § 8), an Institution with IntelSourceProfile.</summary>
        public NetworkActor EnsureExchange()
        {
            NetworkActor e = ctx.actors.Exchange;
            if (e != null) return e;
            e = new NetworkActor
            {
                id = new ActorId(ctx.ids.NextId()),
                kind = ActorKind.Institution,
                foundedTick = ctx.Now,
                name = NameSnapshot.Org("the Exchange"),
                provenance = new Provenance { source = ProvenanceSource.Content, templateId = ExchangeKey, importedTick = ctx.Now }
            };
            e.seed = SeedFor(e.id);
            e.reputation.fame = FameBand.Established;
            e.Add(SourcePolicies.ForExchange());
            ctx.actors.Add(e);
            return e;
        }

        // ------------------------------------------------------------------ cast snapshot

        /// <summary>
        /// Copies the enabled, non-quarantined templates into this world, once (DATA_MODEL § 18.2).
        /// The snapshot is authoritative afterwards; the roster is never read again for this world.
        /// </summary>
        public bool ImportCast(GlobalNetworkRoster roster, int settingsVersion, List<string> report)
        {
            if (ctx.cast.imported) return false;
            ctx.cast.imported = true;
            ctx.cast.importedTick = ctx.Now;
            ctx.cast.settingsVersionAtImport = settingsVersion;
            ctx.cast.generatorVersionAtImport = roster?.generation?.generatorVersion ?? 0;
            if (roster == null) return true;
            int skipped = 0;
            for (int i = 0; i < roster.contractorTemplates.Count; i++)
            {
                ContractorTemplate t = roster.contractorTemplates[i];
                if (!t.IsActive) { skipped++; continue; }
                ctx.cast.entries.Add(new CastEntry { templateId = t.templateId, kind = CastEntryKind.Contractor, contractor = t.Copy() });
            }
            for (int i = 0; i < roster.fixerTemplates.Count; i++)
            {
                FixerTemplate t = roster.fixerTemplates[i];
                if (!t.IsActive) { skipped++; continue; }
                ctx.cast.entries.Add(new CastEntry { templateId = t.templateId, kind = CastEntryKind.Fixer, fixer = t.Copy() });
            }
            report?.Add("Imported " + ctx.cast.Count(CastEntryKind.Contractor) + " contractor and " + ctx.cast.Count(CastEntryKind.Fixer) + " Fixer templates" + (skipped > 0 ? " (" + skipped + " disabled or quarantined skipped)" : "") + ".");
            return true;
        }

        /// <summary>Phase 1 instantiates Fixers only. Contractor entries stay as snapshot data for Phase 2.</summary>
        public int InstantiateFixers()
        {
            int made = 0;
            for (int i = 0; i < ctx.cast.entries.Count; i++)
            {
                CastEntry e = ctx.cast.entries[i];
                if (e.kind != CastEntryKind.Fixer || e.actor.IsValid || e.fixer == null) continue;
                NetworkActor a = InstantiateFixer(e.fixer);
                e.actor = a.id;
                made++;
            }
            return made;
        }

        private NetworkActor InstantiateFixer(FixerTemplate t)
        {
            NetworkActor a = new NetworkActor
            {
                id = new ActorId(ctx.ids.NextId()),
                kind = ActorKind.Individual,
                foundedTick = ctx.Now,
                name = new NameSnapshot { display = t.displayName, nick = t.nickname },
                provenance = new Provenance { source = ProvenanceSource.GlobalCast, templateId = t.templateId, importedTick = ctx.Now }
            };
            a.seed = SeedFor(a.id);
            a.reputation.fame = t.startingFame;

            FixerProfile fp = new FixerProfile
            {
                contractorReach = t.contractorReach,
                marketAccess = t.geographicReach,
                feeBand = t.feeBand,
                feePolicyKey = "fixer.fee." + (t.intelStyle ?? "Standard"),
                brokeragePolicyKey = "fixer.brokerage." + (t.brokerageStyle ?? "Standard"),
                depositPolicyKey = "fixer.deposit.Standard",
                insurancePolicyKey = string.IsNullOrEmpty(t.insuranceStyle) || t.insuranceStyle == "None" ? null : "fixer.insurance." + t.insuranceStyle,
                quotePolicyKey = "fixer.quote.Standard",
                replacementPolicyKey = "fixer.replacement.Standard"
            };
            fp.specialties.AddRange(t.specialties);
            a.Add(fp);
            a.Add(SourcePolicies.ForFixer(t));
            ctx.actors.Add(a);

            KnownCharacter c = new KnownCharacter
            {
                id = new CharacterId(ctx.ids.NextId()),
                name = a.name.Copy(),
                role = CharacterRole.Freelancer,
                embodiedBy = a.id,
                createdTick = ctx.Now,
                notability = 0.3f + 0.15f * (int)t.startingFame
            };
            ctx.characters.Add(c);
            a.bindings.embodies = c.id;
            return a;
        }

        // ------------------------------------------------------------------ faction proxies

        public static FactionRef RefFrom(FactionFacts f)
        {
            if (f == null) return null;
            return new FactionRef { loadId = f.loadId, name = f.name, defName = f.defName, defPackageId = f.defPackageId, wasPlayer = f.isPlayer };
        }

        /// <summary>
        /// A faction's Network identity, created lazily the first time Network data needs one (as an
        /// Intel contact, or so history can name a site's holder). Keyed by loadID.
        /// </summary>
        public NetworkActor GetOrCreateFactionProxy(FactionFacts f, bool asIntelSource)
        {
            if (f == null) return null;
            NetworkActor a = ctx.actors.ProxyForFaction(f.loadId);
            if (a == null)
            {
                a = new NetworkActor
                {
                    id = new ActorId(ctx.ids.NextId()),
                    kind = ActorKind.FactionProxy,
                    foundedTick = ctx.Now,
                    name = NameSnapshot.Org(f.name),
                    provenance = new Provenance { source = ProvenanceSource.Bootstrap, importedTick = ctx.Now }
                };
                a.seed = SeedFor(a.id);
                a.bindings.faction = RefFrom(f);
                ctx.actors.Add(a);
            }
            if (asIntelSource)
            {
                IntelSourceProfile existing = a.Get<IntelSourceProfile>();
                IntelSourceProfile derived = SourcePolicies.ForFaction(f);
                if (existing == null) a.Add(derived);
                else if (existing.derived)
                {
                    // Derived profiles follow the faction's current signals; a running search keeps its frozen terms.
                    a.components.Remove(existing);
                    a.Add(derived);
                }
            }
            return a;
        }

        public FactionFacts FindFaction(int loadId)
        {
            List<FactionFacts> all = ctx.world?.LiveFactions();
            if (all == null) return null;
            for (int i = 0; i < all.Count; i++) if (all[i].loadId == loadId) return all[i];
            return null;
        }

        /// <summary>Marks faction proxies whose faction no longer exists (DATA_MODEL § 2: Destroyed(FactionVanished)).</summary>
        public int ReconcileFactionProxies()
        {
            List<FactionFacts> live = ctx.world?.LiveFactions();
            if (live == null) return 0;
            HashSet<int> ids = new HashSet<int>();
            for (int i = 0; i < live.Count; i++) ids.Add(live[i].loadId);
            int ended = 0;
            for (int i = 0; i < ctx.actors.actors.Count; i++)
            {
                NetworkActor a = ctx.actors.actors[i];
                if (a.kind != ActorKind.FactionProxy || a.status != ActorStatus.Active || a.bindings.faction == null) continue;
                if (!ids.Contains(a.bindings.faction.loadId))
                {
                    a.status = ActorStatus.Destroyed;
                    a.endedTick = ctx.Now;
                    a.endReasonKey = "FactionVanished";
                    ended++;
                }
            }
            if (ended > 0) StateVersion.Bump();
            return ended;
        }

        // ------------------------------------------------------------------ sources

        /// <summary>Can this actor take an Intel request right now?</summary>
        public bool IsUsableSource(NetworkActor a, out string reasonKey)
        {
            reasonKey = null;
            if (a == null) reasonKey = "SourceMissing";
            else if (a.quarantinedReason != null) reasonKey = "SourceQuarantined";
            else if (a.status != ActorStatus.Active) reasonKey = "SourceEnded";
            else if (!a.Has<IntelSourceProfile>()) reasonKey = "NotAnIntelSource";
            else if (a.kind == ActorKind.FactionProxy)
            {
                FactionFacts f = a.bindings.faction == null ? null : FindFaction(a.bindings.faction.loadId);
                if (!SourcePolicies.FactionCanBeContact(f, out reasonKey)) return false;
            }
            return reasonKey == null;
        }

        /// <summary>Is the source still able to finish a running search (weaker than taking a new one)?</summary>
        public bool SourceStillExists(ActorId id)
        {
            NetworkActor a = ctx.actors.Get(id);
            if (a == null || a.status != ActorStatus.Active) return false;
            if (a.kind == ActorKind.FactionProxy)
            {
                FactionFacts f = a.bindings.faction == null ? null : FindFaction(a.bindings.faction.loadId);
                return f != null && !f.defeated;
            }
            return true;
        }

        public List<NetworkActor> IntelSources()
        {
            List<NetworkActor> list = new List<NetworkActor>();
            for (int i = 0; i < ctx.actors.actors.Count; i++)
            {
                NetworkActor a = ctx.actors.actors[i];
                if (a.IsActive && a.Has<IntelSourceProfile>()) list.Add(a);
            }
            return list;
        }
    }
}
