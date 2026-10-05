using System;
using System.Collections.Generic;
using RimWorld;
using TheNetwork.Domain.Physical;
using TheNetwork.Kernel;
using Verse;

namespace TheNetwork.Integration.Physical
{
    /// <summary>
    /// The per-episode temporary encounter faction (PHYSICAL_LIFECYCLE § 13.2, S10), built exactly the way vanilla's own refugee and beggar
    /// quests build theirs: FactionGenerator.NewGeneratedFactionWithRelations(def, neutral relations, hidden: true), temporary = true,
    /// FactionManager.Add. Hidden means no settlement is generated. The faction is NOT the actor (the actor stays a Network actor) and it
    /// is never provenance. Vanilla removes it once nothing spawned is in it (and nulls its pawns' faction, which a reserved retained pawn
    /// tolerates); RELEASE hands it back to that removal for the paths vanilla does not queue (a map removal).
    /// </summary>
    public static class EncounterFactions
    {
        /// <summary>
        /// The def by capability, never by mod name: a hidden, humanlike, non-player faction that is not permanently hostile, has a humanlike basic
        /// member kind AND offers at least one GENERIC member (<see cref="FactionMemberKinds"/>: a basic member kind that is a boss, a leader,
        /// a royal, a cultist or a forced-xenotype kind does not count). Vanilla's own OutlanderRefugee (the def its hidden temporary refugee
        /// factions use) is preferred when it qualifies; otherwise the first qualifying def in name order. Null when none exists (the episode
        /// then places nobody, rather than borrowing a special-purpose kind).
        /// </summary>
        public static FactionDef ChooseDef()
        {
            FactionDef preferred = FactionDefOf.OutlanderRefugee;
            if (Qualifies(preferred)) return preferred;
            List<FactionDef> all = new List<FactionDef>(DefDatabase<FactionDef>.AllDefsListForReading);
            all.Sort((a, b) => string.CompareOrdinal(a.defName, b.defName));
            for (int i = 0; i < all.Count; i++) if (Qualifies(all[i])) return all[i];
            return null;
        }

        public static bool Qualifies(FactionDef d)
        {
            // Vanilla TryEnslavePrisoner clears the old faction only for a HIDDEN DEF. The instance's hidden override does not count;
            // otherwise SetGuestStatus caches our temporary faction as slaveFaction AFTER SetFaction can already have queued its removal.
            return d != null && d.hidden && d.humanlikeFaction && !d.isPlayer && !d.permanentEnemy && d.basicMemberKind != null && d.basicMemberKind.RaceProps != null
                && d.basicMemberKind.RaceProps.Humanlike && FactionMemberKinds.HasGenericPool(d);
        }

        /// <summary>Returns <paramref name="current"/> when it is still a live temporary faction; otherwise creates the episode's faction.</summary>
        public static FactionRef Ensure(EpisodeId episode, string actorName, FactionRef current, int goodwill)
        {
            Faction live = current?.Resolve();
            if (live != null && live.temporary) return current;
            FactionDef def = ChooseDef();
            if (def == null) throw new InvalidOperationException("no hidden, humanlike, non-player, non-hostile FactionDef with generic members is loaded for a temporary encounter faction");
            List<FactionRelation> relations = new List<FactionRelation>();
            foreach (Faction other in Find.FactionManager.AllFactionsListForReading)
            {
                if (other.def.PermanentlyHostileTo(def)) continue;
                relations.Add(new FactionRelation { other = other, kind = FactionRelationKind.Neutral });
            }
            Faction f = FactionGenerator.NewGeneratedFactionWithRelations(def, relations, true);
            f.temporary = true;
            if (!string.IsNullOrEmpty(actorName)) f.Name = actorName;
            Find.FactionManager.Add(f);
            // Goodwill towards the player is seeded ONCE from the Network relation (§ 13.3); the kind stays Neutral (never a hostile visit in 3.1).
            Faction player = Faction.OfPlayer;
            if (player != null)
            {
                FactionRelation toPlayer = f.RelationWith(player, true);
                if (toPlayer != null) toPlayer.baseGoodwill = goodwill;
                FactionRelation fromPlayer = player.RelationWith(f, true);
                if (fromPlayer != null) fromPlayer.baseGoodwill = goodwill;
            }
            NetLog.Info(LogCategory.Physical, "Encounter faction for " + episode + ": " + f.loadID + " \"" + f.Name + "\" (def " + def.defName + ", hidden, temporary, goodwill " + goodwill + ").");
            return FactionRef.Of(f);
        }

        /// <summary>
        /// RELEASE: hand the faction to vanilla's own removal (FactionManager.Notify_PawnLeftFaction queues it only if vanilla agrees it can
        /// be removed). Idempotent: an already-removed faction resolves to nothing; a non-temporary faction is never touched.
        /// </summary>
        public static bool Release(FactionRef r)
        {
            Faction f = r?.Resolve();
            if (f == null || !f.temporary) return false;
            Find.FactionManager.Notify_PawnLeftFaction(f);
            return true;
        }
    }

    /// <summary>
    /// Truthful aging (PHYSICAL_LIFECYCLE § 6.4, S12, the aging mechanism M1 of that section, not the reservation M1): vanilla's own mothball path, Pawn_AgeTracker.AgeTickMothballed, over the
    /// FULL elapsed interval, uncapped, chunked per game year (it takes an int, and the child/adult rate is re-read each chunk). It crosses
    /// every birthday with vanilla's consequences. BirthAbsTicks is never written, so chronological age stays derived and truthful.
    ///
    /// ATOMICITY (audited in the 1.6 assembly): <c>AgeTickMothballed</c> is NOT atomic. It advances the biological ticks by the whole step
    /// (<c>TickBiologicalAge</c>) FIRST, then runs growth, the age-reversal check and one <c>BirthdayBiological</c> per birthday crossed (hediff
    /// rolls, bed unclaim, work-type unlocks, growth moments, mod patches), with no try/catch and no rollback. A throw inside it leaves the pawn
    /// older by the whole step with an unknowable number of birthday effects applied. So only the steps that RETURNED are known to be fully
    /// applied; a step that threw is reported as <see cref="AgingUncertainException"/> (never as "the part that was applied"), and nothing here
    /// retries or compensates for it.
    /// </summary>
    public static class PawnAging
    {
        public const int ChunkTicks = 3600000;

        /// <summary>Ages the pawn by the full interval and returns the ticks applied (always the whole interval: a failure throws).</summary>
        public static long CatchUp(Pawn p, long elapsedTicks)
        {
            if (p == null) throw new InvalidOperationException("no pawn to age");
            if (elapsedTicks <= 0) return 0;
            Pawn_AgeTracker age = p.ageTracker;
            if (age == null) throw new InvalidOperationException(p + " has no age tracker");
            long completed = 0;
            while (completed < elapsedTicks)
            {
                int step = (int)Math.Min(ChunkTicks, elapsedTicks - completed);
                long before = age.AgeBiologicalTicks;
                try
                {
                    age.AgeTickMothballed(step);
                }
                catch (Exception ex)
                {
                    // Not provable how far the step got: report what completed and what is uncertain, with the measured evidence.
                    throw new AgingUncertainException(completed, step, before, age.AgeBiologicalTicks, ex);
                }
                completed += step;
            }
            return completed;
        }
    }

    /// <summary>
    /// Store-time normalization (PHYSICAL_LIFECYCLE § 10.5–10.6, S12): a returned named pawn's TEMPORARY injuries, already converted by the
    /// commit into the abstract recovery (woundedUntilTick), are healed on the pawn through vanilla's hediff API, so recovery runs once and
    /// the frozen pawn carries no wound the abstract record heals again. Permanent truth (scars, missing parts, implants, chronic
    /// conditions) stays. Unknown modded hediffs are left alone. Idempotent by observed state.
    /// </summary>
    public static class PawnNormalization
    {
        public static int Normalize(Pawn p)
        {
            if (p == null || p.Dead || p.Discarded || p.health?.hediffSet == null) return 0;
            List<Hediff> temporary = new List<Hediff>();
            List<Hediff> all = p.health.hediffSet.hediffs;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] is Hediff_Injury injury && !injury.IsPermanent()) temporary.Add(injury);
            }
            for (int i = 0; i < temporary.Count; i++) p.health.RemoveHediff(temporary[i]);
            return temporary.Count;
        }

        /// <summary>The non-permanent hediffs normalization leaves (measured, never guessed away): their def names.</summary>
        public static List<string> Remaining(Pawn p)
        {
            List<string> r = new List<string>();
            if (p?.health?.hediffSet == null) return r;
            foreach (Hediff h in p.health.hediffSet.hediffs) if (!(h is Hediff_Injury) && !h.IsPermanent() && h.def != null && h.def.isBad) r.Add(h.def.defName);
            return r;
        }

        /// <summary>
        /// The catch-up's needs rule (§ 6.4): a stored pawn's needs froze at its exit; the organization fed and rested it off-map, so food,
        /// rest and recreation come back no lower than comfortable. Never lowered; mood, memories and everything else untouched.
        /// </summary>
        public static void ComfortableNeeds(Pawn p)
        {
            if (p?.needs == null) return;
            Raise(p.needs.food, 0.8f);
            Raise(p.needs.rest, 0.8f);
            Raise(p.needs.joy, 0.6f);
        }

        private static void Raise(Need n, float to)
        {
            if (n != null && n.CurLevelPercentage < to) n.CurLevelPercentage = to;
        }
    }
}
