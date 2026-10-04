using System;
using System.Collections.Generic;
using RimWorld;
using TheNetwork.Domain.Physical;
using Verse;

namespace TheNetwork.Integration.Physical
{
    /// <summary>
    /// The GENERIC member pool of one faction (Phase 3.1 runtime-QA correction): the kinds a first projection may draw from, read from the
    /// FACTION'S OWN def, never from a scan of every loaded kind. Candidates are the faction's basic member kind and the kinds its Combat and
    /// Peaceful pawn groups list; <see cref="GenericMemberPolicy"/> admits only the ones that carry no special-purpose content (boss, leader,
    /// royal title, mutant, fixed backstory, built-in conditions or abilities, forced traits or xenotype, trader, another faction's kind).
    /// Plain def facts only: no def name is ever consulted, so a modded faction or race is judged by what its kinds are.
    /// </summary>
    public sealed class FactionMemberPool
    {
        public FactionDef faction;

        /// <summary>Every candidate the faction offers, as plain facts (the policy's input).</summary>
        public readonly List<MemberKindFacts> candidates = new List<MemberKindFacts>();

        /// <summary>The generic members, in the order the projection tries them (the basic member kind first).</summary>
        public readonly List<PawnKindDef> chain = new List<PawnKindDef>();

        /// <summary>Provenance of each admitted kind: "defName (basic member kind of X)".</summary>
        public readonly List<string> provenance = new List<string>();

        /// <summary>"kind: reason" for every candidate the policy refused.</summary>
        public readonly List<string> rejected = new List<string>();

        public bool Any => chain.Count > 0;

        public bool Allows(PawnKindDef k)
        {
            return k != null && chain.Contains(k);
        }

        public string Describe()
        {
            return "faction " + (faction != null ? faction.defName : "none") + " generic pool [" + (provenance.Count > 0 ? string.Join("; ", provenance.ToArray()) : "EMPTY") + "]"
                + (rejected.Count > 0 ? ", refused [" + string.Join("; ", rejected.ToArray()) + "]" : "");
        }
    }

    public static class FactionMemberKinds
    {
        /// <summary>The pool of a faction def for a role class and equipment tier. Never null; empty when the faction offers no generic member.</summary>
        public static FactionMemberPool For(FactionDef def, RoleClass cls, int equipmentTier)
        {
            FactionMemberPool pool = new FactionMemberPool { faction = def };
            if (def == null) return pool;
            if (def.basicMemberKind != null) pool.candidates.Add(Facts(def.basicMemberKind, def, "basic member kind of " + def.defName, true));
            if (def.pawnGroupMakers != null)
            {
                for (int i = 0; i < def.pawnGroupMakers.Count; i++)
                {
                    PawnGroupMaker maker = def.pawnGroupMakers[i];
                    if (maker == null || maker.options == null) continue;
                    if (maker.kindDef != PawnGroupKindDefOf.Combat && maker.kindDef != PawnGroupKindDefOf.Peaceful) continue;
                    for (int k = 0; k < maker.options.Count; k++)
                    {
                        PawnKindDef kind = maker.options[k]?.kind;
                        if (kind != null) pool.candidates.Add(Facts(kind, def, maker.kindDef.defName + " group of " + def.defName, kind == def.basicMemberKind));
                    }
                }
            }
            List<string> names = GenericMemberPolicy.Chain(pool.candidates, cls, equipmentTier);
            for (int i = 0; i < names.Count; i++)
            {
                PawnKindDef k = DefDatabase<PawnKindDef>.GetNamedSilentFail(names[i]);
                if (k == null) continue;
                pool.chain.Add(k);
                MemberKindFacts f = pool.candidates.Find(c => c.defName == names[i]);
                pool.provenance.Add(names[i] + " (" + (f != null ? f.source : "?") + ")");
            }
            pool.rejected.AddRange(GenericMemberPolicy.Rejections(pool.candidates));
            return pool;
        }

        /// <summary>Does the faction offer at least one generic member for ANY role? (The encounter faction def must.)</summary>
        public static bool HasGenericPool(FactionDef def)
        {
            return For(def, RoleClass.Generalist, 1).Any;
        }

        public static MemberKindFacts Facts(PawnKindDef k, FactionDef faction, string source, bool basic)
        {
            RaceProperties race = k.race?.race;
            bool ranged = false, melee = false;
            if (k.weaponTags != null)
            {
                for (int t = 0; t < k.weaponTags.Count; t++)
                {
                    string w = k.weaponTags[t] ?? "";
                    if (w.IndexOf("Melee", StringComparison.OrdinalIgnoreCase) >= 0) melee = true;
                    if (w.IndexOf("Gun", StringComparison.OrdinalIgnoreCase) >= 0 || w.IndexOf("Rifle", StringComparison.OrdinalIgnoreCase) >= 0 || w.IndexOf("Ranged", StringComparison.OrdinalIgnoreCase) >= 0) ranged = true;
                }
            }
            return new MemberKindFacts
            {
                defName = k.defName,
                source = source,
                basic = basic,
                humanlike = race != null && race.Humanlike,
                toolUser = race != null && race.ToolUser,
                native = k.defaultFactionDef == null || k.defaultFactionDef == faction,
                playerKind = k.defaultFactionDef != null && k.defaultFactionDef.isPlayer,
                factionLeader = k.factionLeader,
                boss = k.isBoss,
                mutant = k.mutant != null,
                titled = k.titleRequired != null || k.minTitleRequired != null || !k.titleSelectOne.NullOrEmpty(),
                trader = k.trader,
                hostileToAll = k.hostileToAll,
                fixedBackstory = !k.fixedAdultBackstories.NullOrEmpty() || !k.fixedChildBackstories.NullOrEmpty(),
                builtInConditions = !k.startingHediffs.NullOrEmpty() || !k.missingParts.NullOrEmpty() || !k.forcedAddictions.NullOrEmpty(),
                builtInAbilities = !k.abilities.NullOrEmpty(),
                forcedTraits = !k.forcedTraits.NullOrEmpty(),
                forcedXenotype = k.xenotypeSet != null && k.xenotypeSet.Count > 0 && k.xenotypeSet.BaselinerChance < 0.5f,
                fighter = k.isFighter,
                ranged = ranged,
                melee = melee,
                combatPower = k.combatPower
            };
        }
    }
}
