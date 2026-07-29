#!/usr/bin/env python3
"""Print current orb prices in chaos, for updating the currency profile weights.

poe.watch, not poe.ninja: ninja is Cloudflare-gated for non-browser clients (see
README). Prints the values formatted like the rules expect; updating VoyageRules.cs
is a deliberate manual step so a market blip cannot silently rewrite the shipped
weights.

    python3 tools/fetch_orb_prices.py [league]
"""
import json, statistics, sys, urllib.request

league = sys.argv[1] if len(sys.argv) > 1 else "Allflame"
def get(cat):
    with urllib.request.urlopen(
            f"https://api.poe.watch/get?league={league}&category={cat}") as r:
        return json.load(r)

currency = {i["name"]: i.get("mean") for i in get("currency")}
for name in ["Divine Orb", "Exalted Orb", "Orb of Annulment", "Ancient Orb",
             "Gemcutter's Prism", "Orb of Regret", "Vaal Orb", "Blessed Orb",
             "Regal Orb", "Chromatic Orb"]:
    print(f"{name:<20} {currency.get(name)}")

scarabs = [i["mean"] for i in get("scarab") if i.get("mean")]
print(f"{'Scarab (median)':<20} {statistics.median(scarabs):.1f}   "
      f"(mean {statistics.mean(scarabs):.1f} over {len(scarabs)}, skewed by outliers)")
