# Where this left off

## Focus

**Voyage planner** is the live thread. **Gem RoI** is done and working.
**Live search is tabled** — the code stays (rate limiter, credentials, trade client,
websocket, diagnosis) but no further work is planned, and its 401 was never confirmed
fixed end to end.

## What the Voyage mechanic actually is

Verified from screenshots and the trade API, not assumed:

* Board is **3x3** (9 squares). Chart panel is **6x10** (60 charts).
* Chart shapes come from the trade site's `chart_shape` filter and the set is closed:
  **End** (the half-step dead end), **Corner**, **Straight**, **Junction**, **Crossing**.
* Every path must meet another path or the border. Connections are **mutual** — an open
  edge facing a closed one is invalid from both sides.
* **TWO independent sources of adjacency bonus**, both modelled:
  1. **12 figurines** carved around the board, fixed in place, each with an adjacency mod
     ("Adjacent Areas contain 8 additional packs of Sea Beasts") → `BoardModifier`.
  2. **Charts** that carry their own `Adjacent Modifier:` line, which travels with the
     chart → `Chart.AdjacentValue`. A Strongbox chart in the centre touches 4 neighbours
     and is worth double a corner.
* Charts also carry a **`Voyage Modifier:`** which is **global** ("8% increased Quantity
  of Items found in all Voyage Areas") — placement-independent, so it belongs in the
  chart's own value, not the adjacency term.
* Chart hover shows everything needed: area name, Area Level, Item Quantity, Item Rarity,
  Monster Pack Size, Gold Found, Dead Man's Sulphur, Requires Level, both special
  modifiers, and the monster mods.

## Built and tested (237 tests)

| Area | State |
|---|---|
| Voyage shapes + board | 5 shapes, rotations, mutual edge matching, violation reporting |
| Voyage solver | Anytime; adjacency in both directions; 100ms within 0.05% of 5s |
| Voyage rules | Hot-reloaded JSON profiles (sulphur / pack size / quantity / safe) |
| Board layout | 12 figurines derived from board size, not hardcoded |
| Screen layout | Fractional coords so it survives a resolution change |
| Gem RoI | 4 paths, 508 vendor gems, live poe.watch prices |
| Stat index | 23 categories, 746 stats, advisory not restrictive |
| Live search (tabled) | Built, untested against a real session |

## Next

* **Second-monitor mirror app.** Not an overlay — a window reproducing the board and chart
  panel, with a **read** mode ticking off each figurine and chart as it is captured.
  `BoardLayout.Unread()` already drives the outstanding list.
* **Screen capture.** Target is 2560x1440 windowed fullscreen but must be configurable —
  `ScreenLayout` holds fractional rectangles for exactly that reason. Shape glyphs are a
  small template set and `L:xx` sits at a known offset, so this is template matching plus
  a digit read, not general OCR. Connectivity data is all on screen; only modifiers need
  hover.
* Parse chart hover text into `Chart` (fields already modelled).

## Still unknown

* **Vaal Orb outcome odds** (gem tool). Wiki challenge-gated, Fandom 402s, poedb has no
  gem section, search results describe PoE2. Defaults 25/25/25/25, every affected row
  labelled, one line to correct.
* Whether a figurine buffs exactly one square or several — `BoardLayout` supports many
  per slot, currently defaults to one.

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
