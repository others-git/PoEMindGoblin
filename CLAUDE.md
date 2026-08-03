# CLAUDE.md — MindGoblin

> **BE CONCISE.** Answer the question asked. Lead with the result. No preamble, no recap
> of what you just did, no unsolicited alternatives.

A Path of Exile toolkit for Windows. One portable self-contained .exe, **no credentials
anywhere** — every tool uses public data or the player's own screen.

| Tool | What it does |
|---|---|
| Voyage planner | Reads the chart panel off a screenshot, solves the 3×3 board, says `square 5 ← chart 23` |
| Gem RoI | Which vendor-buyable gems are worth levelling, priced off poe.watch |

```
src/MindGoblin.Core/        no UI: gem RoI, prices, the whole Voyage model and solver
  Voyage/VoyageStrategy.cs  THE SCORING CORE: ModCatalog (every mod -> stat + normalized
                            value, once) x Strategies (weight presets). Compiles into
                            VoyageProfile so the solver never sees the split.
src/MindGoblin/             WPF app (net10.0-windows10.0.19041.0 — the OCR needs the SDK projection)
tests/MindGoblin.Tests/     xunit; Core is UI-free so these run headless
tools/fetch_voyage_mods.py  regenerates assets/voyage-mods.json from poedb
tools/VoyageProbe/          decode a screenshot, print a plan, benchmark, all headless
research/                   gitignored notes; community research lives here
```

## Running things

```bash
./build.ps1                          # test, then publish to publish/ (never publish-v2)
dotnet run --project tools/VoyageProbe -- shot.png sulphur overlay.png   # decode + plan + calibration overlay
dotnet run --project tools/VoyageProbe -- session.json --refine          # every profile over a saved session
dotnet run --project tools/VoyageProbe -- session.json --budget strongbox  # does more time find better?
python3 tools/fetch_voyage_mods.py   # refresh the mod tables after a patch
publish/MindGoblin.exe --render voyage out.png --demo shot.png           # render the UI offscreen
publish/MindGoblin.exe --render voyage out.png --demo shot.png --weights # ...with the weights panel open
```

**The game's resolution is DETECTED, not assumed.** `ScreenCapture.ResolveGameBounds()`
asks the game window for its CLIENT rect (`GameWindow`), falls back to a resolution the
user pinned in the calibrator, and only then to the primary screen. Everything downstream
works in CLIENT coordinates — the capture is that rectangle, so a windowed game on a
second monitor reads exactly like a fullscreen one, and only `GameInput.HoverAt` adds the
window origin back. Before this the app rescaled to the primary screen, which is the same
number only when the game is fullscreen on it.

**Identify Charts always saves its screenshot** to `MindGoblin_data/last-identify.png`
— when a read goes wrong, the pixels ARE the bug report; VoyageProbe decodes that file
offline, overlay and all. The in-app Calibrate window draws the grid over it live.

**Never screenshot the primary screen to inspect the UI** — the game is usually on it, and
once that captured a live boss fight instead of the app. Use `--render`.

## Voyage mechanics (verified against the game's own help text)

* Board is **3×3**, chart panel **6×10**. Place **up to** nine charts — but the game
  never leaves a square empty *by choice*: with nine-plus charts the board must fill,
  even when every chart scores negative (the currency profile once answered its own
  penalties with a seven-chart board). Empties are only legal when the pool or the
  placement cap cannot cover the cells.
* **The voyage starts in the bottom-left chart** and travels by connections.
* **Every chart has exactly one implicit**, and it is either *"in all Voyage Areas"*
  (global, position irrelevant) or *"adjacent Areas"* (buffs neighbours). The copied item
  text has NO label for it — scope is read from the wording.
* **Every board edge applies a modifier to the chart touching it**, rerolled each voyage.
  Twelve edge segments on a 3×3, so a corner square is touched by two, an edge-centre by
  one, and **the middle by none** — which is why square 5 is off the read checklist.
* **The figurine TOOLTIP is the authoritative border text** (hover the carving, not the
  tile): the Area Modifiers panel never lists a figurine's adjacent-scope lines. Figurines
  are read by hover + OCR of the magic-blue tooltip text, bind one BoardModifier per line,
  and take precedence over any square-panel read of the same cell — the reverse once held
  and one stale panel read silently muted twelve fresh figurine captures.
* **Bottles are the chase** (researched): "Adjacent Areas contain 1–2 additional Messages
  in Bottles" is a hidden-until-charted voyage mod, ilvl 68+, max +2, on every base that
  shows a table. ~39c each, sellable UNOPENED. It cannot be crafted for — only revealed.
  **A bottle is GROUND LOOT** (field-confirmed): a fixed-value item whose count the roll
  fixes — receiver quantity multiplies nothing, so the gift scores FLAT per adjacent
  area and belongs in the centre. **The bottles solve seats ONE bottle chart per voyage**
  (best roll, PINNED to the centre; the rest held back and said so in the solve notes) —
  a second chart mostly re-covers the same areas, while held back it is a whole extra
  voyage of bottles. The pin is a solver CONSTRAINT (`VoyageSolver`'s `pin:`), not a
  bonus: priced at 1e7 the bound could not see an early rotation closing the centre off
  and the chart came back cornered — the Soul Eater lesson, measured.
* **3.29.1**: the max-res chart mod NEVER FUNCTIONED and no longer rolls (a chart still
  carrying it is free upside — the alert says so); Grasping Vines is its replacement
  danger; dredged currency now rolls Strongboxes mid-voyage; Eldritch Depths is a new
  chart variant, unmeasured.
* **All connections must lead to the board edge or to another connection.** Connections
  are mutual: an open edge facing a closed one is invalid from both sides. An open edge
  facing an *empty in-bounds cell* is invalid — which is why a partial board must form a
  closed cluster (four Corners can, four Crossings cannot).
* A square cut off from the route is **never visited**, so a chart there is wasted.
* **Order of visit matters, and it is not shortest-path** — every square gets visited
  either way. Allflame Lanterns deplete as they are placed, and a single death ends the
  voyage, so both pressures say the same thing: take the valuable squares EARLY. That is
  the traveling repairman's problem, solved exactly by subset DP (9 squares = 512 subsets).

## Gotchas (each of these was a silent bug)

* **GGG misspells Quantity as "Qauntity"** — but only in the global lines. The adjacent and
  in-area versions spell it correctly. A rule matching "Quantity" scores some rolls and
  silently misses others.
* **The Divine Orb line has its words the wrong way round** — `Rare Monsters adjacent in
  Areas drop # additional Divine Orbs`, alone among its eleven `in adjacent Areas`
  siblings. Also `Diviner's` contains the letters of `Divine`, so match `Divine Orb`, never
  `Divine`.
* **Copied chart text is not a tooltip.** It has `{ Implicit Modifier }` / `{ Prefix
  Modifier "Savage" (Tier: 3) }` headers, bracketed reminders, and a vendor line. The
  in-game HOVER instead uses a bare `Adjacent Modifier:` label with the value on the NEXT
  line — untreated, that label was taken for the chart's name.
* **Numbers carry their roll range inline**: `9(8-10) additional packs`. A rule anchored on
  `(\d+)\s+additional` never matches, because after the digits comes a bracket.
* **The headline stats are AGGREGATES.** A chart showing `Dead Man's Sulphur: +90%` carries
  three separate `30% increased ... in this Area` lines. Scoring both trebles it.
* **The game writes the singular when a roll is 1** — `an additional cage of Tormented
  Spirits` where the mod table only ever shows `# additional cages`.
* **The Area Modifiers panel shows board mods RESOLVED** for the hovered square (`Rare
  Monsters in Area drop an additional Chaos Orb`) where the table has the template
  (`... in adjacent Areas drop # additional Chaos Orbs`). Both wordings must score.
* **An empty Area Modifiers panel has three meanings** — placeholder (nothing hovered),
  genuinely no modifiers, or the capture missed the panel. The panel's *heading* tells them
  apart, and conflating them either stalls the read or records a failed capture as fact.
* **EVERY PIXEL CONSTANT IN A READER MUST SCALE, AND A COUNT SCALES DIFFERENTLY FROM A
  LENGTH.** The calibration is measured at 2560x1440 and rescaled; anything left fixed
  silently means something *stricter* on a smaller screen. Three of them together decoded
  eight of twenty-four charts wrongly at 1080p — Crossings as Junctions, Straights as
  Ends — and each looked harmless alone:
  `LongestRun`'s density floor (a Crossing's dark arm leaves few green pixels in its
  column, so under a fixed floor the run SPLIT at the arm, the box collapsed to half the
  tile, and "the east edge" got measured at the centre); `OpenThreshold` (asking a picture
  with half the scan lines for the same count of them); and `Pitch`, which is *multiplied*
  by row and column — round it once and the error accumulates a cell down the panel.
  Lengths scale with `s`, sample COUNTS with `s²`, and thresholds must round DOWN or they
  tighten as the picture shrinks. `ShapesDecodeIdenticallyAtEveryResolution` pins it.
* **The level TEMPLATES cannot be re-rendered, only resampled.** They are carved from a
  real capture (no installed font matches Fontin), so at another resolution they are
  resampled to the caption height — and a resampled mask is exactly the kind of thing this
  reader distrusts, so the rule is enforced directly: whatever it reads must match the
  reference, and everything else reads null. Below 1440p most levels still read blank; a
  missing level is recoverable, a fabricated one corrupts the plan. Closing that gap needs
  templates carved from a NATIVE capture at that resolution (`Learn`), not better maths.
* **Windows OCR reads PoE prose well and small numerals badly** — it read `L:76` as
  `L.'j6`. Chart levels go through template matching; only modifier text goes through OCR.
* **PoE's font (Fontin) is not on Windows.** A sweep of every system `.ttf` at every
  plausible size missed by 134 of ~130 ink pixels, so glyphs must be carved from a capture.
* **Fontin's `ffi` ligature is an EMPTY glyph** — zero contours, full 1658-unit advance —
  in SmallCaps, Bold and Italic; only Regular draws it. Any word carrying one loses three
  letters and keeps their space (`Free di⎵⎵culty roll`). `VoyageView.xaml` sets
  `Typography.StandardLigatures="False"` on the root, where it inherits to tooltips too;
  a new window using `PoeFonts` needs the same line.
* **Anything that reads the border must go through `BoardModifiers()`**, never
  `SquareModifiers` directly. The slurp writes FIGURINES, so a feature reading the square
  dictionary sees an empty board in the normal workflow — which is how `VoyageAlerts`
  came to read, score and badge a Divine figurine and never once announce it. Same split
  put `ReadProgress` (panel reads only) permanently behind `SquaresAwaitingModifiers`
  (figurine-aware), so a finished pass showed 75%.
* **Calibration is measured in pixels, and the app both READS and HOVERS at it.** Scale
  both or neither: `ChartPanelReader.Options.ForScreen` / `AreaModifierPanel.Options.ForScreen`
  are the one way in. The reader scaled its own copy while the slurp hovered the raw
  numbers, so off 2560x1440 the panel decoded correctly and the cursor sat on a different
  cell — and the wrong chart's text parses perfectly.
* **A hotkey handler that awaits is `async void`.** `HotkeyService` catches only the
  synchronous part; anything past the first await is unhandled on the dispatcher and ends
  the app. Every such handler carries its own try/catch.
* **WSL does not pass environment variables to Windows binaries.** An experiment gated on
  one measures nothing; pass a flag or an argument instead.

## Where the data comes from

`assets/voyage-mods.json` is **generated**, not hand-written — `tools/fetch_voyage_mods.py`
pulls poedb's four chart bases plus the league page's *Deep Water Border Mods* (the 40
figurine modifiers) and *Roomss* (19 tilesets, sic). 120 lines, classified reward vs
difficulty. Anything the rules do not cover prints as **UNCLASSIFIED** rather than being
silently mis-filed. Thermal Vents Chart exists as an item but has no mod table and only
`[DNT]` placeholder rooms — it is not implemented.

Verify mechanics against **generated data or the game itself**, never from memory: the
league is newer than any model's cutoff.

## Solver notes

* Anytime branch-and-bound. It reports `proved` vs `best found in budget` — only the final
  **unrestricted** pass can prove anything, because the earlier passes deliberately exclude
  legal layouts.
* **Connected boards are rare** — roughly one in ten million legal ones on a real panel.
  Forbidding edges that open onto the border makes them common, so the search optimises
  inside that restricted space first and widens after, keeping the incumbent.
* **Stranding is forbidden, not priced.** The seed doubles as an existence proof: if a
  joined board is possible, a layout cutting a square off is rejected. Pricing it was tried
  twice (flat fee; forfeiting the stranded chart's value) and both let profiles choose a
  dead corner.
* **The seed keeps the BEST of six dives, not the first.** Plain panel order settles
  existence in a handful of nodes; score order finds a well-valued incumbent but wanders.
  Returning the first success failed both ways round — score-first blew the clock,
  plain-first handed back a board the search could not improve on (sulphur 3515 → 2250).
* **The seed dives over SHAPES, not charts, and every dive gets its own slice of the
  clock.** Connectivity depends only on a chart's shape, so trying a second Crossing at
  a cell re-refutes a subproblem already refuted — eleven times over on a 56-chart panel.
  The "cheap existence proof" then spent its ENTIRE clock failing, the five remaining
  dives were skipped on the clock, and a seed that fails silently drops the stranding
  constraint: sulphur answered with squares 1, 2 and 3 unreachable, at half the score of
  the joined board (1905 → 4046). Deduping by shape is feasibility-preserving — charts of
  one shape are interchangeable, so any board needing a later Crossing has a twin using
  this one — and the dive that had been failing after 900ms now succeeds in 26ms.
  `AFullPanelOfMixedShapesNeverStrandsUnderAnyShippedProfile` pins it, and it only bites
  with the shapes INTERLEAVED the way a real stash looks: grouped by shape the seed
  places nine Crossings and succeeds trivially, which is why an all-Crossing panel test
  never caught this.
* **A recursive budget guard must SPEND the budget, not just return false.** Returning
  false only fails the current branch: the loops above carry on and each candidate still
  pays its placement checks. The seed's clock check did that, so six dives each ground
  through all 200k nodes after time was up — a 500ms budget ran 2.9s. Zero the counter.
* **A tiny node count is not automatically the bug it looks like.** When a profile scores
  no adjacency modifier and no border modifier, every chart is worth the same on every
  cell, the objective collapses to "take the nine best charts", and seed + polish solve it
  outright — the root bound then equals the incumbent and the search correctly expands
  NOTHING. Sulphur on a panel whose sulphur is all headline stats does exactly this.
  Check the VALUE against the top-nine sum before hunting: that is what separates this
  from the two real bugs wearing the same costume (a reused board bottoming out in two
  nodes, a seed spending the whole clock). `NodesExplored` counts all three stages for
  this reason — reporting search nodes alone made a perfect solve read as a collapsed one.
* **Scoring must stay O(1) per call.** It runs for every chart at every cell at every node.
  Precompute per-square board value; running the profile's regexes in there cost ~7×.
* Adjacency is scored **once per adjacent pair**, when the second of the two is placed, and
  counted in both directions.
* **On a FULL board, per-square board value cannot decide anything.** All nine squares are
  occupied either way, so the sum of their board modifiers is a constant and placement only
  matters through adjacency and shape. "Put the good reward in a corner" is therefore not
  something the additive model can express — it needs the multiplicative synergy fix below.
  It is also why the Soul Eater pin has to be an explicit constraint rather than something
  the optimiser would work out.
* **Required charts are a VALUE, not a constraint.** The right-click star adds a bonus
  larger than any real board can score, so "seats the chart" always outranks "does not"
  while the search still chooses WHERE freely — which quietly subsumes the old
  cheapest-square pin (deleted, machinery and all: the layout that loses least is the
  optimum). The bonus is peeled back off the reported score. The X (exclude) beats the
  star, and a corrupt session file cannot make a chart both.

## Sources, in order of trust

1. **The game itself** — help text, copied item text, the Area Modifiers panel.
2. **GGG's patch notes on the official forum.** Reachable, authoritative, and where the
   balance actually lives. 3.29.0b alone moved Golden Lanterns, gated the good strongboxes
   at area level 67, and made Ends and Straights rarer.
3. **poedb**, for the generated mod tables.
4. **SEO strategy sites**, which paraphrase each other and get numbers wrong. Useful for
   the shape of a strategy, never for a figure.

**Reddit is unreachable** — it blocks Anthropic's crawler at the platform level. Not a
setting, and not something to route around with a spoofed user agent. Mirrors are dead
too (teddit/removeddit gone; live redlib instances 403 the fetcher or challenge curl).
If a thread matters, paste it in. Web search snippets quoting reddit are fair game.

## Community strategy (see `research/`)

Corroborated: plan from the reward outward rather than filling the board; adjacency charts
belong in the centre, global ones on the edge, high-value rewards in a corner (two border
modifiers); read the borders *before* placing; borders are rerollable for sulphur.

**The multiplication gap is CLOSED — four channels.** Border payouts multiply with the
receiving tile: per-RARE payouts by its rare density (pack size + the tileset's measured
room bonus — and per poedb, NO self-scope rare-adding chart mods exist, so nothing else
may count), at-least-Magic payouts by its pack density, container gifts (boxes,
barrels, cages…) by its Item Quantity, and **amplifiers** (`#% increased explicit modifier
magnitudes`) by the receiving chart's EXPLICIT value — its rolled affixes and the stats
that aggregate them, never its one implicit. **Bottles are NOT a container gift**
(field-confirmed): a Message in a Bottle is ground loot sold UNOPENED — a fixed-value
item whose count is fixed by the roll — so nothing multiplies it and the gift scores
FLAT per adjacent area. Its existence is what gets maximised, which is why the bottle
chart belongs in the centre. The amplifier is the odd one out and the
shape to copy for any future mod like it: it has no payout of its own, so it is priced as
a FRACTION (0.01 per percent, not chaos) and carries no flat baseline — worth everything
beside a fat chart and exactly nothing beside a blank one. Scored flat it was counted but
inert; moving the figurine between squares changed neither the board value nor a single
placement. `PairAdjacency` is the
single source of truth — search gain, seed and polish all call it, because the first time
they each had a copy the polish drifted. Rare feeders are priced: a rolled Strongbox ≈4
rares (dredged currency rolls them since 3.29.1), starfish are one rare per pack, count
1:1 (field-confirmed), imprisoned monsters ARE rares and DO collect per-rare payouts.

**Discard:** the widely-repeated "centre is 6× a corner". The same article's own example
gives 12 vs 6 boxes — it is **2×**, which is what the model already says.

**From 3.29.0b, and reflected in the weights:** Golden Lanterns now grant increased
Quantity as well as being a container; the Diviner/Arcanist/Operative strongboxes spawn by
default above area level 67, so tier is a precondition for that objective; Ends and
Straights are rarer, which eases the connectivity problem the solver fights; and lantern
shrinking is 60% slower, so a full board costs much less time than the strategy sites
imply.

## Conventions

* Every feature ships with its tests in the same change.
* **What a mod is worth and how much a plan cares are separate questions.** `ModCatalog`
  answers the first (one entry per mod, chaos-anchored where market-backed, judged and
  documented where not); `Strategies` answer the second (weight presets over stats).
  They compile into the old profile shape, the rule file is hot-reloaded, and the Weights
  sliders edit stats directly with the catalog as donor for stats a strategy skips. The
  catalog is the thing most worth arguing with.
* A rule that matches nothing the game can roll **fails the build** (`ProfileCoverageTests`),
  as does a reward no profile scores. The same applies to `VoyageAlerts` patterns.
* **A `Stat` is an identifier; the slider shows a LABEL and explains itself on hover.**
  `StatText.Label`/`Describe` carry both and a stat missing either fails the build —
  catalog shorthand ("LootLock", "Meta", "Player") on a weight the user is meant to tune
  is a weight they will not touch. Renaming a stat means adding it to
  `WeightCategories.Renamed`, or every tuned `voyage-weights.json` silently reverts to
  the shipped baseline.
* **`VoyageAlerts` is not scoring.** A Divine Orb line and a Chromatic Orb line are the same
  shape and score alike; one is worth a hundred times the other, and a sum cannot say so. It
  is a short list checked by name, and it works because it is usually empty — keep it that
  way. Two tiers above trap: **GRAIL** (the Divine figurine and Messages in Bottles) sorts
  above every jackpot and displays louder. Traps (`cannot drop Equipment`, `reduced
  quantity per connection`) matter as much: the game's own tables file both as *rewards*.
* **The app sends input to the game in exactly one place** (`GameInput`): a single
  hover-and-copy inside the user's own F9 press, per GGG's one-action-per-keypress line.
  Nothing may call it in a loop or from a timer. Screenshots and clipboard reads are the
  only other contact. The slurp identifies the panel on arm, walks unread figurines then
  unidentified charts only, and never auto-solves — nothing does, except the Solve button.
