using System;
using TheNetwork.Domain.Actors;
using TheNetwork.Kernel;

namespace TheNetwork.Domain.Physical
{
    // Two PURE rules the Phase 3.1 runtime-QA correction pass pulled out of the real adapter so they are proven headlessly: how a durable
    // person-to-pawn binding is judged once cross-references exist (BindingRules.Judge), and the narrow load-time bridge that lets the
    // retained-pawn registry answer from the DURABLE thing id before any pointer is resolvable (BindingRules.BridgeCovers). Neither reads a
    // RimWorld object: the adapter gathers plain facts and asks.

    /// <summary>What the durable binding of a person turned out to be, judged after the load's cross-references are resolved.</summary>
    public enum BindingIntegrity : byte
    {
        /// <summary>The person has no binding at all (nothing to judge).</summary>
        NotBound = 0,

        /// <summary>The pointer resolved to a live (not discarded) pawn whose own thing id equals the persisted one.</summary>
        Healthy = 1,

        /// <summary>A binding is persisted but its pointer did not resolve: no such pawn exists in this game.</summary>
        Unresolved = 2,

        /// <summary>The pointer resolved to a pawn vanilla has DISCARDED: positive bad physical evidence.</summary>
        Discarded = 3,

        /// <summary>The resolved pawn's thing id disagrees with the persisted binding (or the binding carries no thing id): identity cannot be attested.</summary>
        IdMismatch = 4
    }

    public static class BindingRules
    {
        /// <summary>
        /// Judges ONE durable binding from plain facts, after cross-references. Never regenerates, never clears: it only classifies. A
        /// mismatch outranks a discard (a pawn whose identity is in doubt cannot even be called discarded).
        /// </summary>
        public static BindingIntegrity Judge(bool bound, int persistedThingId, bool pointerResolved, int pointerThingId, bool pointerDiscarded)
        {
            if (!bound) return BindingIntegrity.NotBound;
            if (!pointerResolved) return BindingIntegrity.Unresolved;
            if (persistedThingId <= 0 || pointerThingId != persistedThingId) return BindingIntegrity.IdMismatch;
            if (pointerDiscarded) return BindingIntegrity.Discarded;
            return BindingIntegrity.Healthy;
        }

        /// <summary>
        /// The LOAD-TIME BRIDGE (ADR-053 / P3-INV-032 across a load): until the pointer index exists, a pawn is covered when its own thing id
        /// equals the thing id persisted in an existing binding AND no resolved pointer contradicts it. This is a load-safety bridge for a
        /// binding that already exists, never an identity system: the persisted <c>PawnRef</c> stays authoritative, a resolved pointer must be
        /// reference-equal to the pawn asked about, and once the pointer index is built the bridge is no longer consulted.
        /// </summary>
        public static bool BridgeCovers(int pawnThingId, int persistedThingId, bool pointerResolved, bool pointerIsThisPawn)
        {
            if (pawnThingId <= 0 || persistedThingId <= 0 || pawnThingId != persistedThingId) return false;
            return !pointerResolved || pointerIsThisPawn;
        }

        public static string Describe(BindingIntegrity i)
        {
            switch (i)
            {
                case BindingIntegrity.Healthy: return "healthy";
                case BindingIntegrity.Unresolved: return "the persisted binding did not resolve to any pawn after the load's cross-references";
                case BindingIntegrity.Discarded: return "the bound pawn was DISCARDED by vanilla (positive bad physical evidence)";
                case BindingIntegrity.IdMismatch: return "the resolved pawn's thing id disagrees with the persisted binding (identity cannot be attested)";
                default: return "not bound";
            }
        }
    }

    /// <summary>One loud, plain-text integrity finding about a living bound person (never acted on: no regeneration, no clearing).</summary>
    public sealed class BindingFinding
    {
        public CharacterId id;
        public string name;
        public CustodyState custody;
        public BindingIntegrity kind;
        public int persistedThingId;
        public int pointerThingId;

        public override string ToString()
        {
            return "PHYSICAL INTEGRITY: living person " + id + " (" + name + ", custody " + custody + ") is bound to pawn #" + persistedThingId + ": " + BindingRules.Describe(kind)
                + (kind == BindingIntegrity.IdMismatch ? " (persisted #" + persistedThingId + ", resolved #" + pointerThingId + ")" : "")
                + ". Nothing was regenerated, cleared or marked healthy.";
        }
    }
}
