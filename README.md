# CrossCrease Launcher

The CrossCrease launcher is a small Windows app that shows every CrossCrease game profile as a cover in one library. It downloads and updates the CrossCrease runtime each game needs, finds your own copy of the game, and starts it. Games that are ready appear in full colour. Games that still need a download, have not been found, or are planned for a future release stay visible in greyscale.

## Installing

Download `crosscrease-launcher-<version>-windows-x64.zip` from the [latest release](https://github.com/CrossCrease/launcher/releases/latest). Unzip it to a folder you own, for example `Documents\CrossCrease`, and start `CrossCrease.exe`. It needs Windows 10 or 11, and the .NET Framework 4.x that ships with Windows. From then on the launcher updates itself.

## Getting a game running

Supported games that are not ready yet show **Set up** on their cover. Select the cover; the bar above the library shows the two steps and the button for the next one.

1. **Your game files** come first. **Set up NHL 17** asks how you have the game:
   - **Install from .pkg files**: choose the game's `.pkg` file, and the update's `.pkg` as well if you have it (hold Ctrl to select both). CrossCrease checks them, shows how much space they need, and unpacks them into `Documents\CrossCrease\NHL 17\` or a folder you choose. The bar shows the progress and a **Cancel** button. Your `.pkg` files are not changed, and you can delete them afterwards.
   - **Select eboot.bin…**: the game is already unpacked into a folder.

   This step is skipped when CrossCrease already found the game.
2. **The CrossCrease runtime** downloads right after that (for NHL 17: the adjusted shadPS4 build, its default configuration, launch scripts and the online connection files). If your game was already found, the button reads **Download runtime (29.8 MB)**. Files are checked against a SHA-256 checksum before they are installed.
3. Then the game is **Ready to play**.

The base game is all you need. An update (for example NHL 17 1.08) is optional and used when it is present. **Start with the game update** in **Game settings**, or in the cover's right-click menu, switches between the update and the base game for **Play**.

You can also choose your game files at any time in **Game settings**:
   - **Select eboot.bin…**: point directly at the game's `eboot.bin`. The folder that contains it becomes the game folder.
   - **Select game folder…**: choose the game folder, or a folder up to three levels above it (for example a "Games" folder). CrossCrease finds the game inside.

   The choice is saved per game. Change it later in **Game settings**. **Use automatic detection** forgets it again.

   Updates must sit next to the base game in a folder with the same name plus `-UPDATE` (or `-patch`), for example `CUSA04326` and `CUSA04326-UPDATE`. shadPS4 applies them from there. If you pick the update folder's `eboot.bin`, CrossCrease uses the base game next to it. The selection bar shows the detected title ID and version, for example "CUSA04326 · version 01.08 (update installed)".

   **Install from .pkg files…** is also in **Game settings** and in the cover's right-click menu (under **Game files**), for example to add an update later. It works with PS4 packages made for PC or homebrew use ("fake PKG" or fPKG). Packages bought from the PlayStation Store are encrypted for one console and can't be unpacked on a PC; CrossCrease says so instead of installing them. Add-on content (DLC) and partial (delta) updates are not supported.
4. **Play** starts the game. The first time, it asks once for any details the game needs (for NHL 17: your online ID and the server, which can stay `default`). Change them later under **Player settings** in **Game settings**.

CrossCrease never downloads or distributes game files, firmware, keys or saves. You need your own copy of each game.

## Using the library

- **Play** (or double-click / press Enter on a cover) starts a ready game.
- **Game settings** sits next to Play in the selection bar for every game, and is the first entry when you right-click a cover. It has everything you can change for that game:
  - **Game files**: where your copy is, which version and update were detected, **Select eboot.bin…**, **Select folder…**, **Use automatic detection**, **Open folder**.
  - **CrossCrease downloads**: installed and available versions, **Download** / **Update**, **Check for updates**, **Remove downloads…**, **Open saves & data folder**.
  - **Player settings**: values such as your online ID and server, with **Save player settings**.
  - **Cover**: **Choose image…** and **Reset cover**.
- **Right-click** a cover for quick actions: game settings, play, game files, start with the game update, open the game folder, check for updates, remove downloads, and the cover image.
- Arrow keys move through the library. **F5** or **Refresh** rescans the profiles folder and checks for updates.

Cover states:

| State | Look | Meaning |
| --- | --- | --- |
| Ready to play | Colour, green dot | The game, its required files and its downloads are present. |
| Update available | Colour, blue dot | A newer runtime is available. Select the game and choose **Update**. |
| Set up | Greyscale, blue dot | A supported game that still needs your game files and/or the CrossCrease runtime. |
| Coming soon | Greyscale | The title is on the CrossCrease roadmap but not supported yet. |

## Downloads and updates

Each game project publishes its packages as GitHub release assets together with a small **feed** file that lists them. Feeds use stable `releases/latest/download/...` addresses, so the launcher always sees the newest release.

- **At startup** (and on **Refresh**) the launcher reads the feed of every game that has one, plus the launcher's own feed. This happens in the background and never blocks the library. Without an internet connection the launcher works as before; affected games say that the download server could not be reached.
- **Updates** are offered on the game's cover and in the selection bar. Updating keeps the previous version on disk and removes anything older.
- **Roll back to x.y.z** (Game settings → CrossCrease downloads) switches to the kept version. The newer one stays on disk, and updating to it again needs no download.
- If a version folder is deleted by hand, the launcher repairs its records and uses the version that is still there. Normally you never need to touch the `packages` folder.
- **Launcher updates** show an **Update launcher to x.y.z** button in the top bar. It downloads the new release, checks its SHA-256, and replaces the launcher files. Profiles you imported are kept. Then it restarts. The previous version is kept as `CrossCrease.exe.old` until the next start. If the launcher sits in a folder it may not change (for example `Program Files`), the button opens the release page instead.
- A feed may require a minimum launcher version. Older launchers then ask you to update the launcher first.
- A game cannot be updated or have its files removed while it is running from those files.

Where things are stored (per Windows user, outside the launcher folder, so reinstalling or updating the launcher keeps them):

| Path under `%LOCALAPPDATA%\CrossCrease\` | Contents |
| --- | --- |
| `games\<id>\packages\<package>\<version>\` | Downloaded packages (replaced by updates). |
| `games\<id>\packages\installed.json` | Installed versions. |
| `games\<id>\user\` | The emulator's player data: saves, configuration, shader caches. Never touched by updates. |
| `games\<id>\logs\` | Launch logs. |
| `games\<id>\settings.json` | Values entered in **Game settings…** (for example your online ID). |
| `locations.json`, `covers\` | Located game folders and custom covers. |

**Remove downloaded files…** deletes only `packages\`. Saves and settings stay.

### Feed format

```json
{
  "schema_version": 1,
  "game": "nhl17",
  "release": "0.1.0",
  "prerelease": false,
  "notes_url": "https://github.com/CrossCrease/nhl17/releases/tag/v0.1.0",
  "minimum_launcher": "0.4.0",
  "packages": [
    {
      "id": "runtime",
      "name": "NHL 17 runtime",
      "version": "0.1.0",
      "url": "crosscrease-nhl17-runtime-0.1.0-windows-x64.zip",
      "size": 31299578,
      "sha256": "…64 hex characters…"
    }
  ]
}
```

Package `url`s may be relative to the feed's final address (after redirects). Package ids and versions may contain only letters, digits, `.`, `_` and `-`. Packages are ZIP files and are extracted as-is into their version folder.

### Testing with a local feed

Until a release is published, or to test a new build, point a game (or `"launcher"`) at another feed in `%LOCALAPPDATA%\CrossCrease\feeds.json`:

```json
{ "nhl17": "file:///C:/path/to/nhl17/artifacts/release/0.1.0/crosscrease-nhl17-feed.json" }
```

Delete the file to go back to the published feeds. For automated tests, the environment variable `CROSSCREASE_DATA_DIRECTORY` moves all launcher data to another folder. Normal users never need either.

## Included profiles

- NHL 17: supported. Downloads the CrossCrease NHL 17 runtime 0.2.0 or newer, which includes the online connection files. **Play** connects to the CrossCrease server when the game update is on, and starts offline with the base game.
- NHL 15, 16, 18–27: planned (`"status": "planned"`), shown as "Coming soon".

## Cover art

The launcher's colours follow the selected cover. CrossCrease finds the main colour of the full-colour cover art: it detects and skips the PS4/PS5 platform banner and the rating strip, and gives the most weight to saturated colours near the centre, which is usually the jersey. The top bar, selection bar, library background, logo, Play button and selection ring are all tinted with that colour. This applies to every game, including greyed-out ones. Hovering a cover lifts it with a soft glow in its own colour. Profiles without a cover image use their `accent` colour.

### Official covers

Profiles can name where the game's official cover is published (`cover_download`). The launcher then downloads it **on your PC** at startup and keeps it in `%LOCALAPPDATA%\CrossCrease\covers\downloaded\`. No publisher artwork is part of the source code or any CrossCrease release.

- The included NHL profiles use the cover image from each game's English Wikipedia article. Downloads are spaced out to respect Wikimedia's rate limits, and each file is checked against the SHA-1 checksum Wikipedia publishes for it.
- The launcher checks for changed covers at most once a day, and immediately when a cover is missing.
- A cover you chose yourself always takes priority over a downloaded one. **Reset cover** switches back to the downloaded cover.
- To turn the downloads off, create `%LOCALAPPDATA%\CrossCrease\launcher.json` containing `{ "download_covers": false }`.
- Wikipedia's covers are the standard key art, at about 270×370 pixels. Some show a different edition than your PS4/PS5 box. Choose your own image for a sharper or platform-specific cover.

Without any cover, CrossCrease generates a branded one from the game name, platform, and `accent` colour.

To use your own image, right-click a cover and choose **Choose cover image…**. The image is copied to `%LOCALAPPDATA%\CrossCrease\covers\<profile id>.<ext>`, so it survives launcher updates. **Reset cover image** removes it. A profile can also point to a bundled image with the `cover` field. Portrait images look best. Box art that is wider than the 2:3 card (most console case scans) is fitted to the card width rather than cropped, and the strip below it is filled with a blurred extension of the cover.

Official publisher box art may be used this way as local user data on your own PC, but it must never be committed to the repository or included in a release.

## Build and release

```powershell
.\build.ps1      # dist\CrossCrease.exe plus profiles, README and CHANGELOG
.\package.ps1    # artifacts\<version>\: release ZIP, crosscrease-launcher-feed.json, SHA256SUMS.txt
```

Both use the .NET Framework 4 `csc.exe` that ships with Windows; nothing else needs to be installed. The version comes from `BuildInfo.Version` in `CrossCrease.cs` (keep [CHANGELOG.md](CHANGELOG.md) in sync). To publish, create GitHub release `v<version>` in `CrossCrease/launcher` and upload the three files from `artifacts\<version>\` unchanged. Mark prereleases as such on GitHub: `releases/latest` ignores them, so testers can use a `feeds.json` entry instead.

## Profile format

```json
{
  "id": "example",
  "name": "Example Game",
  "system": "PlayStation 4",
  "status": "supported",
  "description": "Short profile description.",
  "package_feed": "https://github.com/CrossCrease/example/releases/latest/download/crosscrease-example-feed.json",
  "packages": ["runtime"],
  "executable": "{package:runtime}\\bin\\emulator.exe",
  "arguments_template": "--game \"{required_file}\"",
  "working_directory": "{data}",
  "required_file_relative": "eboot.bin",
  "search_roots": ["%USERPROFILE%\\Documents\\CrossCrease\\Example", "{launcher}\\games\\example"],
  "minimum_versions": { "runtime": "1.0.0" },
  "settings": [
    { "id": "online_id", "label": "Online ID", "description": "3 to 16 characters.", "pattern": "^[a-zA-Z0-9_-]{3,16}$" }
  ],
  "play_settings": ["online_id"],
  "launch_options": [
    {
      "id": "safe", "label": "Safe mode",
      "arguments_template": "--game \"{required_file}\" --safe"
    }
  ],
  "cover": "covers\\example.png",
  "accent": "#27C9B3"
}
```

- `id` must stay stable. Saved locations, custom covers, downloads, saves and settings are keyed by it.
- `status` is optional. Omit it or use `"supported"` for a launchable profile. `"planned"` adds a greyed-out "Coming soon" entry, and then only `id`, `name`, `system`, `description`, and `accent` are needed.
- `system` is shown above the title and sets the platform badge on the generated cover (`PS4`, `PS5`, `PC`, and so on).
- `executable` / `executable_relative`, `required_file` / `required_file_relative` and `working_directory` / `working_directory_relative` describe how to validate and start the game. The `_relative` forms are relative to the game folder (the found or located root).
- `search_roots` are tried automatically, then the user's saved **Locate game** choice. A saved choice that no longer matches the profile is ignored, so changing a profile's layout falls back to discovery.
- `package_feed` (optional) is the feed URL. `packages` lists the package ids that must be installed before **Play** is offered.
- `package_install` (optional) lets players install the game from their own package files: `{ "format": "ps4_pkg", "title_ids": ["CUSA04326"], "folder": "%USERPROFILE%\\Documents\\CrossCrease\\NHL 17" }`. `title_ids` limits which games are accepted (empty accepts any), and `folder` is the suggested install location (`{launcher}` and environment variables work). The game is unpacked into `<folder>\<title id>` and its update into `<folder>\<title id>-UPDATE`, and the game's location is saved for the profile.
- `minimum_versions` (optional) maps a package id to the oldest installed version the profile can start with. An older installed version counts as not installed, so the game shows **Set up** and offers the current download (saves and settings are kept). Use it when a profile starts relying on files that only newer packages contain.
- `launch_options` (optional) add buttons next to Play. Each may list its own `packages` and `settings`, and may override `executable`, `arguments_template` and `working_directory`.
- `settings` (optional) are values asked for once in a small dialog and saved per user. `play_settings` lists the ones **Play** asks for before the first launch; a launch option asks for its own `settings`. `pattern` is a regular expression the value must match; values can never contain quotes. `default` pre-fills the field.
- `toggles` (optional) are per-game on/off choices, shown in **Game settings** and the right-click menu. Each has an `id`, `label`, `description` and `default`. `arguments_on` / `arguments_off` are appended to the launch command. `launches` lists the launches it applies to (`"play"` is the main Play button; other values are `launch_options` ids). `"requires": "game_update"` only offers the toggle when an update folder (`<game folder>-UPDATE` or `-patch`) exists. NHL 17 uses this for **Start with the game update** (off adds `-BaseOnly`).
- Paths and argument templates support Windows environment variables and these tokens:
  - `{launcher}`: the launcher folder.
  - `{data}`: the game's per-user data folder.
  - `{package:<id>}`: an installed package's folder. A path that uses a missing package counts as not found.
  - `{setting:<id>}`: a saved setting.
  - In arguments only: `{root}`, `{required_file}` and `{executable_dir}`.
- `cover` is optional and is resolved relative to the profile file. It may also use environment variables or `{launcher}`.
- `cover_download` (optional) names the official cover to download on the player's PC:
  - `{ "source": "wikipedia", "file": "NHL 17 cover art.jpg" }` for an English Wikipedia file
  - `{ "source": "url", "url": "https://…" }` for a direct HTTPS image address

  Cover priority is: the player's own cover, then the downloaded cover, then `cover`, then the generated cover.
- Covers are sorted by name in natural order (NHL 15, NHL 16, … NHL 27).

Use **Import profile** to copy another JSON profile into the launcher, or place JSON files directly in the `profiles` folder and press **Refresh**.

## Troubleshooting

- **"This package is encrypted for a PlayStation 4 console":** the `.pkg` comes from the PlayStation Store and only works on the console it was bought for. Use a package made from your own game for PC or homebrew use.
- **"Not enough space" when installing from .pkg:** the unpacked game needs about as much space as the `.pkg` files. Choose a folder on another drive, or free up space.
- **"No download has been published for this game yet":** the game project has no release with a feed yet. Developers can test with a local feed (see above).
- **"Could not reach the download server":** check your internet connection, then press **Refresh**. Already-installed games still start.
- **"Failed its integrity check":** the download was damaged or changed. Nothing was installed; try again.
- **A game still says Set up:** select it and press the button in the bar. It names the step that is missing (your game files or the runtime download).
- **The game starts without its update:** the update must sit next to the base game folder, with the same name plus `-UPDATE`, and **Start with the game update** must be on in **Game settings**.
- **A custom cover does not appear:** make sure it is a PNG, JPG, or BMP file, then use **Refresh**.
- **No covers appear:** check that `profiles\*.json` sits next to `CrossCrease.exe` and contains valid JSON.

## Attribution

Installing from `.pkg` files uses CrossCrease's own C# reader for the PS4 package (PKG) and PlayStation file system (PFS/PFSC) formats. It follows the publicly documented formats and the behaviour of earlier shadPS4 releases (GPL-2.0-or-later). The two RSA keysets in `Ps4Package.cs` are the publicly known "fake PKG" keysets that open-source PS4 tools such as LibOrbisPkg and shadPS4 (up to 0.6.0) also publish. They only open packages built with them. CrossCrease contains no console keys or game keys.
