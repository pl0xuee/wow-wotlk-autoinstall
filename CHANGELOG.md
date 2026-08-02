# Changelog

## v0.1.11

- **A fresh install needs nothing typed in.** The realm now defaults to `209.25.140.23:1170`
  rather than `127.0.0.1`, and the build ships the Google Drive id of the client zip rather than
  leaving it blank, so the first run is one press of INSTALL EVERYTHING. Both boxes still take
  anything else — the Install and Settings pages are unchanged apart from their starting values.
- **Both of those put something real into a public repository**, which is the price of the line
  above. The realm address names a reachable server, and the Drive id is one share link handed to
  every copy of the app — Google's download quota is per file, so that daily allowance is now
  shared between all of them rather than one each. When it runs out, Drive answers with an HTML
  page and a 200; the downloader still catches that and says so, and the Zip-on-disk source is
  the way through.
- **DungeonClear added to the catalog**
  ([jrad7/mod-dungeon-clear-addon](https://github.com/jrad7/mod-dungeon-clear-addon)), and ticked
  by default in the one-click install. It is the in-game front end for the `mod-dungeon-clear`
  server module: On, Off, Skip and Pause for a playerbots tank bot clearing a dungeon on its own,
  a live status line for what the tank is doing, and a Go button on each boss. It needs that
  module on the server and a tank bot in the party — untick it if your realm doesn't run it. The
  repo holds the addon's files at its root rather than in a `DungeonClear/` folder, which would
  install under the wrong folder name by hand; the installer names the folder from the `.toc`, so
  it lands as `Interface/AddOns/DungeonClear` and the client loads it.

## v0.1.10

- **Details! Damage Meter added to the catalog**
  ([Kowson/Details-Damage-Meter-for-3.3.5a](https://github.com/Kowson/Details-Damage-Meter-for-3.3.5a)),
  and it is now the damage meter the one-click install ticks by default. It installs ten folders
  — the meter plus its encounter, threat and per-raid info modules.
- **Skada is no longer a default.** It stays in the catalog and installs in one click. Both read
  the same combat log, and running two meters at once is duplicated work for one answer.
- **ElvUI AddOnSkins added to the catalog**
  ([ElvUI-WotLK/ElvUI_AddOnSkins](https://github.com/ElvUI-WotLK/ElvUI_AddOnSkins)), which
  restyles other addons to match ElvUI. Not pre-ticked, and for a different reason than the rest
  of the untickeds: it is additive, but it does nothing without ElvUI, which is not pre-ticked
  either.
- **The private realm address is out of the repository.** A real server's IP and port sat in the
  v0.1.3 changelog entry and in the realmlist tests as sample data. Both now use `203.0.113.10`,
  which RFC 5737 reserves for documentation. It remains in the git history and in the published
  v0.1.3 release notes; removing it from those would need a history rewrite.

## v0.1.9

- **A software cursor by default.** `gxCursor "0"`, because everything this installs runs under
  Proton and the hardware cursor is the long-standing cause of cursor lag and a pointer that does
  not line up with what it clicks. Left alone, every user finds the same fix for themselves.
- **4x anti-aliasing by default.** `gxMultisample "4"`. The client's own Ultra preset never sets
  it — anti-aliasing lives on the resolution panel rather than the effects panel, so
  `GraphicsQualityLevels.lua` does not describe it — and it costs a 2010 D3D9 game nothing on
  hardware that can run this installer.
- Both apply only to a client nobody has played, on the same terms as the graphics preset: an
  install with settings of its own keeps them.

## v0.1.8

- **The chosen resolution now actually reaches the game.** It was written into `WTF/Config.wtf`
  correctly but the client discarded it on the first launch: with no `hwDetect` directive in the
  file the client treats it as `1` and runs its hardware detection, which resets the whole video
  block — resolution included — before it ever sets a mode. An install told to use 3440x1440
  opened at 1024x768. Writing `hwDetect "0"` alongside the resolution is what makes it stick.
- **A client nobody has played also gets the game's Ultra graphics preset.** Suppressing that
  detection means suppressing what would otherwise have chosen the quality settings, so leaving
  them out would trade a first launch at the wrong resolution for one at the client's built-in
  defaults. The values are the client's own — `GraphicsQualityLevels.lua` in its `Interface` MPQ
  defines six levels and level 5 is the one labelled `--ULTRA mode`.
- **An install someone has already played keeps its graphics settings.** The client writes
  `hwDetect "0"` itself once it has run, which is the one durable record that a config is the
  user's own work rather than ours to furnish. Reinstalling still fixes the resolution.

## v0.1.7

- **Game patches on the Addons tab.** A GAME PATCHES section with Install, Reinstall and Remove
  per patch, so a patch can be added to a client that is already installed without re-running
  the whole install.
- A patch recorded as installed whose file is not actually in the client is shown as missing and
  offers **Restore**, rather than claiming to be installed. The two disagree whenever a client is
  reinstalled or a file is deleted by hand, and calling that "installed" leaves no way back.

## v0.1.6

- **Game patches.** A new GAME PATCHES section on the Install page, installed as part of the
  one-click run, starting with **Dungeon Maps (WDM)**
  ([Trimitor/WDM-patch](https://github.com/Trimitor/WDM-patch)) — 56 Classic and Burning Crusade
  dungeon, continent and battleground maps made to work in Wrath.
- These are MPQ files rather than addons, so they are handled as their own thing: they go into
  `Data/<locale>` rather than `Interface/AddOns`, the file to fetch depends on the client's
  language, and removing one deletes a file rather than a folder. The locale is read from the
  client's own `Data` folder — the wrong file is not a broken install, it is a silently absent
  one.
- **A patch already using the same name is kept, not overwritten.** Two patches cannot share a
  letter; the engine loads one and silently ignores the other, and the symptom turns up much
  later as missing models. The displaced file is set aside and put back if the patch is removed.
- The download is staged beside its target and renamed on completion, so an interrupted install
  never leaves a half-written MPQ where the engine will try to read it.

## v0.1.5

- **Resolution picker.** The Install page now has a DISPLAY section listing the resolutions your
  monitors actually report, with the primary display's native mode selected by default and a
  windowed toggle. It is written to `WTF/Config.wtf` during the install, so the first launch is
  already the right size — a client with no config starts at 800x600, and fixing that from the
  in-game menu means finding the menu at 800x600 first.
- Modes come from `/sys/class/drm`, so the list is what the panels support rather than the
  current desktop layout, and it works under Wayland with no external tool. Only real modes are
  offered: a 3.3.5a client asked for one its monitor cannot show starts to a black screen.
- Every other directive in `Config.wtf` is preserved — that file accumulates every option-menu
  change a player has ever made.
- A machine whose displays cannot be read leaves the client's own default alone rather than
  guessing.

## v0.1.4

- **Zygor Guides Viewer added to the catalog** ([ErebusAres/ZygorGuidesRemaster-3.3.5a_WOTLK](https://github.com/ErebusAres/ZygorGuidesRemaster-3.3.5a_WOTLK)),
  and it is now the levelling guide the one-click install ticks by default.
- **Questie is no longer a default.** It stays in the catalog and installs in one click — both
  addons do the same job, and having two guides on at once is redundancy nobody asked for.
  Tick it instead if you prefer map markers to a step-by-step guide.

## v0.1.3

- **The realmlist no longer loses the port.** `203.0.113.10:1170` was written as
  `203.0.113.10`, so the client fell back to the default 3724 and silently failed to connect —
  a client that looks correctly configured and simply doesn't work. A port is part of a
  realmlist entry; only a scheme and a trailing path are stripped now.

## v0.1.2

- **Updating now restarts the app into the new version.** It used to swap the AppImage and
  tell you to close and reopen it. It starts the replacement first and shuts down only once
  that has actually started, so a failed launch leaves the running app alone rather than
  closing it with nothing to replace it. The new process gets a clean environment — `APPIMAGE`,
  `APPDIR` and `LD_LIBRARY_PATH` all describe the image the old process has mounted, and that
  mount disappears the moment it exits.

## v0.1.1

### One-click install

**INSTALL EVERYTHING** now runs the whole thing: acquire the client, unpack it, write the
realmlist, install addons, and add the shortcut to Steam. The phase track gained an ADDONS
segment, and the Install page shows exactly which addons will be installed with a tick box for
each. Seven additive ones are pre-ticked; ElvUI, Dominos, Immersion, Grid2 and CartoMapper are
not, because they replace core parts of the interface and two of them conflict with each other.
An addon that can't be fetched is skipped and reported — it never fails the install.

### Data-loss fixes

- **Steam's `config.vdf` could lose unrelated blocks.** Setting the compatibility tool ran an
  unanchored search for the appid across the whole file, so it deleted any other section keyed
  by the same non-Steam appid — `ShaderCacheManager` on a stock install. All edits are now
  scoped to the `CompatToolMapping` block by brace matching.
- **A second addon could delete the first.** The destination folder took its name from the
  extractor's scratch directory, so any zip with its `.toc` at the archive root installed to
  `Interface/AddOns/unpacked` — a folder the client never loads — and the next such zip
  overwrote it. The name now comes from the `.toc`, which is what the client matches on.
- **Addon records could collide and stop the app from starting.** Catalog ids and folder names
  shared one namespace, so two records could claim the same folder; the Addons page indexed
  folders by name and threw during startup, permanently, because the record file persisted.
- An addon update that dropped or renamed a folder left the old one on disk, loaded by the
  client at the old version and beyond the reach of Remove.
- A failure partway through replacing a multi-folder addon is now rolled back rather than left
  half-applied.
- Steam shortcut artwork and launch options a user had set by hand are no longer overwritten.

### Correctness fixes

- Only the first `set realmlist` line was replaced, but WoW honours the last — a client with two
  kept dialling the old server while the app reported success.
- A backup client deeper in the install folder could beat the real one, and then had its `Cache`
  deleted. The search is breadth-first now, so the shallowest client wins.
- A single unrelated file in the download folder downgraded a hard disk-space failure to a
  warning, letting an install start that then ran the disk dry. Space is measured in bytes
  actually present now, not "is anything there".
- A partial download longer than the real file was a permanent dead end (repeated HTTP 416); a
  complete one at exactly the expected size was deleted and fetched again.
- A zip you had already downloaded was deleted before the replacement was known to be fetchable.
- Directory entries stored without a trailing slash became files and wedged the extract;
  backslash-separated entries from Windows zip tools extracted as one flat filename.
- Valve's own Proton builds were undiscoverable, so a machine with Proton Experimental and no
  GE build failed the Steam phase for want of a Proton it had.
- `pkill steam` matched any process with "steam" in its name — `steamcmd`, `steamtinkerlaunch`.
  Anchored now.
- Quitting Steam and immediately running setup could have the dying process overwrite the
  freshly written shortcut.
- A failed Steam step left Steam shut down and never restarted. Prefix creation is no longer
  fatal — Steam builds one on first launch anyway.
- A zero-byte `shortcuts.vdf` bricked the whole Steam feature.
- One throwing event subscriber could wedge the operation runner for the life of the process,
  refusing every later operation.

### Interface fixes

- The Install page froze during extraction — progress was posted to the UI thread once per
  file, with input gaps up to 5.7 seconds. Sampled now, as the status bar already was.
- The Install page could write its own stale folder and realm back over changes made on the
  Settings page, sending the client to the wrong folder and the wrong realm.
- Buttons stayed enabled while another operation was running, and the rejected click did nothing
  at all — no message, no log line.
- A failed install left the page reading "Starting…" over a half-lit phase track.
- The Addons page's status line was cleared a moment after being set, so it never displayed.

### Build

- `appimagetool` pinned to 1.9.1 instead of the rolling `continuous` build, and the checksum is
  now verified against a cached copy too rather than only on download.
- The AppImage runtime — the code that actually executes on a user's machine — is pinned and
  checksummed instead of fetched unverified at build time.
- Release artifacts carry build provenance attestation, verifiable with `gh attestation verify`.
- `X-AppImage-Version` is stamped from the build version rather than hardcoded.

## v0.1.0

First release.

- Download the 3.3.5a client from Google Drive with resume, or use a zip or unpacked folder you
  already have
- Preflight checks for Steam, Proton and disk space before anything is downloaded
- `set realmlist` written to every `realmlist.wtf` the client has, with the client `Cache` cleared
  so the change takes effect
- Addons tab: curated 3.3.5a catalog with install and update, install from `.zip` or URL, and
  enable/disable/remove for what's installed
- Steam tab: non-Steam shortcut to `Wow.exe`, Proton assignment, prefix creation and library artwork
