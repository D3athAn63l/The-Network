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
    /// <see cref="PawnRef"/>) of a living named person whose custody is <see cref="CustodyState.Deployed"/> or <see cref="CustodyState.Stored"/>.
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
        private Quest cachedQuest;

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

        /// <summary>The persisted custody that keeps a bound pawn reserved (alive, Deployed or Stored).</summary>
        public static bool RetainedCustody(KnownCharacter c)
        {
            return c != null && c.IsAlive && (c.custody == CustodyState.Deployed || c.custody == CustodyState.Stored);
        }

        /// <summary>
        /// STAGE 1, at runtime build (FinalizeInit, before cross-references): the durable thing-id index, from persisted values only. It never
        /// reads <c>PawnRef.pawn</c> (null until the load resolves references). Bounded by the bound people. Returns the bindings indexed.
        /// </summary>
        public int RebuildEarly()
        {
            index.Clear();
            byThing.Clear();
            pointersResolved = false;
            rebuilds++;
            if (ctx.characters == null) return 0;
            List<KnownCharacter> all = ctx.characters.characters;
            for (int i = 0; i < all.Count; i++)
            {
                KnownCharacter c = all[i];
                PawnRef r = c?.pawn;
                if (r != null && r.thingIdNumber > 0) byThing[r.thingIdNumber] = c.id;
            }
            return byThing.Count;
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
                    if (RetainedCustody(c)) report.durableRetained++;
                }
            }
            pointersResolved = true;
            report.covered = RetainedCount();
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
        /// THE predicate (O(1)): is this pawn a retained named Network pawn right now? Pure read of durable truth. Once the pointer index is
        /// built (and for any binding made this session) it is reference equality plus retained custody; BEFORE it is built (a load between
        /// FinalizeInit and PostLoadInit) the durable thing id answers through <see cref="BindingRules.BridgeCovers"/>.
        /// </summary>
        public bool Reserves(Pawn p)
        {
            queries++;
            if (inert || p == null) return false;
            CharacterId id;
            if (index.TryGetValue(p, out id))
            {
                KnownCharacter c = ctx.characters?.Get(id);
                if (c?.pawn == null || !ReferenceEquals(c.pawn.pawn, p) || !RetainedCustody(c)) return false;
                hits++;
                return true;
            }
            if (pointersResolved || p.thingIDNumber <= 0 || !byThing.TryGetValue(p.thingIDNumber, out id)) return false;
            KnownCharacter bound = ctx.characters?.Get(id);
            PawnRef r = bound?.pawn;
            if (r == null || !RetainedCustody(bound)) return false;
            if (!BindingRules.BridgeCovers(p.thingIDNumber, r.thingIdNumber, r.pawn != null, ReferenceEquals(r.pawn, p))) return false;
            hits++;
            return true;
        }

        public CharacterId CharacterOf(Pawn p)
        {
            CharacterId id;
            if (p == null) return CharacterId.None;
            if (index.TryGetValue(p, out id)) return id;
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
            if (!pointersResolved) return DurableRetainedCount();
            int n = 0;
            foreach (KeyValuePair<Pawn, CharacterId> kv in index) if (Reserves(kv.Key)) n++;
            return n;
        }

        /// <summary>
        /// The people the durable state says MUST be reserved: living, bound, custody Deployed or Stored. Read from persisted values only
        /// (no pointer), so it is right at every stage of a load. A covered count below this is a reservation gap.
        /// </summary>
        public int DurableRetainedCount()
        {
            List<KnownCharacter> all = ctx.characters?.characters;
            int n = 0;
            if (all == null) return 0;
            for (int i = 0; i < all.Count; i++)
            {
                KnownCharacter c = all[i];
                if (RetainedCustody(c) && c.pawn != null && c.pawn.IsBound) n++;
            }
            return n;
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
            if (!id.IsValid) return;
            KnownCharacter c = ctx.characters?.Get(id);
            if (c == null || c.pawn == null || !ReferenceEquals(c.pawn.pawn, p)) return;
            if (!c.IsAlive) return; // a dead person's corpse going away (a removed map) changes nothing the Network holds
            PhysicalEpisode e = c.episode.IsValid ? ctx.episodes?.Get(c.episode) : null;
            if (e != null) ctx.Lifecycle?.Wake(e, what);
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

        /// <summary>People the durable state says must be reserved (living, bound, Deployed or Stored).</summary>
        public int durableRetained;

        /// <summary>People the registry actually covers now. Below <see cref="durableRetained"/> is a reservation gap.</summary>
        public int covered;

        /// <summary>Living people with an unresolved, discarded or mismatching binding. Reported loudly, never repaired.</summary>
        public readonly List<BindingFinding> findings = new List<BindingFinding>();

        public override string ToString()
        {
            return bound + " bound, " + healthy + " healthy, " + covered + " of " + durableRetained + " retained covered, " + findings.Count + " integrity finding(s)";
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
