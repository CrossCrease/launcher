# CrossCrease Launcher

The CrossCrease launcher is a small Windows app that shows every CrossCrease game profile as a cover in one library. It downloads and updates the CrossCrease runtime each game needs, finds your own copy of the game, and starts it. Games that are ready appear in full colour. Games that still need a download, have not been found, or are planned for a future release stay visible in greyscale.

## Getting a game running

1. **Select** the game's cover. The bar above the library shows what is needed next.
2. **Download** installs the CrossCrease runtime for that game (for NHL 17: the adjusted shadPS4 build, its default configuration and launch scripts). The button shows the download size. Files are checked against a SHA-256 checksum before they are installed.
3. **Locate game** appears if CrossCrease cannot find your copy of the game. Choose either:
   - **Select eboot.bin…**: point directly at the game's `eboot.bin`. The folder that contains it becomes the game folder.
   - **Select game folder…**: choose the game folder, or a folder up to three levels above it (for example a "Games" folder). CrossCrease finds the game inside.

   The choice is saved per game. Change it later in **Game settings**. **Use automatic detection** forgets it again.

   Updates must sit next to the base game in a folder with the same name plus `-UPDATE` (or `-patch`), for example `CUSA04326` and `CUSA04326-UPDATE`. shadPS4 applies them from there. If you pick the update folder's `eboot.bin`, CrossCrease uses the base game next to it. The selection bar shows the detected title ID and version, for example "CUSA04326 · version 01.08 (update installed)".

   Installing from `.pkg` files is not supported yet. Unpack them with your own tools first.
4. **Play** starts the game. Extra ways to start a game, such as **Play online**, appear next to Play. If they need more files, the launcher offers to download them first, and it asks once for any details they need (such as your online ID).

CrossCrease never downloads or distributes game files, firmware, keys or saves. You need your own copy of each game.

## Using the library

- **Play** (or double-click / press Enter on a cover) starts a ready game.
- **Game settings** sits next to Play in the selection bar for every game, and is the first entry when you right-click a cover. It has everything you can change for that game:
  - **Game files**: where your copy is, which version and update were detected, **Select eboot.bin…**, **Select folder…**, **Use automatic detection**, **Open folder**.
  - **CrossCrease downloads**: installed and available versions, **Download** / **Update**, **Check for updates**, **Remove downloads…**, **Open saves & data folder**.
  - **Player settings**: values such as your online ID and server, with **Save player settings**.
  - **Cover**: **Choose image…** and **Reset cover**.
- **Right-click** a cover for quick actions: game settings, play, locate game, open the game folder, check for updates, remove downloads, and the cover image.
- Arrow keys move through the library. **F5** or **Refresh** rescans the profiles folder and checks for updates.

Cover states:

| State | Look | Meaning |
| --- | --- | --- |
| Ready to play | Colour, green dot | The game, its required files and its downloads are present. |
| Update available | Colour, blue dot | A newer runtime or online package is available. Select the game and choose **Update**. |
| Download needed | Greyscale, blue dot | The CrossCrease runtime for this game is not installed yet. |
| Not found | Greyscale, amber dot | The game itself has not been found or located yet. |
| Coming soon | Greyscale | The title is on the CrossCrease roadmap but not supported yet. |

## Downloads and updates

Each game project publishes its packages as GitHub release assets together with a small **feed** file that lists them. Feeds use stable `releases/latest/download/...` addresses, so the launcher always sees the newest release.

- **At startup** (and on **Refresh**) the launcher reads the feed of every game that has one, plus the launcher's own feed. This happens in the background and never blocks the library. Without an internet connection the launcher works as before; affected games say that the download server could not be reached.
- **Updates** are offered on the game's cover and in the selection bar. Updating keeps the previous version on disk for rollback and removes anything older.
- **Launcher updates** show a **Get launcher x.y.z** button in the top bar that opens the release page.
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

- NHL 17: supported. Downloads the CrossCrease NHL 17 runtime; **Play online** adds the online connection files.
- NHL 24: supported profile (needs a local NHL 24 build).
- NHL 15, 16, 18–23, 25, 26, 27: planned (`"status": "planned"`).

## Cover art

The launcher's colours follow the selected cover. CrossCrease finds the main colour of the full-colour cover art: it detects and skips the PS4/PS5 platform banner and the rating strip, and gives the most weight to saturated colours near the centre, which is usually the jersey. The top bar, selection bar, library background, logo, Play button and selection ring are all tinted with that colour. This applies to every game, including greyed-out ones. Hovering a cover lifts it with a soft glow in its own colour. Profiles without a cover image use their `accent` colour.

CrossCrease generates a branded cover for every profile from the game name, platform, and `accent` colour. No publisher artwork is shipped.

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
  "settings": [
    { "id": "online_id", "label": "Online ID", "description": "3 to 16 characters.", "pattern": "^[a-zA-Z0-9_-]{3,16}$" }
  ],
  "launch_options": [
    {
      "id": "online", "label": "Play online",
      "packages": ["runtime", "online"], "settings": ["online_id"],
      "arguments_template": "--game \"{required_file}\" --online \"{package:online}\" --id \"{setting:online_id}\""
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
- `launch_options` (optional) add buttons next to Play. Each may list its own `packages` and `settings`, and may override `executable`, `arguments_template` and `working_directory`.
- `settings` (optional) are values asked for once in a small dialog and saved per user. `pattern` is a regular expression the value must match; values can never contain quotes. `default` pre-fills the field.
- Paths and argument templates support Windows environment variables and these tokens:
  - `{launcher}`: the launcher folder.
  - `{data}`: the game's per-user data folder.
  - `{package:<id>}`: an installed package's folder. A path that uses a missing package counts as not found.
  - `{setting:<id>}`: a saved setting.
  - In arguments only: `{root}`, `{required_file}` and `{executable_dir}`.
- `cover` is optional and is resolved relative to the profile file. It may also use environment variables or `{launcher}`.
- Covers are sorted by name in natural order (NHL 15, NHL 16, … NHL 27).

Use **Import profile** to copy another JSON profile into the launcher, or place JSON files directly in the `profiles` folder and press **Refresh**.

## Troubleshooting

- **"No download has been published for this game yet":** the game project has no release with a feed yet. Developers can test with a local feed (see above).
- **"Could not reach the download server":** check your internet connection, then press **Refresh**. Already-installed games still start.
- **"Failed its integrity check":** the download was damaged or changed. Nothing was installed; try again.
- **A game stays grey after installing it:** select it, open **Game settings**, and choose **Select eboot.bin…** (or **Select folder…**).
- **The selection bar says "no update found":** move the update next to the base game folder and give it the same name plus `-UPDATE`.
- **A custom cover does not appear:** make sure it is a PNG, JPG, or BMP file, then use **Refresh**.
- **No covers appear:** check that `profiles\*.json` sits next to `CrossCrease.exe` and contains valid JSON.
