# poe-market-watch

Windows market tool for Path of Exile. Two tools so far:

* **Live search** — watch pathofexile.com trade searches over the websocket, aimed at the
  async Market / Merchant's Tab listings. See a match the moment it is listed and be one
  keypress from the seller's hideout.
* **Gem RoI** — which vendor-buyable gems are worth levelling, priced off poe.watch.
* **Voyage planner** — reads the chart panel off a screenshot and tells you which chart
  goes in which board square: `square 5 <- chart 23`.

Portable: a single self-contained .exe, no installer, no runtime prerequisite.

## What it does / does not do

**Does:** watch many live searches at once, notify on a match, validate your filters
against real game data, and put you one **keypress** away from travelling.

**Does not:** buy anything for you. GGG's third-party policy allows a tool that turns one
keypress into one server-side action — that is the same shape as the trade site's own
whisper button. Automating detection → travel → purchase is multiple server actions from
zero input, which is botting and gets accounts banned. The keypress stays yours, and you
complete the trade at Faustus yourself.

## Layout

```
src/PoeMarketWatch.Core/    API client, rate limiting, credentials, stat index (no UI)
src/PoeMarketWatch.Core/Voyage/  board, solver, rules, screen readers, session
src/PoeMarketWatch/         WPF desktop app
tests/PoeMarketWatch.Tests/ xunit; Core is UI-free so these run headless
tools/VoyageProbe/          decode a screenshot and print the plan, headless
assets/trade-index.json     generated stat/spawn index (see below)
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
`gold`, `flasks`, `safe`. Every rule is checked against the generated mod table by the
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

That pulls all three tables, splits each affix into its stat lines (the league pairs
danger with payout in a single affix, so this has to be per line), reduces every number to
`#`, and classifies each line. The app normalises a chart's text the same way and looks it
up, so matching is exact rather than pattern-guessed. Anything the rules do not cover is
printed as UNCLASSIFIED and the file records it — a patch that adds a modifier shows up as
a warning instead of being silently mis-filed. Current: 86 lines, 42 reward, 44 difficulty.

**Known gap:** the level reader was trained from one capture, which contained only the
digits 1,2,3,4,6,7,8. A level containing 0, 5 or 9 reads as unknown rather than as a wrong
number. Teach it from a later capture via `level-digits.json`; no rebuild needed.

## The stat index

`assets/trade-index.json` maps every trade stat id to the item categories it can actually
spawn on, with affix type, mod group, and any influence requirement.

It is **generated**, not hand-maintained — by `analyze/export_trade_index.py` in the
sibling `path-of-claude` repo, which parses Path of Building's Lua data and reruns PoB's
own `GetModSpawnWeight`. That logic lives in exactly one place, in Python, and ships here
as a flat lookup so this app needs no Lua parser and no copy of the spawn rules.

Regenerate whenever Path of Building is updated:

```bash
cd ../path-of-claude
./.venv/bin/python analyze/export_trade_index.py -o ../poe-market-watch/assets/trade-index.json
```

Current: 23 categories, 746 stats, 2280 spawn rows (235 KB).

Note it covers **explicit affixes only**. Implicit, crafted, veiled and eldritch stats are
absent, so the index is used to *warn* ("cannot spawn here", "needs Shaper influence"),
never to restrict what you may search for — otherwise the app would be less capable than
the in-game filter.

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
`%LOCALAPPDATA%\PoeMarketWatch\voyage-rules.json` as named profiles, re-read on save
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

## Authentication

**Why not OAuth?** Because there is no trade scope. GGG's OAuth has exactly twelve:

| Account | Service |
|---|---|
| `account:profile` | `service:leagues` |
| `account:leagues` | `service:leagues:ladder` |
| `account:stashes` | `service:pvp_matches` |
| `account:characters` | `service:pvp_matches:ladder` |
| `account:league_accounts` | `service:psapi` |
| `account:item_filter` | `service:cxapi` |

You cannot request a scope that does not exist. Asked directly about trade, GGG said only
that *"the internal APIs currently used by the trade website will remain available without
authentication for now."* Currency exchange got a service scope while trade did not, so the
omission reads as deliberate.

There is a second wall behind the first: a portable exe is a **public client** (no way to
hold a secret), and public clients *"cannot use any `service:*` scopes"*. So even a
hypothetical `service:trade` would be unusable here — it would have to be `account:trade`.

Other PoE apps that use OAuth are doing OAuth-shaped things: stash price checks
(`account:stashes`), build import (`account:characters`), filter management
(`account:item_filter`). Every tool that does *live search* uses `POESESSID`.

The one OAuth-shaped alternative is `service:psapi`, the raw public-stash river the trade
site indexes — consume it and you could detect listings with no session cookie at all. But
it needs a confidential client (a server you run), it means rebuilding poe.ninja's
ingestion, and it still cannot travel, because that token is minted by the session-gated
fetch endpoint.

Measured against the live API:

| Endpoint | Auth | Result |
|---|---|---|
| `POST /api/trade/search/{league}` | none | 200 |
| `GET /api/trade/fetch/{ids}?query=` | none | 200, but **no tokens** in the response |
| `wss://…/api/trade/live/{league}/{id}` | none | **401** |
| `POST /api/trade/whisper` | none | **401** |

So live search and travel require `POESESSID` + `POETOKEN` — full-account session
cookies, unscoped and not per-app revocable. They are stored DPAPI-encrypted at
`%LOCALAPPDATA%\PoeMarketWatch\credentials.dat` (CurrentUser scope, so the file is
useless on another machine or to another Windows user), never logged, and deliberately
kept out of the program directory so a portable exe cannot carry an account around.

Revoke by logging out of pathofexile.com.

## Travel

Travel and whisper are the *same* endpoint — `POST /api/trade/whisper` — distinguished by
a `tok` claim inside a server-signed JWT (`hideout` vs whisper). The token is HS256-signed
by GGG, scoped to one search (`iss`) and one item (`sub`), and lives **300 seconds**.
It cannot be forged, only relayed, which is what keeps this inside the rules.

The 5-minute TTL is a real design constraint: tokens cannot be pre-fetched and held, so a
hotkey press does an authenticated fetch *then* the travel POST.

## Rate limits

Every response carries the policy, and this client obeys it:

```
x-rate-limit-ip: 5:10:60, 15:60:300, 30:300:1800, 600:21600:3600
```

`hits:period:penalty`. The limiter stops one slot below every cap, because the bucket is
**per-IP** and shared with your own browser on the trade site — sitting at the edge means
someone else's request triggers your 429.

## Build

Requires the .NET 10 SDK.

```bash
dotnet test                                   # headless, no credentials needed
dotnet build
dotnet publish src/PoeMarketWatch -c Release  # portable exe -> publish/
```
