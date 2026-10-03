using System;

namespace TheNetwork.Kernel
{
    // Typed Network IDs (DATA_MODEL § 1). One persisted counter (IdAllocator.nextId) feeds every kind,
    // so values are unique across kinds inside a save. 0 means "none". IDs are never reused, never
    // renumbered and never derived from RimWorld objects. Only the kinds the implemented phases use are
    // declared (Phase 2 adds contracts, offers and operations); later phases add their own structs over
    // the same counter (a pure addition: the save holds plain ints).

    public enum EntityKind : byte
    {
        None = 0,
        Actor = 1,
        Character = 2,
        IntelRequest = 3,
        Lead = 4,
        Opportunity = 5,
        Contract = 6,
        Offer = 7,
        Operation = 8,

        /// <summary>A Physical Episode (Phase 3). Value 9 was reserved as "Deployment" in Phase 0; renamed, value and prefix unchanged.</summary>
        Episode = 9,
        Lease = 10,
        Obligation = 11,
        Belief = 12,
        HistoryRecord = 13,
        Legend = 14
    }

    public static class EntityKindUtility
    {
        public static char Prefix(EntityKind kind)
        {
            switch (kind)
            {
                case EntityKind.Actor: return 'A';
                case EntityKind.Character: return 'K';
                case EntityKind.IntelRequest: return 'I';
                case EntityKind.Lead: return 'L';
                case EntityKind.Opportunity: return 'O';
                case EntityKind.Contract: return 'C';
                case EntityKind.Offer: return 'B';
                case EntityKind.Operation: return 'P';
                case EntityKind.Episode: return 'D';
                case EntityKind.Lease: return 'E';
                case EntityKind.Obligation: return 'F';
                case EntityKind.Belief: return 'R';
                case EntityKind.HistoryRecord: return 'H';
                case EntityKind.Legend: return 'G';
                default: return '?';
            }
        }

        public static EntityKind FromPrefix(char c)
        {
            switch (c)
            {
                case 'A': return EntityKind.Actor;
                case 'K': return EntityKind.Character;
                case 'I': return EntityKind.IntelRequest;
                case 'L': return EntityKind.Lead;
                case 'O': return EntityKind.Opportunity;
                case 'C': return EntityKind.Contract;
                case 'B': return EntityKind.Offer;
                case 'P': return EntityKind.Operation;
                case 'D': return EntityKind.Episode;
                case 'E': return EntityKind.Lease;
                case 'F': return EntityKind.Obligation;
                case 'R': return EntityKind.Belief;
                case 'H': return EntityKind.HistoryRecord;
                case 'G': return EntityKind.Legend;
                default: return EntityKind.None;
            }
        }
    }

    public readonly struct ActorId : IEquatable<ActorId>
    {
        public readonly int Value;
        public ActorId(int value) { Value = value; }
        public static readonly ActorId None = default(ActorId);
        public bool IsValid => Value > 0;
        public EntityRef Ref => new EntityRef(EntityKind.Actor, Value);
        public bool Equals(ActorId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is ActorId o && o.Value == Value;
        public override int GetHashCode() => Value;
        public static bool operator ==(ActorId a, ActorId b) => a.Value == b.Value;
        public static bool operator !=(ActorId a, ActorId b) => a.Value != b.Value;
        public override string ToString() => IsValid ? "A" + Value : "A-";
    }

    public readonly struct CharacterId : IEquatable<CharacterId>
    {
        public readonly int Value;
        public CharacterId(int value) { Value = value; }
        public static readonly CharacterId None = default(CharacterId);
        public bool IsValid => Value > 0;
        public EntityRef Ref => new EntityRef(EntityKind.Character, Value);
        public bool Equals(CharacterId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is CharacterId o && o.Value == Value;
        public override int GetHashCode() => Value;
        public static bool operator ==(CharacterId a, CharacterId b) => a.Value == b.Value;
        public static bool operator !=(CharacterId a, CharacterId b) => a.Value != b.Value;
        public override string ToString() => IsValid ? "K" + Value : "K-";
    }

    public readonly struct IntelRequestId : IEquatable<IntelRequestId>
    {
        public readonly int Value;
        public IntelRequestId(int value) { Value = value; }
        public static readonly IntelRequestId None = default(IntelRequestId);
        public bool IsValid => Value > 0;
        public EntityRef Ref => new EntityRef(EntityKind.IntelRequest, Value);
        public bool Equals(IntelRequestId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is IntelRequestId o && o.Value == Value;
        public override int GetHashCode() => Value;
        public static bool operator ==(IntelRequestId a, IntelRequestId b) => a.Value == b.Value;
        public static bool operator !=(IntelRequestId a, IntelRequestId b) => a.Value != b.Value;
        public override string ToString() => IsValid ? "I" + Value : "I-";
    }

    public readonly struct LeadId : IEquatable<LeadId>
    {
        public readonly int Value;
        public LeadId(int value) { Value = value; }
        public static readonly LeadId None = default(LeadId);
        public bool IsValid => Value > 0;
        public EntityRef Ref => new EntityRef(EntityKind.Lead, Value);
        public bool Equals(LeadId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is LeadId o && o.Value == Value;
        public override int GetHashCode() => Value;
        public static bool operator ==(LeadId a, LeadId b) => a.Value == b.Value;
        public static bool operator !=(LeadId a, LeadId b) => a.Value != b.Value;
        public override string ToString() => IsValid ? "L" + Value : "L-";
    }

    public readonly struct OpportunityId : IEquatable<OpportunityId>
    {
        public readonly int Value;
        public OpportunityId(int value) { Value = value; }
        public static readonly OpportunityId None = default(OpportunityId);
        public bool IsValid => Value > 0;
        public EntityRef Ref => new EntityRef(EntityKind.Opportunity, Value);
        public bool Equals(OpportunityId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is OpportunityId o && o.Value == Value;
        public override int GetHashCode() => Value;
        public static bool operator ==(OpportunityId a, OpportunityId b) => a.Value == b.Value;
        public static bool operator !=(OpportunityId a, OpportunityId b) => a.Value != b.Value;
        public override string ToString() => IsValid ? "O" + Value : "O-";
    }

    public readonly struct HistoryRecordId : IEquatable<HistoryRecordId>
    {
        public readonly int Value;
        public HistoryRecordId(int value) { Value = value; }
        public static readonly HistoryRecordId None = default(HistoryRecordId);
        public bool IsValid => Value > 0;
        public EntityRef Ref => new EntityRef(EntityKind.HistoryRecord, Value);
        public bool Equals(HistoryRecordId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is HistoryRecordId o && o.Value == Value;
        public override int GetHashCode() => Value;
        public static bool operator ==(HistoryRecordId a, HistoryRecordId b) => a.Value == b.Value;
        public static bool operator !=(HistoryRecordId a, HistoryRecordId b) => a.Value != b.Value;
        public override string ToString() => IsValid ? "H" + Value : "H-";
    }

    public readonly struct ContractId : IEquatable<ContractId>
    {
        public readonly int Value;
        public ContractId(int value) { Value = value; }
        public static readonly ContractId None = default(ContractId);
        public bool IsValid => Value > 0;
        public EntityRef Ref => new EntityRef(EntityKind.Contract, Value);
        public bool Equals(ContractId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is ContractId o && o.Value == Value;
        public override int GetHashCode() => Value;
        public static bool operator ==(ContractId a, ContractId b) => a.Value == b.Value;
        public static bool operator !=(ContractId a, ContractId b) => a.Value != b.Value;
        public override string ToString() => IsValid ? "C" + Value : "C-";
    }

    public readonly struct OfferId : IEquatable<OfferId>
    {
        public readonly int Value;
        public OfferId(int value) { Value = value; }
        public static readonly OfferId None = default(OfferId);
        public bool IsValid => Value > 0;
        public EntityRef Ref => new EntityRef(EntityKind.Offer, Value);
        public bool Equals(OfferId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is OfferId o && o.Value == Value;
        public override int GetHashCode() => Value;
        public static bool operator ==(OfferId a, OfferId b) => a.Value == b.Value;
        public static bool operator !=(OfferId a, OfferId b) => a.Value != b.Value;
        public override string ToString() => IsValid ? "B" + Value : "B-";
    }

    public readonly struct OperationId : IEquatable<OperationId>
    {
        public readonly int Value;
        public OperationId(int value) { Value = value; }
        public static readonly OperationId None = default(OperationId);
        public bool IsValid => Value > 0;
        public EntityRef Ref => new EntityRef(EntityKind.Operation, Value);
        public bool Equals(OperationId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is OperationId o && o.Value == Value;
        public override int GetHashCode() => Value;
        public static bool operator ==(OperationId a, OperationId b) => a.Value == b.Value;
        public static bool operator !=(OperationId a, OperationId b) => a.Value != b.Value;
        public override string ToString() => IsValid ? "P" + Value : "P-";
    }

    /// <summary>One Physical Episode (Phase 3, PHYSICAL_LIFECYCLE § 5.3). Drawn from the shared counter like every other kind.</summary>
    public readonly struct EpisodeId : IEquatable<EpisodeId>
    {
        public readonly int Value;
        public EpisodeId(int value) { Value = value; }
        public static readonly EpisodeId None = default(EpisodeId);
        public bool IsValid => Value > 0;
        public EntityRef Ref => new EntityRef(EntityKind.Episode, Value);
        public bool Equals(EpisodeId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is EpisodeId o && o.Value == Value;
        public override int GetHashCode() => Value;
        public static bool operator ==(EpisodeId a, EpisodeId b) => a.Value == b.Value;
        public static bool operator !=(EpisodeId a, EpisodeId b) => a.Value != b.Value;
        public override string ToString() => IsValid ? "D" + Value : "D-";
    }

    /// <summary>
    /// A reference to any Network entity (event subjects, history participants). The kind is a
    /// validation and readability aid; IDs are unique across kinds. Persisted as the compact string
    /// "A17", "O301", …
    /// </summary>
    public readonly struct EntityRef : IEquatable<EntityRef>
    {
        public readonly EntityKind Kind;
        public readonly int Id;

        public EntityRef(EntityKind kind, int id)
        {
            Kind = kind;
            Id = id;
        }

        public static readonly EntityRef None = default(EntityRef);
        public bool IsValid => Kind != EntityKind.None && Id > 0;

        public bool Equals(EntityRef other) => Kind == other.Kind && Id == other.Id;
        public override bool Equals(object obj) => obj is EntityRef o && Equals(o);
        public override int GetHashCode() => NetHash.Combine((int)Kind, Id);
        public static bool operator ==(EntityRef a, EntityRef b) => a.Equals(b);
        public static bool operator !=(EntityRef a, EntityRef b) => !a.Equals(b);

        public override string ToString()
        {
            return IsValid ? EntityKindUtility.Prefix(Kind) + Id.ToString() : "-";
        }

        public static EntityRef Parse(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 2) return None;
            EntityKind kind = EntityKindUtility.FromPrefix(s[0]);
            int id;
            if (kind == EntityKind.None || !int.TryParse(s.Substring(1), out id) || id <= 0) return None;
            return new EntityRef(kind, id);
        }

        public ActorId AsActor => Kind == EntityKind.Actor ? new ActorId(Id) : ActorId.None;
        public OpportunityId AsOpportunity => Kind == EntityKind.Opportunity ? new OpportunityId(Id) : OpportunityId.None;
        public IntelRequestId AsIntelRequest => Kind == EntityKind.IntelRequest ? new IntelRequestId(Id) : IntelRequestId.None;
        public LeadId AsLead => Kind == EntityKind.Lead ? new LeadId(Id) : LeadId.None;
        public CharacterId AsCharacter => Kind == EntityKind.Character ? new CharacterId(Id) : CharacterId.None;
        public ContractId AsContract => Kind == EntityKind.Contract ? new ContractId(Id) : ContractId.None;
        public OfferId AsOffer => Kind == EntityKind.Offer ? new OfferId(Id) : OfferId.None;
        public OperationId AsOperation => Kind == EntityKind.Operation ? new OperationId(Id) : OperationId.None;
        public EpisodeId AsEpisode => Kind == EntityKind.Episode ? new EpisodeId(Id) : EpisodeId.None;
    }
}
