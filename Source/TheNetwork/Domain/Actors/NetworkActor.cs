using System.Collections.Generic;
using TheNetwork.Kernel;
using TheNetwork.Persist;
using Verse;

namespace TheNetwork.Domain.Actors
{
    public enum ActorKind : byte
    {
        Organization = 0,
        Individual = 1,
        FactionProxy = 2,
        PlayerProxy = 3,
        Institution = 4
    }

    public enum ActorStatus : byte
    {
        Active = 0,
        Dormant = 1,
        Retired = 2,
        Dissolved = 3,
        Absorbed = 4,
        Destroyed = 5,
        Tombstone = 6
    }

    public enum ProvenanceSource : byte
    {
        GlobalCast = 0,
        WorldGenerated = 1,
        Bootstrap = 2,
        Transformation = 3,
        Content = 4
    }

    /// <summary>Lineage links (DATA_MODEL § 4). Empty in Phase 1; present so the actor core is stable.</summary>
    public sealed class Lineage : IExposable
    {
        public List<ActorId> predecessors = new List<ActorId>();
        public List<ActorId> successors = new List<ActorId>();
        public ActorId absorbedInto;
        public ActorId splitFrom;

        public void ExposeData()
        {
            NetScribe.LookIntList(ref predecessors, "predecessors", a => a.Value, v => new ActorId(v));
            NetScribe.LookIntList(ref successors, "successors", a => a.Value, v => new ActorId(v));
            NetScribe.Look(ref absorbedInto, "absorbedInto");
            NetScribe.Look(ref splitFrom, "splitFrom");
        }
    }

    public sealed class ActorBindings : IExposable
    {
        public FactionRef faction;
        public CharacterId embodies;

        public void ExposeData()
        {
            Scribe_Deep.Look(ref faction, "faction");
            NetScribe.Look(ref embodies, "embodies");
        }
    }

    /// <summary>Where an identity came from. Never used as identity.</summary>
    public sealed class Provenance : IExposable
    {
        public ProvenanceSource source = ProvenanceSource.Bootstrap;
        public string templateId;
        public int importedTick = -1;

        public void ExposeData()
        {
            NetScribe.LookEnum(ref source, "source", ProvenanceSource.Bootstrap);
            Scribe_Values.Look(ref templateId, "templateId");
            Scribe_Values.Look(ref importedTick, "importedTick", -1);
        }
    }

    /// <summary>
    /// Public reputation (EVENTS_AND_HISTORY § 6). The score is the truth (Phase 2.75, ADR-046); the
    /// fame band is DERIVED from it through <see cref="CareerPolicy"/> and can only be changed by changing
    /// the score (or by starting an actor at a band's floor), so the two can never disagree. Fame is public
    /// standing, never capability: nothing here reads or sets the experience band.
    /// </summary>
    public sealed class PublicReputation : IExposable
    {
        private FameBand band = FameBand.Unknown;
        private int points;

        /// <summary>The derived public tier. Read-only: use <see cref="SetScore"/> or <see cref="SetBand"/>.</summary>
        public FameBand fame => band;

        /// <summary>The numeric reputation beneath the tier (0 .. <see cref="CareerPolicy.ScoreCap"/>).</summary>
        public int score => points;

        /// <summary>Sets the score (clamped) and re-derives the band.</summary>
        public void SetScore(int value)
        {
            points = CareerPolicy.ClampScore(value);
            band = CareerPolicy.FameFor(points);
        }

        /// <summary>Starts at a band's floor: the one mapping a template's starting fame and a migration use.</summary>
        public void SetBand(FameBand value)
        {
            SetScore(CareerPolicy.FloorOf(value));
        }

        public void ExposeData()
        {
            NetScribe.LookEnum(ref band, "fame", FameBand.Unknown);
            Scribe_Values.Look(ref points, "score", 0);
        }
    }

    /// <summary>
    /// A Network actor (DATA_MODEL § 4): identity is the ActorId, never the name, leader or faction.
    /// Capabilities are components; services interpret them.
    /// </summary>
    public sealed class NetworkActor : IExposable
    {
        public ActorId id;
        public ActorKind kind;
        public ActorStatus status = ActorStatus.Active;
        public NameSnapshot name = new NameSnapshot();
        public int seed;
        public int foundedTick;
        public int endedTick = -1;
        public string endReasonKey;
        public Lineage lineage = new Lineage();
        public ActorBindings bindings = new ActorBindings();
        public Provenance provenance = new Provenance();
        public string homeRegion;
        public List<ActorComponent> components = new List<ActorComponent>();
        public PublicReputation reputation = new PublicReputation();
        public bool playerVisible = true;

        /// <summary>Non-null when quarantined: kept, skipped by simulation (SAVE_AND_MIGRATION § 7).</summary>
        public string quarantinedReason;

        public bool IsActive => status == ActorStatus.Active && quarantinedReason == null;

        public T Get<T>() where T : ActorComponent
        {
            for (int i = 0; i < components.Count; i++)
            {
                T c = components[i] as T;
                if (c != null) return c;
            }
            return null;
        }

        public bool Has<T>() where T : ActorComponent
        {
            return Get<T>() != null;
        }

        public void Add(ActorComponent c)
        {
            if (c != null) components.Add(c);
        }

        public void ExposeData()
        {
            NetScribe.Look(ref id, "id");
            NetScribe.LookEnum(ref kind, "kind", ActorKind.Individual);
            NetScribe.LookEnum(ref status, "status", ActorStatus.Active);
            Scribe_Deep.Look(ref name, "name");
            Scribe_Values.Look(ref seed, "seed", 0);
            Scribe_Values.Look(ref foundedTick, "foundedTick", 0);
            Scribe_Values.Look(ref endedTick, "endedTick", -1);
            Scribe_Values.Look(ref endReasonKey, "endReason");
            Scribe_Deep.Look(ref lineage, "lineage");
            Scribe_Deep.Look(ref bindings, "bindings");
            Scribe_Deep.Look(ref provenance, "provenance");
            Scribe_Values.Look(ref homeRegion, "homeRegion");
            NetScribe.LookListTolerant(ref components, "components", "actors.components");
            Scribe_Deep.Look(ref reputation, "reputation");
            Scribe_Values.Look(ref playerVisible, "playerVisible", true);
            Scribe_Values.Look(ref quarantinedReason, "quarantined");
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (name == null) name = new NameSnapshot();
                if (lineage == null) lineage = new Lineage();
                if (bindings == null) bindings = new ActorBindings();
                if (provenance == null) provenance = new Provenance();
                if (reputation == null) reputation = new PublicReputation();
                if (components == null) components = new List<ActorComponent>();
            }
        }

        public override string ToString()
        {
            return id + " " + kind + " '" + name.Display + "'";
        }
    }

    public enum CharacterRole : byte
    {
        Leader = 0,
        Lieutenant = 1,
        Specialist = 2,
        Member = 3,
        Freelancer = 4,
        Retired = 5
    }

    public enum CharacterStatus : byte
    {
        Active = 0,
        Wounded = 1,
        Captured = 2,
        Missing = 3,
        Dead = 4,
        Retired = 5,
        Defected = 6,
        Lost = 7
    }

    public enum CustodyState : byte
    {
        Unmaterialized = 0,
        Stored = 1,
        Deployed = 2,
        OutOfCustody = 3,
        Released = 4,
        Lost = 5
    }

    /// <summary>
    /// An individual who matters (DATA_MODEL § 5): Fixers, Solo contractors, organization leaders,
    /// lieutenants and notable members. Records only: no pawn, custody Unmaterialized. Wounds, capture
    /// and death are record states in Phase 2. Pawn binding (PawnRef) is Phase 3 and deliberately absent.
    /// </summary>
    public sealed class KnownCharacter : IExposable
    {
        public CharacterId id;
        public NameSnapshot name = new NameSnapshot();
        public CharacterRole role = CharacterRole.Freelancer;
        public ActorId org;
        public ActorId embodiedBy;
        public CharacterStatus status = CharacterStatus.Active;
        public CustodyState custody = CustodyState.Unmaterialized;
        public float notability;
        public int createdTick;
        public int diedTick = -1;
        public string quarantinedReason;

        /// <summary>Abstract recovery: Wounded until this tick (DATA_MODEL § 5).</summary>
        public int woundedUntilTick = -1;

        public string deathCauseKey;

        /// <summary>When the status last changed.</summary>
        public int statusTick;

        public bool IsAlive => status != CharacterStatus.Dead && status != CharacterStatus.Lost;

        /// <summary>Can take part in work right now.</summary>
        public bool IsAvailable => status == CharacterStatus.Active;

        public void ExposeData()
        {
            NetScribe.Look(ref id, "id");
            Scribe_Deep.Look(ref name, "name");
            NetScribe.LookEnum(ref role, "role", CharacterRole.Freelancer);
            NetScribe.Look(ref org, "org");
            NetScribe.Look(ref embodiedBy, "embodiedBy");
            NetScribe.LookEnum(ref status, "status", CharacterStatus.Active);
            NetScribe.LookEnum(ref custody, "custody", CustodyState.Unmaterialized);
            Scribe_Values.Look(ref notability, "notability", 0f);
            Scribe_Values.Look(ref createdTick, "createdTick", 0);
            Scribe_Values.Look(ref diedTick, "diedTick", -1);
            Scribe_Values.Look(ref quarantinedReason, "quarantined");
            Scribe_Values.Look(ref woundedUntilTick, "woundedUntil", -1);
            Scribe_Values.Look(ref deathCauseKey, "deathCause");
            Scribe_Values.Look(ref statusTick, "statusTick", 0);
            if (Scribe.mode == LoadSaveMode.LoadingVars && name == null) name = new NameSnapshot();
        }
    }
}
