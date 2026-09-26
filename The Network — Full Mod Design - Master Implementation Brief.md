# THE NETWORK
## Full Design & Implementation Brief
### Standalone RimWorld 1.6 Contractor, Intel, Procurement, and Emergent Story Ecosystem

---

# 1. HIGH-LEVEL CONCEPT

Create a NEW standalone RimWorld 1.6 mod with the working title:

**The Network**

Alternative descriptive name:

**Contractor Network**

This is NOT primarily a bounty hunting mod.

This is NOT simply a quest reward mod.

This is NOT a helper that examines the player's research tree and gives them whatever they need next.

This is NOT a replacement for the vanilla storyteller or vanilla quest generator.

The goal is to create a persistent background ecosystem of:

- contractors
- mercenary groups
- adventurers
- scavengers
- brokers
- intelligence contacts
- procurement companies
- rival groups
- factions looking for outside help
- player-created contractor organizations

These actors participate in a loose economy of:

- information
- procurement
- contracts
- resource acquisition
- recovery
- exploration
- competition
- rescue
- rivalry
- sponsorship
- reputation

The player may interact with this ecosystem as:

- a customer
- an intelligence buyer
- a procurement client
- an employer
- a sponsor
- a contractor
- a competitor
- an ally
- a rival
- a rescuer

The primary design philosophy is:

> **The world should create stories from what the player asks for, what other groups attempt, and what succeeds or fails.**

The mod should feel as though interesting activity continues outside the player's colony without actually simulating hundreds of pawns in real time.

---

# 2. ORIGIN OF THE DESIGN

A player may have a heavily modded game containing unusual materials such as:

- Tenebrite
- Quasar Alloy
- exotic components
- modded artifacts
- rare weapons
- rare medicines
- unusual resources

Vanilla quests may rarely expose these items.

The naive solution would be:

> Detect what the player needs and generate a quest rewarding it.

DO NOT DO THIS.

That would make the mod feel like a progression helper.

Instead:

> The player explicitly tells the world what they are interested in.

From there, the world reacts.

This keeps player agency intact.

---

# 3. CORE DESIGN PRINCIPLES

## 3.1 Player-driven interest

The mod NEVER automatically decides:

> "The player needs Tenebrite."

Instead:

> "The player has asked contacts for information about Tenebrite."

or:

> "The player has posted a procurement request for 500 Tenebrite."

That distinction is fundamental.

---

## 3.2 Information is not certainty

Intel should sometimes be:

- accurate
- incomplete
- outdated
- misleading
- false
- deliberately falsified
- bait
- contested

Buying intel should never feel like spawning a guaranteed loot quest.

---

## 3.3 Procurement trades money for safety

The player may pay others to acquire goods.

This should be:

**very expensive**

because the player is outsourcing:

- travel
- scouting
- combat
- risk
- logistics
- casualties
- acquisition
- transport

The player avoids risking their own colonists.

They risk their money instead.

---

## 3.4 Failure creates content

Never reduce meaningful failure to:

> Mission failed. Money gone.

Whenever possible, failure should create:

- a last known location
- a wreck
- a battle site
- captured contractors
- stranded survivors
- abandoned cargo
- enemy occupation
- a rescue mission
- a recovery opportunity
- a betrayal story

Failure should create another branch of gameplay.

---

## 3.5 Organizations persist

Contractor groups should have names and histories.

Example:

**Dead Red**

The player should eventually recognize them.

They may:

- succeed
- fail
- suffer casualties
- get captured
- be rescued
- become stronger
- receive sponsorship
- develop rivalries
- change leaders
- retire
- dissolve
- potentially produce successor groups

The goal is for the player eventually to react:

> "Oh shit, Dead Red took this contract too."

without the game needing to explain why that matters.

---

## 3.6 Simulate organizations, not entire off-map pawn populations

Performance is extremely important.

DO NOT simulate dozens of contractor pawns every tick.

Off-map organizations should normally exist as lightweight serialized records.

Instantiate detailed pawns/maps only when the player actually interacts with them.

---

## 3.7 Use RimWorld's systems

Do NOT reinvent:

- pawn combat
- factions
- world sites
- caravans
- quests
- incidents
- map generation
- transport systems

Use existing RimWorld systems wherever practical.

The mod should provide:

> **context, persistence, and procedural intent**

while RimWorld provides most of the physical gameplay.

---

# 4. OVERALL ARCHITECTURE

The full system consists of several major layers:

1. **Item Catalog**
2. **Intel Network**
3. **Procurement Network**
4. **Persistent Contractor Ecosystem**
5. **Player Contractor System**
6. **Contract Board**
7. **Opportunity / Mission Generator**
8. **Reputation and Relationships**
9. **Rivalry System**
10. **Sponsorship**
11. **Failure and Recovery System**
12. **Long-term Careers / Retirement**
13. **World History**
14. **Compatibility Layer**
15. **Background Simulation**

These systems should share common data structures instead of being implemented as disconnected minigames.

---

# 5. ITEM CATALOG

The mod should dynamically scan the currently loaded `ThingDef` database.

This scan should happen:

- once during initialization
- when a save with changed active mods is loaded
- when explicitly rebuilt through debugging/settings if needed

DO NOT repeatedly scan all ThingDefs during normal gameplay.

Build and cache a lightweight searchable catalog.

---

# 6. ITEM ELIGIBILITY

The system should attempt to automatically identify physical things the player can reasonably search for.

Examples:

- raw materials
- components
- medicines
- drugs
- apparel
- weapons
- artifacts
- usable miscellaneous items
- modded resources
- exotic items

Automatically reject obviously invalid content such as:

- motes
- gases
- filth
- blueprints
- frames
- pawns
- corpses unless intentionally supported later
- internal controllers
- invisible helper objects
- debug objects
- bookkeeping objects
- objects that cannot meaningfully exist in player possession
- temporary internal quest objects
- invalid ThingClasses
- zero-value technical definitions where clearly inappropriate

Use conservative heuristics.

Provide an optional:

**Show unusual items**

toggle.

Also provide per-item override:

- Auto
- Allowed
- Blocked

This ensures compatibility does not depend on perfect heuristics.

---

# 7. ITEM METADATA

Cached catalog entries should ideally know:

- ThingDef
- defName
- package/source mod
- label
- market value
- stackability
- tech level where inferable
- item category
- whether craftable
- whether tradeable
- whether used by recipes
- whether equipment
- rarity indicators
- unusual/internal confidence flag

Do NOT use this information to decide what the player "needs."

It exists for:

- economics
- opportunity generation
- contextual faction selection
- search filtering
- quantity estimation
- danger scaling

---

# 8. INTEL NETWORK

The first major player-facing feature is:

# REQUEST INTEL

The player contacts:

- a faction
- a broker
- a known contractor
- a generic information network
- another suitable contact

and says:

> "I'm looking for Tenebrite."

IMPORTANT:

The player does NOT specify quantity.

This is NOT procurement.

The player is purchasing information.

---

# 9. INTEL REQUEST FLOW

Example:

1. Open Comms Console / Network interface.
2. Select **Request Intel**.
3. Select contact/source.
4. Search item catalog.
5. Select:
   **Tenebrite**
6. Pay a relatively modest intel/search fee.
7. Receive:
   > "We'll let you know if we hear anything."
8. Wait.
9. After some time, the search resolves.

Possible result:

> "A Tenebral convoy carrying Tenebrite is expected to pass through this region."

The convoy's actual amount is determined by the generated opportunity.

NOT by the player's request.

---

# 10. INTEL OPPORTUNITY QUANTITIES

The selected item tells the generator:

> "Find an opportunity involving this."

The generated opportunity determines how much exists.

Example:

### Small convoy
20-60 Tenebrite

### Heavy convoy
70-180

### Storage depot
120-350

### Fortress
300-900+

### Ruin
5-100

### Trader lead
variable

### Orbital wreck
variable

The numbers above are illustrative only.

Actual quantity should consider:

- market value
- stack limits
- item category
- world wealth
- threat level
- opportunity archetype
- faction strength
- balance

---

# 11. INTEL REPORT

Once intelligence succeeds, the player should receive enough information to decide whether pursuing it is worthwhile.

Example:

**Tenebral convoy sighted**

Reported cargo:

- Tenebrite x47
- Quasar Alloy x123
- unknown additional cargo

Escort assessment:

**Moderate**

Distance:

**1.8 days**

Operational window:

**3.2 days**

Information confidence:

**Moderate**

The player may then:

- pursue
- ignore
- abandon
- continue waiting for another lead

---

# 12. INTEL QUALITY

Intel should have hidden truth values.

Player-facing confidence may be:

- Very Low
- Low
- Moderate
- High
- Very High

Do NOT show exact percentages by default.

Possible outcomes include:

## Accurate intel

Everything is approximately correct.

---

## Partial intel

Correct item and location, but:

- quantity wrong
- defenses wrong
- timing wrong
- additional threats missed

---

## Outdated intel

The convoy already moved.

The site was looted.

The group relocated.

Only remnants remain.

---

## Bad intel

The information is simply wrong.

Example:

Player travels expecting Tenebrite.

They find:

> a herd of sheep.

This should be rare enough to be funny instead of infuriating.

---

## Fake news / deliberate misinformation

Someone intentionally submitted false information.

Potential reasons:

- hostile faction
- rival contractor
- corrupt informant
- low-trust source

---

## Enemy trap

The supposed lead exists only to lure the player's group somewhere.

Example:

> "Small Tenebrite convoy."

Actually:

> hostile ambush.

---

## Competing claimant

The intelligence is real.

However another group obtained the same information.

They also arrive.

This is NOT necessarily an ambush.

They want the same thing.

---

## Unexpected jackpot

Intel significantly underestimated the opportunity.

---

## Unexpected complication

The goods are real.

The threat is much larger than expected.

---

# 13. CONTACT RELIABILITY

Different sources should naturally produce different intel quality.

Relevant factors:

- faction tech level
- faction type
- relationship/goodwill
- source specialization
- contact reputation
- geographic relevance
- source mod relationship if inferable
- previous reliability

Do NOT simply expose:

`Reliability = 72.15%`

Prefer narrative descriptors.

Over time the player should learn:

> "These guys usually know what they're talking about."

or:

> "This broker is full of shit."

---

# 14. OPPORTUNITY GENERATION

Once the player asks for an item, the system should determine plausible sources.

Example:

Selected ThingDef:

**BOR_Tenebrite**

Source mod:

**Beyond Our Reach Redux**

The mod may scan FactionDefs from the same source mod.

If appropriate active factions exist, they gain high relevance.

IMPORTANT:

Do NOT assume:

> same mod = enemy

Filter candidate factions based on:

- actual world presence
- hostility
- defeated status
- hidden status
- combat capability
- faction generation rules
- player relationship

---

# 15. SOURCE SELECTION FALLBACK

Preferred hierarchy:

### 1. Same-mod faction with plausible relationship to item

Best thematic match.

### 2. Faction known or likely to use similar technology

### 3. Suitable hostile faction by tech level

### 4. Neutral trader / merchant source

### 5. Ancient ruin/cache

### 6. Mechanoid-protected site

### 7. Pirate possession

### 8. Generic abandoned location

### 9. No credible lead found

The generator does not have to succeed.

---

# 16. INTEL OPPORTUNITY ARCHETYPES

Potential generated opportunities:

- small convoy
- large convoy
- transport caravan
- material depot
- warehouse
- remote outpost
- fortified settlement
- major fortress
- abandoned cache
- ancient ruin
- wreckage
- derelict installation
- crashed transport
- trader carrying stock
- private broker
- faction willing to sell
- hostile faction stockpile
- contested salvage
- orbital wreck
- asteroid deposit
- space station storage
- Odyssey/VGE-compatible future sites

Not every archetype must exist initially.

Architecture should permit expansion.

---

# 17. PHYSICAL LOOT

Whenever practical, intel missions should place the desired item physically in the world.

Example:

The player requests intelligence about Tenebrite.

The result is:

> Tenebral material convoy.

Actual Tenebrite exists in:

- cargo containers
- carried inventory
- transport animals
- storage buildings
- crates

The player can potentially:

- attack
- sneak in
- steal
- take only some
- retreat

The mission should not necessarily require:

> Kill every enemy.

The actual objective is acquisition.

---

# 18. EXTRA CARGO

Generated opportunities should contain believable additional loot.

If a convoy carries Tenebrite, it may also carry:

- related resources
- weapons
- medicine
- food
- components
- money
- unrelated cargo

This makes the opportunity feel like something that existed independently of the player's request.

---

# 19. PROCUREMENT NETWORK

The second major service is:

# PROCUREMENT

Procurement is fundamentally different from Intel.

Intel:

> "Tell me where I might find Tenebrite."

Procurement:

> "Bring me 500 Tenebrite."

The player specifies:

- exact item
- requested quantity

The contractor handles acquisition.

---

# 20. PROCUREMENT ECONOMICS

Procurement should be expensive.

Cost should consider:

- market value
- requested quantity
- rarity
- technological difficulty
- dangerous source factions
- distance/logistics
- acquisition difficulty
- contractor quality
- contractor profit margin
- urgency
- delivery method

The final price should normally be significantly higher than simply buying the item from a normal trader.

The entire point is:

> You're paying someone else to risk their neck.

---

# 21. PROCUREMENT PAYMENT STRUCTURE

Suggested default:

**50% deposit**

**50% upon successful delivery**

This may vary by:

- contractor reputation
- player relationship
- contract risk
- faction
- special negotiations

The deposit is at risk.

---

# 22. PROCUREMENT CONTRACTOR SELECTION

A request may be:

### Open Contract

Any eligible contractor may accept.

Potentially cheaper.

Less predictable.

---

### Direct Contract

The player hires a known group.

Example:

**Dead Red**

More predictable.

Relationship matters.

---

### Premium / Sponsored Contract

Player contributes:

- extra money
- equipment
- medicine
- logistical support

Improves expected capability.

---

# 23. PROCUREMENT OUTCOMES

Possible outcomes:

## Full Success

Requested goods delivered.

Player pays remaining balance.

---

## Partial Success

Requested:

500 Tenebrite

Recovered:

312

Player may:

- accept partial delivery at adjusted price
- request continuation
- renegotiate

---

## Delay

Contractors encounter difficulties.

ETA extended.

---

## Renegotiation

Contractor reports:

> "This is much worse than expected."

They request additional payment.

Player may:

- pay
- refuse
- cancel
- accept reduced scope

---

## Failure

Contractors fail.

Deposit is normally lost.

BUT FAILURE SHOULD CREATE WORLD CONSEQUENCES.

---

## Missing

No contact.

Last known location becomes available.

---

## Captured

Contractor group survived but was captured.

Creates rescue opportunity.

---

## Stranded

Group cannot leave.

Needs extraction/help.

---

## Catastrophic loss

Most or all members dead.

Battle site may be generated.

---

## Betrayal / Fraud

Very rare.

Contractor disappears with deposit or recovered cargo.

This should depend heavily on group reliability/history.

Can later create:

- manhunt
- intel lead
- rivalry
- reputation consequences

---

# 24. PROCUREMENT FAILURE SITES

When procurement fails, the player should often receive:

> **Last Known Location**

Example:

> The group known as **Dead Red** accepted your request for 500 Tenebrite.
>
> Their last transmission originated from an abandoned Tenebral facility.
>
> No further contact has been received.
>
> Last known coordinates have been added to the world map.

When player arrives, possible realities:

- everyone dead
- survivors wounded
- contractors captured
- enemy still present
- requested material partially acquired
- requested material untouched
- contractors succeeded but transport failed
- another group arrived afterward
- bodies looted
- leader missing
- site abandoned

---

# 25. PERSISTENT CONTRACTOR GROUPS

Contractors should NOT be anonymous random rolls.

Groups should persist.

Example:

**Dead Red**

Possible persistent data:

- unique ID
- generated name
- founder/leader identity
- member count
- operational strength
- experience tier
- equipment profile
- specialization
- reliability
- aggressiveness/caution
- loyalty
- reputation
- relationship with player
- injuries/losses
- current contract
- contract history
- sponsorship history
- rivals/allies
- age of organization
- retirement tendency
- source faction/region where applicable

---

# 26. OFF-MAP SIMULATION

DO NOT maintain full active pawns for every group indefinitely.

Preferred model:

**abstract organizational record**

while off-map.

When encountered:

- instantiate or restore relevant pawns
- create equipment
- generate leader/key members
- preserve notable individuals when necessary

When leaving relevance:

- collapse results back into group state

Key/notable individuals may require stronger persistence than generic members.

---

# 27. KNOWN INDIVIDUALS

Not every contractor member needs persistent identity.

Promote individuals to **Known Characters** when meaningful things happen.

Examples:

- group leader
- repeated survivor
- captured contractor rescued by player
- person who killed player's pawn
- rival champion
- person player directly interacted with
- betrayer
- famous veteran
- founder

Known characters should persist more strongly.

---

# 28. EXPERIENCE

Avoid MMO-style numerical leveling displays.

Prefer descriptive tiers:

- Green
- Experienced
- Seasoned
- Veteran
- Elite
- Legendary

Internally numeric values are fine.

Experience can influence:

- contract success
- casualty rates
- mission choice
- retreat behavior
- speed
- ability to acquire difficult items
- intel quality

---

# 29. CONTRACTOR PERSONALITY / DOCTRINE

Groups may possess broad operational tendencies.

Examples:

### Aggressive
Higher success in combat acquisition.
More casualties.

### Cautious
More likely to retreat.
Higher survival.
May abandon impossible contracts.

### Professional
Reliable but expensive.

### Opportunistic
Cheap.
May return with unexpected extra loot.
Less predictable.

### Scavenger
Strong salvage/recovery performance.

### Explorer
Strong remote/orbital/ruin discovery.

These should shape outcomes without becoming huge RPG stat sheets.

---

# 30. EQUIPMENT PERSISTENCE

If player sponsors a contractor with:

- weapons
- armor
- medicine
- exotic equipment

the contractor should retain appropriate representation of this gear.

If physically encountered later, sponsored equipment should appear when reasonable.

This enables stories such as:

> Player arrives at Dead Red's failed mission site and finds the Quasar rifle they gave the leader lying next to their corpse.

---

# 31. SPONSORSHIP

Player may invest in known groups.

Possible sponsorship:

- silver
- armor
- weapons
- medicine
- transport resources
- technology
- supplies

Effects may include:

- higher operational strength
- better survival
- access to harder contracts
- faster recovery
- stronger relationship
- better equipment

Sponsorship should not guarantee success.

---

# 32. CONTRACTOR CASUALTIES

Organizations should lose members.

Example:

Dead Red:

Members before job: 8

Outcome:

2 killed

Members afterward: 6

They may later:

- recruit replacements
- remain weakened
- change behavior
- retire
- dissolve

---

# 33. LEADERSHIP CHANGES

Leaders can:

- die
- retire
- disappear
- defect

New leaders may subtly alter group behavior.

Example:

Old leader:
Aggressive / loyal

New leader:
Cautious / profit-driven

Same organization.

Different era.

---

# 34. RESCUE AND RELATIONSHIP

If a contractor is captured while working for the player:

Generate a rescue opportunity.

If the player saves them, this should matter.

Possible effects:

- stronger relationship
- reduced fees
- greater willingness to accept dangerous contracts
- improved intel
- occasional favors
- higher loyalty

The relationship exists because of history.

Not because the player clicked:

> +10 Reputation

---

# 35. PLAYER AS CONTRACTOR

The player should eventually be able to:

# REGISTER A CONTRACTING GROUP

This does NOT replace the colony/faction.

It registers the colony as an organization capable of accepting jobs.

Player chooses:

- organization name
- optional emblem/icon later
- basic public profile

Initial reputation:

**Unknown**

---

# 36. NPC CONTRACT BOARD

Factions and other actors may post contracts.

Examples:

## Procurement

> Deliver 80 Quasar Alloy.

---

## Recovery

> Retrieve stolen property.

---

## Rescue

> Recover missing personnel.

---

## Hunt

> Eliminate threat.

---

## Investigation

> Determine what happened at a location.

---

## Escort

> Protect caravan/personnel.

---

## Salvage

> Recover material from wreck.

---

## Transport

> Move goods from A to B.

---

## Acquisition

> Obtain specified rare object.

Not every contract should involve combat.

---

# 37. PHYSICAL DELIVERY

If player accepts a procurement contract:

> Deliver 120 Plasteel to Settlement X.

The game should NOT magically remove 120 Plasteel from player's stockpile.

Player must physically deliver it.

Possible methods:

- caravan
- transport pods
- shuttle
- gravship
- modded compatible transport methods where practical

This makes logistics meaningful.

---

# 38. CONTRACT DEADLINES

Contracts may include deadlines.

This creates emergent logistics.

Example:

Deadline:
1.6 days

Caravan travel:
2.4 days

Player:

> use transport pods.

The mod should leverage RimWorld transport systems, not bypass them.

---

# 39. PLAYER CONTRACTOR REPUTATION

Player's registered group develops reputation.

Possible conceptual dimensions:

- reliability
- prestige
- speed
- combat capability
- integrity
- discretion

Do not expose excessive numerical dashboards by default.

Translate into broader labels:

- Unknown
- Local
- Established
- Respected
- Renowned
- Legendary

Reputation influences available jobs.

---

# 40. DIRECT CONTRACT OFFERS

At higher reputation:

Factions may contact player directly.

Example:

> "We've worked together before. We need something handled."

These may offer:

- better pay
- rare rewards
- politically sensitive missions
- difficult acquisition jobs
- secret contracts

---

# 41. FACTION SPONSORSHIP OF PLAYER

Factions may occasionally supply the player with temporary equipment/resources for contracts.

Example:

- armor
- weapons
- medicine
- transport

Player may be expected to return sponsored equipment.

Failure/refusal can affect:

- integrity
- faction goodwill
- future offers

---

# 42. COMPETING CONTRACTORS

Some contracts should NOT be exclusive.

Two or more groups may pursue the same objective.

Example:

Faction posts:

> Procure Tenebrite.

Player accepts.

Dead Red also accepts.

Both discover the same material depot.

Possible player responses:

- race them
- cooperate
- split cargo
- fight
- wait for them to fight defenders
- steal from them afterward
- abandon objective

This creates organic rivalry.

---

# 43. RIVALRIES

Rivalry should emerge from history.

Do NOT simply randomly assign:

> Rival.

Potential rivalry triggers:

- repeatedly winning contracts against same group
- stealing cargo
- killing contractor members
- sabotaging operations
- betraying agreements
- rescuing their enemies
- competing for same sites
- humiliating them
- refusing payment
- abandoning joint operation

Relationship states may include:

- Unknown
- Familiar
- Friendly
- Professional
- Competitive
- Trusted
- Rival
- Bitter Rival
- Hostile

Exact structure can evolve.

---

# 44. GROUP-LEVEL RIVALRY

Rivalry should primarily persist at organization level.

If player kills a famous rival leader:

The organization does NOT automatically disappear.

Possible consequence:

> leadership changes

New leader may:

- continue rivalry
- intensify it
- de-escalate
- blame previous leader
- seek revenge

This prevents a powerful pawn from destroying the entire persistent story system with one headshot.

Especially important with mods such as GM21.

---

# 45. INDIVIDUAL RIVALS

Important NPCs may become individually memorable.

Example:

**Rika "Ash" Morino**
Commander, Dead Red

Known history:

- encountered 4 times
- competed for 3 contracts
- killed one allied pawn
- escaped twice
- currently hostile

If she dies:

She remains dead.

DO NOT give story-important NPCs arbitrary plot armor.

The world should react to death rather than undoing it.

---

# 46. SUCCESSOR STORIES

If an organization loses leadership:

Possible results:

- new leader
- fragmentation
- dissolution
- successor company
- absorbed by another contractor

A successor may inherit:

- some reputation
- some equipment
- some rivalries
- selected history

This should be rare enough to remain meaningful.

---

# 47. RETIREMENT

Contractors should not all fight until extinction.

Organizations or leaders may retire.

Reasons:

- wealth
- age
- accumulated losses
- success
- low morale
- long career
- random personal choice

Possible outcome:

> Dead Red announces retirement after eleven years of operations.

High-relationship players may receive a farewell message.

---

# 48. POST-RETIREMENT POSSIBILITY

Long-term optional feature:

Retired contractors may:

- settle
- establish a small settlement
- become traders
- become intel contacts
- become recruiters
- disappear peacefully
- create successor organizations

Do not require this for core architecture, but leave room for it.

---

# 49. JOINT OPERATIONS

Player may sometimes cooperate with contractor groups.

Examples:

- raid together
- salvage together
- escort together
- rescue together

Contract terms may determine reward split.

Working repeatedly together affects relationship.

---

# 50. SUBCONTRACTING

Potential later system:

Player accepts a difficult contract.

Player may hire known contractor group to assist.

This creates funny legitimate situations where:

Dead Red prepares six elite mercenaries.

Player sends one absurdly powerful GM21 pawn.

Mission ends in twelve seconds.

Contractors may react accordingly.

---

# 51. WORLD HISTORY

Maintain a lightweight history of important events.

Examples:

- Dead Red first hired
- successful contracts
- catastrophic failures
- player rescued Dead Red
- leader killed
- rivalry began
- sponsored equipment
- retirement
- betrayal
- joint victories

This history feeds:

- relationship
- letters
- flavor text
- known character summaries

Do NOT record every trivial event forever.

Use pruning/limits.

---

# 52. MISSION GENERATOR PHILOSOPHY

The mod should primarily generate opportunities from:

> player intent + world context

NOT:

> arbitrary reward first.

Intel example:

Player chooses:
Tenebrite

Generator asks:

1. What item is this?
2. Where could it plausibly exist?
3. Who might possess it?
4. What scale of opportunity was discovered?
5. How reliable is the information?
6. Who else might know?
7. What danger makes sense?
8. What additional cargo makes sense?

Then generate the situation.

---

# 53. DO NOT PRIORITIZE PLAYER RESEARCH

Very important.

DO NOT inspect unfinished research and automatically push matching materials toward the player.

The mod does not know or care that Tenebrite is required for some BOR technology.

It only knows:

> the player asked about Tenebrite.

This prevents the mod from becoming a hidden progression assistant.

---

# 54. VANILLA QUEST GENERATION

DO NOT globally patch or replace vanilla quest generation unless absolutely necessary.

Vanilla storyteller quests should continue normally.

Other quest mods should continue normally.

ISEKAI should continue normally.

The Network should operate alongside them.

---

# 55. STORYTELLER INDEPENDENCE

The Network is NOT a storyteller.

It does not replace:

- Cassandra
- Randy
- VOID
- custom storytellers

Storytellers still control normal incident pacing.

The Network controls:

> contracts, intelligence, procurement, and contractor activity.

This separation allows chaotic interactions naturally.

Example:

Player attacks a contractor-generated site.

VOID storyteller decides to trigger another disaster simultaneously.

That is allowed.

---

# 56. MOD COMPATIBILITY PHILOSOPHY

The mod should attempt broad automatic compatibility.

DO NOT hardcode:

> If BOR loaded...

unless absolutely necessary.

The ideal test:

If a mod adds:

**Weirdium**

The Network should be capable of displaying/searching for Weirdium without knowing what Weirdium is.

---

# 57. SOURCE MOD AWARENESS

Although there should be no hard dependency, source-mod metadata may be used as contextual evidence.

Example:

Item:
Tenebrite

Source mod:
BOR

Faction:
Tenebral faction

Source mod:
BOR

This increases contextual relevance.

But never assume a relation merely because both come from same package.

Treat it as one signal among several.

---

# 58. OPTIONAL COMPATIBILITY ADAPTERS

Architecture may later allow small optional compatibility adapters.

These may improve:

- faction association
- item categorization
- special transport
- unique world sites
- special quest logic

But the base system must remain functional without them.

---

# 59. ODYSSEY / SPACE SUPPORT

The architecture should leave room for:

- orbital salvage
- asteroid caches
- derelicts
- space-based convoys
- orbital installations
- gravship delivery
- space contractor operations

Do not make Odyssey a hard dependency.

If Odyssey is absent:

space-specific archetypes simply do not exist.

---

# 60. PROCUREMENT DELIVERY TO PLAYER

Successful NPC procurement should deliver goods plausibly.

Possible methods:

- arriving caravan
- drop pods
- shuttle
- designated meeting
- trade delivery
- gravship/space delivery later

Delivery method may depend on:

- contractor
- faction tech
- player tech
- location
- item

---

# 61. PROCUREMENT SAFETY VS CERTAINTY

Procurement is safer for PLAYER PAWNS.

It is NOT guaranteed.

The contractor may die.

The player's risk becomes:

> financial

rather than:

> pawn mortality.

This distinction is critical.

---

# 62. INSURANCE

Optional procurement feature:

Player may purchase insurance.

Example:

Base contract:
80,000 silver

Deposit:
40,000

Insurance:
+12,000

If contractor fails:

recover part of deposit.

Insurance should never make contracts risk-free.

---

# 63. CONTRACTOR MARKET

Over time, player should accumulate a roster of known groups.

Example:

### Dead Red
Veteran
Aggressive
Trusted
Expensive

### Golden Compass
Elite
Professional
Very Reliable
Very Expensive

### Lucky Rats
Seasoned
Opportunistic
Cheap
Questionable

### Horizon Company
Explorer
Slow
Excellent remote acquisition

Choice of contractor becomes meaningful.

---

# 64. OPEN MARKET CONTRACTS

Not every contract needs a known group.

Player may post to open market.

A newly generated company may accept.

This becomes how new persistent groups enter the player's story.

---

# 65. CONTRACTOR DISCOVERY

Groups may become known through:

- accepting player's contract
- competing against player
- appearing at same site
- rescue
- faction introduction
- trade contact
- direct hire
- random world activity
- player accepting one of their requests

---

# 66. GROUP FAME

Some groups may become notable independently.

Possible descriptors:

- Unknown
- Local
- Established
- Famous
- Legendary

Fame affects:

- pricing
- job access
- reputation
- likelihood factions hire them
- rival attention

---

# 67. ABSTRACT CONTRACT RESOLUTION

Off-screen contracts should generally be resolved mathematically rather than simulated with actual combat maps.

Possible resolution inputs:

- contractor experience
- equipment
- specialization
- group size
- leadership
- mission danger
- enemy strength
- distance
- logistics
- item difficulty
- random variation
- sponsorship
- injuries
- morale/relationship

Output may include:

- success
- casualties
- recovered amount
- delay
- capture
- retreat
- catastrophic failure
- extra loot

Only create a physical map if the player becomes involved.

---

# 68. PERFORMANCE REQUIREMENTS

Performance is a major requirement.

DO NOT:

- scan all ThingDefs every tick
- update every contractor every tick
- keep dozens of contractor pawns actively simulated
- perform expensive LINQ scans continuously
- globally patch broad hot paths without necessity
- constantly recompute item/faction relationships

Prefer:

- startup caching
- scheduled low-frequency world ticks
- event-driven updates
- lightweight serialized state
- deterministic generation
- cached lookup dictionaries
- lazy pawn instantiation

Background simulation should be effectively invisible to TPS during normal gameplay.

---

# 69. SAVE SAFETY

All persistent systems must serialize cleanly.

Use stable identifiers.

Support:

- saving during contracts
- loading during searches
- loading with pending procurement
- contractors currently captured
- active rivalry
- missing contractor sites

If an external mod is removed:

- missing ThingDefs must not crash the save
- invalid requests should gracefully cancel
- invalid cached entries should rebuild/remove
- explain cancellation where appropriate
- never spam red errors

---

# 70. UI PHILOSOPHY

Keep UI compact.

This is primarily a background simulation.

Avoid turning the game into:

> Contractor Management Spreadsheet Simulator.

Potential main interface:

# THE NETWORK

Tabs:

- Intel
- Procurement
- Contracts
- Contractors
- History

---

# 71. INTEL UI

Example:

**Request Intel**

Contact:
Kwazaari Traders

Looking for:
Tenebrite

Fee:
650 silver

Estimated search:
Unknown / several days

[Submit Request]

Once submitted:

Tenebrite
Status: Searching
Elapsed: 4.8 days
Source: Kwazaari Traders

[Cancel]

---

# 72. PROCUREMENT UI

Example:

**Procurement Request**

Item:
Tenebrite

Quantity:
500

Contract type:
Open

Estimated total:
86,000 silver

Deposit:
43,000 silver

[Post Contract]

Known contractors may show offers.

---

# 73. CONTRACTOR UI

Example:

**Dead Red**

Status:
Active

Experience:
Veteran

Specialty:
Combat Acquisition

Relationship:
Trusted

Operational strength:
High

Equipment:
Spacer

Jobs for you:
8

Successful:
6

Failed:
2

Notable history:

- Rescued by colony, 5508
- Lost founder Mara Red, 5512
- Sponsored with Quasar equipment

Avoid excessive raw stats.

---

# 74. PLAYER CONTRACT BOARD UI

Shows:

- issuer
- contract type
- requested objective
- payment
- deadline
- competition status where known
- delivery target
- threat information where known

Player may:

- accept
- decline
- inspect
- negotiate later if supported

---

# 75. SETTINGS

Useful configuration options:

- enable/disable Intel
- enable/disable Procurement
- enable player contractor registration
- contractor population scale
- background activity rate
- intel reliability modifier
- fake/bad intel frequency
- rival contractor frequency
- procurement price multiplier
- procurement mortality multiplier
- retirement enabled
- detailed logging
- unusual item visibility
- rebuild item catalog

Do not expose hundreds of tiny tuning sliders unless necessary.

---

# 76. DEBUGGING TOOLS

Provide developer/debug utilities.

Examples:

- rebuild catalog
- print item classification
- force Intel result
- force specific opportunity archetype
- spawn contractor group
- force procurement success
- force procurement capture
- generate failed expedition site
- inspect group state
- set relationship
- force retirement
- simulate 1000 contract resolutions without physical maps

Detailed logging should be optionally available.

Prefix logs clearly.

Example:

`[TheNetwork]`

---

# 77. BALANCE RULE

The mod must not become an infinite wealth generator.

Intel opportunities require:

- danger
- travel
- cost
- uncertainty

Procurement costs:

- should normally exceed straightforward market purchase
- should scale aggressively for rare/high-value items

Player contracts should:

- pay enough to be worthwhile
- not allow trivial infinite arbitrage

Use actual market value as one input, not the sole balancing mechanism.

---

# 78. ECONOMIC EXPLOIT PROTECTION

Watch for:

- procuring item cheaper than selling it
- accepting NPC procurement contract then buying same item from their own settlement for profit
- infinite transport-pod loops
- stack-value exploits
- quality exploits
- modded items with broken market values
- zero-cost recipe products with enormous market value
- quest-only artifacts
- faction goods duplication

Have sanity caps/fallback calculations.

---

# 79. ITEM QUALITY

For quality-bearing items:

Intel may specify:

- minimum quality
- approximate quality
- no quality requirement

Procurement could later allow quality requirements at extreme additional cost.

Do not make this required for the basic system.

---

# 80. UNIQUE ARTIFACTS

Treat direct progression keys, artifacts, and unique items cautiously.

They may appear in catalog if valid.

But:

- procurement prices should be severe
- success should be difficult
- intel opportunities should be rare
- some may be marked unavailable if clearly unsafe/internal

Raw materials should naturally be easier to work with than unique quest objects.

---

# 81. STORY LETTERS

Use narrative letters sparingly but effectively.

Examples:

### Intel Result

> A contact reports that a small Tenebral transport convoy is moving through the eastern wastes. The convoy is believed to contain Tenebrite.

### Procurement Failure

> Dead Red has failed to report after entering a fortified complex. Their last transmission has been traced to a location 2.4 days away.

### Capture

> Survivors from Dead Red are believed to have been taken prisoner.

### Rival Encounter

> Golden Compass has arrived at the salvage site. They appear to be after the same cargo.

### Retirement

> After twelve years of operations, Dead Red has formally retired from contracting.

Make these feel like RimWorld events rather than system notifications.

---

# 82. STORY OVER STATISTICS

Whenever possible, prefer:

> "Dead Red has become known for surviving impossible recovery contracts."

over:

> `RecoverySkill +18`

Prefer:

> "Trusted"

over:

> `Relationship 82.4`

Internal numbers may exist.

Presentation should remain narrative-first.

---

# 83. EXAMPLE FULL STORY

Player wants:

**Tenebrite**

They contact a broker.

Pay:
500 silver.

Five days later:

> A Tenebral convoy reportedly carries approximately 40-70 Tenebrite.

Player travels there.

They discover:

- convoy actually contains 53 Tenebrite
- defenses heavier than expected
- contractor group Dead Red arrives seeking same convoy

Player and Dead Red fight Tenebral guards separately.

Player obtains 31 Tenebrite.

Dead Red escapes with 22.

Months later:

Player needs:
500 Tenebrite.

Instead of doing it themselves:

> Post Procurement Contract.

Dead Red accepts because they now know the material.

Deposit:
35,000 silver.

Eight days later:

> Dead Red missing.

Last known location generated.

Player investigates.

They find:

- three Dead Red members dead
- two captured
- their leader wounded
- 180 Tenebrite already recovered
- hostile Tenebral survivors

Player rescues Dead Red.

Dead Red becomes:

**Trusted**

Years later:

Dead Red has become an elite contractor group.

Player sponsors them with advanced weapons.

Later they compete against player for another contract.

Eventually their founder dies.

Leadership changes.

Years afterward they retire.

The player remembers them.

No handcrafted story was written.

The systems created it.

THIS is the target experience.

---

# 84. PLAYER CONTRACTOR EXAMPLE

Another faction posts:

> Procurement Request:
> 100 Quasar Alloy
>
> Reward:
> 24,000 silver
>
> Deadline:
> 9 days

Player accepts.

Player currently owns only 20.

They request Intel about Quasar Alloy.

A lead identifies a hostile depot.

Player raids it.

Golden Compass appears at the same site.

Player obtains 140.

Player keeps 40.

Player sends 100 via transport pods to requesting settlement.

Contract completes.

Player contractor reputation increases.

Golden Compass now regards player as a serious competitor.

Again:

one contract produced several systems interacting naturally.

---

# 85. MAJOR NON-GOALS

DO NOT:

- automatically solve player progression
- automatically grant needed research items
- replace storyteller
- replace vanilla quest generation
- simulate entire contractor armies continuously
- create mandatory BOR/AOTC dependencies
- force every mission into combat
- guarantee Intel accuracy
- guarantee Procurement success
- make named NPCs immortal
- create huge mandatory micromanagement
- use constant hot-path Harmony patches if avoidable
- hardcode compatibility unless necessary
- turn organization management into an RPG spreadsheet

---

# 86. TECHNICAL DESIGN DIRECTION

Preferred high-level classes/systems might conceptually include:

`NetworkWorldComponent`

Maintains global Network state.

---

`NetworkItemCatalog`

Cached eligible ThingDefs and metadata.

---

`ContractorManager`

Persistent contractor organizations.

---

`ContractorGroupRecord`

Serialized lightweight group state.

---

`KnownContractorPawnRecord`

Persistent important characters.

---

`IntelManager`

Tracks searches and resolves leads.

---

`ProcurementManager`

Tracks procurement contracts.

---

`ContractBoardManager`

Generates NPC requests.

---

`OpportunityGenerator`

Creates plausible world opportunities from item + context.

---

`NetworkRelationshipManager`

Tracks inter-group and player relationships/history.

---

`NetworkHistory`

Stores meaningful events.

---

`NetworkSettings`

Mod settings.

Actual implementation may differ after repository/API inspection.

Do not blindly implement these exact names if a better architecture exists.

---

# 87. QUEST / SITE IMPLEMENTATION

Where practical, use:

- Quest
- QuestPart
- QuestScriptDef
- WorldObject
- Site
- SitePart
- Faction
- Incident
- Letter
- Caravan/transport systems

Do not create parallel implementations for systems RimWorld already provides.

Mission generator should provide appropriate Slate/context values and let vanilla systems handle downstream gameplay where possible.

---

# 88. ABSTRACT → PHYSICAL TRANSITION

A contractor exists abstractly off-map.

When a relevant site is generated:

- determine participating contractor group
- instantiate meaningful pawns/equipment
- use persistent leader if required
- generate generic members around group profile
- run actual gameplay normally

After resolution:

- read surviving state
- update contractor record
- persist important pawn outcomes
- discard unnecessary transient simulation

This transition is one of the most technically important parts of the mod.

---

# 89. MOD REMOVAL ROBUSTNESS

External item/faction references must be defensive.

Do not serialize raw assumptions that an external Def will always exist.

On load:

- resolve by stable identifiers
- verify Def exists
- gracefully invalidate missing entries
- rebuild caches where appropriate

Contractor data should survive unrelated mod removal where possible.

---

# 90. EXTENSIBILITY

The architecture should support future additions such as:

- orbital contractor operations
- smuggling
- black markets
- insurance companies
- faction-specific brokers
- escort contracts
- assassination warrants
- prisoner recovery
- diplomacy contracts
- sabotage
- bounty hunting
- contractor settlements
- group mergers
- group fragmentation
- succession
- recruiting retired contractors
- faction sponsorship
- shared raids
- contractor vehicles
- modded transport integration

Do not implement these merely because they are listed.

Ensure core architecture does not block them.

---

# 91. MOD IDENTITY

This should feel like:

> **a living contractor economy behind the normal RimWorld game**

rather than:

> a quest menu.

The player should sometimes forget the mod is there until:

> "Dead Red has accepted your procurement request."

or:

> "Golden Compass has arrived at the same salvage site."

or:

> "Your contact's Tenebrite lead was fake."

or:

> "Dead Red has been captured."

The systems should emerge into the foreground only when something worth caring about happens.

---

# 92. FINAL DESIGN PHILOSOPHY

The central question is NOT:

> "How can the mod give the player what they need?"

It is:

> **"How can the player express an interest, and how might a living RimWorld respond?"**

If the player asks for information:

the world gives them a rumor.

If the player offers money:

someone may take the job.

If that person fails:

their failure leaves a mark on the world.

If the player rescues them:

they remember.

If they meet again:

history matters.

If the player becomes a contractor:

other groups become colleagues, competitors, friends, or enemies.

If somebody dies:

the story changes.

Nothing exists simply to deliver the player's desired outcome.

The entire system should exist to produce:

**opportunities, consequences, relationships, and stories.**

---

# 93. CORE TEST OF SUCCESS

The ultimate test is this:

A player initially installs the mod because:

> "I can't find Tenebrite."

Several in-game years later they should be telling a story like:

> "Remember Dead Red? Those idiots who beat me to the first Tenebrite convoy? I ended up hiring them later. They screwed up a procurement run and got captured, so I rescued them. I gave their leader a Quasar rifle afterward. Then he died during another contract. Their new leader hated me for a while, but eventually we worked together again. They retired last year."

At that point the mod has succeeded.

The Tenebrite was never the real feature.

**The story was.**

---

# 94. IMPLEMENTATION INSTRUCTION

Before implementing anything:

1. Inspect RimWorld 1.6 APIs and existing vanilla quest/site/world systems.
2. Identify reusable vanilla mechanisms.
3. Identify performance risks.
4. Identify save/load requirements.
5. Identify likely mod compatibility hazards.
6. Design the persistent data model.
7. Design abstract contractor simulation.
8. Design abstract→physical pawn transition.
9. Design the item eligibility scanner.
10. Design Intel and Procurement state machines.
11. Only then propose development phases.

Do NOT immediately start coding the entire design.

First return:

- architectural review
- feasibility assessment
- risky areas
- proposed data model
- vanilla systems to reuse
- Harmony patches truly required, if any
- compatibility strategy
- save/load strategy
- performance strategy
- recommended implementation phases

Preserve the FULL long-term design above even if the first implementation phase is intentionally much smaller.

The end goal is not merely a working procurement menu.

The end goal is:

# A persistent procedural contractor ecosystem that generates RimWorld stories behind the scenes.