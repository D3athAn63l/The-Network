# The Network

**RimWorld 1.6 · standalone · status: Phase 0 (architecture only, no runtime code)**

The Network is a persistent, procedural contractor ecosystem that runs behind the normal RimWorld
game. The player hires outsiders to find and fetch things they cannot easily get themselves. The
contractors, brokers and factions they deal with remember what happened, and that memory changes
how they behave afterwards.

```
Player intent → world reaction → opportunity → success / failure → consequences
             → remembered history → changed future behaviour → new stories
```

The first thing a player will do with the mod is look for a scarce item such as Tenebrite. That is
only the way in. What the mod is really about is **persistent procedural relationships and world
history**. The goal is for a player to say *"Remember Dead Red?"* and then tell a story that no
developer wrote.

---

## Repository status

This branch contains **only the Phase 0 technical architecture**, alongside the master design
from `main`. It deliberately contains no C#, no XML Defs, no Harmony patches, no UI and no
gameplay. Each implementation phase adds one vertical slice on top of the foundations described
here. See [docs/IMPLEMENTATION_PHASES.md](docs/IMPLEMENTATION_PHASES.md).

> **Design authority.** The [master design document](The%20Network%20%E2%80%94%20Full%20Mod%20Design%20-%20Master%20Implementation%20Brief.md) defines product and gameplay intent. The
> architecture documents define technical implementation. Where they conflict, the design takes
> precedence unless a reviewed ADR explicitly records a necessary deviation
> ([DECISIONS](docs/DECISIONS.md)). The first Phase 0 pass was written before the master design
> was on `main`; the architecture has since been reconciled against it
> ([ARCHITECTURE § 14](docs/ARCHITECTURE.md#14-master-design-reconciliation)).

## Documentation map

The master design is the canonical **product and gameplay specification**. The documents in
`docs/` are the canonical **technical specification** for anyone, human or coding agent,
implementing The Network. When the code and the docs disagree, fix whichever one is wrong in the
same change.

| Document | Read it for |
|---|---|
| [The Network — Full Mod Design - Master Implementation Brief](The%20Network%20%E2%80%94%20Full%20Mod%20Design%20-%20Master%20Implementation%20Brief.md) | **Canonical product/gameplay specification**: what the mod is, how it should play, and what it must never do |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | System diagram, subsystem contracts, stable-vs-replaceable map, self-review, master-design reconciliation |
| [docs/DATA_MODEL.md](docs/DATA_MODEL.md) | Identifiers, external references, every persistent entity and its fields |
| [docs/EVENTS_AND_HISTORY.md](docs/EVENTS_AND_HISTORY.md) | Network events, history ledger, summaries, reputation, awareness, gossip, legends |
| [docs/STATE_MACHINES.md](docs/STATE_MACHINES.md) | Intel, opportunity, contract, procurement, offer, operation, custody, actor lifecycles |
| [docs/SIMULATION.md](docs/SIMULATION.md) | Scheduler, abstract resolver, willingness/refusal, morale, determinism and RNG |
| [docs/ABSTRACT_PHYSICAL_LIFECYCLE.md](docs/ABSTRACT_PHYSICAL_LIFECYCLE.md) | Contractors moving abstract → physical → abstract without duplication |
| [docs/RIMWORLD_INTEGRATION.md](docs/RIMWORLD_INTEGRATION.md) | What vanilla 1.6 APIs we reuse, avoid or wrap; Harmony policy; runtime spikes |
| [docs/SAVE_AND_MIGRATION.md](docs/SAVE_AND_MIGRATION.md) | Save layout, `NetworkSaveVersion`, migrations, mod add/remove behaviour |
| [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md) | Item catalog, external Def safety, DLC and optional systems |
| [docs/PERFORMANCE.md](docs/PERFORMANCE.md) | Work classes, budgets, scheduling frequencies, anti-patterns |
| [docs/DEBUGGING.md](docs/DEBUGGING.md) | Logging policy, dev actions, validators, timing instrumentation |
| [docs/IMPLEMENTATION_PHASES.md](docs/IMPLEMENTATION_PHASES.md) | Phase plan, Phase 1 vertical slice and acceptance criteria |
| [docs/DECISIONS.md](docs/DECISIONS.md) | Architectural decision records, including the alternatives we rejected |
| [docs/RISKS.md](docs/RISKS.md) | Technical risk register |

## Core principles

1. **Story over statistics.** Numbers exist so there is something to tell stories about. The
   player sees what actors did, not stat sheets.
2. **History is load-bearing.** Meaningful events are recorded once and consumed by several
   systems (relationships, reputation, morale, gossip, willingness). History is not decoration.
3. **Abstract by default, physical only when relevant.** Off-map contractors are records, not
   ticking pawns. Real pawns and sites exist only where the player can touch them.
4. **Uncertainty is part of the game.** Intel describes what somebody *reports*, which is kept
   apart from what is actually true in the world. Leads can be wrong.
5. **Failure is meaningful and branches.** A failed contract closes its own record and can open
   new stories: a rescue, a recovery, a grudge.
6. **Built for heavily modded games.** Nothing assumes a Def, faction, DLC or mod still exists.
   A missing reference cancels the affected activity cleanly and leaves the rest of the save alone.
7. **Invisible to TPS.** No per-tick contractor processing, no repeated Def scans and no history
   rescans. Work is scheduled, staggered and budgeted.
8. **Safe across long saves.** Persistent data is versioned from the first release and migrated
   forward. History stays bounded.
9. **Interest, not need.** The player says what they are interested in. The Network never
   infers what the colony needs and never reads research, stockpiles or demand. Intel is about an
   item, never an amount; only Procurement names a quantity.

## Conventions (for implementation phases)

| Item | Convention |
|---|---|
| Target | RimWorld 1.6, .NET Framework 4.7.2 class library |
| Proposed packageId | `aRed.TheNetwork` (confirm before first release) |
| Assembly / root namespace | `TheNetwork` |
| Log prefix | `[TheNetwork]` |
| Def name prefix | `TheNetwork_` |
| Signal / quest-tag prefix | `TheNetwork.` |
| Harmony | Not required for Phases 1–3 (see [RIMWORLD_INTEGRATION.md § Harmony](docs/RIMWORLD_INTEGRATION.md#3-harmony-policy)) |
| Dependencies | None. Grandmaster21 and RegenNanites were read as references only. The Network does not depend on them or assume they are installed. |

Planned repository layout, created when Phase 1 begins and not before:

```
About/                 About.xml, Preview.png
1.6/Assemblies/        TheNetwork.dll (build output)
1.6/Defs/              Network-owned Defs (only for systems that exist)
1.6/Patches/           XML patches (Phase 1: one comp added to the vanilla Site WorldObjectDef)
Languages/English/     Keyed + DefInjected strings
Source/TheNetwork/     C# sources (see ARCHITECTURE.md § 4 for module layout)
Tests/                 Headless tests for pure domain logic
docs/                  This architecture
```

## License

To be decided by the repository owner before the first release.
