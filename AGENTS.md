# Working on this plugin

Notes for whoever (human or agent) picks this up next: how the pieces fit, why they are shaped that way, and the
places that bite. The README is the user-facing side — features, Spotify's 2026 limits, install steps; this file is
the mechanics. Read both before changing something structural.

## What this is

A Stream Deck plugin (Elgato wire protocol, run under [OpenDeck](https://github.com/nekename/OpenDeck) on Linux)
that turns a whole deck into a Spotify picker. One .NET 10 process, launched by OpenDeck with

```
opendeck-spotifymusicpicker -port <N> -pluginUUID <id> -registerEvent registerPlugin -info <json>
```

It connects back to `ws://127.0.0.1:<N>`, registers, and then it is a long-lived event loop. `Program.cs` also has
CLI modes (`--login`, `--dump`, `--probe`, `--play`, `--render`, `--logout`, `--version`) that run without a socket —
use them to reproduce anything Spotify-related without touching the deck.

Everything the plugin writes to stderr lands in
`~/.local/share/opendeck/logs/plugins/com.josbol.spotifymusicpicker.sdPlugin.log`. `SPOTIFYMUSICPICKER_DEBUG=1`
turns on `Log.Debug`. That log is the first place to look when a key misbehaves on the real deck.

## Map

```
src/Opendeck.SpotifyMusicPicker/
  Program.cs      entry point: CLI modes, else wire up DeckClient + Curator + PluginHost and run
  Deck/           DeckClient (WebSocket, send/receive, the setImage/showOk/… wrappers), DeckEvent (parsing)
  Actions/        PluginHost (event routing, rendering, profile switching), DeckAction + one class per action,
                  GlobalSettings (plugin-wide settings, also readable from OpenDeck's settings file)
  Picker/         Curator (all networking, the three rows, playback, volume), PlayHistory, MadeForYou, Recommender,
                  MetadataCache, ArtCache, LocalPlayer (playerctl/MPRIS fallback)
  Spotify/        SpotifyAuth (PKCE + loopback + token file), SpotifyClient (typed calls, 401/429), OEmbedClient,
                  Model (PickItem, PlaybackState, Snapshot…), Paths
  Rendering/      KeyRenderer (SkiaSharp; every key image)
plugin/com.josbol.spotifymusicpicker.sdPlugin/   manifest.json, icons, pi/*.html property inspectors, fonts, bin/
scripts/         build.sh, package.sh, install.sh, install-profile.py, make-icons.py, render-docs.sh
tests/           xunit; FakeSpotify (HTTP) and FakeOpenDeck (WebSocket) make the end-to-end tests hermetic
```

Data on disk: `~/.local/share/opendeck-spotifymusicpicker/` (tokens.json mode 600, history.json, metadata.json),
covers in `~/.cache/opendeck-spotifymusicpicker/art/`. All of it goes through `Spotify/Paths.cs`, which tests
repoint at a temp dir — never build such a path by hand elsewhere.

## The OpenDeck side

**Contexts encode the layout.** An OpenDeck context is `device.profile.controller.position.index`, e.g.
`ulanzi-d200x.Spotify.Encoder.0.0`. So an event names the profile ("layout") its key or dial sits on — that is how
the volume dial's *back* mode knows where to return to, with no extra call to OpenDeck; see
`PluginHost.ProfileFromContext` / `NoteProfile` / `PreviousProfile`. Three rules there:

- profile names may contain dots and spaces (`AI Agents`), so parse by stripping the device prefix and the last
  three segments, not by `Split('.')[1]`;
- **only a press or a turn proves what is on screen.** `willAppear` does not: on a plugin start or
  `--reload-plugin`, OpenDeck announces every instance of *every loaded profile*, whatever the deck is showing
  (`reload_plugin` → `all_from_plugin` in `events/frontend/plugins.rs`); `willDisappear` is sent for the layout
  being *left*. So `NoteProfile` runs for keyDown/keyUp/dialRotate/dialDown/dialUp/touchTap only. Learned the hard
  way — the first version trusted willAppear and came up pointing at the wrong layout after every reload;
- noting happens *before* the event is dispatched to the action, so the press that asks to go back has already
  recorded the layout it was pressed on. That is what makes a switch performed elsewhere (the OpenDeck window, an
  application profile) self-correcting instead of leaving a stale "current".

On a profile switch OpenDeck sends willDisappear for the old profile's instances, then willAppear for the new ones
(`events/frontend/profiles.rs`), and writes `selected_profile` to `~/.config/opendeck/profiles/<device>.json`
immediately — the file is a good cross-check when debugging, though the plugin does not read it.

**Plugins may not switch profiles over the socket.** OpenDeck ignores a `switchProfile` event coming from a plugin.
`PluginHost.SwitchProfileAsync` therefore hands the same JSON to the running instance out of band: first the
single-instance D-Bus hook (`busctl … org.SingleInstance.DBus ExecuteCallback`, ~10 ms), then `opendeck
--process-message` (~300 ms), then the Flatpak variant. Tests replace the whole thing through
`PluginHost.ProfileSwitcher`.

**Key images.** `setImage` carries a PNG (JPEG for photos) data URL. OpenDeck stores the last image with the key and
redraws it itself when a layout comes back, so `PluginHost._sent` keeps what was sent per context *across*
willDisappear/willAppear and `RenderOne` skips an identical image — that is what makes layout switching feel
instant. Renders are coalesced (120 ms) through `RequestRender`, plus a slow `TickLoopAsync` repaint. A browse-dial
tick skips the coalescing: `RenderNow` renders at once and folds ticks that arrive meanwhile into one more pass.
`RenderOne` is serialised and reads `Curator.Current` inside its lock, so a render that waited never paints older
state over a newer image. How fast a scroll then *shows* is the D200X plugin's business: every tick is a ZIP of ten
covers, and its bundle pacing (one bundle per burst, a short gap, JPEG covers) is where the time goes.

**Sizes.** Stock OpenDeck squeezes every key into 144×144 before it reaches a device plugin, so 144 is the native
size here. The one exception is the wide now-playing image, composed at the D200X screen's own 458×196
(`KeyRenderer.NowPlayingWideKey`); the D200X plugin ≥ 1.3 expands it again when its *Wide screen* action is set to
*Action icon* + *Stretch* (what `install-profile.py` writes), and a patched OpenDeck draws it 1:1. Anything that
ticks (elapsed seconds, volume) is deliberately not drawn there: each change repaints the screen and the D200X
firmware flashes its gauges. Hence `Progress.Coarse` by default on wide.

**Actions.** `PluginHost.Create` maps the manifest UUID suffix (`slot`, `nowplaying`, `previous`, `next`, `like`,
`volumedial`, `browsedial`) to a `DeckAction` subclass in `Actions/DeckAction.cs`. An action is one *placed
instance*: it holds its context, device, controller and settings, renders from a `Snapshot`, and handles presses.
`WantsPlayback` (false for the dials) drives `Curator.KeypadVisible`, i.e. the fast vs. idle playback poll.

**Property inspectors** are the plain HTML files in `plugin/…/pi/`, wired by `pi/pi.js`: `bind(id, key, opts)` for a
per-key setting, `{global: true}` for a plugin-wide one, `command(name)` for `sendToPlugin`. The plugin answers with
`status` + `globalSettings` (`PluginHost.Status`, `BroadcastStatus`). OpenDeck's own copy of the global settings
(`didReceiveGlobalSettings`) stays the source of truth; the plugin's echo is only for display.

## The Spotify side

`Curator` owns everything networked, publishes an immutable `Snapshot`, and raises `Changed`; actions only read it.
Three loops (`Curator.Start`), each interruptible by a semaphore so a key press can force a refresh:

| loop | default | what |
|---|---|---|
| lists | 15 min | rebuilds the three rows (`RefreshListsAsync`) |
| playback | 5 s with keys on screen, 60 s idle | `/me/player`, liked state, cover prefetch |
| history | 3 min | `/me/player/recently-played` → local history → re-rank the "most played" row |

Spotify has no play counts and keeps only the last 50 plays, so `PlayHistory` accumulates them locally and ranks
contexts by recency-weighted plays (14-day half-life). `Recommender` picks from a pool built out of saved albums
and top/followed artists — deterministic per (day, reroll) so keys do not shuffle under your fingers. The
made-for-you row leans on the public **oEmbed** endpoint, because the Web API refuses Spotify-owned playlists to
Development Mode apps; see the README table for exactly which endpoints are still open, and run `--probe` against a
real account before assuming an endpoint works.

Watch the request budget: covers cost one request each (the batch endpoints are gone), which is why
`MetadataCache` caches refusals too (`Missing`, 1 day) and volatile Spotify playlists for 2 h. Polling faster than
roughly 13 requests/minute earns 429s.

**Volume dial mechanics** (`Curator.VolumeDelta` / `SendVolumeAsync`) are subtler than they look, and the shape is
deliberate: one request in flight at a time with the newest target winning (a fast spin becomes a handful of
requests), steps build on the last *target* rather than on a lagging poll, `HoldVolume` keeps the dial's value in
the snapshot for `VolumeHold` (5 s) because Spotify reports a just-set volume late, and a 429 puts the Web API in
backoff while MPRIS takes over instead of stalling the dial. If you touch this, keep
`VolumeStepsBuildOnTheLastTargetAndOutrankStalePolls` and `RateLimitedVolumeDoesNotStallTheDial` green.

**Fallbacks.** Every player command tries the Web API first, then `playerctl -p spotify` (`LocalPlayer`) for the
local client. "No active device" (404/403 with `DEVICE` in the error) triggers `ChooseDeviceAsync` and a retry.

**Auth**: authorization code + PKCE, loopback redirect `http://127.0.0.1:<port>/callback` (Spotify rejects
`localhost`). Adding a scope to `SpotifyAuth.Scopes` invalidates nothing automatically — the stored consent keeps
the old scope list, so guard the feature with `Auth.HasScope(...)` and tell the user to press *Connect* again
(`LikeAction` is the worked example).

## Tests

`dotnet test` — unit tests plus two hermetic end-to-end tests that run the real `PluginHost` against `FakeOpenDeck`
(a WebSocket server playing OpenDeck) and `FakeSpotify` (an HTTP server playing the Web API), asserting on actual
rendered pixels. When adding to them:

- wait for the *final* image, not the first: keys render a placeholder before Spotify answers, so a predicate must
  not-match other images rather than throw on them (`Pixel` returns null on an unexpected size for that reason);
- `FakeOpenDeck.WillAppearAsync` accepts any context string. The older test uses short ones (`Encoder.0.0`); use
  full `device.profile.controller.position.index` contexts whenever profile tracking is involved;
- `Scenario` builds the fake account (visible + hidden playlists, plays, saved albums); `TestData` the JSON shapes.

## Build, install, release

```sh
./scripts/build.sh                     # publish linux-x64 into plugin/…/bin (RIDS="linux-x64 linux-arm64" for both)
./scripts/install.sh --link            # symlink into ~/.config/opendeck/plugins (needs OpenDeck developer mode)
./scripts/package.sh                   # dist/com.josbol.spotifymusicpicker.sdPlugin-<version>.zip
./scripts/install-profile.py [--main-dial 0]
```

The version lives in **two** places that must agree: `manifest.json` (`Version`) and the csproj (`<Version>`);
`package.sh` names the zip from the manifest. After reinstalling, restart OpenDeck or
`opendeck --reload-plugin com.josbol.spotifymusicpicker.sdPlugin`.

`install-profile.py` writes `~/.config/opendeck/profiles/<device>/<name>.json` (backing up an existing one). Big
caveat: **OpenDeck keeps loaded profiles in memory and writes them back on exit**, so editing a profile it already
knows (`--main-dial`) means *stop OpenDeck → run the script → start OpenDeck*. A brand-new profile is picked up on
first selection. The device's currently selected profile is in `~/.config/opendeck/profiles/<device>.json`
(`selected_profile`), written immediately on a switch — handy when debugging layout switching.

## Conventions

- C# is dense and comment-light, but every non-obvious decision carries a `///` or a trailing comment saying *why*
  (the API is gone, the screen flashes, the poll lags). Match that: explain the constraint, not the code.
- Prose in comments, docs and the property inspectors is plain and lowercase-ish, in the README's voice. No
  marketing adjectives.
- Commits use a noreply author — no personal e-mail anywhere in this repo or its forks.
- Rendering changes: `./scripts/render-docs.sh` regenerates `docs/keys.png` for the README, and
  `opendeck-spotifymusicpicker --render <dir>` dumps every key style without touching the network.

## If you need OpenDeck's own behaviour

The protocol details above were read off the OpenDeck source (Rust, `src-tauri/src/events/{inbound,outbound,
frontend}`), which the maintainer keeps checked out at `~/source/OpenDeck` on the dev machine. When something about
events, contexts or profiles is unclear, read that rather than guessing from the Elgato docs: OpenDeck implements
the Elgato protocol but not all of it, and the differences (no plugin-initiated `switchProfile`, contexts that carry
the profile, the 144 px render) are exactly where this plugin's odd corners come from.
