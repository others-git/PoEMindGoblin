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
* **The chart panel is TABBED, and growing it means more TABS, not a bigger grid.**
  A screenshot only ever shows the open tab, which is why a panel index means nothing on
  its own — "chart 7" is a different chart on tab 1 and tab 2. Indices run straight
  through instead (tab 1 owns 1..60, tab 2 the next 60), so every stored session stays
  valid and the solver, the plan and the stash search never learn tabs exist.
  **`Pages` in `panel-calibration.json` is the tab count, and it DEFAULTS TO WHAT THE
  GAME HAS (2).** It was defaulted to 1 as a prepare-for-later flag, which shipped an app
  that disagreed with the screen and asked the user to go and fix it — the same mistake as
  assuming a resolution instead of detecting one. A file written before the field existed
  inherits the default, so nobody has to edit JSON; the calibrator has a Tabs control for
  when the count changes again. Rows/Cols live there too and NOWHERE else: they used to be
  duplicated in `screen-layout.json`, and two files holding one fact means every chart can
  draw on the wrong tile while the solver places the right ones.
  Two things the app cannot do: it cannot SEE which tab the game is showing (the user
  clicks the matching tab), and it cannot turn the page for them — input goes to the game
  in exactly one place, inside their own F9 press. So the slurp queues only the open tab
  and, when that tab is done, names the next one that still has unread charts.
  A read is scoped the same way: reconciling the whole session against one tab's
  screenshot would strike every chart on every other tab and then delete them.
  **The SOLVER sees every tab** — placement is about which charts are best and a tab is
  only where one is kept, so a board can be built entirely from tab 2. Which is why a
  chart is NAMED with its tab as an exponent: `32²` is cell 32 of tab 2 (`PanelPage.Label`,
  Unicode superscript so it works in plain status strings as well as in a TextBlock).
  The plan's job is "go and fetch this one", and a bare index cannot say where — chart 67
  is not the sixty-seventh thing the user can see. Board labels for charts off tab 1 are
  tinted as well, so nine squares can be sorted by tab at a glance.
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
* **3.29.2** (checked 2026-08-06, [notes](https://www.pathofexile.com/forum/view-thread/3994431)):
  * **"Doubled the size of the Charted Charts inventory"** — this is the patch the tab
    support above exists for, and it confirms two tabs of 60.
  * **GOLD FOUND NEVER FUNCTIONED IN CHARTS.** *"All modifiers which previously granted
    increased gold found on Charts will now grant increased rarity of items found, as the
    increased amount of gold found did not function in Charts and Voyages."* Same shape as
    the max-res disclosure. Verified that every rarity wording it converts INTO already
    scores, self-scope "in this Area" included, so nothing needed parsing — but a chart
    read BEFORE the patch still says "Gold Found" in a saved session and is now really a
    rarity roll. Re-hover those. The equipment→gold CONVERSION is deliberately left alone:
    it does not grant "increased gold found", so the note does not obviously cover it.
  * **Altars to the Goddess more than doubled**: a blessing is now a 10% chance to convert
    certain common currencies to rarer ones (from 4%), empowered 20% (from 8%), and it
    applies to Mercenaries. Repriced 12 → 30 by that ratio. They also spawn less often,
    which does NOT touch the rule — the mod states how many it grants, and GGG's note says
    the per-voyage total lands "roughly higher or equivalent". **This also retires an
    UNVERIFIED**: what an Altar does is now known from GGG rather than from SEO sites.
  * No action, recorded so nobody re-reads them: faster sulphur-gathering beam, clearer
    Golden Lanterns, capsule/Valerie/gamepad UI, and the planning-UI fix for re-placing a
    chart in the same spot.
* **All connections must lead to the board edge or to another connection.** Connections
  are mutual: an open edge facing a closed one is invalid from both sides. An open edge
  facing an *empty in-bounds cell* is invalid — which is why a partial board must form a
  closed cluster (four Corners can, four Crossings cannot).
* A square cut off from the route is **never visited**, so a chart there is wasted.
* **Order of visit matters, and it is not shortest-path** — every square gets visited
  either way. Allflame Lanterns deplete as they are placed, and a single death ends the
  voyage, so both pressures say the same thing: take the valuable squares EARLY. That is
  the traveling repairman's problem, solved exactly by subset DP (9 squares = 512 subsets).

## Voyage facts, checked against poedb (2026-08-03)

Full writeup: `research/voyage-league-facts.md`. **poedb serves fine to
`curl -A '<browser UA>'` and WebFetch's summariser refuses to reproduce its tables** —
scrape raw and parse locally. poewiki is behind Anubis and unreachable, like reddit;
`pathofexile.com/allflame` is a JS SPA with no server-rendered text.

* **`reduced quantity per connection` is NOT a trap.** poedb's border table shows it never
  rolls alone: the figurine carries it WITH `120%` or `180% increased Quantity of Items
  found in adjacent Areas`. One open connection = +70%/+130%; it only goes negative at
  three. It is the one board mod whose worth depends on the shape the solver picked.
  Badged as a trap it told the user to avoid the best figurine on the board.
  **Its per-connection half is still scored FLAT** — the penalty should scale with the
  receiving square's open edges, which the solver knows and the model does not use yet.
* **Every per-rare currency payout is FIGURINE-ONLY.** Diffing the pools: 88 chart lines,
  40 border lines, only 6 shared. All twelve `Rare Monsters ... drop # additional <orb>`
  lines, the Support Gem chance, `more Currency/Rarity/Scarabs`, the Stacked Deck
  conversion, the magnitude amplifier, Captainsbane/Filthscrabble/Pirate's Locker/
  Brinerot, Treasure Anchors and Altars are border-only. Payouts live on the BORDER,
  multipliers live on the CHARTS — which is the synergy model, stated by the data.
  Caveat: `assets/voyage-mods.json`'s `lines` is the UNION of both pools, so the corpus
  cannot answer "can a chart roll this?".
* **Every chart affix is a bundle: one payout plus one downside, and the family is
  fixed.** Quantity-in-adjacent only comes with monster tankiness; Sulphur only with
  monster damage; Rarity only with extra-phys-as-element; Pack Size only with
  speed/crit/AoE. **Gold is the only chart payout with `in this Area` self scope.**
* **All 65 border rolls are Level 1** — figurines are not ilvl-gated, so a level-68 board
  can roll a Divine figurine. Every orb figurine is exactly `1 additional`, no tier
  ladder: which is precisely why `VoyageAlerts` must stay a name list and cannot be a sum.
* **The implemented chart bases have byte-identical 142-row mod tables** — the base picks
  the TILESET and nothing else.
* **poedb is STALE against 3.29.1**: no Grasping Vines anywhere, the disabled max-res mod
  still listed, Sunken Opulence/Sunken Gems absent. Re-running `fetch_voyage_mods.py`
  today returns the PRE-PATCH corpus, and the hand-added Grasping Vines line has to
  survive it. Its upside is now known: **14/16/18% increased Pack Size in adjacent Areas**
  at ilvl 68/80/83.
* **3.29.1 "Fixed a typo in a Voyage modifier" and does not say which.** Both typos the
  parser leans on (`Qauntity`, `Rare Monsters adjacent in Areas`) are still on stale
  poedb. Verified 2026-08-03 that BOTH spellings of both lines score identically through
  scoring, channel, density and reward classification, pinned by
  `BothSpellingsOfAGGGTypoScoreAlike`. Keep it that way.
* **Captainsbane and Filthscrabble are UNIQUE minibosses, not Rares** — level 68, ~1.85M
  life, `monster dropped item rarity +% [15000]`. **Per-rare figurine payouts do NOT
  collect from them.** Never price them as rare feeders.
* **Imprisoned Monsters are Essence-imprisoned RARES** (confirmed by wording), so they do
  feed per-rare payouts — which is what the model already assumes.
* **Sulphur is area-harvested, not monster-dropped** — the game's own Maiden Voyage step 8:
  "Look for clusters of luminous coral and collect them by placing Allflame Lanterns near
  them." Every scaling line says *found*, every orb line says *drop*, and Vesper's
  `IncreasedResourceGain1` is `map deepwater league resource found +% [20]`, area-scoped.
  Exactly ONE modifier in the corpus makes monsters drop it: the figurine `Rare Monsters
  in adjacent Areas drop Dead Man's Sulphur`. That single line is the only sulphur term a
  rare-count multiplier may touch. **Market price 0.01c** (poe.watch, Allflame, 44
  listings) — a bulk consumable; the catalog's ~5c/point is a UTILITY judgement about
  board rerolls, not a market price, and the two should not be confused.
* **"Dredged currency rolls Strongboxes" is a drifted paraphrase.** The real note:
  "Dredged currency items can now be used on Strongboxes and Essences" — about what you
  may apply currency TO, saying nothing about spawn rates. The `≈4 rares per strongbox`
  figure has no reachable source: it is OUR estimate. Separately confirmed and more
  useful: Vesper's `ScatterObjects1` carries `map strongboxes are rare [1]`, so chart
  strongboxes are Rare rarity by default once unlocked.
* **Bottles are ~70c** — FIELD price, 2026-08-03, and the catalog carries it. No API can
  check this: poe.watch does not track the item at all (absent from all 335 Allflame
  currency entries), and the old ~39c was a stale research figure. poedb's
  `Message in a Bottle /25` outcome table IS known: Mageblood, Headhunter, Kalandra's Touch, Ryslatha's Coil, Taste of Hate,
  Defiance of Destiny, Inspired Learning, Unnatural Instinct, Bloodnotch, Rain of
  Splinters, Replica Dragonfang's Flight, generic Unique Belt/Jewel/Ring/Amulet/Flask,
  Hinekora's Lock, Fracturing Orb, Divine Orb (and a stack), Reflecting Mist, a Stacked
  Deck stack, Broken Mirror, Albino Crab Claw, Chain Hook of Angling. It is Stackable
  Currency, "Right click to open" — which is the mechanical reason it sells unopened.
* **Eldritch Depths is UNCERTAIN as a base.** 3.29.1 calls it "a new Chart variant", but
  poedb lists it as a ROOM of the Coral Forest tileset, it was already in our corpus
  before the patch, and `Eldritch_Depths_Chart` 404s where all four real bases return 200.
  Do not add a fifth base on this evidence.
* **No per-room density or loot data exists anywhere reachable** — poedb's room table is
  name→tileset only. Our measured room bonuses can be neither corroborated nor refuted,
  so they stay field measurements.

## PoE core mechanics — what actually multiplies what

Researched 2026-08-03 because three scoring bugs in a row came from ASSUMING these.
Full writeup with per-claim sources: `research/poe-core-mechanics.md`.
**Source caveat:** poewiki.net blocks the fetcher (Anubis) and fandom 402s, so most wiki
claims below are the wiki's own wording recovered from search snippets, not pages read in
full. Only **poedb.tw** and the **poe.watch API** were fetched directly. No SEO site was
used for any number. Confidence is marked; UNVERIFIED means read it off the game, do not
encode it.

* **A drop rolls against FOUR multiplicative quantity sources: player, AREA, party,
  monster.** The Voyage board's quantity is the *area* slot — not the player slot that
  almost all community writing (and the "magic find" folklore) is about. [confirmed]
* **The governing rule for this whole model:** *natural* drops are affected by all four
  categories; *non-natural* drops "may ignore some, ignore all, or interact in alternative
  ways". So monster loot scales, and anything PLACED is case-by-case. [confirmed]
* **Area quantity DOES multiply strongboxes and destructible containers.** The famous
  "quantity doesn't affect strongboxes" is about the player's GEAR: poedb states outright
  that gear IIQ does not affect strongbox drops while **map quantity modifiers do**, and
  the wiki says only the containing area's IIQ and the party bonus affect chests. Barrels
  carry their own object bonus, multiplicative with area quantity, on a low base ("only a
  small chance of containing an item"). This is what licenses the container channel —
  it was assumed before it was checked, and it happened to be right. [confirmed, poedb]
* **IIQ raises only the CHANCE per drop roll.** It "does not affect the type, quality, or
  rarity of item dropped" — it never enlarges the pool and never upgrades anything.
  [confirmed]
* **IIR never increases item COUNT, and does not touch currency, divination cards, scrolls
  or gems** — none of them have a rarity tier. It raises the unique chance on equipment.
  So a currency, card or Stacked-Deck gift gains NOTHING from a neighbour's rarity: rarity
  and quantity are different axes over different item classes. [confirmed]
* **Monster rarity is itself the fourth multiplier**: life, damage, experience, and item
  drop *rarity and quantity* all rise with it. Rares genuinely drop more AND rarer, which
  is why per-rare payouts get their own channel. The exact rare-vs-normal multiple is
  UNVERIFIED — leave it a tunable weight, do not invent a number.
* **Pack size probably does NOT raise the RARE count** (community forum reply, no GGG
  post) [uncertain] — which is the split the model already makes: pack size feeds the
  pack/magic channel, rare density comes only from explicit "increased number of Rare
  Monsters" mods.
* **Placed league ground items are the fixed-value class** [likely, from the natural/
  non-natural rule] — independent support for the field-confirmed flat bottle.
* **Stacked Decks: the FIELD says ~2c; poe.watch said 1.1c and the field wins.** The API
  number was used to "correct" the player's own figure and that was the wrong call — see
  the lag rule under Sources. What the API is still good for is the OTHER half of the sum:
  the basic currency a deck replaces prices at Transmutation 0.025, Augmentation 0.1,
  Jeweller's 0.098, Alchemy 0.167, Alteration 0.2, Fusing 0.25, so a conversion nets
  roughly the deck's price, and against a Chromatic (1.3c) it is close to a wash.
  **"Basic Currency" means currency with no drop restrictions**, i.e. non-league
  [confirmed], so the mod is a CONVERSION worth `(basic drops) x (deck price - what it
  replaced)` — not an addition.
* **Soul Eater is not a loot mod at all** — attack/cast speed and size per kill. It buys
  TIME, which this model does not price. Already `Stat.PlayerPower`; do not let it drift
  into a reward. [confirmed]
* **Tormented Spirits**: Touch (normal/magic) = 25% increased quantity AND rarity; Grip
  (rare/unique) = 50% increased rarity; killing the spirit guarantees a rare, a possessed
  rare ≥2, a possessed unique ≥3. The "Possessed" chart line is this same mechanic,
  rare-scoped. [confirmed]
* **Wildwood Wisps**: Vivid = MORE quantity, Wild = MORE rarity, Primal = additional
  currency — *more*, a separate multiplier, and they stack. [confirmed]
* **UNVERIFIED, do not encode — read them off the game:** Altars to the Goddess, Atziri's
  Influence, Pantheon Modifier on rares. All three are Voyage mod texts and only SEO sites
  had anything to say about them. Also unverified: whether pack size has diminishing
  returns.

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
* **LOCATE CORRECTS DRIFT; IT DOES NOT SEARCH THE DESKTOP.** The grid is chosen by
  counting how many shapes each candidate decodes, and more always wins — fine on a full
  panel, fatal on a nearly empty one. With ONE chart the true grid scores 1, so any patch
  of green elsewhere that decodes two beats it. Field-reported on a real capture: tab 2
  held a single chart and the reader announced FIVE, off a grid at x 279 while the panel
  sat at x 1768. A candidate origin more than three pitches from the calibration is now
  rejected outright — far more than any real drift or rescale error, and a factor of five
  short of that. The cost is that a wildly wrong calibration can no longer be rescued by
  Locate, which is what the calibrator is for and beats reading furniture as charts.
  `SparsePanelTests` pins it, decoys and all.
* **THE PANEL READ DOES NOT READ LEVELS. Deleted 2026-08-06, and do not bring it back.**
  The "L:83" caption went through template matching against masks carved from a real
  capture (no installed font matches Fontin), resampled at every other resolution. It
  never earned its keep: below 1440p most levels read blank, and on a 2560×1440 capture
  whose calibration was measured at 2490×1401 it read **83 as 3** — wrong, not missing,
  which is the failure mode that corrupts a plan rather than delaying one. The level
  arrives exactly and for free in the copied chart text (`Item Level: 83`), which every
  chart worth scoring gets anyway, so the caption was a worse copy of a fact the hover
  already supplies. A chart is level 0 until hovered.
  **The consequence, stated:** a panel-read-only session now has NO scoring signal —
  "solve before hovering" used to rank on level alone and now ranks on nothing. That was
  judged worth it, because ranking on levels that are silently wrong is worse than not
  ranking.
* **Windows OCR reads PoE prose well and small numerals badly** — it read `L:76` as
  `L.'j6`. That is why nothing numeric goes through OCR; only modifier text does.
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

1. **The game itself** — help text, copied item text, the Area Modifiers panel. **And the
   player, for PRICES.** They are the one looking at live trade.
2. **GGG's patch notes on the official forum.** Reachable, authoritative, and where the
   balance actually lives. 3.29.0b alone moved Golden Lanterns, gated the good strongboxes
   at area level 67, and made Ends and Straights rarer.
3. **poedb**, for the generated mod tables.
4. **Price APIs (poe.watch, poe.ninja and the rest) — LAGGING, and below poedb for this
   reason.** They are scrapes of listings, they trail the real market, and on a league
   item they may not list it at all. Bottles: absent entirely while trading at ~70c.
   Stacked Decks: the API said 1.1c and the field said ~2c. **A price the player quotes
   OUTRANKS a price fetched from an API — do not "correct" them with a scrape.** What
   the APIs remain good for is relative pricing of liquid core currency (what an
   Alteration is worth against a Chaos), where the lag is small and the volume is real.
5. **SEO strategy sites**, which paraphrase each other and get numbers wrong. Useful for
   the shape of a strategy, never for a figure.

**Reddit is unreachable** — it blocks Anthropic's crawler at the platform level. Not a
setting, and not something to route around with a spoofed user agent. Mirrors are dead
too (teddit/removeddit gone; live redlib instances 403 the fetcher or challenge curl).
If a thread matters, paste it in. Web search snippets quoting reddit are fair game.

## Community strategy (see `research/`)

Corroborated: plan from the reward outward rather than filling the board; adjacency charts
belong in the centre, global ones on the edge, high-value rewards in a corner (two border
modifiers); read the borders *before* placing; borders are rerollable for sulphur.

**The multiplication gap is CLOSED — five channels.** Border payouts multiply with the
receiving tile: per-RARE payouts by its rare density (pack size + the tileset's measured
room bonus — and per poedb, NO self-scope rare-adding chart mods exist, so nothing else
may count), at-least-Magic payouts by its pack density, container gifts (boxes,
barrels, cages…) by its Item Quantity, **drop CONVERSIONS** (`Basic Currency items
dropped by Monsters … will instead drop as Stacked Decks`) by its pack density AND its
quantity — the only TWO-factor channel, because a conversion adds nothing to the area,
it upgrades what the tile's own MONSTERS drop, so both how many monsters it has and the
quantity multiplying their drops decide the count converted. Scored flat it was worth
the same 40 wherever it sat; scored on quantity alone a +150% pack tile paid it exactly
what a blank one did, so the solver fed the square BARRELS — whose loot no monster drops
and which a monster-drop conversion therefore cannot touch. That asymmetry is the point:
pack size feeds a conversion and never a container. And **amplifiers** (`#% increased explicit modifier
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
* **THE MACRO MUST NEVER CLICK. Asked and answered, 2026-08-05.** Having F9 also click
  through to the next chart tab was considered and REJECTED. The community macro guide
  GGG's own forum hosts reports emailing them about a macro that moves the cursor and
  clicks, and being told it *"would definitely constitute botting"*; the standing rule is
  that you may not perform multiple in-game actions from one press. What keeps the
  current slurp on the right side of that line is that hover-and-copy performs no
  in-game action at all — the character does nothing, and Ctrl+C is a client-side copy.
  That is the same shape as PoE Trade Macro, the tool the community treats as the
  canonical safe one. A CLICK is an in-game action, and adding one would move this tool
  from "reads the screen" to "plays the game".
  So the tab handoff is the user's: the slurp finishes the open tab and NAMES the next
  one. Caveat worth keeping: no first-party GGG staff post was found saying this in so
  many words — the sources are valued posters relaying GGG email replies — so if the
  answer ever needs to be authoritative rather than careful, it comes from
  support@grindinggear.com.
