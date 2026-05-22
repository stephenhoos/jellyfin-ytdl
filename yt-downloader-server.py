#!/usr/bin/env python3
"""
yt-dlp companion server for the YouTube Download Button extension.
Run this script once; leave it running in the background while you use Chrome.

Usage:
    python3 yt-downloader-server.py

Requirements:
    pip install yt-dlp flask

The server listens on http://localhost:9876 and accepts download requests
from the Chrome extension. Downloads are saved to ~/Downloads/YouTube/.
"""

import os
import sys
import json
import secrets
import shlex
import threading
import subprocess
from pathlib import Path
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib import parse, request, error
from urllib.parse import urlparse

# ─── Configuration ────────────────────────────────────────────────────────────

PORT        = 9876
JSON_CONTENT_TYPE = 'application/json'
DOWNLOAD_DIR = Path(os.environ.get('YTDL_DOWNLOAD_DIR', Path.home() / 'Downloads' / 'YouTube')).expanduser()
MUSIC_DOWNLOAD_DIR = Path(os.environ.get('YTDL_MUSIC_DOWNLOAD_DIR', Path.home() / 'Music' / 'YouTube Music')).expanduser()

JELLYFIN_URL = os.environ.get('JELLYFIN_URL', '').strip().rstrip('/')
JELLYFIN_API_KEY = os.environ.get('JELLYFIN_API_KEY', '').strip()
JELLYFIN_LIBRARY_NAME = os.environ.get('JELLYFIN_LIBRARY_NAME', 'YouTube').strip()
JELLYFIN_LIBRARY_TYPE = os.environ.get('JELLYFIN_LIBRARY_TYPE', 'tvshows').strip()
JELLYFIN_MUSIC_LIBRARY_NAME = os.environ.get('JELLYFIN_MUSIC_LIBRARY_NAME', 'YouTube Music').strip()
JELLYFIN_MUSIC_LIBRARY_TYPE = os.environ.get('JELLYFIN_MUSIC_LIBRARY_TYPE', 'music').strip()
BROWSER_API_TOKEN = os.environ.get('YTDL_BROWSER_API_TOKEN', '').strip()
ALLOWED_DOWNLOAD_HOSTS = {'youtube.com', 'www.youtube.com', 'm.youtube.com', 'music.youtube.com', 'youtu.be'}

# yt-dlp format: best video+audio merged into mp4, fallback to best available
FORMAT = 'bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best'
QUALITY_FORMATS = {
    'best': FORMAT,
    '1080': 'bestvideo[height<=1080][ext=mp4]+bestaudio[ext=m4a]/best[height<=1080][ext=mp4]/best',
    '720': 'bestvideo[height<=720][ext=mp4]+bestaudio[ext=m4a]/best[height<=720][ext=mp4]/best',
    '480': 'bestvideo[height<=480][ext=mp4]+bestaudio[ext=m4a]/best[height<=480][ext=mp4]/best',
    'audio': 'bestaudio/best',
}
SUPPORTED_AUDIO_FORMATS = {'mp3', 'm4a', 'opus'}

# Archive-friendly output for Jellyfin metadata plugins:
#   Channel Name [UCxxxxxxxxxxxxxxxxxxxxxx]/
#     2026-05-21 - Video Title [dQw4w9WgXcQ].mp4
#     2026-05-21 - Video Title [dQw4w9WgXcQ].info.json
#     2026-05-21 - Video Title [dQw4w9WgXcQ].webp
OUTPUT_TEMPLATE = (
    '%(channel,uploader|Unknown_Channel).200B [%(channel_id|unknown-channel)s]/'
    '%(upload_date>%Y-%m-%d,release_date>%Y-%m-%d|NA)s - %(title).200B [%(id)s].%(ext)s'
)

# ─── Ensure download directory exists ────────────────────────────────────────

DOWNLOAD_DIR.mkdir(parents=True, exist_ok=True)
MUSIC_DOWNLOAD_DIR.mkdir(parents=True, exist_ok=True)

# ─── Active downloads tracker ────────────────────────────────────────────────

active = {}   # url -> {'status': ..., 'title': ...}
lock   = threading.Lock()

# ─── Find yt-dlp ─────────────────────────────────────────────────────────────

def find_ytdlp():
    """Return the yt-dlp executable path, or None if not found."""
    import shutil
    # 1. Try PATH
    found = shutil.which('yt-dlp')
    if found:
        return found
    # 2. Try pip-installed location
    for candidate in [
        Path.home() / '.local' / 'bin' / 'yt-dlp',
        Path('/usr/local/bin/yt-dlp'),
        Path('/opt/homebrew/bin/yt-dlp'),
    ]:
        if candidate.exists():
            return str(candidate)
    # 3. Try running as a Python module
    try:
        subprocess.run([sys.executable, '-m', 'yt_dlp', '--version'],
                       capture_output=True, check=True)
        return f'{sys.executable} -m yt_dlp'
    except Exception:
        pass
    return None

YTDLP = find_ytdlp()

# ─── Jellyfin integration ────────────────────────────────────────────────────

def jellyfin_enabled():
    return bool(JELLYFIN_URL and JELLYFIN_API_KEY)

def jellyfin_request(method, path, query=None, body=None):
    if not jellyfin_enabled():
        return None

    url = f'{JELLYFIN_URL}{path}'
    if query:
        url = f'{url}?{parse.urlencode(query, doseq=True)}'

    payload = None
    headers = {
        'X-Emby-Token': JELLYFIN_API_KEY,
        'Content-Type': JSON_CONTENT_TYPE,
        'Accept': JSON_CONTENT_TYPE,
    }
    if body is not None:
        payload = json.dumps(body).encode()

    req = request.Request(url, data=payload, headers=headers, method=method)
    with request.urlopen(req, timeout=15) as response:
        data = response.read()
        if not data:
            return None
        content_type = response.headers.get('Content-Type', '')
        if JSON_CONTENT_TYPE in content_type:
            return json.loads(data.decode())
        return data.decode()

def ensure_jellyfin_library(name, library_type, directory):
    if not jellyfin_enabled():
        print('Jellyfin integration disabled. Set JELLYFIN_URL and JELLYFIN_API_KEY to auto-create the library.')
        return

    try:
        folders = jellyfin_request('GET', '/Library/VirtualFolders') or []
        for folder in folders:
            if folder.get('Name', '').casefold() == name.casefold():
                locations = folder.get('Locations') or []
                collection_type = folder.get('CollectionType')
                print(f'Jellyfin library already exists: {folder.get("Name")}')
                if str(directory) not in locations:
                    print(f'WARNING: Existing Jellyfin library path is {locations}, downloader path is {directory}')
                if collection_type != library_type:
                    print(f'WARNING: Existing Jellyfin library type is {collection_type}, preferred type is {library_type}')
                return

        jellyfin_request(
            'POST',
            '/Library/VirtualFolders',
            query={
                'name': name,
                'collectionType': library_type,
                'paths': [str(directory)],
                'refreshLibrary': 'true',
            },
            body={}
        )
        print(f'Created Jellyfin library "{name}" at {directory}')
    except error.HTTPError as exc:
        details = exc.read().decode(errors='replace')
        print(f'Could not create Jellyfin library: HTTP {exc.code} {details}')
    except Exception as exc:
        print(f'Could not create Jellyfin library: {exc}')

def refresh_jellyfin_library(name):
    if not jellyfin_enabled():
        return

    try:
        jellyfin_request('POST', '/Library/Refresh')
        print(f'Requested Jellyfin library scan after updating "{name}"')
    except Exception as exc:
        print(f'Could not refresh Jellyfin library: {exc}')

# ─── Download worker ──────────────────────────────────────────────────────────

def archive_directory_for_target(target):
    return MUSIC_DOWNLOAD_DIR if target == 'music' else DOWNLOAD_DIR

def library_name_for_target(target):
    return JELLYFIN_MUSIC_LIBRARY_NAME if target == 'music' else JELLYFIN_LIBRARY_NAME

def build_ytdlp_command(url, quality, audio_format=None, target='video'):
    fmt = QUALITY_FORMATS.get(quality, FORMAT)
    archive_dir = archive_directory_for_target(target)
    cmd = shlex.split(YTDLP) + [
        '-f', fmt,
        '--output', str(archive_dir / OUTPUT_TEMPLATE),
        '--no-playlist',
        '--write-info-json',
        '--write-thumbnail',
        '--no-mtime',
        '--restrict-filenames',
        '--print', 'before_dl:%(title)s',
    ]

    if quality == 'audio':
        audio_format = audio_format if audio_format in SUPPORTED_AUDIO_FORMATS else 'mp3'
        cmd += [
            '--extract-audio',
            '--audio-format', audio_format,
            '--audio-quality', '0',
        ]
    else:
        cmd += ['--merge-output-format', 'mp4']

    cmd.append(url)
    return cmd

def run_download(url, quality, audio_format=None, target='video'):

    with lock:
        active[url] = {'status': 'downloading', 'title': ''}

    try:
        cmd = build_ytdlp_command(url, quality, audio_format, target)

        result = subprocess.run(cmd, capture_output=True, text=True)

        title = result.stdout.strip().split('\n')[0] if result.stdout.strip() else url

        if result.returncode == 0:
            with lock:
                active[url] = {'status': 'done', 'title': title, 'target': target}
            print(f'[✓] Downloaded: {title}')
            refresh_jellyfin_library(library_name_for_target(target))
        else:
            err = result.stderr.strip().split('\n')[-1] if result.stderr else 'Unknown error'
            with lock:
                active[url] = {'status': 'error', 'error': err}
            print(f'[✗] Failed: {err}')

    except Exception as e:
        with lock:
            active[url] = {'status': 'error', 'error': str(e)}
        print(f'[✗] Exception: {e}')

# ─── HTTP handler ─────────────────────────────────────────────────────────────

class Handler(BaseHTTPRequestHandler):

    def log_message(self, format, *args):
        pass  # Suppress default access log; we print our own

    def send_json(self, code, data):
        body = json.dumps(data).encode()
        self.send_response(code)
        self.send_header('Content-Type', JSON_CONTENT_TYPE)
        self.send_header('Content-Length', str(len(body)))
        origin = self.headers.get('Origin', '')
        if origin == 'null' or origin.startswith(('chrome-extension://', 'moz-extension://')) or origin in {
            'https://www.youtube.com',
            'https://music.youtube.com',
            'http://localhost:8096',
            'http://127.0.0.1:8096',
        }:
            self.send_header('Access-Control-Allow-Origin', origin)
            self.send_header('Vary', 'Origin')
        self.send_header('Access-Control-Allow-Methods', 'GET, POST, OPTIONS')
        self.send_header('Access-Control-Allow-Headers', 'Content-Type, X-YtdlArchive-Token')
        self.end_headers()
        self.wfile.write(body)

    def do_OPTIONS(self):
        self.send_json(200, {})

    def authorized(self):
        return bool(BROWSER_API_TOKEN) and secrets.compare_digest(
            self.headers.get('X-YtdlArchive-Token', ''),
            BROWSER_API_TOKEN,
        )

    def require_authorized(self):
        if self.authorized():
            return True

        self.send_json(401, {'error': 'Missing or invalid YtdlArchive browser API token'})
        return False

    def do_GET(self):
        if not self.require_authorized():
            return

        if self.path == '/ping':
            self.send_json(200, {
                'ok': True,
                'ytdlp': YTDLP or 'not found',
                'downloadDir': str(DOWNLOAD_DIR),
                'musicDownloadDir': str(MUSIC_DOWNLOAD_DIR),
                'jellyfin': {
                    'enabled': jellyfin_enabled(),
                    'url': JELLYFIN_URL or None,
                    'libraryName': JELLYFIN_LIBRARY_NAME,
                    'libraryType': JELLYFIN_LIBRARY_TYPE,
                    'musicLibraryName': JELLYFIN_MUSIC_LIBRARY_NAME,
                    'musicLibraryType': JELLYFIN_MUSIC_LIBRARY_TYPE,
                },
            })
        elif self.path == '/save-types':
            self.send_json(200, {'saveTypes': [
                {'label': 'Best to Other', 'quality': 'best', 'icon': '★', 'target': 'video'},
                {'label': '1080p to Other', 'quality': '1080', 'icon': 'HD', 'target': 'video'},
                {'label': '720p to Other', 'quality': '720', 'icon': 'HD', 'target': 'video'},
                {'label': '480p to Other', 'quality': '480', 'icon': 'SD', 'target': 'video'},
                {'label': 'MP3 to Music', 'quality': 'audio', 'icon': '♫', 'audioFormat': 'mp3', 'target': 'music'},
                {'label': 'M4A to Music', 'quality': 'audio', 'icon': '♫', 'audioFormat': 'm4a', 'target': 'music'},
                {'label': 'Opus to Music', 'quality': 'audio', 'icon': '♫', 'audioFormat': 'opus', 'target': 'music'},
            ]})
        elif self.path == '/status':
            with lock:
                self.send_json(200, dict(active))
        else:
            self.send_json(404, {'error': 'Not found'})

    def do_POST(self):
        if not self.require_authorized():
            return

        if self.path != '/download':
            self.send_json(404, {'error': 'Not found'})
            return

        length = int(self.headers.get('Content-Length', 0))
        body   = self.rfile.read(length)
        try:
            data = json.loads(body)
        except Exception:
            self.send_json(400, {'error': 'Bad JSON'})
            return

        url     = data.get('url', '').strip()
        quality = data.get('quality', 'best')
        audio_format = data.get('audioFormat')
        target = data.get('target', 'video')

        if not url:
            self.send_json(400, {'error': 'Missing url'})
            return

        parsed = urlparse(url)
        if parsed.scheme not in {'http', 'https'} or parsed.hostname not in ALLOWED_DOWNLOAD_HOSTS:
            self.send_json(400, {'error': 'Only YouTube URLs are allowed by default'})
            return

        if quality not in QUALITY_FORMATS:
            self.send_json(400, {'error': f'Unsupported quality: {quality}'})
            return

        if quality == 'audio' and audio_format and audio_format not in SUPPORTED_AUDIO_FORMATS:
            self.send_json(400, {'error': f'Unsupported audio format: {audio_format}'})
            return

        if target not in {'video', 'music'}:
            self.send_json(400, {'error': f'Unsupported target: {target}'})
            return

        if not YTDLP:
            self.send_json(500, {
                'error': 'yt-dlp not found. Install it with: pip install yt-dlp'
            })
            return

        # Check if already downloading
        with lock:
            if url in active and active[url]['status'] == 'downloading':
                self.send_json(200, {'queued': False, 'reason': 'already downloading'})
                return

        # Start download in background thread
        t = threading.Thread(target=run_download, args=(url, quality, audio_format, target), daemon=True)
        t.start()

        label = f'{quality}/{audio_format}' if quality == 'audio' and audio_format else quality
        save_to = archive_directory_for_target(target)
        print(f'[→] Queued: {url}  [{label}] -> {target}')
        self.send_json(200, {'queued': True, 'saveTo': str(save_to), 'target': target})

# ─── Main ─────────────────────────────────────────────────────────────────────

if __name__ == '__main__':
    if not BROWSER_API_TOKEN:
        print('ERROR: set YTDL_BROWSER_API_TOKEN before starting the standalone development server.')
        print('The embedded Jellyfin plugin server generates and stores this token automatically.')
        sys.exit(1)

    if not YTDLP:
        print('ERROR: yt-dlp not found.')
        print('Install it with:  pip install yt-dlp')
        sys.exit(1)

    print(f'yt-dlp found at: {YTDLP}')
    print(f'Saving downloads to: {DOWNLOAD_DIR}')
    print(f'Saving music downloads to: {MUSIC_DOWNLOAD_DIR}')
    ensure_jellyfin_library(JELLYFIN_LIBRARY_NAME, JELLYFIN_LIBRARY_TYPE, DOWNLOAD_DIR)
    ensure_jellyfin_library(JELLYFIN_MUSIC_LIBRARY_NAME, JELLYFIN_MUSIC_LIBRARY_TYPE, MUSIC_DOWNLOAD_DIR)
    print(f'Server running at http://localhost:{PORT}')
    print('Keep this window open while using the Chrome extension.\n')

    server = HTTPServer(('localhost', PORT), Handler)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print('\nServer stopped.')
