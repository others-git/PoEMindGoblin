# Where this left off

## Focus

**Voyage planner**, deep in live use through 3.29/3.29.1. **Gem RoI** is done.
~700 tests. Every session bug became a fixture: the OCR mangles, the glyph glow,
the post-patch panel art all live in `tests/` as regression pixels.

## The current shape of the thing

* **Scoring** = `ModCatalog` (mod → stat + normalized value, once; chaos-anchored where
  market-backed) × `Strategies` (weight presets; "Optimise for" is labelled Strategy).
  Compiled into `VoyageProfile`, so solver/channels/board machinery never moved.
* **Three multiplication channels**: per-rare payouts × tile rare density, at-least-Magic
  × pack density, container gifts × Item Quantity. `PairAdjacency` is the one pairwise
  truth. Boards always fill when the pool covers; required charts are a value bonus, not
  a pin; exclusions beat requirements.
* **Reading**: Identify Charts (screenshot; saves `last-identify.png`; in-app Calibrate
  draws the live grid over it) → **Slurp** (arms by re-identifying, then one F9 = one
  hover-and-copy through unread figurines and unidentified charts; blue-threshold OCR +
  corpus canonicalization clean the tooltips) → Solve, by hand, always — nothing
  auto-solves.
* **Figurines are the border truth** (tooltips, not the tile panel) and bind per line.
  Square displays show the effective per-square view whichever source is authoritative.
* **Board icons**: lantern ×N, ∞ free-lanterns, bottle ×N (the chase), barrel
  (openables), boss ☠, per-rare coin, chest, ⚠ danger (Grasping Vines / pen).
  "Gained from adjacent" lines on every solved square's tooltip.
* **Alerts**: GRAIL tier (Divine figurine, Messages in Bottles) above Jackpot above
  Trap; max-res flipped to "free difficulty roll" (3.29.1: it never functioned).

## Open threads

* **Digit '5' still has no level template** — no capture has contained one. When an
  L:x5 chart appears: carve it from `last-identify.png` exactly as 0 and 9 were
  (they came from the 3.29.1 fixture's L:80/L:79 captions).
* **Rooms unmeasured**: Eldritch Depths (new in 3.29.1), Lost Shipment, Lost Ruins,
  Runes of the Deep, Infested Bathyspheres, Undersea Groves, Seafloor Ridges, Abyssal
  Plain, Unremarkable Seabed. One field number each → `RoomRareDensity`.
  Measured so far: Sea Pillars 1.0 > Brine King's 0.8 > Pelagic 0.6; zeros are
  deliberate records, not gaps.
* **Ancient Orb price** may move (3.29.1 made it Allflame-craftable) — rerun
  `tools/fetch_orb_prices.py` (path-of-claude repo) once the market settles and update
  the catalog's 4.8.
* **Strategy presets live in code**; the rules FILE still persists compiled rules
  (Edit rules keeps working). A strategy-level file format is the natural next step if
  hand-editing presets matters.
* **Bottle-per-tileset spawn rates**: nobody has published data. Field-log counts per
  tileset and they slot into the same map as rares.
* **Icon candidates declined for now** (approved set was containers/bottles):
  Altars, imprisoned/starfish bonus-rare squares, at-least-Magic payout coin,
  chart-refund ♻, conversion family (gold/unique/fractured/decks).
* **Thermal Vents Chart** still shows no mod table on poedb ("revealed once Charted").

## Things worth not relearning (the newer batch — older ones below still hold)

* **Suffix-looking ≠ craftable.** The adjacent/voyage chart mods are hidden-until-charted
  implicits whatever poedb's table typing suggests; bottles cannot be target-crafted.
* **poedb is the arbiter of scope.** "50% inc rare monsters" as a chart's OWN mod does
  not exist — every rare-adder is adjacent- or global-scope. A density term summed over
  OwnLines can only ever catch globals, crediting a board-wide buff to one tile.
* **A glyph's bounding box needs CONTIGUITY, not just a density floor** — 3.29.1's glow
  put an isolated green fleck above glyphs and min/max bounds walked into the frame art;
  a real open edge's exit glow defeats any density floor.
* **Tooltip OCR: keep exactly the magic-blue pixels** (threshold to black-on-white),
  then snap survivors to the corpus by word overlap, numbers into '#' slots, declining
  when unsure. The right/bottom figurine tooltips render over the dark chart stash and
  read far worse than the parchment side without this.
* **`BitmapImage` holds the file handle** (default cache) — the calibration window froze
  its own view by blocking the next capture's overwrite. `OnLoad` + ignore-cache.
* **UI-thread `async void` handlers re-enter at every await** — a second F9 or a Skip
  click mid-capture double-injected and dequeued blind. One in-flight capture owns the
  queue; re-verify the queue head after awaits.
* **The default WPF Slider is invisible on a dark theme** and a centred `MaxWidth`
  panel collapses star columns to zero width — the "slider" was a thumb on a
  zero-length rail.
* **Hand-drawn vector creatures die at 20px.** Several rounds of sea-serpent ornaments
  ended as "the female reproduction system" (user's words) and green blobs; the
  figurine markers are struck-coin studs now. If the carved look returns, trace real
  raster assets.
* **grep -c returning 0 exits 1** and kills && chains; python heredocs need raw strings
  or \n writes real newlines into C# string literals; XML comments reject `--`.

## Older lessons (all still true)

* `HttpClientHandler` defaults to `UseCookies = true`, which silently drops a manual
  Cookie header. `HttpFactory` exists to make that impossible.
* Solver candidate order IS the bound's power: input order took >2 min, value order 21
  nodes. Two bounds, take the smaller.
* Transfigured gems are PoB `Alt<Letter>` variantIds; " of " name-matching wrongly
  excludes Purity of Elements et al.
* Sorting a grid on formatted text ranks "99%" above "970%" — numeric columns need
  `SortMemberPath`.
* poe.ninja is Cloudflare-gated for non-browser clients; poe.watch is not.
* PoB stores support gems without the " Support" suffix; the market uses it.
* Fontin is not on Windows and cannot be re-rendered — glyphs are carved from captures
  (and its license forbids shipping the TTFs; `PoeFonts` loads them privately from
  `MindGoblin_data/fonts/` with a Georgia fallback).
* Level digits TOUCH ("82" is one 17px run); match left-to-right, advance by template
  width. Column-projection signatures collide (74 vs 82).
* Label parsing needs the colon, or `Area contains many Totems` becomes an area name.
* Typed stat fields are invisible to regex rules — charts offer their stats back as
  tooltip-worded lines.
* Vaal Orb outcome odds (gem tool) remain unfound; defaults 25/25/25/25, labelled.
