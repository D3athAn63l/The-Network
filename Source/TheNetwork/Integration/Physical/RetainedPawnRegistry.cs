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
    /// QuestPartReserves asks this registry. The part persists NO pawn list: the Network stores are the single truth, and the runtime index
    /// (pawn → character) is rebuilt from them when the runtime is built (FinalizeInit), before the first tick. No runtime ⇒ the part answers
    /// false (fail safe on an unprepared removal: vanilla drops the unknown part class and the pawns become ordinary world pawns).
    ///
    /// Cost: one dictionary lookup per vanilla reservation query, instead of a list scan; nothing runs per tick of its own.
    /// </summary>
    public sealed class RetainedPawnRegistry
    {
        public const string QuestTag = "TheNetwork.RetainedRegistry";
        public const string QuestName = "The Network: retained people (hidden)";

        private readonly DomainContext ctx;
        private readonly Dictionary<Pawn, CharacterId> index = new Dictionary<Pawn, CharacterId>(PawnReferenceComparer.Instance);
        private Quest cachedQuest;

        /// <summary>Prepared for removal: the registry reserves nobody (§ 20 step 2).</summary>
        public bool inert;

        // runtime diagnostics (never persisted)
        public long queries;
        public long hits;
        public int rebuilds;
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

        /// <summary>Rebuilds the runtime index from the characters store (bounded by the bound people; at runtime build and on resume).</summary>
        public int Rebuild()
        {
            index.Clear();
            rebuilds++;
            if (ctx.characters == null) return 0;
            List<KnownCharacter> all = ctx.characters.characters;
            for (int i = 0; i < all.Count; i++)
            {
                KnownCharacter c = all[i];
                Pawn p = c?.pawn?.pawn;
                if (p != null) index[p] = c.id;
            }
            return index.Count;
        }

        /// <summary>A new binding's pawn (at creation, before the lifecycle writes the binding: the predicate still requires the binding).</summary>
        public void Note(Pawn p, CharacterId c)
        {
            if (p != null && c.IsValid) index[p] = c;
        }

        /// <summary>THE predicate (O(1)): is this pawn a retained named Network pawn right now? Pure read of durable truth.</summary>
        public bool Reserves(Pawn p)
        {
            queries++;
            if (inert || p == null) return false;
            CharacterId id;
            if (!index.TryGetValue(p, out id)) return false;
            KnownCharacter c = ctx.characters?.Get(id);
            if (c?.pawn == null || !ReferenceEquals(c.pawn.pawn, p) || !RetainedCustody(c)) return false;
            hits++;
            return true;
        }

        public CharacterId CharacterOf(Pawn p)
        {
            CharacterId id;
            return p != null && index.TryGetValue(p, out id) ? id : CharacterId.None;
        }

        /// <summary>How many bound people the registry covers right now (R in the § 7.4 cost).</summary>
        public int RetainedCount()
        {
            int n = 0;
            foreach (KeyValuePair<Pawn, CharacterId> kv in index) if (Reserves(kv.Key)) n++;
            return n;
        }

        // ================================================================== the registry quest (vanilla-owned container, Network-owned part)

        /// <summary>The live registry quest, or null. Bounded by the quest list; called at creation, placement, load and release only.</summary>
        public Quest FindQuest()
        {
            if (cachedQuest != null && !cachedQuest.Historical && Find.QuestManager.QuestsListForReading.Contains(cachedQuest)) return cachedQuest;
            cachedQuest = null;
            List<Quest> all = Find.QuestManager?.QuestsListForReading;
            if (all == null) return null;
            for (int i = 0; i < all.Count; i++)
            {
                Quest q = all[i];
                if (q == null || q.Historical) continue;
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
            if (RetainedCount() > 0) EnsureQuest();
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
            return "registry: " + (inert ? "INERT (prepared for removal)" : "active") + ", index " + index.Count + ", retained " + RetainedCount()
                + ", quest " + (q == null ? "none" : q.id + " " + q.State) + ", queries " + queries + " (hits " + hits + ")";
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
