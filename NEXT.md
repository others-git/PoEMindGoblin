# Where this left off

## Focus

**Voyage planner** is the live thread. **Gem RoI** is done and working.
**Live search is deleted** — trade client, websocket, rate limiter, credentials and the
stat index are gone, as is the trade-index asset. It never worked end to end against a
real session. The app now holds no credentials of any kind.

## What the Voyage mechanic actually is

Verified from screenshots and the trade API, not assumed:

* Board is **3x3** (9 squares). Chart panel is **6x10** (60 charts).
* Chart shapes come from the trade site's `chart_shape` filter and the set is closed:
  **End** (the half-step dead end), **Corner**, **Straight**, **Junction**, **Crossing**.
* Every path must meet another path or the border. Connections are **mutual** — an open
  edge facing a closed one is invalid from both sides.
* **TWO independent sources of adjacency bonus**, both modelled:
  1. **12 figurines** carved around the board, fixed in place. **Every figurine modifier
     is adjacency-type** — "Adjacent Areas contain 8 additional packs of Sea Beasts" —
     never global, so a figurine only ever affects the squares it touches. → `BoardModifier`.
  2. **Charts** that carry their own `Adjacent Modifier:` line, which travels with the
     chart → `Chart.AdjacentValue`. A Strongbox chart in the centre touches 4 neighbours
     and is worth double a corner.
* Charts also carry a **`Voyage Modifier:`** which is **global** ("8% increased Quantity
  of Items found in all Voyage Areas") — placement-independent, so it belongs in the
  chart's own value, not the adjacency term.
* Chart hover shows everything needed: area name, Area Level, Item Quantity, Item Rarity,
  Monster Pack Size, Gold Found, Dead Man's Sulphur, Requires Level, both special
  modifiers, and the monster mods.

## Built and tested (347 tests)

| Area | State |
|---|---|
| Voyage shapes + board | 5 shapes, rotations, mutual edge matching, violation reporting |
| Voyage solver | Anytime; adjacency in both directions; 100ms within 0.05% of 5s |
| Voyage rules | Hot-reloaded JSON profiles (sulphur / pack size / quantity / safe) |
| Chart panel reader | Reads shape + rotation off real pixels by topology, not templates |
| Level reader | `L:xx` by digit templates; digits 1,2,3,4,6,7,8 trained |
| Chart text parser | Label-driven, order-independent; clipboard hover text |
| Voyage session | Two-pass read with a checklist; solves without pass 2 |
| Voyage UI | Mirror window, read mode, profile picker, plan, pop-out |
| Board layout | 12 figurines derived from board size, not hardcoded |
| Screen layout | Fractional coords so it survives a resolution change |
| Panel calibration | JSON file + overlay probe, editable without a rebuild |
| Gem RoI | 4 paths, 508 vendor gems, live poe.watch prices |

## Using the Voyage planner

1. Open the Voyage screen in game, **Read panel** — one screenshot gives every chart's
   shape, rotation and area level. Enough to plan the layout on its own.
2. Optional: **Read mode**, then hover the chart it names and press `Ctrl+C`. Each copy
   ticks one off and advances. Same for the 12 figurines. This is the only way to get
   stats and the two modifier lines, which exist only in tooltips.
3. Pick a profile, **Solve**. The plan reads `square 5 <- chart 23`.

`VoyageProbe <screenshot.png> [profile] [overlay.png]` does the same headless and writes
a calibration overlay — the way to check the grid still lands on the glyphs.

## Prior art

**https://voyage.exilekit.dev/** — a community Voyage planner, worth looking at before
designing the UI. Confirms the board is 9 squares ("0/9" progress) and that filtering by
desired **Reward** is the natural way to express a goal, which is what our rule profiles
already do.

It is **manual**: you search for charts, place them yourself, with undo/redo/clear, saved
in browser storage. That is the gap — placing 9 of 60 charts by hand is the tedious part,
and it is exactly what the solver removes. Capture the board, pick a profile, get the
placement. Their layout is a good reference for what to show; the automation is ours.

## Next

* **Digits 0, 5 and 9 have no template.** The training capture contained none. A level
  containing one reads as `null` rather than a wrong number. To close it: capture a panel
  showing such a level, carve the glyph, add it to `level-digits.json` (or
  `LevelReader.Learn`). No rebuild needed.
* **Reading charts already on the board.** The board was empty in every capture so far, so
  there is nothing to verify a board reader against. Only matters for resuming a
  part-placed board; planning from empty is the normal case.
* **Confirm the real hover-text format.** `ChartText` is deliberately tolerant and
  order-independent, but it has never seen an actual Ctrl+C from a chart. First real
  capture will confirm it or show which labels differ.

## Still unknown

* **Vaal Orb outcome odds** (gem tool). Wiki challenge-gated, Fandom 402s, poedb has no
  gem section, search results describe PoE2. Defaults 25/25/25/25, every affected row
  labelled, one line to correct.

## Things worth not relearning

* `HttpClientHandler` defaults to `UseCookies = true`, which **silently drops a manually
  set Cookie header**. Cost a long hunt through POESESSID / POETOKEN / cf_clearance when
  the credentials were fine. `HttpFactory` exists to make that impossible.
* Solver: trying candidates in input order made branch-and-bound useless (>2 min vs 21
  nodes). Two bounds are needed — per-chart collapses when modifiers exist, per-cell
  collapses when they do not — so it takes the smaller.
* Transfigured gems are identified by PoB's `Alt<Letter>` variantId. Matching " of " in
  the name wrongly excludes Purity of Elements, Rain of Arrows, Herald of Thunder.
* Sorting a grid on formatted text ranks "99%" above "970%" — numeric columns need
  `SortMemberPath`.
* poe.ninja is Cloudflare-gated for non-browser clients; poe.watch is not.
* PoB stores support gems without the " Support" suffix; the market uses it.
* **PoE's UI font (Fontin) is not on Windows.** A sweep of every system `.ttf` at every
  plausible size missed by 134 of ~130 ink pixels on the best candidate, so text glyphs
  cannot be re-rendered — they must be carved from a real capture.
* **The level digits TOUCH.** `82` is one unbroken 17px run; `83` has a 1px gap. Splitting
  on blank columns or halving by width both mis-segment. Match left to right and advance
  by the matched template's width.
* **Reading a 1-D column projection is not identification.** Grouping level captions by
  their column-occupancy signature collided: `74` and `82` produce the same pattern. Two
  charts were mislabelled until the template matcher disagreed and turned out to be right.
* **Label parsing needs the colon.** Without it `Area contains many Totems` parses as the
  area name — eating a modifier line *and* overwriting the real area — and
  `Area Levelling Grounds` parses as an area level.
* **Typed fields are invisible to regex rules.** Lifting `Dead Man's Sulphur: +9` into a
  `double` put it out of reach of the rule engine, so the shipped "sulphur" profile scored
  every chart zero. Charts now offer their stats back as tooltip-worded text.
