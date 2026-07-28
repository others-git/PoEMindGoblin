# MindGoblin

A Path of Exile toolkit for Windows. Portable: one self-contained .exe, no installer, no
runtime prerequisite, and **no credentials** — every tool here uses public data or your
own screen.

* **Voyage planner** — reads the chart panel off a screenshot and tells you which chart
  goes in which board square, optimised for whatever you are farming.
* **Gem RoI** — which vendor-buyable gems are worth levelling, priced off poe.watch.

## Not here any more

There used to be a live trade-search watcher. It is gone, along with its credential
storage, rate limiter and stat index — it was never confirmed working end to end against a
real session, and removing it means the app holds no session cookie, no DPAPI blob and
nothing worth leaking. The git history has it if it is ever wanted.

## Layout

```
src/MindGoblin.Core/    price data, gem RoI, Voyage model and solver (no UI)
src/MindGoblin.Core/Voyage/  board, solver, rules, screen readers, session
src/MindGoblin/         WPF desktop app
tests/MindGoblin.Tests/ xunit; Core is UI-free so these run headless
tools/VoyageProbe/          decode a screenshot and print the plan, headless
```

## Voyage planner

Placing 9 of 60 charts by hand is the tedious part of the league mechanic, and the
constraint is real: every path must meet another path or the board edge, and connections
are mutual. On top of that, two independent adjacency effects change what a square is
worth — the 12 figurines fixed around the board, and charts that carry their own
`Adjacent Modifier:`. A chart that buffs its neighbours is worth twice as much in the
centre (4 neighbours) as in a corner (2).

**Reading the board is two passes**, because the game only shows half of what matters:

1. **Read panel** — one screenshot. Every chart's shape, rotation and area level, decoded
   from pixels. Enough to solve the layout outright.
2. **Read mode** — the rest lives only in tooltips. The app names the next chart, you
   hover it and press `Ctrl+C`, and each copy ticks one off. Same for the 12 figurines.

Pass 2 is **optional**: with nothing hovered you still get a legal layout scored on area
level. Detail improves the plan; its absence does not block one.

There is deliberately no single "best" board — it depends on what you are farming — so the
objective is a **rule profile** you pick, hot-reloaded from JSON with no restart:
`sulphur`, `quantity`, `pack size`, `strongbox`, `containers`, `rare monsters`, `uniques`,
`currency`, `gold`, `flasks`, `high tier`. Every rule is checked against the generated mod table by the
test suite — a rule that matches nothing the game can roll fails the build, and so does a
payout no profile scores. The solver is anytime and says whether the answer was *proved
optimal* or merely the best found in the budget.

It is a **mirror, not an overlay**: it draws its own board on a second monitor rather than
painting over the game. It reads the screen and the clipboard, and sends the client
nothing.

Headless equivalent, and the way to check the calibration still lines up:

```
dotnet run --project tools/VoyageProbe -- screenshot.png sulphur overlay.png
```

### Chart modifiers

Which modifiers are a payout and which are monster difficulty is **generated, not judged**.
Voyage charts have exactly three bases and poedb publishes the full mod table for each, so
the set is closed:

```
python3 tools/fetch_voyage_mods.py          # -> assets/voyage-mods.json
```

That pulls the chart tables for all four bases, the **Deep Water Border Mods** table (the
40 modifiers the figurines grant), and the **room list** (19 tilesets and which base opens
each), splits each affix into its stat lines (the league pairs
danger with payout in a single affix, so this has to be per line), reduces every number to
`#`, and classifies each line. The app normalises a chart's text the same way and looks it
up, so matching is exact rather than pattern-guessed. Anything the rules do not cover is
printed as UNCLASSIFIED and the file records it — a patch that adds a modifier shows up as
a warning instead of being silently mis-filed. Current: 120 lines (76 reward, 44 difficulty), 40 of them from figurines, plus 19 rooms.
Thermal Vents Chart exists as an item but has no mod table and only `[DNT]` placeholder
rooms, so it is not implemented — a missing table is a warning, not a failure.

### Tilesets

Every chart states the area it opens — Anchorfield, Seafloor Ridges, Abyssal Plain,
Undersea Groves — and they are **not equal**: Anchorfield is thick with Sunken Loot chests,
which no chart modifier accounts for. The list is generated too — poedb's Maiden Voyage
page carries it under a `Roomss` tab — but which tilesets are *worth more* is not published
anywhere. So the app captures the tileset, exposes
it as a scorable line (`Area: Anchorfield`), and the Voyage tab lists which tilesets you
hold and whether the current profile values them. Adding a preference is a rule and a
number. The one shipped weight is an **observation, not a measurement**, and is the first
thing worth retuning.

**Known gap:** the level reader was trained from one capture, which contained only the
digits 1,2,3,4,6,7,8. A level containing 0, 5 or 9 reads as unknown rather than as a wrong
number. Teach it from a later capture via `level-digits.json`; no rebuild needed.


## Gem RoI

Buy a gem from a vendor at 1/0, level it while you play, sell it. Four paths:

| Path | Steps |
|---|---|
| level only | `1/0` → `20/0` |
| level + quality | `1/0` → `20/20`, spending 20 GCP |
| vaal only | buy a `20/20`, corrupt it, expected value over the outcomes |
| full chain | `1/0` → `20/20` → corrupt |

Both sides price at **mean**. Buying at min and selling at mean would flatter every row.

"Ignore quality" and "ignore corruption" are **strategy** toggles — they change what you
do, and therefore which paths are on the table — not price-matching filters.

### Only gems you can actually buy

The maths is meaningless without a cheap level-1 entry point, so `assets/gem-index.json`
(generated by `analyze/export_gem_index.py` in the sibling repo) excludes:

| Excluded | Count | Why |
|---|---|---|
| Vaal | 52 | obtained by corrupting |
| Transfigured | 209 | from the Wildwood |
| Awakened | 38 | drop-only |
| Exceptional | 3 | Empower/Enlighten/Enhance, drop-only |

Transfigured gems are detected by PoB's `Alt<Letter>` variantId, **not** by matching
" of " in the name — that would wrongly exclude Purity of Elements, Rain of Arrows and
Herald of Thunder, which are ordinary vendor gems. Before this filter existed they
dominated the profit table with an entry price they do not have.

Gems absent from the catalogue are treated as **not** buyable. The PoB clone lags the live
game (63 live gem names were missing from it), and assuming an unknown gem is buyable
invents a cheap entry price.

### The vaal odds are not verified

The Vaal Orb outcome distribution could not be confirmed against any primary source: the
PoE wiki is challenge-gated, the Fandom mirror returns 402, poedb has no gem corruption
section, and search results describe PoE 2, whose outcomes differ.

So the odds are **configuration, not a constant** — editable under "Corruption odds...",
defaulting to the widely-repeated 25/25/25/25, and every affected row is labelled
`EV — unverified vaal odds`. Correct them in one place and every vaal number updates.

### Prices come from poe.watch, not poe.ninja

poe.ninja is Cloudflare-gated for non-browser clients: its documented `/api/data/*`
endpoints 404 on every league including Standard, `api.poe.ninja` does not resolve, and
the HTML served to a plain client has no script tags at all. poe.watch is an open JSON API
carrying `gemLevel` / `gemQuality` / `gemIsCorrupted` per row, which is exactly the axis
this needs. It is a different dataset and may disagree or lag.

## Voyage solver

Places charts on the Voyage board so every path connects to another path or the border,
maximising whatever you are farming.

Output is the instruction you actually need — both grids numbered row-major from 1, the
board 1-9 and the chart panel 1-60:

```
square 1 <- chart 33  Kraken Shelf Chart (End, rotate 90°)
square 5 <- chart 18  Coral Reef Chart (Crossing, as-is)
```

### Anytime, not exhaustive

The five shapes come from the trade site's `chart_shape` filter — End (the half-step dead
end), Corner, Straight, Junction, Crossing — so the set is closed. Even so, 60 charts over
9 squares with rotations is a large search, and proving optimality once board modifiers
are involved can run for minutes.

So the search is anytime with a deadline. **100 ms lands within 0.05% of the 5-second
answer**, and the result carries `ProvedOptimal` so a good layout is never presented as a
proven best one.

Two bounds are computed and the tighter is used at each depth: per-chart (captures "a
chart is used once", collapses when modifiers exist) and per-cell (captures buffed-square
scarcity, collapses when they do not). Neither alone is enough — each left the search
grinding through millions of nodes on its bad case.

Board modifiers buff **adjacent** squares, so value is scored per `(chart, cell)`: the
same chart is worth more beside "Adjacent Areas contain 8 additional packs of Sea Beasts"
than in a dead corner.

### Rules are yours, and hot-reloaded

There is no single best board — it depends what you are farming. Objectives live in
`%LOCALAPPDATA%\MindGoblin\voyage-rules.json` as named profiles, re-read on save
with no restart:

```json
{
  "name": "pack size",
  "boardModifierWeight": 1.5,
  "rules": [ { "pattern": "(\\d+)\\s+additional packs", "weight": 4.0 } ]
}
```

Patterns capture the **number** in the modifier text, so "8 additional packs" outranks
"2" — a flat per-match score would rank them the same. Ships with sulphur, pack size,
quantity and safe profiles to edit.

Broken JSON keeps the last good profiles and raises an error: a half-saved file mid-edit
must not blank the tool, but silently ignoring an edit is worse — you would think a weight
applied when it had not.

## Build

Requires the .NET 10 SDK.

```bash
dotnet test                                   # headless, no credentials needed
dotnet build
dotnet publish src/MindGoblin -c Release  # portable exe -> publish/
```
