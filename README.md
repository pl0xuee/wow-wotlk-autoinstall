<p align="center">
  <img src="packaging/icon-256.png" width="110" alt="WotLK Autoinstall icon" />
</p>

# WotLK Autoinstall

One-click installer for a **World of Warcraft 3.3.5a** client on Linux, for playing on your own
local server.

Pick a folder, set the realm address, click **INSTALL EVERYTHING**, and walk away. One button
does the whole thing — client, realmlist, addons, Steam shortcut:

- **The client** — downloads your 3.3.5a zip from Google Drive (resumable, so a dropped
  connection costs you nothing), unpacks it, and finds `Wow.exe` wherever the archive put it.
  Already have the zip, or an unpacked client? Point at either instead and skip the download.
- **The display** — pick from the resolutions your monitors actually report, fullscreen or
  windowed, written into `WTF/Config.wtf` so the first launch is the right size instead of
  800x600.
- **The realm** — writes `set realmlist <your address>` into every `realmlist.wtf` the client has,
  not just one, and clears the client `Cache` folder so the new realm actually takes effect.
- **Addons** — a curated catalog of 3.3.5a addons with one-click install and update, plus
  install-from-`.zip` and install-from-URL for anything else. Enable, disable and remove what's
  installed, with a warning on any addon whose `## Interface` isn't `30300`. The Install page
  lists the whole catalog with a tick box each: seven additive addons are pre-ticked, and ElvUI,
  Dominos, Immersion, Grid2 and CartoMapper are not, because they replace core parts of the
  interface and some of them conflict with each other. An addon that can't be fetched is skipped
  and reported — it never fails the install.
- **Game patches** — MPQ patches installed into `Data/<locale>` for the client's own language,
  starting with [WDM Dungeon Maps](https://github.com/Trimitor/WDM-patch). A patch already using
  the same file name is kept rather than overwritten.
- **Steam** — adds a **World of Warcraft 3.3.5a** shortcut pointing at `Wow.exe`, assigns a Proton
  build, creates the prefix up front so your first Play click goes straight to the login screen,
  and installs library artwork.

This installs the **game client only**. For running the server itself, see
[AzerothCore Control](https://github.com/pl0xuee/AzerothCore-Control).

## Requirements

- Native Steam (not Flatpak or Snap)
- A Proton build in `compatibilitytools.d` — GE-Proton is preferred; install one with
  [ProtonUp-Qt](https://davidotek.github.io/protonup-qt/)
- ~18 GB free for the download and ~27 GB for the install (the app checks, and adds the two
  budgets together when both folders are on the same drive)
- A running 3.3.5a server to connect to — AzerothCore or TrinityCore

No protontricks, no winetricks, no prefix components: a stock 3.3.5a client needs none of them.

## Install

Grab the AppImage from [Releases](../../releases), make it executable, run it:

```sh
chmod +x WowWotlkAutoinstall-x86_64.AppImage
./WowWotlkAutoinstall-x86_64.AppImage
```

## First run

**This app ships no game client and no link to one.** Bring your own 3.3.5a client, then pick a
source on the Install page:

- **Zip on disk** or **Folder already unpacked** — point at what you have, and you're done.
- **Google Drive** — put your own share link's file id on the Settings page first. It's the long
  code between `/d/` and `/view`:
  `https://drive.google.com/file/d/`**`<this part>`**`/view`. Set **Zip size, bytes** to match
  your upload, or `0` to skip the size check.

The id is stored in your own `settings.json` and is never part of this repo — a shared public
link burns one daily Google quota between everyone who has it.

## Building from source

```sh
dotnet build
dotnet test
scripts/build-appimage.sh    # produces the AppImage
```

The Steam library artwork and the app icon are generated, not committed by hand:

```sh
scripts/gen-steam-grid.sh    # needs ImageMagick
```

## Where things live

| Path | What |
|---|---|
| `~/.config/wow-wotlk-autoinstall/settings.json` | Install paths, realm address, Drive file id, Proton pin |
| `~/.config/wow-wotlk-autoinstall/installed-addons.json` | Which folders belong to which addon, so update and remove are exact |
| `~/.config/wow-wotlk-autoinstall/logs/` | Full run log — the in-app pane only keeps the last 2000 lines |

## Notes

The Drive link has a per-file daily download quota. When it's exhausted Google answers with an
HTML error page and HTTP 200 rather than an error status; the downloader detects that and tells
you, instead of writing a web page to disk and calling it a game client. If you hit it, download
the zip in a browser and use the **Zip on disk** source.

## Credits

Architecture follows [LoreRim Autoinstall](https://github.com/pl0xuee/lorerim-autoinstall) and the
STALKER GAMMA Linux GUI; the Steam integration is a port of theirs, which in turn follows
[Jackify](https://github.com/Omni-guides/Jackify).

## License

MIT
