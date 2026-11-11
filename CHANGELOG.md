# Changelog

All notable changes to the CrossCrease launcher are documented here. The launcher uses [semantic versioning](https://semver.org/). The version source of truth is `BuildInfo.Version` in `CrossCrease.cs`.

## 0.4.0 — Downloads and updates

### Added
- In-launcher downloads: games whose profile has a `package_feed` show **Download** with the size. Packages are verified with SHA-256, installed per user under `%LOCALAPPDATA%\CrossCrease\games\<id>\packages\`, and kept separate from saves and settings.
- Update checks at startup and on Refresh for every game feed and for the launcher itself. Covers show "Update available", the selection bar offers **Update**, and the top bar offers a newer launcher. The previous package version is kept for rollback.
- Extra launch options (for example **Play online**) with their own packages, plus per-game settings asked for once in a small dialog (**Game settings…** in the right-click menu).
- Right-click actions: Check for updates, Open data folder, Remove downloaded files (keeps saves).
- New profile fields `package_feed`, `packages`, `launch_options`, `settings`, and path tokens `{data}`, `{package:<id>}`, `{setting:<id>}`.
- `package.ps1` builds the launcher release ZIP, its feed and checksums.
- `feeds.json` override for testing local feeds; `CROSSCREASE_DATA_DIRECTORY` for automated tests.
- **Game settings** window per game, opened from a button that is always next to Play and from the top of the right-click menu. It brings together game files, CrossCrease downloads and updates, player settings (online ID, server) and the cover.
- Per-game **Game files** choice: select the game's `eboot.bin` directly or a game folder (searched up to three levels deep), or go back to automatic detection. Picking an update folder resolves to the base game next to it.
- The selection bar shows the detected PS4 title ID and version from `param.sfo`, and whether an update (`<game folder>-UPDATE` / `-patch`) was found.

### Changed
- The NHL 17 profile now uses the downloadable CrossCrease NHL 17 runtime and your own game folder (the folder with `eboot.bin`) instead of the developer workspace layout. It adds **Play online**.
- A saved game location is used only while it still matches the profile.

### Compatibility
- Profiles without the new fields behave as before.
- NHL 17 saves from the developer workspace (`nhl17\runtime\user`) are not moved automatically. Copy its `home\` folder into `%LOCALAPPDATA%\CrossCrease\games\nhl17\user\` to keep them.

## 0.3.0 — Fluid library

### Added
- Planned profile for NHL 27 (PlayStation 5).
- Hovered covers lift with a soft drop shadow and a glow in the cover's own colour.
- Smooth, eased mouse-wheel and keyboard scrolling in the library.

### Changed
- Hover, scroll and theme animations are time-based and run at a steady high frame rate. Springs keep their momentum when the pointer moves between covers, so animations never jump.
- Library repaints only the cards that change. Card artwork is composited once and cached, and per-frame paint time drops from about 50 ms to about 1–2 ms.
- The theme colour now always comes from the full-colour cover, including for games that are not ready, and tints the window much more strongly.
- Dominant-colour detection skips the detected PS4/PS5 platform banner and the rating strip, and weights saturated colours near the centre of the cover.

### Compatibility
- Profiles, saved locations and custom covers are unchanged.

## 0.2.0 — Library redesign

### Added
- Library view: every profile appears as a poster cover in a responsive grid. Ready games are in colour, unavailable and planned games are in greyscale.
- Slim selection bar for the selected game with a cover thumbnail, platform, status, and context-aware actions (Play / Locate game / Coming soon), so the library gets most of the window.
- Generated CrossCrease cover art per profile (name, platform badge, accent colour). No publisher artwork is included.
- Custom cover images per profile, stored in `%LOCALAPPDATA%\CrossCrease\covers`, plus an optional `cover` profile field. Wide box-art scans are fitted to the card width instead of being cropped.
- `"status": "planned"` profile field for roadmap titles that are not launchable yet.
- Planned profiles for NHL 15, 16, 18–23, 25, and 26.
- Cover-driven theme: the launcher picks the dominant colour of each cover and smoothly tints the top bar, selection bar, library background, logo, Play button and selection ring to match the selected game (muted for games that are not ready).
- Right-click menu, keyboard navigation (arrows, Enter, F5), double-click to play, and hover animation.
- High-DPI awareness and a dark title bar on Windows 10/11.
- Assembly version metadata.

### Changed
- `system` for NHL 17 and NHL 24 is now `PlayStation 4`.
- Profiles are sorted in natural order (NHL 9 before NHL 10).

### Compatibility
- Existing profiles and saved locations (`%LOCALAPPDATA%\CrossCrease\locations.json`) work unchanged. Launch behaviour is the same as before.

## 0.1.0

- First launcher with a sidebar profile list, automatic game discovery, Locate game, and profile import.
