using System.Collections.Generic;
using TheNetwork.Kernel;
using TheNetwork.Settings;
using Verse;

namespace TheNetwork.Domain.Actors
{
    /// <summary>All actors, including tombstones. Indexes are runtime-only and rebuilt on load.</summary>
    public sealed class ActorStore : IExposable
    {
        public List<NetworkActor> actors = new List<NetworkActor>();

        private readonly Dictionary<int, NetworkActor> byId = new Dictionary<int, NetworkActor>();
        private readonly Dictionary<int, NetworkActor> byFactionLoadId = new Dictionary<int, NetworkActor>();

        public ActorId PlayerProxyId { get; private set; }
        public ActorId ExchangeId { get; private set; }

        /// <summary>How many world-generated contractors this world has created (the newcomer seed index).</summary>
        public int worldGeneratedCount;

        /// <summary>When an Open contract last brought a new contractor into the world as a bidder.</summary>
        public int lastNewcomerBidderTick = -1;

        public void ExposeData()
        {
            NetScribe.LookListTolerant(ref actors, "actors", "actors");
            Scribe_Values.Look(ref worldGeneratedCount, "worldGenerated", 0);
            Scribe_Values.Look(ref lastNewcomerBidderTick, "lastNewcomerBidder", -1);
        }

        public void RebuildIndex()
        {
            byId.Clear();
            byFactionLoadId.Clear();
            PlayerProxyId = ActorId.None;
            ExchangeId = ActorId.None;
            for (int i = 0; i < actors.Count; i++) Index(actors[i]);
        }

        private void Index(NetworkActor a)
        {
            if (a == null || !a.id.IsValid) return;
            byId[a.id.Value] = a;
            if (a.kind == ActorKind.PlayerProxy && !PlayerProxyId.IsValid) PlayerProxyId = a.id;
            if (a.kind == ActorKind.Institution && a.provenance.templateId == ActorService.ExchangeKey && !ExchangeId.IsValid) ExchangeId = a.id;
            if (a.kind == ActorKind.FactionProxy && a.bindings.faction != null && a.bindings.faction.IsValid)
            {
                byFactionLoadId[a.bindings.faction.loadId] = a;
            }
        }

        public void Add(NetworkActor a)
        {
            actors.Add(a);
            Index(a);
        }

        public NetworkActor Get(ActorId id)
        {
            NetworkActor a;
            return id.IsValid && byId.TryGetValue(id.Value, out a) ? a : null;
        }

        public NetworkActor ProxyForFaction(int loadId)
        {
            NetworkActor a;
            return byFactionLoadId.TryGetValue(loadId, out a) ? a : null;
        }

        public NetworkActor PlayerProxy => Get(PlayerProxyId);
        public NetworkActor Exchange => Get(ExchangeId);

        public string NameOf(ActorId id)
        {
            NetworkActor a = Get(id);
            return a == null ? (id.IsValid ? "an archived contact" : "nobody") : a.name.Display;
        }
    }

    public sealed class CharacterStore : IExposable
    {
        public List<KnownCharacter> characters = new List<KnownCharacter>();

        private readonly Dictionary<int, KnownCharacter> byId = new Dictionary<int, KnownCharacter>();

        public void ExposeData()
        {
            NetScribe.LookListTolerant(ref characters, "characters", "characters");
        }

        public void RebuildIndex()
        {
            byId.Clear();
            for (int i = 0; i < characters.Count; i++)
            {
                if (characters[i] != null && characters[i].id.IsValid) byId[characters[i].id.Value] = characters[i];
            }
        }

        public void Add(KnownCharacter c)
        {
            characters.Add(c);
            byId[c.id.Value] = c;
        }

        public KnownCharacter Get(CharacterId id)
        {
            KnownCharacter c;
            return id.IsValid && byId.TryGetValue(id.Value, out c) ? c : null;
        }
    }

    public enum CastEntryKind : byte
    {
        Contractor = 0,
        Fixer = 1
    }

    /// <summary>One snapshotted template. The copy is this world's data; the global roster is never read again.</summary>
    public sealed class CastEntry : IExposable
    {
        public string templateId;
        public CastEntryKind kind;
        public ContractorTemplate contractor;
        public FixerTemplate fixer;

        /// <summary>Set when instantiated (Fixers in Phase 1; contractors in Phase 2).</summary>
        public ActorId actor;

        public void ExposeData()
        {
            Scribe_Values.Look(ref templateId, "templateId");
            NetScribe.LookEnum(ref kind, "kind", CastEntryKind.Contractor);
            Scribe_Deep.Look(ref contractor, "contractor");
            Scribe_Deep.Look(ref fixer, "fixer");
            NetScribe.Look(ref actor, "actor");
        }

        public string DisplayName => kind == CastEntryKind.Fixer ? fixer?.DisplayLabel : contractor?.DisplayLabel;
    }

    /// <summary>
    /// This world's copy of the global cast (DATA_MODEL § 18.2), taken once at bootstrap and
    /// authoritative for this save from then on. Later settings edits never reach it.
    /// </summary>
    public sealed class WorldCastSnapshot : IExposable
    {
        public bool imported;
        public int importedTick = -1;
        public int settingsVersionAtImport;
        public int generatorVersionAtImport;
        public List<CastEntry> entries = new List<CastEntry>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref imported, "imported", false);
            Scribe_Values.Look(ref importedTick, "importedTick", -1);
            Scribe_Values.Look(ref settingsVersionAtImport, "settingsVersionAtImport", 0);
            Scribe_Values.Look(ref generatorVersionAtImport, "generatorVersionAtImport", 0);
            NetScribe.LookListTolerant(ref entries, "entries", "cast");
        }

        public int Count(CastEntryKind kind)
        {
            int n = 0;
            for (int i = 0; i < entries.Count; i++) if (entries[i].kind == kind) n++;
            return n;
        }

        public int CountInstantiated(CastEntryKind kind)
        {
            int n = 0;
            for (int i = 0; i < entries.Count; i++) if (entries[i].kind == kind && entries[i].actor.IsValid) n++;
            return n;
        }
    }
}
