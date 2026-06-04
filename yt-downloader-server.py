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
import shutil
import threading
import subprocess
import tempfile
import time
from pathlib import Path
from http.server import BaseHTTPRequestHandler, HTTPServer
from urllib import parse, request, error
from urllib.parse import urlparse

# ─── Configuration ────────────────────────────────────────────────────────────

PORT        = 9876
JSON_CONTENT_TYPE = 'application/json'
DOWNLOAD_DIR = Path(os.environ.get('YTDL_DOWNLOAD_DIR', Path.home() / 'Downloads' / 'YouTube')).expanduser()
MUSIC_DOWNLOAD_DIR = Path(os.environ.get('YTDL_MUSIC_DOWNLOAD_DIR', Path.home() / 'Music' / 'YouTube Music')).expanduser()
PODCAST_DOWNLOAD_DIR = Path(os.environ.get('YTDL_PODCAST_DOWNLOAD_DIR', Path.home() / 'Music' / 'YT-Podcast')).expanduser()
AUDIOBOOK_DOWNLOAD_DIR = Path(os.environ.get('YTDL_AUDIOBOOK_DOWNLOAD_DIR', Path.home() / 'Music' / 'YT-Audiobooks')).expanduser()
OTHER_DOWNLOAD_DIR = Path(os.environ.get('YTDL_OTHER_DOWNLOAD_DIR', Path.home() / 'Downloads' / 'YT-Other')).expanduser()

JELLYFIN_URL = os.environ.get('JELLYFIN_URL', '').strip().rstrip('/')
JELLYFIN_API_KEY = os.environ.get('JELLYFIN_API_KEY', '').strip()
JELLYFIN_LIBRARY_NAME = os.environ.get('JELLYFIN_LIBRARY_NAME', 'YouTube').strip()
JELLYFIN_LIBRARY_TYPE = os.environ.get('JELLYFIN_LIBRARY_TYPE', 'tvshows').strip()
JELLYFIN_MUSIC_LIBRARY_NAME = os.environ.get('JELLYFIN_MUSIC_LIBRARY_NAME', 'YouTube Music').strip()
JELLYFIN_MUSIC_LIBRARY_TYPE = os.environ.get('JELLYFIN_MUSIC_LIBRARY_TYPE', 'music').strip()
JELLYFIN_PODCAST_LIBRARY_NAME = os.environ.get('JELLYFIN_PODCAST_LIBRARY_NAME', 'YT-Podcast').strip()
JELLYFIN_AUDIOBOOK_LIBRARY_NAME = os.environ.get('JELLYFIN_AUDIOBOOK_LIBRARY_NAME', 'YT-Audiobooks').strip()
JELLYFIN_OTHER_LIBRARY_NAME = os.environ.get('JELLYFIN_OTHER_LIBRARY_NAME', 'YT-Other').strip()
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
SUPPORTED_AUDIO_FORMATS = {'mp3', 'm4a', 'm4b', 'opus'}
SUPPORTED_TARGETS = {'video', 'other', 'music', 'podcast', 'audiobook', 'book'}

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
PODCAST_DOWNLOAD_DIR.mkdir(parents=True, exist_ok=True)
AUDIOBOOK_DOWNLOAD_DIR.mkdir(parents=True, exist_ok=True)
OTHER_DOWNLOAD_DIR.mkdir(parents=True, exist_ok=True)

# ─── Active downloads tracker ────────────────────────────────────────────────

active = {}   # url -> {'status': ..., 'title': ...}
lock   = threading.Lock()

# ─── Find yt-dlp ─────────────────────────────────────────────────────────────

def find_ytdlp():
    """Return the yt-dlp executable path, or None if not found."""
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
    target = normalize_target(target)
    if target == 'music':
        return MUSIC_DOWNLOAD_DIR
    if target == 'podcast':
        return PODCAST_DOWNLOAD_DIR
    if target == 'audiobook':
        return AUDIOBOOK_DOWNLOAD_DIR
    if target == 'other':
        return OTHER_DOWNLOAD_DIR
    return DOWNLOAD_DIR

def library_name_for_target(target):
    target = normalize_target(target)
    if target == 'music':
        return JELLYFIN_MUSIC_LIBRARY_NAME
    if target == 'podcast':
        return JELLYFIN_PODCAST_LIBRARY_NAME
    if target == 'audiobook':
        return JELLYFIN_AUDIOBOOK_LIBRARY_NAME
    if target == 'other':
        return JELLYFIN_OTHER_LIBRARY_NAME
    return JELLYFIN_LIBRARY_NAME

def normalize_target(target):
    target = (target or 'other').strip().lower()
    if target == 'book':
        return 'audiobook'
    if target == 'video':
        return 'other'
    return target if target in SUPPORTED_TARGETS else ''

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
        audio_format = 'm4a' if audio_format == 'm4b' else audio_format
        audio_format = audio_format if audio_format in SUPPORTED_AUDIO_FORMATS else 'mp3'
        cmd += [
            '--extract-audio',
            '--audio-format', audio_format,
            '--audio-quality', '0',
            '--embed-metadata',
        ]
    else:
        cmd += ['--merge-output-format', 'mp4']

    cmd.append(url)
    return cmd

def run_download(url, quality, audio_format=None, target='video', chapter_percent=None):

    with lock:
        active[url] = {'status': 'downloading', 'title': ''}

    try:
        started_at = time.time()
        cmd = build_ytdlp_command(url, quality, audio_format, target)

        result = subprocess.run(cmd, capture_output=True, text=True)

        title = result.stdout.strip().split('\n')[0] if result.stdout.strip() else url

        if result.returncode == 0:
            if quality == 'audio' and audio_format == 'm4b':
                finalize_m4b(archive_directory_for_target(target), started_at, chapter_percent)
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

def find_newest_m4a(archive_dir, started_at):
    candidates = []
    for path in Path(archive_dir).rglob('*.m4a'):
        try:
            modified_at = path.stat().st_mtime
        except OSError:
            continue
        if modified_at >= started_at - 120:
            candidates.append((modified_at, path))
    return max(candidates, default=(None, None))[1]

def read_duration(media_path):
    info_path = Path(media_path).with_suffix('.info.json')
    if not info_path.exists():
        return 0
    try:
        with info_path.open('r', encoding='utf-8') as handle:
            return float(json.load(handle).get('duration') or 0)
    except (OSError, TypeError, ValueError):
        return 0

def escape_metadata(value):
    return (
        value
        .replace('\\', '\\\\')
        .replace('=', '\\=')
        .replace(';', '\\;')
        .replace('#', '\\#')
        .replace('\n', '\\\n')
    )

def build_chapter_metadata(media_path, chapter_percent):
    duration = read_duration(media_path)
    lines = [';FFMETADATA1', f'title={escape_metadata(Path(media_path).stem)}']
    if duration <= 0:
        return '\n'.join(lines)

    chapter_count = 100 // chapter_percent
    duration_ms = int(duration * 1000)
    for index in range(chapter_count):
        start = duration_ms * index // chapter_count
        end = duration_ms if index == chapter_count - 1 else duration_ms * (index + 1) // chapter_count
        lines.extend([
            '[CHAPTER]',
            'TIMEBASE=1/1000',
            f'START={start}',
            f'END={end}',
            f'title={chapter_percent * index}%',
        ])
    return '\n'.join(lines)

def find_ffmpeg():
    jellyfin_ffmpeg = os.environ.get('JELLYFIN_FFMPEG_PATH', '')
    if jellyfin_ffmpeg and Path(jellyfin_ffmpeg).exists():
        return jellyfin_ffmpeg
    return shutil.which('ffmpeg') or 'ffmpeg'

def finalize_m4b(archive_dir, started_at, chapter_percent=None):
    source = find_newest_m4a(archive_dir, started_at)
    if not source:
        print('WARNING: could not find extracted m4a to finalize as m4b')
        return

    destination = source.with_suffix('.m4b')
    if chapter_percent is None:
        source.replace(destination)
        return

    metadata_path = None
    temp_path = source.with_name(f'{source.stem}.tmp.m4b')
    try:
        with tempfile.NamedTemporaryFile('w', suffix='.ffmetadata', delete=False, encoding='utf-8') as handle:
            metadata_path = Path(handle.name)
            handle.write(build_chapter_metadata(source, chapter_percent))

        result = subprocess.run([
            find_ffmpeg(),
            '-y',
            '-i', str(source),
            '-i', str(metadata_path),
            '-map_metadata', '1',
            '-codec', 'copy',
            str(temp_path),
        ], capture_output=True, text=True)
        if result.returncode == 0:
            temp_path.replace(destination)
            source.unlink(missing_ok=True)
        else:
            print(f'WARNING: could not add m4b chapters: {result.stderr.strip()}')
            source.replace(destination)
    except (OSError, subprocess.SubprocessError) as exc:
        print(f'WARNING: could not add m4b chapters: {exc}')
        source.replace(destination)
    finally:
        if metadata_path:
            metadata_path.unlink(missing_ok=True)
        temp_path.unlink(missing_ok=True)

def parse_download_payload(handler):
    length = int(handler.headers.get('Content-Length', 0))
    body = handler.rfile.read(length)
    try:
        data = json.loads(body)
    except Exception:
        return None, 'Bad JSON'

    url = data.get('url', '').strip()
    quality = data.get('quality', 'best')
    audio_format = data.get('audioFormat')
    target = normalize_target(data.get('target', 'other'))
    chapter_percent = data.get('chapterPercent')
    return {
        'url': url,
        'quality': quality,
        'audio_format': audio_format,
        'target': target,
        'chapter_percent': chapter_percent,
    }, None

def validate_download_request(request_data):
    url = request_data['url']
    quality = request_data['quality']
    audio_format = request_data['audio_format']
    target = request_data['target']
    chapter_percent = request_data['chapter_percent']

    if not url:
        return 'Missing url'

    parsed = urlparse(url)
    if parsed.scheme not in {'http', 'https'} or parsed.hostname not in ALLOWED_DOWNLOAD_HOSTS:
        return 'Only YouTube URLs are allowed by default'

    if quality not in QUALITY_FORMATS:
        return f'Unsupported quality: {quality}'

    if quality == 'audio' and audio_format and audio_format not in SUPPORTED_AUDIO_FORMATS:
        return f'Unsupported audio format: {audio_format}'

    if not target:
        return f'Unsupported target: {target}'

    if chapter_percent is not None and chapter_percent not in {10, 20}:
        return 'chapterPercent must be 10 or 20'

    return None

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
                'podcastDownloadDir': str(PODCAST_DOWNLOAD_DIR),
                'audiobookDownloadDir': str(AUDIOBOOK_DOWNLOAD_DIR),
                'otherDownloadDir': str(OTHER_DOWNLOAD_DIR),
                'jellyfin': {
                    'enabled': jellyfin_enabled(),
                    'url': JELLYFIN_URL or None,
                    'libraryName': JELLYFIN_LIBRARY_NAME,
                    'libraryType': JELLYFIN_LIBRARY_TYPE,
                    'musicLibraryName': JELLYFIN_MUSIC_LIBRARY_NAME,
                    'musicLibraryType': JELLYFIN_MUSIC_LIBRARY_TYPE,
                    'podcastLibraryName': JELLYFIN_PODCAST_LIBRARY_NAME,
                    'podcastLibraryType': 'music',
                    'audiobookLibraryName': JELLYFIN_AUDIOBOOK_LIBRARY_NAME,
                    'audiobookLibraryType': 'books',
                    'otherLibraryName': JELLYFIN_OTHER_LIBRARY_NAME,
                    'otherLibraryType': 'tvshows',
                },
            })
        elif self.path == '/save-types':
            self.send_json(200, {'saveTypes': [
                {'label': 'Best to Other', 'quality': 'best', 'icon': '★', 'target': 'other'},
                {'label': '1080p to Other', 'quality': '1080', 'icon': 'HD', 'target': 'other'},
                {'label': '720p to Other', 'quality': '720', 'icon': 'HD', 'target': 'other'},
                {'label': '480p to Other', 'quality': '480', 'icon': 'SD', 'target': 'other'},
                {'label': 'MP3 to Music', 'quality': 'audio', 'icon': '♫', 'audioFormat': 'mp3', 'target': 'music'},
                {'label': 'M4A to Music', 'quality': 'audio', 'icon': '♫', 'audioFormat': 'm4a', 'target': 'music'},
                {'label': 'Opus to Music', 'quality': 'audio', 'icon': '♫', 'audioFormat': 'opus', 'target': 'music'},
                {'label': 'MP3 to Podcast', 'quality': 'audio', 'icon': '◉', 'audioFormat': 'mp3', 'target': 'podcast'},
                {'label': 'M4A to Podcast', 'quality': 'audio', 'icon': '◉', 'audioFormat': 'm4a', 'target': 'podcast'},
                {'label': 'M4B Audiobook', 'quality': 'audio', 'icon': '▣', 'audioFormat': 'm4b', 'target': 'audiobook'},
                {'label': 'M4B Audiobook 10% chapters', 'quality': 'audio', 'icon': '▣', 'audioFormat': 'm4b', 'target': 'audiobook', 'chapterPercent': 10},
                {'label': 'M4B Audiobook 20% chapters', 'quality': 'audio', 'icon': '▣', 'audioFormat': 'm4b', 'target': 'audiobook', 'chapterPercent': 20},
                {'label': 'M4A to Audiobooks', 'quality': 'audio', 'icon': '▣', 'audioFormat': 'm4a', 'target': 'audiobook'},
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

        request_data, parse_error = parse_download_payload(self)
        if parse_error:
            self.send_json(400, {'error': parse_error})
            return

        validation_error = validate_download_request(request_data)
        if validation_error:
            self.send_json(400, {'error': validation_error})
            return

        url = request_data['url']
        quality = request_data['quality']
        audio_format = request_data['audio_format']
        target = request_data['target']
        chapter_percent = request_data['chapter_percent']

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
        t = threading.Thread(target=run_download, args=(url, quality, audio_format, target, chapter_percent), daemon=True)
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
    print(f'Saving podcast downloads to: {PODCAST_DOWNLOAD_DIR}')
    print(f'Saving audiobook downloads to: {AUDIOBOOK_DOWNLOAD_DIR}')
    print(f'Saving other downloads to: {OTHER_DOWNLOAD_DIR}')
    ensure_jellyfin_library(JELLYFIN_LIBRARY_NAME, JELLYFIN_LIBRARY_TYPE, DOWNLOAD_DIR)
    ensure_jellyfin_library(JELLYFIN_MUSIC_LIBRARY_NAME, JELLYFIN_MUSIC_LIBRARY_TYPE, MUSIC_DOWNLOAD_DIR)
    ensure_jellyfin_library(JELLYFIN_PODCAST_LIBRARY_NAME, 'music', PODCAST_DOWNLOAD_DIR)
    ensure_jellyfin_library(JELLYFIN_AUDIOBOOK_LIBRARY_NAME, 'books', AUDIOBOOK_DOWNLOAD_DIR)
    ensure_jellyfin_library(JELLYFIN_OTHER_LIBRARY_NAME, 'tvshows', OTHER_DOWNLOAD_DIR)
    print(f'Server running at http://localhost:{PORT}')
    print('Keep this window open while using the Chrome extension.\n')

    server = HTTPServer(('localhost', PORT), Handler)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print('\nServer stopped.')
