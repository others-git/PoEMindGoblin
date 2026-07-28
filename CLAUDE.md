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
```

**Never screenshot the primary screen to inspect the UI** — the game is usually on it, and
once that captured a live boss fight instead of the app. Use `--render`.

## Voyage mechanics (verified against the game's own help text)

* Board is **3×3**, chart panel **6×10**. Place **up to** nine charts; fewer is legal.
* **The voyage starts in the bottom-left chart** and travels by connections.
* **Every chart has exactly one implicit**, and it is either *"in all Voyage Areas"*
  (global, position irrelevant) or *"adjacent Areas"* (buffs neighbours). The copied item
  text has NO label for it — scope is read from the wording.
* **Every board edge applies a modifier to the chart touching it**, rerolled each voyage.
  Twelve edge segments on a 3×3, so a corner square is touched by two, an edge-centre by
  one, and **the middle by none** — which is why square 5 is off the read checklist.
* **All connections must lead to the board edge or to another connection.** Connections
  are mutual: an open edge facing a closed one is invalid from both sides. An open edge
  facing an *empty in-bounds cell* is invalid — which is why a partial board must form a
  closed cluster (four Corners can, four Crossings cannot).
* A square cut off from the route is **never visited**, so a chart there is wasted.

## Gotchas (each of these was a silent bug)

* **GGG misspells Quantity as "Qauntity"** — but only in the global lines. The adjacent and
  in-area versions spell it correctly. A rule matching "Quantity" scores some rolls and
  silently misses others.
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
* **Windows OCR reads PoE prose well and small numerals badly** — it read `L:76` as
  `L.'j6`. Chart levels go through template matching; only modifier text goes through OCR.
* **PoE's font (Fontin) is not on Windows.** A sweep of every system `.ttf` at every
  plausible size missed by 134 of ~130 ink pixels, so glyphs must be carved from a capture.
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
* **Scoring must stay O(1) per call.** It runs for every chart at every cell at every node.
  Precompute per-square board value; running the profile's regexes in there cost ~7×.
* Adjacency is scored **once per adjacent pair**, when the second of the two is placed, and
  counted in both directions.

## Sources, in order of trust

1. **The game itself** — help text, copied item text, the Area Modifiers panel.
2. **GGG's patch notes on the official forum.** Reachable, authoritative, and where the
   balance actually lives. 3.29.0b alone moved Golden Lanterns, gated the good strongboxes
   at area level 67, and made Ends and Straights rarer.
3. **poedb**, for the generated mod tables.
4. **SEO strategy sites**, which paraphrase each other and get numbers wrong. Useful for
   the shape of a strategy, never for a figure.

**Reddit is unreachable** — it blocks Anthropic's crawler at the platform level. Not a
setting, and not something to route around with a spoofed user agent. If a thread matters,
paste it in.

## Community strategy (see `research/`)

Corroborated: plan from the reward outward rather than filling the board; adjacency charts
belong in the centre, global ones on the edge, high-value rewards in a corner (two border
modifiers); read the borders *before* placing; borders are rerollable for sulphur.

**Known model gap:** a border modifier paying *per rare* multiplied by a chart adding rares
is a MULTIPLICATION, and our scoring is additive. Fixing it means splitting the corpus into
multipliers and payouts; the shape is clear, the magnitude would be a guess.

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
* Weights in `VoyageRules.Defaults()` are a starting point, not a claim about the economy —
  they are the thing most worth arguing with. The rule file is hot-reloaded, and shipped
  profiles missing from an existing file are added on load.
* A rule that matches nothing the game can roll **fails the build** (`ProfileCoverageTests`),
  as does a reward no profile scores.
