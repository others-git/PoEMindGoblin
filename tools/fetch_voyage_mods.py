#!/usr/bin/env python3
"""Regenerate assets/voyage-mods.json from poedb's Voyage chart mod tables.

Voyage charts have exactly three bases and poedb publishes the complete mod table for
each, so the set of lines a chart can carry is closed and knowable.  This pulls it
directly rather than transcribing it, so refreshing after a patch is one command and no
judgement.

    python3 tools/fetch_voyage_mods.py [-o assets/voyage-mods.json] [--print-unclassified]

Two things about the source shaped this:

  * One table row is one AFFIX, and an affix can grant several stat lines separated by
    <br>.  The league pairs danger with payout in a single affix -- "Monsters have 80%
    chance to Avoid Elemental Ailments" and "45% increased Quantity of Items found in
    adjacent Areas" are the same suffix -- so classification has to be per LINE.  A
    per-affix split would file every reward on a dangerous suffix as danger.
  * Numbers are wrapped in <span class='mod-value'>, which is what makes exact matching
    possible: with every number reduced to '#', a line from a real chart can be looked up
    in the table verbatim instead of pattern-matched at it.

Classification itself is the one part that cannot come from the page -- poedb tags affixes
by damage type, not by whether the player wants them.  The rules below are ordered and
explicit, and anything they do not cover is emitted as "unclassified" and printed, so a
patch that adds a modifier shows up as a warning rather than being silently mis-filed.
"""

from __future__ import annotations

import argparse
import html
import json
import re
import sys
import urllib.error
import urllib.request
from collections import OrderedDict

# Every base linked from poedb's Chart item-class page.
BASES = [
    "Coral_Reef_Chart",
    "Coral_Forest_Chart",
    "Sandy_Seabed_Chart",
    "Thermal_Vents_Chart",
]

# Thermal Vents exists as an item but has no mod table published, so parsing nothing off
# it is the truth. Every OTHER base yielding nothing means the markup moved, and writing
# that result would ship a corpus silently missing a whole base -- which reads as a
# successful refresh. Listing the exception is what lets the rest be fatal.
NO_TABLE = {"Thermal_Vents_Chart"}

# The league page carries two things the chart pages do not: the BORDER mods, which are
# what the figurines around the board grant, and the room list -- the tilesets a chart can
# open. Neither is documented anywhere else.
LEAGUE_PAGE = "Maiden_Voyage"

URL = "https://poedb.tw/us/{}"

# poedb 403s an unidentified client.
UA = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
      "(KHTML, like Gecko) Chrome/120.0 Safari/537.36")

# --- extraction ---------------------------------------------------------------------

CHART_MODS_TAB = 'MapDeepWaterChartMods'
BORDER_MODS_TAB = 'DeepWaterBorderMods'
# The tab id really is spelled "Roomss" on the page.
ROOMS_TAB = 'Roomss'

ROOM = re.compile(
    r'<div class="flex-grow-1 ms-2">([^<]+)<div><a[^>]*class="WorldAreas[^"]*"[^>]*>([^<]+)</a>',
    re.S)
ROW = re.compile(r"<tr>(.*?)</tr>", re.S)
CELL = re.compile(r"<td[^>]*>(.*?)</td>", re.S)
# Badges carry the affix's crafting tags; drop them with their text, or "Elemental Fire
# Cold Lightning Ailment" ends up appended to the modifier.
BADGE = re.compile(r"<span[^>]*class=\"badge[^\"]*\"[^>]*>.*?</span>", re.S)
# poedb prints the raw stat id in a "secondary" span when GGG ships no display string.
SECONDARY = re.compile(r"<span[^>]*class=\"secondary\"[^>]*>.*?</span>", re.S)
MOD_VALUE_OPEN = re.compile(r"<span[^>]*class=['\"]mod-value['\"][^>]*>", re.S)
SPAN_ANY = re.compile(r"<span\b[^>]*>|</span>", re.S)
BREAK = re.compile(r"<br\s*/?>", re.I)
TAG = re.compile(r"<[^>]+>")
DIGITS = re.compile(r"[-+]?\d+(?:\.\d+)?")
SIGNED_HASH = re.compile(r"[-+]#")


def fetch(base: str) -> str:
    request = urllib.request.Request(URL.format(base), headers={"User-Agent": UA})
    with urllib.request.urlopen(request, timeout=60) as response:
        return response.read().decode("utf-8", errors="replace")


def strip_mod_values(html_text: str) -> str:
    """Replace each <span class='mod-value'>...</span> with '#', nesting included.

    A range is rendered with a span INSIDE the value span --
    <span class='mod-value'>(12<span class="ndash">-</span>15)</span> -- so a non-greedy
    match ends at the inner tag and leaves "15)" behind, which came out as "##)".
    Depth counting is the only thing that reads it correctly.
    """
    out = []
    position = 0
    while (match := MOD_VALUE_OPEN.search(html_text, position)) is not None:
        out.append(html_text[position:match.start()])
        out.append("#")
        depth = 1
        cursor = match.end()
        for tag in SPAN_ANY.finditer(html_text, cursor):
            depth += -1 if tag.group(0) == "</span>" else 1
            if depth == 0:
                cursor = tag.end()
                break
        else:
            cursor = len(html_text)
        position = cursor
    out.append(html_text[position:])
    return "".join(out)


def normalise(line: str) -> str:
    """A modifier with every number replaced by '#', so rolls collapse to one entry.

    Signs are dropped with the number.  poedb renders "+<span>18</span>%" -- the value is
    in the span but the plus is not -- while a copied chart says "+18%", so keeping the
    sign would make the two sides disagree on exactly the mods that carry one.  The app
    normalises identically; see ChartRewards.Normalise.
    """
    line = html.unescape(TAG.sub("", line))
    line = DIGITS.sub("#", line)
    line = SIGNED_HASH.sub("#", line)
    return " ".join(line.split()).strip(" .")


def tab(page: str, tab_id: str) -> str | None:
    """
    The markup of one tab.

    A mod tab ends at its </table>; the room tab is a grid of divs with no table at all,
    so it runs to wherever the NEXT tab begins. Stopping at the first closing triple
    caught exactly one room.
    """
    start = page.find(f'id="{tab_id}"')
    if start < 0:
        return None

    next_tab = re.search(r'id="[A-Za-z0-9_]+" class="tab-pane', page[start + 1:])
    end = start + 1 + next_tab.start() if next_tab else len(page)

    table_end = page.find("</table>", start)
    if 0 < table_end < end:
        end = table_end
    return page[start:end]


def extract(page: str, tab_id: str = CHART_MODS_TAB) -> list[tuple[str, str]]:
    """Every (affix_kind, normalised_line) in a mod table."""
    section_text = tab(page, tab_id)
    if not section_text:
        return []
    section = re.match(r"(?s).*", section_text)
    if not section:
        return []

    found: list[tuple[str, str]] = []
    for row in ROW.findall(section_text):
        cells = CELL.findall(row)
        if len(cells) < 3:
            continue
        kind = normalise(cells[1]) or "Unknown"
        body = strip_mod_values(SECONDARY.sub("", BADGE.sub("", cells[2])))
        for part in BREAK.split(body):
            line = normalise(part)
            if line:
                found.append((kind, line))
    return found


# --- classification -----------------------------------------------------------------
#
# Ordered, and REWARD is tried first: several payouts mention monsters, and a
# difficulty-first pass throws them away.  "# increased number of Rare Monsters",
# "Rare monsters ... are imprisoned by Essences", "Monsters have a chance to be Empowered
# by # Wildwood Wisps" and "Monsters in all Voyage Areas are at least Magic" are all
# rewards that name monsters.

REWARD = [
    # containers and extra spawns
    r"additional Imprisoned Monsters",
    r"additional (?:Diviner's |Arcanist's |Operative's )?Strongboxes",
    r"additional packs of",
    r"additional Messages? in Bottles",
    r"additional cages? of Tormented Spirits",
    r"additional Clusters of Barrels",
    r"additional Giant Starfish",
    r"additional Golden Lanterns",
    r"additional Treasure",
    r"increased number of (?:Rare|Magic) Monsters",
    r"contains? highly prized and exotic Fish",
    r"contain Friendly Jellyfish",
    # loot conversion, upgrades and extra drops
    r"is converted to Gold",
    r"chance to be Fractured",
    r"chance to Fracture on death",
    r"chance to instead drop as",
    r"imprisoned by Essences",
    r"to be Possessed",
    r"Pantheon Modifier",
    r"drop an additional",
    r"chance to have #% Quality",
    r"Chart to not be consumed",
    r"increased explicit modifier magnitudes",
    # the headline stats -- note "Qauntity", which GGG misspells in the global lines only
    r"increased Q(?:uantity|auntity) of Items found",
    r"increased Rarity of Items found",
    r"increased Pack Size",
    r"increased Gold found",
    r"increased Dead Man's Sulphur",
    # currency and drops off rare monsters -- the figurine modifiers, which are where
    # the real money on this board is
    r"drop #? ?additional (?:Divine|Exalted|Chaos|Ancient|Blessed|Chromatic|Regal|Vaal) Orbs",
    r"drop #? ?additional Orbs of (?:Annulment|Regret)",
    r"drop #? ?additional Gemcutter's Prisms",
    r"drop #? ?additional Scarabs",
    r"drop Dead Man's Sulphur",
    r"chance to drop a Support Gem",
    r"more (?:Currency|Scarabs|Rarity of Items) found",
    r"instead drop as Stacked Decks",
    r"Altars to the Goddess",
    r"a lost Pirate's Locker",
    r"contain Captainsbane",
    r"a Brinerot raiding party",
    r"Charts have #% chance to not be consumed",
    r"gain #% increased Experience",
    r"have an additional modifier",
    r"Placing Lanterns does not reduce your Lantern count",
    # A payout PENALTY, and an unusual one: it scales with how connected the board is.
    r"reduced quantity of items found in adjacent Areas per connection",
    # league set pieces
    r"have Soul Eater",
    r"Empowered by #+ Wildwood Wisps",
    r"Atziri's Influence",
    r"contains? Filthscrabble",
    r"are at least Magic",
    # A payout REDUCTION is still about the payout; hiding it would flatter the chart.
    r"cannot drop Equipment, Flasks or Tinctures",
]

DIFFICULTY = [
    # monster defences
    r"chance to Avoid Elemental Ailments",
    r"Monster Physical Damage Reduction",
    r"Monster Chaos Resistance",
    r"Monster Elemental Resistances",
    r"chance to Suppress Spell Damage",
    r"of Maximum Life as Extra Maximum Energy Shield",
    r"more Monster Life",
    r"chance to avoid Poison, Impale, and Bleeding",
    r"reduced Extra Damage from Critical Strikes",
    r"Monsters have increased Accuracy Rating",
    r"Monsters reflect",
    # monster offence
    r"increased Monster Damage",
    r"extra Physical Damage as (?:Fire|Cold|Lightning)",
    r"Physical Damage as Extra Chaos Damage",
    r"Inflict Withered",
    r"additional Projectiles",
    r"skills Chain",
    r"Monster Damage Penetrates",
    r"increased Critical Strike Chance",
    r"Monster Critical Strike Multiplier",
    r"increased Area of Effect",
    r"Monsters deal .* Damage",
    # monster speed, charges and control
    r"increased Monster (?:Movement|Attack|Cast) Speed",
    r"Speed cannot be modified to below Base Value",
    r"cannot be Stunned",
    r"cannot be Taunted",
    r"are Hexproof",
    r"(?:gain|gain an?) (?:a )?(?:Frenzy|Endurance|Power) Charge on Hit",
    r"steal Power, Frenzy and Endurance charges on Hit",
    r"Monsters (?:Maim|Poison|Blind|Hinder) on Hit",
    # 3.29.1's replacement for the max-res roll it disabled. It is a hinder, and the
    # patch notes are the only place it appears -- no chart table publishes it -- so
    # without a rule the app's "matches neither" bias would file the danger as a payout.
    r"apply Grasping Vines on Hit",
    # area and player penalties
    r"to all maximum Resistances",
    r"less effect of Curses on Monsters",
    r"Area has patches of",
    r"increased Effect of Curses on you",
]


# Lines that are never seen on an actual chart and so have no bucket.
#
# poedb prints a raw stat id in brackets when GGG ships no display string for it --
# "local deepwater mod applies to adjacent charts [#]" is the internal flag behind the
# adjacency implicit, whose player-facing wording is captured separately. And an
# uncharted base shows a placeholder where its Voyage Modifier will go.
EXCLUDE = [
    r"\[#\]$",
    r"^Voyage Modifier will be revealed once Charted$",
]


def extract_rooms(page: str) -> dict[str, str]:
    """Each room (the tileset a chart opens) mapped to the base area it belongs to."""
    section_text = tab(page, ROOMS_TAB)
    if not section_text:
        return {}
    rooms = {}
    for name, area in ROOM.findall(section_text):
        name, area = normalise(name), normalise(area)
        # "[DNT]" marks a Do Not Translate placeholder -- content that exists in the data
        # but not in the game. Thermal Vents is entirely placeholders, which is the same
        # story its missing mod table tells.
        if name.startswith("[DNT]"):
            continue
        rooms[name] = area
    return rooms


def classify(line: str, reward: re.Pattern, difficulty: re.Pattern,
             exclude: re.Pattern) -> str:
    if exclude.search(line):
        return "excluded"
    if reward.search(line):
        return "reward"
    if difficulty.search(line):
        return "difficulty"
    return "unclassified"


def compile_group(patterns: list[str]) -> re.Pattern:
    return re.compile("|".join(f"(?:{p})" for p in patterns), re.I)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("-o", "--out", default="assets/voyage-mods.json")
    parser.add_argument("--print-unclassified", action="store_true",
                        help="list unclassified lines and exit non-zero")
    args = parser.parse_args()

    reward = compile_group(REWARD)
    difficulty = compile_group(DIFFICULTY)
    exclude = compile_group(EXCLUDE)

    lines: dict[str, str] = {}
    kinds: dict[str, set[str]] = {}
    per_base: dict[str, int] = {}
    board_lines: dict[str, str] = {}
    rooms: dict[str, str] = {}

    for base in BASES:
        try:
            page = fetch(base)
        except (urllib.error.URLError, urllib.error.HTTPError) as error:
            print(f"error: could not fetch {base}: {error}", file=sys.stderr)
            return 2

        found = extract(page)
        per_base[base] = len(found)
        if not found:
            if base not in NO_TABLE:
                print(f"error: no mod table on {base} -- has poedb changed?",
                      file=sys.stderr)
                return 2
            print(f"warning: no mod table on {base} (none published yet)", file=sys.stderr)
            continue
        if base in NO_TABLE:
            print(f"note: {base} now publishes a mod table -- drop it from NO_TABLE",
                  file=sys.stderr)

        for kind, line in found:
            lines.setdefault(line, classify(line, reward, difficulty, exclude))
            kinds.setdefault(line, set()).add(kind)

    # poedb elides the value on some tier-1 rows, emitting "+% Monster Chaos Resistance"
    # where the tiered rows say "+(26-40)%". Drop a line whose only difference from an
    # existing one is the missing number -- it describes the same modifier and could
    # never match a real chart, which always carries a value.
    for line in list(lines):
        if "#" in line:
            continue
        # The sign goes with the number, exactly as normalise() does it, or "+#%" would
        # never match the stored "#%".
        filled = SIGNED_HASH.sub("#", re.sub(r"(?<![#\d])%", "#%", line, count=1))
        if filled != line and filled in lines:
            del lines[line]

    excluded = sorted(l for l, c in lines.items() if c == "excluded")
    for line in excluded:
        del lines[line]

    # The league page: border mods (what the figurines grant) and the room list. Nothing
    # else publishes either, so a page that parses to nothing is a corpus with no
    # figurines and no tilesets -- fatal for the same reason a missing base is.
    try:
        league = fetch(LEAGUE_PAGE)
    except (urllib.error.URLError, urllib.error.HTTPError) as error:
        print(f"error: could not fetch {LEAGUE_PAGE}: {error}", file=sys.stderr)
        return 2

    rooms = extract_rooms(league)
    border = extract(league, BORDER_MODS_TAB)
    per_base[LEAGUE_PAGE + " (border)"] = len(border)
    if not border:
        print(f"error: no border mod table on {LEAGUE_PAGE} -- has poedb changed?",
              file=sys.stderr)
        return 2
    if not rooms:
        print(f"error: no room list on {LEAGUE_PAGE} -- has poedb changed?",
              file=sys.stderr)
        return 2
    for kind, line in border:
        category = classify(line, reward, difficulty, exclude)
        if category == "excluded":
            continue
        board_lines.setdefault(line, category)
        lines.setdefault(line, category)
        kinds.setdefault(line, set()).add(kind)

    if not lines:
        print("error: no mod table found on any base -- has poedb changed?", file=sys.stderr)
        return 2

    unclassified = sorted(l for l, c in lines.items() if c == "unclassified")

    ordered = OrderedDict()
    ordered["_source"] = "https://poedb.tw/us/{} for " + ", ".join(BASES)
    ordered["_generated_by"] = "tools/fetch_voyage_mods.py -- do not edit by hand, rerun it"
    ordered["_note"] = (
        "'lines' maps every modifier the three Voyage bases can roll, with each number "
        "reduced to '#', to whether it is a payout or monster difficulty. The app "
        "normalises a chart's line the same way and looks it up, so matching is exact. "
        "The pattern lists are only a fallback for a line not in the table -- a modifier "
        "added by a patch before this file is regenerated."
    )
    ordered["bases"] = BASES
    ordered["counts"] = {
        "lines": len(lines),
        "reward": sum(1 for c in lines.values() if c == "reward"),
        "difficulty": sum(1 for c in lines.values() if c == "difficulty"),
        "unclassified": len(unclassified),
        "excluded": len(excluded),
        "board_lines": len(board_lines),
        "rooms": len(rooms),
        "rows_per_base": per_base,
    }
    ordered["lines"] = OrderedDict(sorted(lines.items()))
    # Which of those come from the FIGURINES rather than a chart. They arrive by a
    # different route -- read off the Area Modifiers panel per square, not copied from an
    # item -- so it is worth knowing which is which.
    ordered["boardLines"] = OrderedDict(sorted(board_lines.items()))
    # The tilesets a chart can open, and the base area each belongs to.
    ordered["rooms"] = OrderedDict(sorted(rooms.items()))
    ordered["reward"] = REWARD
    ordered["difficulty"] = DIFFICULTY
    ordered["excluded"] = excluded

    with open(args.out, "w", encoding="utf-8") as handle:
        json.dump(ordered, handle, indent=2, ensure_ascii=False)
        handle.write("\n")

    counts = ordered["counts"]
    print(f"{args.out}: {counts['lines']} distinct lines "
          f"({counts['reward']} reward, {counts['difficulty']} difficulty, "
          f"{counts['excluded']} excluded), {counts['board_lines']} from figurines, "
          f"{counts['rooms']} rooms")
    for base, count in per_base.items():
        print(f"  {base}: {count} lines")

    if unclassified:
        # Loud on purpose. A new modifier that nothing matches would otherwise be filed
        # as a reward by the app's fallback and never looked at again.
        print(f"\n{len(unclassified)} UNCLASSIFIED -- add a rule for each:", file=sys.stderr)
        for line in unclassified:
            print(f"  {line}", file=sys.stderr)
        if args.print_unclassified:
            return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
