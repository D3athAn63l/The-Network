using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using RimWorld;
using TheNetwork.Core;
using TheNetwork.Domain;
using TheNetwork.Domain.Actors;
using TheNetwork.Domain.Physical;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Integration.Physical
{
    /// <summary>Reference equality for pawns: provenance is the binding's pointer, never a value comparison (P3-INV-012).</summary>
    public sealed class PawnReferenceComparer : IEqualityComparer<Pawn>
    {
        public static readonly PawnReferenceComparer Instance = new PawnReferenceComparer();

        public bool Equals(Pawn a, Pawn b)
        {
            return ReferenceEquals(a, b);
        }

        public int GetHashCode(Pawn p)
        {
            return RuntimeHelpers.GetHashCode(p);
        }
    }

    /// <summary>
    /// The production retained-pawn registry (PHYSICAL_LIFECYCLE § 7.4, § 16.3; S9r; M1 timing by ADR-053's rule).
    ///
    /// WHO IS RESERVED is derived, never stored: a pawn is reserved iff it is the bound pawn (reference equality with the character's own
    /// <see cref="PawnRef"/>) of a living named person whose custody is <see cref="CustodyState.Deployed"/>, <see cref="CustodyState.Stored"/> or
    /// (Phase 3.2A, ADR-056) <see cref="CustodyState.OutOfCustody"/>, or a living bound member of an Episode whose RELEASE is not complete.
    /// Phase 3.2B's temporary coverage comes from Episode PawnRefs, not temporary people or another ownership store. The same quest serves
    /// both categories. Named coverage takes precedence; an indexed Episode member bridges promotion until named coverage is installed.
    /// The binding is written BEFORE the pawn is spawned, so the reservation already covers a retained pawn while it is spawned (where it
    /// changes nothing: every vanilla consumer is gated on WorldPawns.Contains) and is in force at the instant vanilla passes it into
    /// WorldPawns, by a normal exit or a map removal. No Free window, no callback, no patch.
    ///
    /// HOW IT IS ASKED: one hidden, accepted quest holds one Network-owned <see cref="QuestPart_NetworkRetainedPawns"/>, whose
    /// QuestPartReserves asks this registry. The part persists NO pawn list: the Network stores are the single truth. No runtime ⇒ the part
    /// answers false (fail safe on an unprepared removal: vanilla drops the unknown part class and the pawns become ordinary world pawns).
    ///
    /// LOAD ORDER (the Phase 3.1 runtime-QA correction, audited in the 1.6 assembly): <c>Game.LoadGame</c> calls <c>World.FinalizeInit</c>
    /// (which builds the Network runtime, and so this registry) right after the world's <c>LoadingVars</c> pass and BEFORE
    /// <c>Scribe.loader.FinalizeLoading()</c> resolves cross-references. At that moment <c>PawnRef.pawn</c> is still null for every binding,
    /// while the persisted <c>thingIdNumber</c> (a plain value) is already loaded. An index built from pointers then is EMPTY, and the first
    /// vanilla tick (<c>World.WorldTick</c> runs <c>WorldPawns</c> BEFORE any world component) would see every retained pawn as an ordinary
    /// Free world pawn. So the registry has two stages: <see cref="RebuildEarly"/> (FinalizeInit: the durable thing-id index, answered through
    /// the narrow <see cref="BindingRules.BridgeCovers"/> bridge) and <see cref="ResolvePointers"/> (the world component's PostLoadInit, after
    /// every cross-reference: the validated pointer index, which becomes the authority; <see cref="Audit"/> reports every living binding that
    /// is unresolved, discarded or disagrees with its persisted thing id).
    ///
    /// Cost: one dictionary lookup per vanilla reservation query, instead of a list scan; nothing runs per tick of its own.
    /// </summary>
    public sealed class RetainedPawnRegistry
    {
        public const string QuestTag = "TheNetwork.RetainedRegistry";
        public const string QuestName = "The Network: retained people (hidden)";

        private readonly DomainContext ctx;
        private readonly Dictionary<Pawn, CharacterId> index = new Dictionary<Pawn, CharacterId>(PawnReferenceComparer.Instance);

        /// <summary>The LOAD-TIME BRIDGE: persisted thingIDNumber → character, built from durable bindings alone (no pointer needed).</summary>
        private readonly Dictionary<int, CharacterId> byThing = new Dictionary<int, CharacterId>();
        // Rebuildable indexes inside the SAME M1 registry. They persist nothing: the Episode remains the owner.
        private readonly Dictionary<Pawn, EpisodeReservation> episodeIndex = new Dictionary<Pawn, EpisodeReservation>(PawnReferenceComparer.Instance);
        private readonly Dictionary<int, EpisodeReservation> episodeByThing = new Dictionary<int, EpisodeReservation>();
        private Quest cachedQuest;

        private sealed class EpisodeReservation
        {
            public PhysicalEpisode episode;
            public EpisodeMember member;
        }

        /// <summary>Prepared for removal: the registry reserves nobody (§ 20 step 2).</summary>
        public bool inert;

        /// <summary>
        /// True once the pointer index was built from RESOLVED pointers and validated (post-load-init, a resume, or a new game). Until then the
        /// registry answers through the durable thing-id bridge. Runtime only.
        /// </summary>
        public bool pointersResolved;

        // runtime diagnostics (never persisted)
        public long queries;
        public long hits;
        public int rebuilds;
        public int resolves;
        public int questsCreated;

        public RetainedPawnRegistry(DomainContext ctx)
        {
            this.ctx = ctx;
        }

        /// <summary>The registry of the running Network (the quest part asks it). Null when no Network runtime exists.</summary>
        public static RetainedPawnRegistry Active => NetworkRuntime.Current?.PhysicalWorld?.Registry;

        public int IndexCount => index.Count;
        public int EpisodeIndexCount => episodeIndex.Count;

        /// <summary>
        /// RELEASE COMPLETE cleanup only: forget derived Episode cache entries after its durable release marker is true. Named retention is
        /// independent and untouched. The transient returned keys let the adapter drop its anonymous exit-tick cache too; no Pawn roster is saved.
        /// </summary>
        public List<Pawn> ForgetReleasedEpisode(PhysicalEpisode episode)
        {
            List<Pawn> forgotten = new List<Pawn>();
            if (episode == null || !episode.id.IsValid || !episode.releaseApplied) return forgotten;
            foreach (KeyValuePair<Pawn, EpisodeReservation> pair in episodeIndex)
            {
                PhysicalEpisode owner = pair.Value?.episode;
                if (owner != null && (ReferenceEquals(owner, episode) || owner.id == episode.id)) forgotten.Add(pair.Key);
            }
            for (int i = 0; i < forgotten.Count; i++) episodeIndex.Remove(forgotten[i]);
            List<int> thingIds = new List<int>();
            foreach (KeyValuePair<int, EpisodeReservation> pair in episodeByThing)
            {
                PhysicalEpisode owner = pair.Value?.episode;
                if (owner != null && (ReferenceEquals(owner, episode) || owner.id == episode.id)) thingIds.Add(pair.Key);
            }
            for (int i = 0; i < thingIds.Count; i++) episodeByThing.Remove(thingIds[i]);
            return forgotten;
        }

        /// <summary>
        /// Durable eligibility only: a bound slot remains owned until RELEASE completes, including quarantine and interrupted release.
        /// No map/world inference and no pointer read: this rule is usable before the load resolves PawnRefs. Positive death releases the
        /// need for living-Pawn reservation; corpse ownership stays vanilla. Named slots may overlap while the post-commit handoff finishes.
        /// </summary>
        public static bool RequiresEpisodeReservation(PhysicalEpisode e, EpisodeMember m)
        {
            return e != null && e.id.IsValid && !e.releaseApplied && m?.pawn != null && m.pawn.thingIdNumber > 0
                && m.outcome != MemberOutcome.Killed && m.observed != ObservedKind.Dead;
        }

        /// <summary>
        /// The persisted custody that keeps a bound pawn reserved: alive, and Deployed, Stored or (Phase 3.2A) OutOfCustody. M1 reserves a retained
        /// named pawn from its binding on (ADR-053); 3.2A keeps that true while vanilla holds the person (ADR-056), so a held person whom vanilla
        /// releases, who escapes or whose map is removed lands in WorldPawns as ReservedByQuest (positive evidence of a free return), never as an
        /// ordinary Free pawn that vanilla could redress, discard or give a random faction. Vanilla's own situations are unchanged by it:
        /// <c>WorldPawns.GetSituation</c> tests FactionLeader, Kidnapped and CaravanMember BEFORE ReservedByQuest, and a reservation of a pawn
        /// that is not a world pawn is read by nothing.
        /// </summary>
        public static bool RetainedCustody(KnownCharacter c)
        {
            return c != null && c.IsAlive && (c.custody == CustodyState.Deployed || c.custody == CustodyState.Stored || c.custody == CustodyState.OutOfCustody);
        }

        /// <summary>
        /// STAGE 1, at runtime build (FinalizeInit, before cross-references): the durable thing-id index, from persisted values only. It never
        /// reads <c>PawnRef.pawn</c> (null until the load resolves references). Bounded by the bound people. Returns the bindings indexed.
        /// </summary>
        public int RebuildEarly()
        {
            index.Clear();
            byThing.Clear();
            episodeIndex.Clear();
            episodeByThing.Clear();
            pointersResolved = false;
            rebuilds++;
            List<KnownCharacter> all = ctx.characters?.characters;
            if (all != null) for (int i = 0; i < all.Count; i++)
            {
                KnownCharacter c = all[i];
                PawnRef r = c?.pawn;
                if (r != null && r.thingIdNumber > 0) byThing[r.thingIdNumber] = c.id;
            }
            List<PhysicalEpisode> episodes = ctx.episodes?.episodes;
            if (episodes != null) for (int i = 0; i < episodes.Count; i++)
            {
                PhysicalEpisode e = episodes[i];
                if (e?.members == null) continue;
                for (int k = 0; k < e.members.Count; k++)
                {
                    EpisodeMember m = e.members[k];
                    if (RequiresEpisodeReservation(e, m) && m.pawn.thingIdNumber > 0)
                        episodeByThing[m.pawn.thingIdNumber] = new EpisodeReservation { episode = e, member = m };
                }
            }
            HashSet<int> ids = new HashSet<int>(byThing.Keys);
            ids.UnionWith(episodeByThing.Keys);
            return ids.Count;
        }

        /// <summary>
        /// STAGE 2, after every cross-reference is resolved (the world component's PostLoadInit; also a resume and a new game): judges each
        /// durable binding (<see cref="BindingRules.Judge"/>) and builds the pointer index from the HEALTHY ones only. The resolved pointer
        /// becomes the authority from here on. Unresolved, discarded and mismatching bindings are never indexed and never repaired: they are
        /// returned as findings. Bounded by the bound people.
        /// </summary>
        public RegistryLoadReport ResolvePointers()
        {
            RegistryLoadReport report = new RegistryLoadReport();
            index.Clear();
            episodeIndex.Clear();
            resolves++;
            List<KnownCharacter> all = ctx.characters?.characters;
            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    KnownCharacter c = all[i];
                    PawnRef r = c?.pawn;
                    if (r == null || !r.IsBound) continue;
                    report.bound++;
                    Pawn p = r.pawn;
                    BindingIntegrity v = BindingRules.Judge(true, r.thingIdNumber, p != null, p != null ? p.thingIDNumber : 0, p != null && p.Discarded);
                    if (v == BindingIntegrity.Healthy)
                    {
                        index[p] = c.id;
                        byThing[r.thingIdNumber] = c.id;
                        report.healthy++;
                    }
                    else if (c.IsAlive)
                    {
                        report.findings.Add(Finding(c, v, p));
                    }
                    if (RetainedCustody(c)) report.namedRetained++;
                }
            }
            List<PhysicalEpisode> episodes = ctx.episodes?.episodes;
            if (episodes != null) for (int i = 0; i < episodes.Count; i++)
            {
                PhysicalEpisode e = episodes[i];
                if (e?.members == null) continue;
                for (int k = 0; k < e.members.Count; k++)
                {
                    EpisodeMember m = e.members[k];
                    if (!NeedsEpisodeBindingAudit(e, m)) continue;
                    report.episodeBound++;
                    PawnRef r = m.pawn;
                    Pawn p = r.pawn;
                    BindingIntegrity v = BindingRules.Judge(true, r.thingIdNumber, p != null, p != null ? p.thingIDNumber : 0, p != null && p.Discarded);
                    if (v == BindingIntegrity.Healthy)
                    {
                        EpisodeReservation owner = new EpisodeReservation { episode = e, member = m };
                        episodeIndex[p] = owner;
                        episodeByThing[r.thingIdNumber] = owner;
                        report.episodeHealthy++;
                    }
                    else report.episodeFindings.Add(EpisodeFinding(e, m, v, p));
                }
            }
            pointersResolved = true;
            report.durableRetained = DurableRetainedCount();
            report.temporaryRequired = DurableTemporaryCount();
            report.covered = RetainedCount();
            report.namedCovered = NamedRetainedCount();
            report.temporaryCovered = TemporaryRetainedCount();
            return report;
        }

        /// <summary>Both stages at once, for a moment when every pointer is already resolved (a resume after removal preparation, tests).</summary>
        public RegistryLoadReport Rebuild()
        {
            RebuildEarly();
            return ResolvePointers();
        }

        /// <summary>A new binding's pawn (at creation, before the lifecycle writes the binding: the predicate still requires the binding).</summary>
        public void Note(Pawn p, CharacterId c)
        {
            if (p == null || !c.IsValid) return;
            index[p] = c;
            if (p.thingIDNumber > 0) byThing[p.thingIDNumber] = c;
        }

        /// <summary>
        /// Registers an already-durably-bound Episode slot before Place. It changes only derived indexes; no person is created and no
        /// ownership fact is invented. Queries recheck the current Episode/member/PawnRef, so rollback or release cannot leave a stale claim.
        /// </summary>
        public void NoteEpisode(Pawn p, EpisodeId episode, int slot)
        {
            if (p == null || !episode.IsValid) return;
            PhysicalEpisode e = ctx.episodes?.Get(episode);
            if (e?.members == null) return;
            for (int i = 0; i < e.members.Count; i++)
            {
                EpisodeMember m = e.members[i];
                if (m == null || m.slot != slot || !RequiresEpisodeReservation(e, m) || !ReferenceEquals(m.pawn.pawn, p)) continue;
                if (BindingRules.Judge(true, m.pawn.thingIdNumber, true, p.thingIDNumber, p.Discarded) != BindingIntegrity.Healthy) return;
                EpisodeReservation owner = new EpisodeReservation { episode = e, member = m };
                episodeIndex[p] = owner;
                episodeByThing[m.pawn.thingIdNumber] = owner;
                return;
            }
        }

        /// <summary>
        /// THE predicate (O(1)): is this pawn a retained named Network pawn right now? Pure read of durable truth. Once the pointer index is
        /// built (and for any binding made this session) it is reference equality plus retained custody; BEFORE it is built (a load between
        /// FinalizeInit and PostLoadInit) the durable thing id answers through <see cref="BindingRules.BridgeCovers"/>.
        /// </summary>
        public bool Reserves(Pawn p)
        {
            queries++;
            if (inert || p == null) return false;
            if (!NamedCovers(p) && !EpisodeCovers(p, out _)) return false;
            hits++;
            return true;
        }

        private bool NamedCovers(Pawn p)
        {
            if (p == null || p.Discarded) return false;
            CharacterId id;
            if (index.TryGetValue(p, out id))
            {
                KnownCharacter c = ctx.characters?.Get(id);
                if (c?.pawn != null && ReferenceEquals(c.pawn.pawn, p) && c.pawn.thingIdNumber == p.thingIDNumber && RetainedCustody(c)) return true;
            }
            if (pointersResolved || p.thingIDNumber <= 0 || !byThing.TryGetValue(p.thingIDNumber, out id)) return false;
            KnownCharacter bound = ctx.characters?.Get(id);
            PawnRef r = bound?.pawn;
            if (r == null || !RetainedCustody(bound)) return false;
            return BindingRules.BridgeCovers(p.thingIDNumber, r.thingIdNumber, r.pawn != null, ReferenceEquals(r.pawn, p));
        }

        private bool EpisodeCovers(Pawn p, out EpisodeReservation owner)
        {
            owner = null;
            if (p == null || p.Discarded || p.Destroyed || (p.health != null && p.Dead)) return false;
            EpisodeReservation found;
            if (episodeIndex.TryGetValue(p, out found) && CurrentOwner(found)
                && ReferenceEquals(found.member.pawn.pawn, p) && found.member.pawn.thingIdNumber == p.thingIDNumber)
            {
                owner = found;
                return true;
            }
            if (pointersResolved || p.thingIDNumber <= 0 || !episodeByThing.TryGetValue(p.thingIDNumber, out found) || !CurrentOwner(found)) return false;
            PawnRef r = found.member.pawn;
            if (!BindingRules.BridgeCovers(p.thingIDNumber, r.thingIdNumber, r.pawn != null, ReferenceEquals(r.pawn, p))) return false;
            owner = found;
            return true;
        }

        private bool CurrentOwner(EpisodeReservation owner)
        {
            PhysicalEpisode e = owner?.episode;
            EpisodeMember m = owner?.member;
            return RequiresEpisodeReservation(e, m) && ReferenceEquals(ctx.episodes?.Get(e.id), e) && e.members.Contains(m);
        }

        private static bool NeedsEpisodeBindingAudit(PhysicalEpisode e, EpisodeMember m)
        {
            return e != null && e.id.IsValid && !e.releaseApplied && m != null && m.IsBound
                && m.outcome != MemberOutcome.Killed && m.observed != ObservedKind.Dead;
        }

        /// <summary>Effective category: named M1 takes precedence over its Episode handoff bridge.</summary>
        public bool IsTemporaryReserved(Pawn p)
        {
            return !inert && !NamedCovers(p) && EpisodeCovers(p, out _);
        }

        /// <summary>The Episode that still owns this bound Pawn, or None. No lookup scans Pawns or the Episode store.</summary>
        public EpisodeId EpisodeOf(Pawn p)
        {
            EpisodeReservation owner;
            return !inert && EpisodeCovers(p, out owner) ? owner.episode.id : EpisodeId.None;
        }

        public CharacterId CharacterOf(Pawn p)
        {
            CharacterId id;
            if (p == null) return CharacterId.None;
            if (index.TryGetValue(p, out id))
            {
                PawnRef binding = ctx.characters?.Get(id)?.pawn;
                if (binding != null && ReferenceEquals(binding.pawn, p) && binding.thingIdNumber == p.thingIDNumber) return id;
            }
            if (!pointersResolved && p.thingIDNumber > 0 && byThing.TryGetValue(p.thingIDNumber, out id))
            {
                PawnRef r = ctx.characters?.Get(id)?.pawn;
                if (r != null && BindingRules.BridgeCovers(p.thingIDNumber, r.thingIdNumber, r.pawn != null, ReferenceEquals(r.pawn, p))) return id;
            }
            return CharacterId.None;
        }

        /// <summary>
        /// How many bound people the registry covers right now (R in the § 7.4 cost). Before the pointer index exists it is the DURABLE count:
        /// an unresolved pointer is never read as "nobody is retained".
        /// </summary>
        public int RetainedCount()
        {
            if (inert) return 0;
            if (!pointersResolved) return DurableRetainedCount();
            HashSet<Pawn> covered = new HashSet<Pawn>(PawnReferenceComparer.Instance);
            foreach (KeyValuePair<Pawn, CharacterId> kv in index) if (Reserves(kv.Key)) covered.Add(kv.Key);
            foreach (KeyValuePair<Pawn, EpisodeReservation> kv in episodeIndex) if (Reserves(kv.Key)) covered.Add(kv.Key);
            return covered.Count;
        }

        public int NamedRetainedCount()
        {
            if (inert) return 0;
            if (!pointersResolved) return DurableNamedCount();
            int n = 0;
            foreach (KeyValuePair<Pawn, CharacterId> kv in index) if (!inert && NamedCovers(kv.Key)) n++;
            return n;
        }

        public int TemporaryRetainedCount()
        {
            if (inert) return 0;
            if (!pointersResolved) return DurableTemporaryCount();
            int n = 0;
            foreach (KeyValuePair<Pawn, EpisodeReservation> kv in episodeIndex) if (IsTemporaryReserved(kv.Key)) n++;
            return n;
        }

        /// <summary>
        /// Distinct named bindings plus unreleased Episode bindings that need living-Pawn protection. Missing pointers never mean no
        /// reservation is required; an already resolved, positively dead Episode Pawn is excluded. Named/slot overlap counts one binding.
        /// This reads only Network-owned bindings, never a world Pawn population.
        /// </summary>
        public int DurableRetainedCount()
        {
            HashSet<int> ids = DurableNamedIds();
            AddEpisodeIds(ids);
            return ids.Count;
        }

        public int DurableNamedCount()
        {
            return DurableNamedIds().Count;
        }

        /// <summary>Distinct Episode bindings that are not already represented by a durable named-retention binding.</summary>
        public int DurableTemporaryCount()
        {
            HashSet<int> named = DurableNamedIds();
            HashSet<int> episode = new HashSet<int>();
            AddEpisodeIds(episode);
            episode.ExceptWith(named);
            return episode.Count;
        }

        private HashSet<int> DurableNamedIds()
        {
            HashSet<int> ids = new HashSet<int>();
            List<KnownCharacter> all = ctx.characters?.characters;
            if (all == null) return ids;
            for (int i = 0; i < all.Count; i++)
            {
                KnownCharacter c = all[i];
                if (RetainedCustody(c) && c.pawn != null && c.pawn.IsBound) ids.Add(c.pawn.thingIdNumber);
            }
            return ids;
        }

        private void AddEpisodeIds(HashSet<int> ids)
        {
            List<PhysicalEpisode> episodes = ctx.episodes?.episodes;
            if (episodes == null) return;
            for (int i = 0; i < episodes.Count; i++)
            {
                PhysicalEpisode e = episodes[i];
                if (e?.members == null) continue;
                for (int k = 0; k < e.members.Count; k++)
                {
                    EpisodeMember m = e.members[k];
                    if (!NeedsEpisodeBindingAudit(e, m)) continue;
                    Pawn p = m.pawn.pawn;
                    if (p != null && (p.Destroyed || (p.health != null && p.Dead))) continue;
                    ids.Add(m.pawn.thingIdNumber);
                }
            }
        }

        /// <summary>
        /// The bounded, READ-ONLY integrity audit (post-load validation, § 16.5): every LIVING bound person whose binding pointer is
        /// unresolved after the load's cross-references, resolves to a Discarded pawn, or disagrees with its persisted thing id. It changes
        /// nothing: no pawn is generated, no binding cleared, nobody marked healthy.
        /// </summary>
        public List<BindingFinding> Audit()
        {
            List<BindingFinding> findings = new List<BindingFinding>();
            List<KnownCharacter> all = ctx.characters?.characters;
            if (all == null) return findings;
            for (int i = 0; i < all.Count; i++)
            {
                KnownCharacter c = all[i];
                PawnRef r = c?.pawn;
                if (r == null || !r.IsBound || !c.IsAlive) continue;
                Pawn p = r.pawn;
                BindingIntegrity v = BindingRules.Judge(true, r.thingIdNumber, p != null, p != null ? p.thingIDNumber : 0, p != null && p.Discarded);
                if (v != BindingIntegrity.Healthy) findings.Add(Finding(c, v, p));
            }
            return findings;
        }

        /// <summary>Read-only integrity findings for unreleased Episode bindings, separately identified from named people.</summary>
        public List<EpisodeBindingFinding> AuditTemporary()
        {
            List<EpisodeBindingFinding> findings = new List<EpisodeBindingFinding>();
            List<PhysicalEpisode> episodes = ctx.episodes?.episodes;
            if (episodes == null) return findings;
            for (int i = 0; i < episodes.Count; i++)
            {
                PhysicalEpisode e = episodes[i];
                if (e?.members == null) continue;
                for (int k = 0; k < e.members.Count; k++)
                {
                    EpisodeMember m = e.members[k];
                    if (!NeedsEpisodeBindingAudit(e, m)) continue;
                    Pawn p = m.pawn.pawn;
                    BindingIntegrity v = BindingRules.Judge(true, m.pawn.thingIdNumber, p != null, p != null ? p.thingIDNumber : 0, p != null && p.Discarded);
                    if (v != BindingIntegrity.Healthy) findings.Add(EpisodeFinding(e, m, v, p));
                }
            }
            return findings;
        }

        private static EpisodeBindingFinding EpisodeFinding(PhysicalEpisode e, EpisodeMember m, BindingIntegrity v, Pawn p)
        {
            return new EpisodeBindingFinding
            {
                episode = e.id, slot = m.slot, kind = v, persistedThingId = m.pawn.thingIdNumber,
                pointerThingId = p != null ? p.thingIDNumber : 0
            };
        }

        private static BindingFinding Finding(KnownCharacter c, BindingIntegrity v, Pawn p)
        {
            return new BindingFinding
            {
                id = c.id, name = c.name?.Display, custody = c.custody, kind = v,
                persistedThingId = c.pawn.thingIdNumber, pointerThingId = p != null ? p.thingIDNumber : 0
            };
        }

        // ================================================================== the registry quest (vanilla-owned container, Network-owned part)

        /// <summary>
        /// The live registry quest, or null. Only an ONGOING quest answers vanilla's reservation query (QuestReserves), so only an ongoing one
        /// counts as live. Bounded by the quest list; called at creation, placement, load and release only.
        /// </summary>
        public Quest FindQuest()
        {
            if (cachedQuest != null && cachedQuest.State == QuestState.Ongoing && Find.QuestManager.QuestsListForReading.Contains(cachedQuest)) return cachedQuest;
            cachedQuest = null;
            List<Quest> all = Find.QuestManager?.QuestsListForReading;
            if (all == null) return null;
            for (int i = 0; i < all.Count; i++)
            {
                Quest q = all[i];
                if (q == null || q.State != QuestState.Ongoing) continue;
                if (q.GetFirstPartOfType<QuestPart_NetworkRetainedPawns>() != null)
                {
                    cachedQuest = q;
                    return q;
                }
            }
            return null;
        }

        /// <summary>
        /// The registry quest exists and is accepted (Ongoing, so QuestReserves answers). Created once, lazily, the first time a pawn is
        /// bound: a hidden raw quest rooted on a vanilla utility script (a root is mandatory: QuestManager drops a root-less quest on load),
        /// with the one Network-owned part. Throws when no root exists (the adapter is then unavailable: fail closed).
        /// </summary>
        public Quest EnsureQuest()
        {
            if (inert) throw new InvalidOperationException("the Network is prepared for removal: no registry quest is created");
            Quest q = FindQuest();
            if (q != null) return q;
            QuestScriptDef root = QuestScriptDefOf.Util_GetDefaultRewardValueFromPoints;
            if (root == null) throw new InvalidOperationException("the vanilla QuestScriptDef Util_GetDefaultRewardValueFromPoints is missing: no registry quest can be rooted");
            q = Quest.MakeRaw();
            q.root = root;
            q.hidden = true;
            q.hiddenInUI = true;
            q.name = QuestName;
            QuestUtility.AddQuestTag(ref q.tags, QuestTag);
            q.AddPart(new QuestPart_NetworkRetainedPawns());
            q.SetInitiallyAccepted();
            Find.QuestManager.Add(q);
            cachedQuest = q;
            questsCreated++;
            NetLog.Info(LogCategory.Physical, "Retained-pawn registry quest created (quest " + q.id + ", hidden, " + q.State + ", root " + root.defName + ").");
            return q;
        }

        /// <summary>Prepare-for-removal (§ 20 step 2): reserve nobody, then end and remove the registry quest through vanilla's own API.</summary>
        public int Release()
        {
            inert = true;
            Quest q = FindQuest();
            if (q == null) return 0;
            if (!q.Historical) q.End(QuestEndOutcome.Unknown, false, false);
            if (Find.QuestManager.QuestsListForReading.Contains(q)) Find.QuestManager.Remove(q);
            cachedQuest = null;
            return 1;
        }

        /// <summary>Resume after a removal preparation: reserve again from the stores and recreate the quest if anyone is retained.</summary>
        public void Resume()
        {
            inert = false;
            Rebuild();
            if (DurableRetainedCount() > 0) EnsureQuest();
        }

        /// <summary>A vanilla notification through the quest part (a reserved pawn was killed or discarded): wake the person's episode.</summary>
        public void OnPawnEvent(Pawn p, string what)
        {
            CharacterId id = CharacterOf(p);
            if (!id.IsValid)
            {
                // A killed Pawn no longer satisfies the living reservation predicate, but its indexed Episode must still wake.
                EpisodeReservation owner;
                if (p != null && episodeIndex.TryGetValue(p, out owner) && CurrentOwner(owner)
                    && ReferenceEquals(owner.member.pawn.pawn, p)) ctx.Lifecycle?.Wake(owner.episode, what);
                return;
            }
            KnownCharacter c = ctx.characters?.Get(id);
            if (c == null || c.pawn == null || !ReferenceEquals(c.pawn.pawn, p)) return;
            if (!c.IsAlive) return; // a dead person's corpse going away (a removed map) changes nothing the Network holds
            PhysicalEpisode e = c.episode.IsValid ? ctx.episodes?.Get(c.episode) : null;
            if (e != null) ctx.Lifecycle?.Wake(e, what);
            else if (PhysicalLifecycleService.IsHeld(c)) ctx.Lifecycle?.WakeHeld(c, what); // 3.2A: the custody watch observes and decides
            else NetLog.WarnOnce(LogCategory.Physical, "retained." + what + "." + id.Value, "Retained person " + id + " (" + c.name?.Display + ", " + c.custody + ") was " + what + " while not in an episode; recorded at their next observation.");
        }

        /// <summary>Measures the reservation lookup over the given pawns (read-only; RT-PHYX-007). Returns microseconds per query.</summary>
        public double MeasureLookup(IList<Pawn> pawns, int rounds, out int reserved)
        {
            reserved = 0;
            if (pawns == null || pawns.Count == 0 || rounds <= 0) return 0;
            Stopwatch sw = Stopwatch.StartNew();
            int n = 0;
            for (int r = 0; r < rounds; r++)
            {
                for (int i = 0; i < pawns.Count; i++)
                {
                    if (Reserves(pawns[i]) && r == 0) reserved++;
                    n++;
                }
            }
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds * 1000.0 / n;
        }

        public string Describe()
        {
            Quest q = FindQuest();
            return "registry: " + (inert ? "INERT (prepared for removal)" : "active") + ", " + (pointersResolved ? "pointer index" : "thing-id bridge (pointers not yet resolved)")
                + " " + (pointersResolved ? index.Count : byThing.Count) + ", retained " + RetainedCount() + " of " + DurableRetainedCount() + " durable"
                + " (named " + NamedRetainedCount() + ", temporary Episode " + TemporaryRetainedCount() + ")"
                + ", quest " + (q == null ? "none" : q.id + " " + q.State) + ", queries " + queries + " (hits " + hits + ")";
        }
    }

    /// <summary>What the post-load pointer resolution found (runtime only, never persisted).</summary>
    public sealed class RegistryLoadReport
    {
        /// <summary>Living or dead people with a persisted binding.</summary>
        public int bound;

        /// <summary>Bindings whose pointer resolved to a live pawn with the persisted thing id.</summary>
        public int healthy;

        /// <summary>People the durable state says must be reserved (living, bound, Deployed, Stored or OutOfCustody).</summary>
        public int durableRetained;

        /// <summary>People the registry actually covers now. Below <see cref="durableRetained"/> is a reservation gap.</summary>
        public int covered;

        public int namedRetained;
        public int namedCovered;
        public int episodeBound;
        public int episodeHealthy;
        public int temporaryRequired;
        public int temporaryCovered;

        /// <summary>Living people with an unresolved, discarded or mismatching binding. Reported loudly, never repaired.</summary>
        public readonly List<BindingFinding> findings = new List<BindingFinding>();
        public readonly List<EpisodeBindingFinding> episodeFindings = new List<EpisodeBindingFinding>();
        public int FindingCount => findings.Count + episodeFindings.Count;

        public override string ToString()
        {
            return bound + " named bound, " + healthy + " named healthy, " + episodeBound + " Episode bound, " + episodeHealthy + " Episode healthy, "
                + covered + " of " + durableRetained + " retained covered (named " + namedCovered + ", temporary " + temporaryCovered + "), " + FindingCount + " integrity finding(s)";
        }
    }

    /// <summary>A durable Episode slot with a bad binding; never represented as a dummy person and never repaired.</summary>
    public sealed class EpisodeBindingFinding
    {
        public EpisodeId episode;
        public int slot;
        public BindingIntegrity kind;
        public int persistedThingId;
        public int pointerThingId;

        public override string ToString()
        {
            return "PHYSICAL INTEGRITY: Episode " + episode + " slot " + slot + " is bound to pawn #" + persistedThingId + ": " + BindingRules.Describe(kind)
                + (kind == BindingIntegrity.IdMismatch ? " (persisted #" + persistedThingId + ", resolved #" + pointerThingId + ")" : "")
                + ". Nothing was regenerated, cleared or marked healthy.";
        }
    }

    /// <summary>
    /// The Network-owned reservation part (S9r's default, § 7.4): it answers QuestPartReserves from the running Network's registry and keeps
    /// NO pawn list of its own. On an unprepared removal vanilla cannot find this class, drops the part with an error and the pawns become
    /// ordinary world pawns: the self-healing property the design prefers over a vanilla-only part that would keep them reserved forever.
    /// THE CLASS NAME IS SAVED: never rename or move it.
    /// </summary>
    public sealed class QuestPart_NetworkRetainedPawns : QuestPart
    {
        public override bool QuestPartReserves(Pawn p)
        {
            RetainedPawnRegistry r = RetainedPawnRegistry.Active;
            return r != null && r.Reserves(p);
        }

        public override void Notify_PawnKilled(Pawn pawn, DamageInfo? dinfo)
        {
            try
            {
                RetainedPawnRegistry.Active?.OnPawnEvent(pawn, "Killed");
            }
            catch (Exception ex)
            {
                NetLog.ErrorOnce(LogCategory.Physical, "registry.killed", "Registry kill notification failed: " + ex.Message);
            }
        }

        public override void Notify_PawnDiscarded(Pawn pawn)
        {
            try
            {
                RetainedPawnRegistry.Active?.OnPawnEvent(pawn, "Discarded");
            }
            catch (Exception ex)
            {
                NetLog.ErrorOnce(LogCategory.Physical, "registry.discarded", "Registry discard notification failed: " + ex.Message);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
        }
    }
}
