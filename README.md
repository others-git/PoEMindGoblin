# poe-market-watch

Windows desktop client for pathofexile.com trade **live search**, aimed at the async
Market / Merchant's Tab listings — find a matching item the moment it is listed and be
one keypress from the seller's hideout.

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
src/PoeMarketWatch/         WPF desktop app
tests/PoeMarketWatch.Tests/ xunit; Core is UI-free so these run headless
assets/trade-index.json     generated stat/spawn index (see below)
```

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
