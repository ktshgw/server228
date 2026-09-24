# SOMS! server

This repository contains the SOMS! osu!(lazer) server, website, client extensions,
launcher, switcher, spectator service, and SOMSAI mode.

See `docs/PROJECT_STRUCTURE.md` for the directory layout. Runtime configuration is
copied from `.env.example`; local secrets and runtime data are intentionally ignored.

Client DLL sources are kept in `client/enhanced-auth` and `client/startup-hook`.
Third-party source snapshots used for compatible client and spectator builds are in
`vendor/`.
