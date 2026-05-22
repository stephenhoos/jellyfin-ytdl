# Jellyfin YtdlArchive

Public beta: `0.0.0.1`

Chrome extension plus Jellyfin plugin for adding a download button to YouTube
pages. The Jellyfin plugin hosts a token-gated local downloader server at
`http://localhost:9876`, so the Chrome extension does not need a separate
terminal process. Downloads are saved in a Jellyfin-friendly archive shape with
yt-dlp sidecar metadata and thumbnails.

## Contents

- `extension/` - unpacked Chrome extension source.
- `src/Jellyfin.Plugin.YtdlArchive/` - Jellyfin plugin source, including the
  embedded downloader server.
- `yt-downloader-server.py` - standalone development fallback for the downloader
  server. Normal installs use the Jellyfin plugin instead.

## Run

Install and start the Jellyfin plugin. It starts the downloader server
automatically when Jellyfin starts.

The embedded server listens on `http://localhost:9876`.
On startup, the plugin installs or updates its own managed `yt-dlp` binary under
the Jellyfin plugin data folder and uses that copy for downloads. You can turn
auto-install/update off or set a custom executable path from the plugin
configuration page.

Default Jellyfin libraries:

```text
YT-Music
YT-Podcast
YT-Audiobooks
YT-Other
```

Default download folders are configured from the plugin settings page. The
browser-based directory picker can create folders inside the configured archive
roots or their parent media folder.

```text
~/Music/YT-Music/
~/Music/YT-Podcast/
~/Music/YT-Audiobooks/
~/Downloads/YT-Other/
```

Each download writes:

```text
Channel Name [CHANNEL_ID]/
  YYYY-MM-DD - Video Title [VIDEO_ID].mp4
  YYYY-MM-DD - Video Title [VIDEO_ID].info.json
  YYYY-MM-DD - Video Title [VIDEO_ID].webp
```

That local `.info.json` file is the primary metadata source for the Jellyfin
plugin work. Jellyfin should be able to scan the media folder without calling
YouTube again when these sidecars are present.

## Jellyfin Libraries

The plugin can create and refresh these libraries on startup or when settings
are saved:

```text
YT-Music       music
YT-Podcast     music
YT-Audiobooks  books
YT-Other       tvshows
```

Downloads trigger a Jellyfin library refresh after they finish.

The standalone `yt-downloader-server.py` remains as a development fallback, but
normal installs use the embedded Jellyfin service. The fallback now requires
`YTDL_BROWSER_API_TOKEN` before it will start.

## Jellyfin Plugin

Build a testable plugin package:

```bash
./build_plugin.sh
```

The script runs the Python syntax check, Release tests, publishes the plugin,
validates that Jellyfin host assemblies are not bundled, and writes:

```text
dist/YtdlArchive/
dist/package/YtdlArchive-0.0.0.1.zip
```

For a manual smoke test, create a `YtdlArchive` folder under your Jellyfin
plugin directory, copy the contents of `dist/YtdlArchive/` into it, and restart
Jellyfin. Then enable the `YtdlArchive` metadata provider on the `YouTube`
library and scan.

## Load In Chrome

1. Open `chrome://extensions`.
2. Enable Developer mode.
3. Click Load unpacked.
4. Select the `extension/` folder in this project.

Then paste the Browser API token from the Jellyfin plugin settings into the
extension popup. Open a YouTube video and use the red `DOWNLOAD` button in the
player. The picker fetches its save types from the Jellyfin plugin, so plugin
updates can change the Chrome menu without editing the extension.

Current save targets include video downloads at best, 1080p, 720p, or 480p;
music and podcast audio as MP3, M4A, or Opus; and audiobook saves as M4A or M4B
with optional rough 10% or 20% chapter breaks.

## Security Notes

- The local downloader API requires the Browser API token.
- Downloads are restricted to YouTube hosts by default.
- Managed `yt-dlp` downloads are checked against the published SHA-256 sums.
- Folder creation is restricted to the configured archive roots and their parent
  media folder.

## Quality Status

The project is scanned with SonarQube Cloud on pushes and pull requests. As of
the latest `main` scan, the SonarQube workflow passes and all imported
SonarQube issues have been resolved.
