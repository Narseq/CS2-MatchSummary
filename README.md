# MatchSummary (CS2 / CounterStrikeSharp)

End-of-map summary in chat for Counter-Strike 2 servers, plus `!stats` during the map.
Standalone: no database, no addon, no other plugins required.

Made by **Ziuber** for [Fragujemy.com](https://fragujemy.com) - free for any community.

## What players see
When the match ends (win panel), every player gets, in their own language:
- **TOP by ADR** (default 3 players): ADR, K/D/A, HS%
- **Awards**: most kills, best HS% (min. 5 kills), entry fragger, clutch master (wins + best 1vX), utility damage, most MVPs
- **Personal line**: place in the ranking, ADR, K/D/A, HS%, 3k/4k/5k rounds

`!stats` (or `!mystats`) shows the same personal numbers mid-map, plus entries, clutches, utility damage, flash assists and MVPs.

## What is counted
Only live rounds (after freeze time, warmup ignored). Team damage and suicides do not count.
Damage is the health actually taken (the last hit cannot count more than the victim had left).
A clutch is a round won by the last player alive on a team who was facing at least one enemy at that moment.
Stats live for one map and reset on map change.

## Install
1. Download the zip from Releases and extract it into `game/csgo/`
   (`addons/counterstrikesharp/plugins/MatchSummary/` with `lang/`).
2. Restart the server or run `css_plugins load MatchSummary`.
3. The config is created on first start: `addons/counterstrikesharp/configs/plugins/MatchSummary/MatchSummary.json`.

Requires CounterStrikeSharp 1.0.37x (.NET 10).

## Config
| key | default | meaning |
|---|---|---|
| `Enabled` | `true` | turns the plugin on/off |
| `IncludeBots` | `false` | count bots in the TOP and awards |
| `MinRounds` | `3` | rounds a player must play to be ranked |
| `TopCount` | `3` | players in the end-of-map TOP (1-10) |
| `ShowTop` / `ShowAwards` / `ShowPersonal` | `true` | parts of the summary |
| `SummaryDelay` | `1.5` | seconds after the win panel before printing |
| `StatsCommands` | `["stats", "mystats"]` | chat commands for `!stats`; empty list disables it |

## Languages
`lang/en.json` and `lang/pl.json`. Players get their CounterStrikeSharp language (`!lang`); add a file
(e.g. `de.json`) with the same keys to support more. Colour tags like `{green}` work in translations.

## Build
`dotnet build -c Release` (needs the .NET 10 SDK).
