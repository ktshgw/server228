# SOMS! 28.9.26b

Released 28 September 2026.

## Added

- Added background SOMSAI matchmaking with penalties for declining matches.
- (Admin) Added scripts for rebuilding dlls and staging project.
- Added download mirrors to both the client and the website as a fallback in case the primary links are down.
- Added Friends tab on web.
- Added player-selectable EZ, HD and HR combinations to SOMSAI FreeMod and tiebreaker rounds.
- Added an option to randomize skin on every map start.
- Added private custom matches to SOMSAI.
- Added an option to ignore recommended difficulty in songs select.
- Added an Easter egg in SOMSAI to a very low ranked lobbies.
- Added mandatory switcher version checks and safer client startup.


## Fixed

- (Admin) Python files were rewritten to prevent potential errors.
- (Admin) Fixed mappool imports so invalid maps are skipped without interrupting the rest of the stage or tournament.
- Map difficulty in Multiplayer now dynamically recalculates stats based on the local mod set.
- Fixed an issue where a map was unnecessarily downloaded when entering SOMSAI Lobby.

## Changed

- Reworked SOMSAI AI bots with rank-scaled skill, rerolled archetypes, player traits and realistic score simulation.
- Replaced Ranked Play with SOMSAI entirely.
- Minor improvements have been made to the profile's play history tab.
- Recent plays have been moved to the Chronology tab; scores older than 24 hours are now automatically cleared from the tab.
- (Admin) The admin panel has been drastically changed.


## Removed

- Removed subscription notifications from another players adding or removing you from friends
