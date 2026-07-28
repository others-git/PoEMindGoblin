# Where this left off

## Needs input before proceeding

* **A screenshot of a CHART hovered in the Voyage planner** (not a board square). Chart
  mods are known from *trade* item text, but what the planner surfaces is unverified, and
  it decides the read-mode design.
* **Resolution / UI scale.** Capture templates key off pixel geometry; better to anchor to
  the real setup than guess.
* **Did the UseCookies fix clear the live-search 401?** The fetch and travel paths are
  still unverified end to end — they need a session, so no test covers them.
* **Vaal Orb outcome odds.** Still unverified: the wiki is challenge-gated, the Fandom
  mirror 402s, poedb has no gem section, and search results describe PoE2. Defaults are
  25/25/25/25 and every affected row is labelled accordingly. One line to correct.

## Built and tested (224 tests)

| Area | State |
|---|---|
| Rate limiter | Real headers, stops one slot below every cap |
| Credentials | DPAPI, keeps every cookie, never logged |
| Trade client | search / fetch / travel, batched at the API's 10-id cap |
| Live search | websocket + reconnect; **untested against a real session** |
| Connection diagnosis | Isolates network / cookies / dead query id |
| Gem RoI | 4 paths, 508 vendor gems, live poe.watch prices |
| Stat index | 23 categories, 746 stats, advisory not restrictive |
| Voyage model | 5 shapes from `chart_shape`, mutual edge matching |
| Voyage solver | Anytime; 100ms within 0.05% of 5s |
| Voyage rules | Hot-reloaded JSON profiles |

## Not started

* **Second-monitor mirror app.** Not an overlay — a window that reproduces the board and
  chart-panel layout, with a *read* mode that ticks off each element as it is captured.
* **Screen capture.** Fixed 6x10 panel and 3x3 board; shape glyphs are a small template
  set and `L:xx` sits at a known offset, so this is template matching plus a digit read,
  not general OCR. The connectivity data is all on screen — only modifiers need hover.
* Gem RoI: wire `StatIndex.Review` into the add-watch flow; tray icon; per-watch hotkeys.

## Things worth not relearning

* `HttpClientHandler` defaults to `UseCookies = true`, which **silently drops a manually
  set Cookie header**. That cost a long hunt through POESESSID / POETOKEN / cf_clearance
  when the credentials were fine. `HttpFactory` exists to make that impossible.
* Travel and whisper are the **same endpoint**, distinguished by a `tok` claim in a
  server-signed JWT with a 300s TTL. Tokens cannot be pre-fetched.
* Transfigured gems are identified by PoB's `Alt<Letter>` variantId. Matching " of " in
  the name wrongly excludes Purity of Elements, Rain of Arrows and Herald of Thunder.
* Sorting a grid on formatted text ranks "99%" above "970%". Numeric columns need
  `SortMemberPath`.
* poe.ninja is Cloudflare-gated for non-browser clients; poe.watch is not.
