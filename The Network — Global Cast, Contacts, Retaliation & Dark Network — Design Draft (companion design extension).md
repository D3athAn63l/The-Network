# The Network — Global Cast, Contact Discovery, Retaliation & Dark Network
## Design Draft

> **Status:** DESIGN DRAFT — frozen as the current direction until revisited.
>
> This is **not an implementation prompt**.
>
> This document records the design that emerged from the Global Actor / multi-save discussion and the follow-up ideas around contact discovery, progressive knowledge, referrals, client risk, retaliation, interdiction/blockade, and the Dark/Confidential side of The Network.

---

# 1. Core Identity

The central idea is:

> **The Network is global. Your network is personal.**

The Network is a large, persistent professional ecosystem containing contractors, fixers, organizations, specialists, brokers, and other actors.

The player should **not** begin the game with omniscient access to the entire cast.

Instead, the player gradually discovers people through:

- requests;
- responses;
- referrals;
- introductions;
- bids;
- completed jobs;
- failed jobs;
- physical encounters;
- reputation;
- consequences;
- conflict.

The result is that the player's "Network" becomes a save-specific social and professional history layered on top of a wider global cast.

This also becomes the answer to the Global Actor multi-save problem.

---

# 2. Global Cast vs Save-Local Knowledge

## 2.1 Global Cast

The mod may maintain a global/generated cast of Network actors.

Conceptually:

```text
GLOBAL NETWORK CAST
~100 active actors
contractors
fixers
organizations
specialists
etc.
```

These actors exist as part of the wider Network universe.

Different saves can begin from the same global cast because they are different RimWorld starting places inside the same broader universe.

However, the player does **not** automatically know all of them.

The global cast answers:

> **Who exists in the wider Network?**

The save-local player state answers:

> **Who does this colony actually know?**

---

## 2.2 New Save

A new save begins with:

```text
Known Contacts:
None, or only specifically justified starting contacts
```

The wider cast still exists behind the scenes.

The first Network interaction should not be:

```text
Contractor Directory
127 contractors online
Sort by DPS
```

Instead, the first interaction should feel like:

> **What are you looking for?**

The player asks the Network for something.

Then somebody answers.

That answer is the first social connection.

---

# 3. Discovery-First Progression

The Network should treat **discovery itself as progression**.

The player begins with little or no knowledge of the people inside the system.

The player asks:

> "I need steel."

A contractor, fixer, or intermediary may respond.

That actor then becomes part of the player's known network.

Example:

```text
Player requests 100 steel
        ↓
Mira Venn responds
        ↓
Mira becomes a Known Contact
```

The player did not unlock Mira because of XP, level, reputation rank, or a menu refresh.

They met her because the Network produced a believable response to something they actually needed.

---

# 4. Known Contact = Save Anchor

Once an actor becomes meaningfully known to the player, that actor becomes **anchored to that save**.

This is the simple player-facing rule:

> **If you know them, they are part of your story.**

If the global cast is later regenerated, known contacts do not disappear from old saves.

Example:

```text
Global Cast v1:
Halvard
Kessler
Mira
Horizon Tide
Dead Red
95 others

Save A knows:
Halvard
Kessler
Dead Red
```

Later:

```text
Global Cast v1 → Global Cast v2
```

Save A preserves:

```text
Halvard
Kessler
Dead Red
```

because those actors have crossed the player-knowledge boundary.

The remaining actors may be reconciled/replaced only if they are still safe to replace.

---

# 5. Internal Continuity Anchor

Known Contact is the player-facing anchor, but internally the mod needs a stronger safety rule.

An actor must also become save-protected if replacing them would rewrite established durable truth, even if the player has not formally discovered them yet.

Conceptually:

```text
CanReplaceFromGlobalCast(actor)
=
    NOT KnownContact
    AND NOT referenced by durable save state
    AND NOT active in an operation
    AND NOT active in a contract
    AND NOT active in a physical episode
    AND NOT concretized into a persistent person/pawn
    AND NOT required by meaningful history
    AND NOT otherwise required for continuity
```

If the answer is uncertain:

> **Anchor rather than replace.**

Global regeneration must never damage save continuity.

---

# 6. Global Regeneration

If the user chooses to regenerate the global cast, the regeneration affects only actors who are still safe to replace.

It should happen in a bounded reconciliation pass, not as live hot-swapping.

Conceptually:

```text
Save sees Global Cast generation changed
        ↓
Reconcile cast once
        ↓
Keep anchored / referenced actors
        ↓
Replace only safe unknown actors
        ↓
Record new generation seen
```

No actor should vanish in the middle of a contract or while the player is looking at their profile.

The save always owns continuity over convenience.

---

# 7. Progressive Knowledge

Knowing that an actor exists does **not** mean the player knows everything about them.

This is a separate design principle:

> **Actor truth is not the same thing as player knowledge of actor truth.**

Internally, an actor may already have:

```text
Form
Operational Role
Experience
Doctrine
Capability
Risk tolerance
Price tendencies
Reliability history
Specialties
Mobility
Connections
```

But the player may initially see only:

```text
Halvard Jarrow
Unknown
```

The player's dossier grows through evidence.

---

# 8. Contact Knowledge Progression

A contact should gradually become more legible.

Example:

### First contact

```text
Halvard Jarrow
Unknown
```

### After first job

```text
Halvard Jarrow
Solo
```

### After several jobs

```text
Halvard Jarrow
Solo
Combat-capable
Reliable?
```

### After substantial interaction

```text
Halvard Jarrow
Solo Contractor
Marksman
Veteran
Reliable
Affordable
Aggressive
Salvage / acquisition specialist
```

The profile should feel like the player is learning the person, not unlocking RPG perk cards.

---

# 9. Fact Types

Not all profile information should be treated the same.

There are at least three useful categories.

## 9.1 Known Facts

Objective facts discovered by reliable evidence.

Examples:

- Solo
- Organization member
- Marksman
- Medic
- Leader
- Fixer
- Long-range capable
- Spacer capable
- organization affiliation

These are truths.

---

## 9.2 Observed Traits

Patterns inferred from direct interaction.

Examples:

- cheap;
- expensive;
- fast;
- slow;
- reliable;
- aggressive;
- cautious;
- accepts high-risk work;
- often renegotiates;
- prefers salvage work.

These should emerge from actual outcomes and quote history.

---

## 9.3 Rumors / Claims

Information learned indirectly.

Examples:

- "Known for dangerous salvage."
- "Supposedly very good with a rifle."
- "People say they never abandon a job."
- "Rumored to have spacer contacts."

These may later become confirmed, contradicted, or remain uncertain.

Possible knowledge strengths:

```text
Known
Likely
Rumored
Confirmed
```

This should remain lightweight and should not turn into a second massive simulation.

---

# 10. Discovery Sources Matter

Different sources may reveal different information.

Examples:

### Fixer referral

> "Experienced marksman. Works alone."

May reveal:

```text
Solo
Marksman?
Experienced?
```

### Direct contract history

May reveal:

```text
Completed 4/5 jobs
Usually on time
Price tendency
```

### Physical encounter

May confirm:

```text
Actual combat role
Observed equipment class
Physical identity
```

### Contractor referral

May reveal:

```text
Professional reputation
Specialty
Personal opinion
```

The Network becomes a system for both acquiring things and acquiring information about people.

---

# 11. Referrals and Introductions

New contacts should usually enter the player's Network through:

- work;
- referrals;
- introductions;
- responses;
- meaningful consequences.

Not through an omniscient directory.

The player should be able to ask existing contacts for new connections in a natural way.

Conceptual interaction:

> "Can you recommend someone?"

or:

> "Do you know anyone who handles this?"

---

# 12. Contextual Referral

Referrals should often be driven by what the player is already trying to do.

Example:

```text
Player asks Halvard for 500 Tenebrite
        ↓
Halvard refuses
        ↓
"This is beyond what I handle.
Talk to Kessler. He brokers work like this."
        ↓
Kessler becomes a new contact
```

The rejection itself becomes progression.

The game does not need a separate "roll new contractors" button.

---

# 13. Different Referral Roles

Fixers and Contractors should not behave identically.

## Contractors

Usually have narrower professional circles.

They may know:

- one trusted fixer;
- one specialist crew;
- an old colleague;
- a rival;
- a former employer;
- a specialist they worked with.

Example:

```text
Halvard
├─ Kessler — Fixer
├─ Horizon Tide — Crew
└─ Voss — Specialist
```

## Fixers

Are natural hubs.

They may know:

- many contractors;
- specialists;
- other fixers;
- organizations;
- couriers;
- smugglers;
- black-market contacts.

Being well-connected becomes part of a Fixer's value without requiring a visible "Networking 84/100" stat.

---

# 14. Known-Of vs Contactable

A useful lightweight distinction:

```text
Known Of
Contactable
```

A player may hear:

> "Dead Red exists."

without being able to call them.

Later, a Fixer may say:

> "I'll introduce you."

That converts them into an actual contact.

This lets the Network reveal names before granting access.

It also enables rumors, mystique, and future contact progression.

---

# 15. Referral Provenance

When a new contact enters the player's network through another actor, preserve that provenance.

Example:

```text
Nessa Vane
Referred by Halvard Jarrow
```

This can later become story fuel.

If Nessa betrays the player, the fact that Halvard vouched for her may matter.

If Halvard later hates the player, old introductions may also become socially relevant.

Do not over-simulate this now, but preserve the relationship seam.

---

# 16. The Player's Network Is a Social Graph

The player is gradually constructing a real social/professional graph:

```text
Player
  │
  ├─ Mira
  │    └─ Kessler
  │          ├─ Horizon Tide
  │          └─ Dead Red
  │
  └─ Halvard
       └─ Voss
```

This becomes progression without needing a traditional tech tree or unlock ladder.

---

# 17. Long-Term UI Direction

The first Network screen should eventually be closer to:

```text
THE NETWORK

What are you looking for?

[ Search / Request ]

Requests
Contacts
Contracts
History
```

rather than exposing:

```text
Intel
Procurement
Contracts
Contractors
History
```

as completely separate conceptual silos from the first click.

The existing semantic distinction still matters:

```text
Intel = exact item, no quantity
Procurement = exact item + quantity
```

but the player-facing entry point can be one human question:

> **What are you looking for?**

Then the UI can ask whether the player wants:

- information;
- acquisition;
- a contact;
- perhaps later a service.

This should be explored later, not forced into current scope.

---

# 18. Abuse / Hostile Player Problem

The Network must survive a hostile player.

Example exploit:

```text
Hire contractor
Meet physically
Betray contractor
Kill contractor
Take equipment
Repeat
```

The answer should NOT be:

```text
Bad Karma ≥ 100
→ Spawn Raid
```

The answer should be:

> **The world learns what kind of client you are.**

The Network reacts through evidence, relationships, claims, risk, market access, and eventually enforcement.

---

# 19. The Network Does Not Punish — It Reacts

Core principle:

> **The Network does not punish the player for hostile play. It reacts to demonstrated client risk.**

The mod should avoid acting like a moral Game Master.

Consequences require causal ownership.

Someone must:

- know what happened;
- care about it;
- have a motive;
- have sufficient evidence;
- possess money, influence, connections, or force to respond.

No invisible omniscient punishment.

---

# 20. Evidence Matters

The Network should not magically know every hostile act.

If a contractor disappears in the middle of nowhere with:

- no witnesses;
- no surviving party;
- no communication;
- no evidence;

the Network may know only:

```text
Missing
Last contact: player
```

not:

```text
Player murdered contractor at 15:42.
```

Possible evidence states can later include:

```text
Unknown
Suspected
Witnessed
Confirmed
Employer exposed
Perpetrator unknown
```

This also connects naturally to the future Black / Confidential systems.

---

# 21. Client Risk

The Network should maintain a concept of professional risk around the player.

Not "morality."

Conceptually:

```text
ClientRiskProfile
```

Possible facts:

- payments honored;
- payments missed;
- contracts completed cleanly;
- agreements broken;
- contractor deaths associated with player jobs;
- contractors attacked by player;
- stolen cargo;
- unpaid claims;
- betrayals;
- restitution;
- time since incidents.

Different actors interpret the same history differently.

---

# 22. Actor Interpretation

A cautious courier may say:

> "Absolutely not."

A desperate Solo may say:

> "Fine. Pay me first."

A wealthy company may say:

> "Full escrow, armed security, and neutral-ground handoff."

A vengeful actor may say:

> "I don't want money anymore."

This prevents one global reputation number from controlling every interaction identically.

---

# 23. First Consequences Should Be Economic

The first response to a dangerous client should usually be reduced trust, not military force.

Possible progression:

```text
Normal terms
↓
Higher deposit
↓
Full prepayment
↓
Higher risk premium
↓
No insurance
↓
No credit
↓
No direct colony meeting
↓
Neutral-site handoff only
↓
Armed escort
↓
Fixers refuse introductions
↓
Contractors stop bidding
```

The player's exploit becomes self-defeating because people stop presenting themselves as easy victims.

---

# 24. Information Propagation

The Network is not a hive mind.

Risk information should propagate through:

- direct witnesses;
- survivors;
- Fixers;
- referrals;
- professional circles;
- organizations;
- gossip;
- contract history;
- evidence.

Repeated misconduct broadens the area of awareness.

A distant Fixer may know nothing.

A local Fixer connected to three victims may know everything.

---

# 25. Blacklisting

Blacklisting should emerge from social/professional propagation rather than a single universal switch.

Possible outcomes:

```text
Contractor declined:
unacceptable client risk

Fixer refused:
prior breach reported

Quote includes:
security premium
```

At extreme levels, broad parts of the Network may refuse the player.

But the player should not necessarily be universally and permanently locked out.

---

# 26. Claims

Serious misconduct can create a durable **claim**.

Examples:

```text
Claim:
40,000 silver

Reason:
Stolen procurement cargo

Claimant:
Horizon Tide

Status:
Outstanding
```

Claims can arise from:

- theft;
- nonpayment;
- broken handoff;
- destroyed assets;
- betrayal;
- injury/death liability where appropriate;
- contract violation.

Claims create a clean bridge from grievance to enforcement.

---

# 27. Restitution

The player needs counterplay.

Possible restitution:

- return stolen goods;
- pay claim;
- pay compensation;
- negotiate settlement;
- use a respected Fixer as mediator;
- complete a restitution contract;
- maintain a long clean record;
- satisfy a specific offended organization.

Some personal relationships may remain permanently damaged.

Broader Network standing may recover.

Example:

> Kessler will broker for you again. Horizon Tide still refuses contact.

This preserves consequences without permanently bricking the mod.

---

# 28. Enforcement

If a claim is severe enough and the claimant has sufficient resources, the actor can pursue enforcement.

Possible escalation:

```text
Request repayment
↓
Fixer mediation
↓
Recovery contract
↓
Bounty
↓
Armed enforcement
↓
Interdiction
↓
Blockade
```

Not every actor can do all of these.

Resources matter.

---

# 29. Retaliation Has an Owner, Motive and Budget

This is a hard principle.

> **Every serious retaliation needs an owner, a motive, and a budget.**

Examples:

Poor Solo:

```text
motive: very high
resources: low
response: warning / personal vendetta / maybe one cheap attempt
```

Wealthy organization:

```text
motive: high
resources: high
response: recovery team / bounty / interdiction campaign
```

Coalition:

```text
multiple actors share grievance
resources combined
response: serious campaign
```

The Network should never conjure high-tech armies from nothing.

---

# 30. External Faction Enforcement

Network actors may hire outside factions.

Example:

```text
Sponsor:
Grey Lantern Recovery

Operator:
Spacer security faction
```

The faction is not attacking because "The Network" is angry.

It is attacking because someone paid them.

This preserves causality.

Possible external operators:

- mercenary teams;
- bounty hunters;
- pirates;
- high-tech faction security;
- specialist recovery teams;
- criminal groups.

---

# 31. Enforcement Objectives

Retaliation should not always mean extermination.

Possible objectives:

### Recovery
Retrieve stolen goods.

### Debt Collection
Take enough value to satisfy a claim.

### Capture
Take a specific pawn.

### Intimidation
Damage infrastructure or force retreat.

### Interdiction
Stop travel.

### Assassination
Kill a named target.

### Extermination
Reserved for truly extreme escalation.

This gives enforcement encounters story context instead of generic raids.

---

# 32. Interdiction

Interdiction is a campaign against player logistics.

Conceptually:

```text
Actor grievance
↓
Enforcement contract
↓
Contractors assigned
↓
Routes monitored
↓
Player caravan intersects coverage
↓
Physical encounter
```

The player is never prevented from forming a caravan.

Instead:

> **Travel becomes dangerous.**

This preserves sandbox freedom.

---

# 33. Blockade

Blockade is the severe form of interdiction.

Possible player-facing warning:

> Multiple contractors are reported operating around your settlement. Travel in the surrounding region may be unsafe.

Later:

> Your colony is considered under blockade.

The blockade should not be a magical global aura.

Its effectiveness depends on:

- geography;
- assigned contractors;
- mobility;
- funding;
- intelligence;
- number of teams;
- external support;
- player behavior.

---

# 34. Blockade as Campaign State

Possible internal concept:

```text
InterdictionCampaign

Sponsor
Reason
Claim
Operators
Coverage
Funding
StartTick
Pressure
KnownIntel
```

This is conceptual only.

The exact schema is not frozen.

---

# 35. Caravan Ambush Objectives

When the player's caravan is intercepted, the operator's objective should reflect the campaign.

Examples:

```text
Debt claim
→ surrender silver / valuables

Stolen cargo
→ return claimant property

Vendetta
→ surrender specific pawn

Blockade
→ turn around

Bounty
→ capture / kill target
```

The encounter may offer:

- comply;
- pay;
- negotiate;
- refuse;
- flee;
- fight.

---

# 36. Hidden Spatial Integration

The existing hidden spatial simulation is well suited to interdiction.

The Network does not need visible NPC caravans constantly moving around the world map.

Instead:

```text
Contractor group
approximate region
mobility capability
interdiction assignment
```

If player route and contractor coverage plausibly intersect:

> create the physical encounter.

This makes geography and mobility matter without turning the mod into a strategic map simulator.

---

# 37. Blockade Costs Money

A blockade should not persist forever because:

```text
PlayerBad = true
```

It costs:

- personnel;
- fuel/logistics;
- surveillance;
- transport;
- risk;
- opportunity cost.

Poor actors cannot maintain strong pressure.

Wealthy organizations can.

Eventually a sponsor may decide:

> This is no longer worth the cost.

This creates natural decay and counterplay.

---

# 38. Counterplay Against Interdiction

Possible legitimate counterplay:

- settle the claim;
- return goods;
- pay restitution;
- negotiate through a Fixer;
- destroy/capture an interdiction team;
- outlast sponsor funding;
- use alternate routes;
- use transport pods/shuttles/gravships;
- hire escorts;
- hire rival contractors;
- identify and pressure the sponsor.

The blockade should create gameplay, not remove gameplay.

---

# 39. "No Trades for You" Philosophy

The Network should not disable vanilla travel buttons.

The correct philosophy is:

> **Do not forbid the action. Make the world react to it.**

Player:

> "Fine, I'll just caravan to the settlement manually."

The Network:

> "Sure."

Then the route becomes dangerous if a credible interdiction campaign exists.

---

# 40. Dark / Confidential Network

The Dark side of The Network should not merely be:

> "The place where evil jobs live."

Its deeper purpose is:

> **A parallel infrastructure for doing things the legitimate Network cannot or will not openly do.**

This becomes especially important when the player is sanctioned, blacklisted, or blockaded.

---

# 41. Dark Network Identity

A useful distinction:

> **The public Network sells access.  
> The dark Network sells ways around denied access.**

Public Network:

- procurement;
- professional contracts;
- trusted introductions;
- standard logistics;
- reputation-based access;
- visible brokerage.

Dark Network:

- smuggling;
- deniable work;
- sanction busting;
- covert intelligence;
- bribery;
- sabotage;
- counter-enforcement;
- criminal logistics;
- illegal acquisition;
- hidden introductions.

This is not "good shop vs evil shop."

It is **formal infrastructure vs covert infrastructure**.

---

# 42. Dark Network Discovery

The player should not receive a Dark Network tab by default.

Dark contacts should be discovered organically.

Possible paths:

- shady Fixer referral;
- criminal contractor referral;
- refusal of a legitimate request;
- severe blockade;
- desperate player circumstances;
- existing criminal history;
- encrypted unsolicited approach.

Example:

```text
Legitimate contacts refuse player
        ↓
Colony is blockaded
        ↓
Unknown encrypted contact appears

"Heard you're having trouble getting things through."
```

No:

```text
Black Market unlocked at Reputation -50
```

---

# 43. Dark Network Anti-Blockade Services

The Dark Network should not simply remove a blockade.

It should offer risky ways to operate despite it.

Three broad design families:

## 43.1 Circumvention

Avoid the blockade.

Examples:

- hidden routes;
- smugglers;
- intermediaries;
- false manifests;
- black-market delivery;
- alternate transport;
- decoy caravan;
- covert drop.

## 43.2 Subversion

Damage the blockade's ability to function.

Examples:

- bribery;
- route intelligence;
- sabotage;
- misinformation;
- comms disruption;
- turn one contractor;
- expose the sponsor;
- compromise a staging point;
- buy patrol schedules.

## 43.3 Counterforce

Fight the blockade.

Examples:

- armed escort;
- counter-ambush;
- strike team;
- capture patrol leader;
- counter-contract;
- hire a rival high-tech operator;
- destroy a staging site.

---

# 44. Dark Solutions Create New Problems

The Dark Network must not become a consequence eraser.

Example:

```text
Player steals from contractors
↓
gets blacklisted
↓
uses Dark Network forever
↓
no meaningful downside
```

This must NOT become optimal play.

Dark Network services should be:

- expensive;
- dangerous;
- unreliable;
- deniable;
- sometimes uniquely capable;
- capable of escalating conflict;
- capable of generating new evidence.

The player gets freedom, not immunity.

---

# 45. Exposure and Escalation

If the player is caught using dark-side methods:

### Smuggling detected
Enforcement tightens.

### Bribe discovered
The bribed contractor may be punished.

### Sabotage traced
The sponsor escalates.

### Interdiction team killed
Casualties / vendetta / bounty increase.

### Sponsor exposed
Political / social consequences may follow.

Again:

> **Actions create evidence, not morality points.**

---

# 46. Dark Network and Contact Discovery

Dark actors should follow the same contact philosophy.

Initially:

```text
Unknown
```

Then:

```text
Known Of
```

Then:

```text
Contactable
```

Then:

```text
Worked With
```

Their dossier may be even more uncertain than public contractors.

A Dark Fixer may deliberately conceal:

- identity;
- location;
- organization;
- capability;
- affiliation.

Progressive knowledge matters even more here.

---

# 47. Dark Network Can Start Internal Wars

A player's attempt to break a blockade may cause conflict between actors.

Example:

```text
Grey Lantern enforces blockade
        ↓
Player hires Black Sun Couriers
        ↓
Black Sun sabotages Grey Lantern
        ↓
Grey Lantern discovers culprit
        ↓
Grey Lantern ↔ Black Sun hostility
```

That conflict can continue beyond the player's immediate problem.

Future possibilities:

- mutual ambushes;
- rival contracts;
- denied referrals;
- bounty escalation;
- alliances;
- revenge.

The player can become the catalyst for Network history.

---

# 48. The Player Can Become a Contract Target

At severe hostility, the system should eventually invert.

The same market used to hire contractors can be used against the player.

Example:

```text
Wanted:
Recovery of stolen assets from [Player Colony]

or

Interdiction Contract:
Target caravans associated with [Player Colony]
```

Future Phase 4 logic can reuse the same contractor market:

```text
Actor issues work
↓
Contractors evaluate
↓
Someone accepts
↓
Operation occurs
```

Sometimes:

```text
target = player
```

This is preferable to a special-purpose punishment raid generator.

---

# 49. Friendly Contacts Can Leak Warnings

Not every actor must participate equally in enforcement.

A loyal contact may warn the player:

> "Don't take the south road tomorrow."

That actor may know about:

- a blockade;
- a bounty;
- a recovery operation;
- a hostile Fixer;
- a planned interdiction.

This connects:

- loyalty;
- contacts;
- knowledge;
- retaliation;
- spatial simulation.

And it gives personal relationships mechanical value without turning them into flat buffs.

---

# 50. Information Is Gameplay

The player should often know less than the simulation.

Possible progression:

```text
Rumors of route trouble
↓
Confirmed interdiction
↓
Known sponsor
↓
Known operator
↓
Known timing
↓
Known staging point
```

The player can then act on increasingly precise information.

This makes intel valuable in both peaceful and hostile play.

---

# 51. Anti-Exploit Philosophy

The best anti-exploit system is not:

> "The mod forbids this."

It is:

> **NPCs remember and adapt.**

If the player repeatedly:

```text
invites contractor
kills contractor
steals gear
```

future actors may change behavior:

- no colony meetings;
- neutral-site handoff only;
- armed escort;
- prepayment;
- cargo remains secured;
- remote transfer;
- no direct access.

The world learns the player's tactic.

---

# 52. Progressive Concretization of Information

The existing "progressive concretization" philosophy should apply to knowledge too.

A useful statement:

> **The Network does not reveal more detail than the player has earned evidence for.**

Unknown actors remain cheap abstractions.

As interaction increases:

- identity becomes known;
- specialties become known;
- professional history becomes known;
- physical identity may become concrete;
- relationships become meaningful.

This keeps information proportional to narrative importance.

---

# 53. Design Principles to Freeze

## 53.1 Global / Personal

> **The Network is global. Your network is personal.**

## 53.2 Discovery

> **New contacts should usually enter the player's Network through work, referrals, introductions, responses, or consequences—not through browsing an omniscient directory.**

## 53.3 Anchoring

> **If you know them, they are part of your story.**

## 53.4 Save Safety

> **Anything required by established durable truth is protected even if the player has not formally discovered it.**

## 53.5 Progressive Knowledge

> **Actor truth and player knowledge are separate.**

## 53.6 Referrals

> **When current contacts cannot satisfy a need, they should become one of the natural bridges to people who can.**

## 53.7 Retaliation

> **The Network does not punish hostile play; it reacts to demonstrated client risk.**

## 53.8 Causality

> **Serious retaliation must have an owner, motive, evidence, and budget.**

## 53.9 Sandbox Freedom

> **Do not forbid the action. Make the world react to it.**

## 53.10 Dark Network

> **The public Network sells access. The dark Network sells ways around denied access.**

## 53.11 Anti-Exploit

> **NPCs should stop falling for the same scam.**

---

# 54. Scope / Phase Guidance

This document is intentionally **not** tied to immediate implementation.

Likely future homes:

### Contact Discovery / Known Contacts
Phase 4-ish market/player interaction work.

### Progressive Knowledge / Dossiers
Phase 4–5, after contact identity is stable.

### Referrals / Introductions
Phase 4–5.

### Client Risk / Claims
Phase 4–5.

### Retaliation / Enforcement
Phase 5+.

### Interdiction / Blockade
Phase 5+ or later depending on physical/spatial readiness.

### Dark / Confidential Network
P7+ was the earlier broad bucket, but this design gives it a clearer gameplay purpose and may justify earlier seams.

### Actor-vs-Player Contracting
Phase 4+ once NPC-issued jobs exist.

No immediate implementation should jump ahead of the current Phase 3 physical-lifecycle work.

---

# 55. Explicit Non-Goals for Now

Do NOT currently implement:

- contact browser overhaul;
- free-text parser;
- known-contact UI;
- progressive dossier UI;
- referral graph;
- player risk score;
- claim system;
- bounty system;
- enforcement operations;
- blockade system;
- dark-market UI;
- smuggling;
- bribery;
- sabotage;
- counter-contracting;
- external-faction enforcement;
- faction diplomacy overhaul.

This draft exists so future implementation does not accidentally lose the design direction.

---

# 56. Open Questions for Future Design

These are intentionally unresolved.

## Global Cast
- How many actors should exist globally?
- How often should the user be allowed to regenerate the cast?
- Should certain signature actors always persist across generations?
- Should global generation be deterministic from a user-controlled seed?

## Known Contacts
- Exactly what event turns "Known Of" into "Contactable"?
- Do all bidders become known contacts?
- Can a contact later become completely unreachable?

## Progressive Knowledge
- Which facts are objective vs inferred?
- How quickly should reliability/price tendencies become visible?
- Should rumors ever be false?
- Does Fame make some facts public immediately?

## Referrals
- Can referrals fail?
- Can a bad relationship cause intentionally poor referrals?
- Can a Fixer charge introduction fees?
- Can referrals create obligations?

## Client Risk
- Which incidents become professional facts?
- How far and how quickly does gossip propagate?
- How should anonymous crimes affect risk?
- Can actors disagree on what happened?

## Claims
- What exact events create a claim?
- Can multiple claims be consolidated?
- Can claims be sold/transferred?
- Can Fixers arbitrate claims?

## Enforcement
- When is a bounty justified?
- How should recovery teams negotiate before combat?
- Can external factions legally or politically refuse enforcement work?

## Blockade
- How should geographic coverage be represented?
- How expensive is sustained interdiction?
- What limits simultaneous blockade teams?
- Can player counter-operations reduce coverage?

## Dark Network
- How is the first dark contact discovered?
- Which actions require a dark Fixer vs direct criminal contractor?
- How much uncertainty should dark profiles retain?
- Can Dark actors betray the player more often without becoming arbitrary?

---

# 57. Closing Design Summary

The original problem was:

> "What happens to global actors across multiple saves?"

The resulting design is broader:

```text
GLOBAL CAST
        ↓
Player asks for something
        ↓
Someone responds
        ↓
Known Contact
        ↓
Save Anchor
        ↓
Progressive knowledge
        ↓
Referrals / Introductions
        ↓
Personal professional network
        ↓
Trust / risk / claims
        ↓
Retaliation / enforcement
        ↓
Interdiction / blockade
        ↓
Dark Network circumvention
        ↓
More relationships, conflict and history
```

The Network should not feel like a shop.

It should feel like a living professional ecosystem where:

- people exist before the player knows them;
- knowledge is earned;
- introductions matter;
- history follows actors;
- trust affects logistics;
- betrayal changes behavior;
- retaliation has causes;
- enemies can hire professionals too;
- denied access creates black markets;
- and the player can become part of the Network's own stories.

The final guiding line is:

> **You can stop using The Network. The Network does not necessarily stop using you.**


---

# 58. Payment Terms and Barter — Frozen Design Addition

> **Status:** FROZEN DESIGN DIRECTION — implementation details remain future work.

The Network should support three simple player-facing compensation modes:

```text
1. Pay Now
2. Pay Later
3. Barter
```

These are intentionally simple UI choices, even if the internal model later separates payment timing from compensation type.

## 58.1 Pay Now

The player secures payment up front.

This is the safest arrangement for contractors, especially when dealing with unknown clients, large orders, high-risk work, or first-time customers.

Benefits may include more contractor interest, lower perceived client risk, fewer security demands, and more willingness from reputable actors. The contractor must still decide whether the job itself is worth their time.

## 58.2 Pay Later

Payment occurs after successful delivery / completion.

This is higher risk for the contractor. A brand-new requester should not automatically receive favorable Pay Later terms from reputable actors.

Willingness should depend on prior payment history, relationship, contract size, contractor risk tolerance, contractor wealth, leverage, and job desirability.

Pay Later should therefore become naturally easier as the player builds professional trust.

## 58.3 Barter

The default barter concept is:

> **"I'm willing to barter. Tell me what you're looking for."**

The player should not be required to guess a hidden exchange rate before posting a request.

Contractors respond with compensation they actually want based on their own needs.

Examples:

```text
Wanted:
1,000 Plasteel

Contractor A:
Seeking high-end ranged equipment.

Contractor B:
Seeking medical supplies.

Contractor C:
Cash only.
```

Barter should be driven by contractor utility, not by treating every item as disguised silver.

---

# 59. Barter Value Philosophy

A barter item's nominal market value is only part of its usefulness.

The contractor should internally consider item category / role fit, current career need, quality, durability / condition, quantity, tech relevance, equipment relevance, whether the contractor already has enough of that type, and the approximate economic scale of the job.

Core principle:

> **A barter item's nominal value determines what it is worth on paper; its condition and usefulness determine whether that particular actor actually wants it.**

A contractor who needs a precision weapon may value a relevant high-quality rifle strongly while rejecting an expensive but irrelevant item. A salvage-oriented actor may accept damaged high-tech equipment that a combat operator would reject.

---

# 60. Low-Value Requests

The Network should allow silly or economically poor requests.

Examples:

```text
Wanted:
1 Silver

Payment:
Open Barter
```

or:

```text
Wanted:
1 Herbal Medicine

Offering:
something absurdly low
```

The UI should not reject such requests merely because they are bad deals. Instead, actors should react intelligently.

Possible outcomes include zero bids, a tiny incidental proposal, a nearby contractor accepting because logistics cost is negligible, or a contractor simply ignoring the request because it is below their normal engagement scale.

Core principle:

> **Do not forbid bad offers. Let actors decide whether they are worth answering.**

The true cost of a job may include:

```text
goods value
+ acquisition effort
+ logistics
+ risk
+ opportunity cost
```

Therefore very low-value requests may be ignored even when the requested item itself is trivial.

---

# 61. Contractor Engagement Scale

Different actors may have different effective minimum engagement scales.

This should not necessarily be exposed as a visible stat.

Examples:

- a desperate Solo may accept tiny jobs;
- a professional crew may ignore very small work;
- a Legend may ignore mundane low-value contracts;
- a trusted customer may receive exceptions;
- a geographically convenient job may become worthwhile despite low value.

This is opportunity cost, not a hard universal threshold.

---

# 62. Equipment Condition in Barter

Equipment quality and equipment condition are separate dimensions.

Example:

```text
Legendary Minigun
Condition: 12%
```

The item being Legendary does not automatically make it acceptable.

A contractor may require:

```text
High-end ranged weapon
Minimum quality: Masterwork
Minimum condition: Good
```

The Legendary Minigun satisfies quality but may fail condition.

Operational users may reject it. A salvage / restoration actor may still value it.

Condition therefore acts as an eligibility filter, a valuation factor, and a contextual factor depending on why the actor wants the item.

The exact condition bands are not frozen, but semantic categories are preferred over awkward exact percentages in the player-facing UI.

Possible future labels:

```text
Pristine
Good
Serviceable
Damaged
Ruined
```

---

# 63. Barter UI Filtering — Frozen UX Rule

The player should **not** manually test every colony item against a contractor's hidden needs.

Contractors define their barter requirements internally.

The barter UI should automatically filter the player's accessible goods and show only eligible items.

Conceptual flow:

```text
Contractor defines hidden requirement
        ↓
Barter UI checks accessible colony goods
        ↓
Filter by usefulness / category
        ↓
Filter by quality
        ↓
Filter by condition
        ↓
Filter by quantity / eligibility
        ↓
Show only valid barter choices
```

Example:

```text
HALVARD'S BARTER REQUEST

Looking for:
High-end ranged equipment

Eligible:
- Legendary Charge Rifle — 91%
- Masterwork Sniper Rifle — 83%

Hidden / ineligible:
- Poor Revolver
- Excellent Minigun — 12%
- Masterwork Club
```

The player should not need to discover failure item-by-item.

If nothing qualifies:

> **No eligible barter goods available.**

The UI may also explain the broad requirement:

> Looking for: Masterwork-or-better ranged weapons in good condition.

Core UX principle:

> **The barter UI should explain eligibility, not expose the contractor's internal valuation math.**

---

# 64. Barter Contribution

Eligible barter items may contribute toward the contractor's requested compensation.

The UI may show progress using a simple abstraction such as:

```text
5 Masterwork-equivalent armor required

Provided:
3 / 5
```

or:

```text
Legendary item:
3 equivalent units
```

The exact formula is not frozen.

Requirements should consider usefulness before raw value. Quality and condition should matter.

The player should receive clear feedback about whether the offered goods fully satisfy the proposed barter.

---

# 65. Open Barter vs Offered Barter

The default recommended mode is:

### Open Barter

> "Tell me what you're looking for."

The contractor proposes useful compensation.

A possible future extension is:

### Offered Barter

> "Here is what I'm offering."

The player explicitly posts goods.

Bad offers are allowed and may receive zero bids.

This is optional future scope and should not complicate the initial barter implementation.

---

# 66. Mixed Compensation — Future Seam

The internal architecture should not prevent future mixed compensation:

```text
Silver + equipment
Goods + silver
Upfront deposit + barter on delivery
```

Example:

> 4,000 silver + 1 Masterwork armor.

However, mixed negotiation should not be required for the initial barter implementation.

Architect for it; do not necessarily build it immediately.

---

# 67. Why Barter Exists

Barter should be worthwhile because it can do things silver alone does not:

- satisfy real contractor career needs;
- convert surplus high-quality gear into meaningful compensation;
- attract actors who want a specific resource;
- support contractor progression;
- create memorable item provenance;
- allow strategically useful goods to be valued more highly than raw market value;
- create relationship/history opportunities through notable compensation.

Example:

```text
Player pays Halvard with a Masterwork rifle
        ↓
EquipmentProfile improves
        ↓
Future physical Halvard may use better equipment
        ↓
The item can become part of Halvard's history
```

Barter should therefore feel like negotiation with another actor, not simply another currency conversion screen.

---

# 68. Frozen Barter Principles

> **The player chooses the payment relationship; the contractor chooses whether the compensation is attractive.**

> **Open Barter means: "I'm willing to barter. Tell me what you're looking for."**

> **Do not forbid bad offers. Let actors reject them.**

> **Barter is about solving contractor needs, not dumping nominal market value onto them.**

> **Quality and condition are separate.**

> **Contractors define barter requirements internally; the UI automatically filters the player's accessible goods and shows only eligible items.**

> **The barter UI explains eligibility and progress, not hidden valuation mathematics.**

> **Barter should feed contractor career progression and story when the compensation is meaningful enough to matter.**
