import importlib.util
import io
import json
import tempfile
import unittest
from http import HTTPStatus
from pathlib import Path


def load_server_module():
    module_path = Path(__file__).resolve().parents[2] / "yt-downloader-server.py"
    spec = importlib.util.spec_from_file_location("yt_downloader_server", module_path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.YTDLP = "yt-dlp"
    return module


class YtdlServerTests(unittest.TestCase):
    def test_audio_command_defaults_unknown_audio_format_to_mp3(self):
        server = load_server_module()

        command = server.build_ytdlp_command(
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            "audio",
            "wav",
            "music",
        )

        self.assertIn("--extract-audio", command)
        self.assertEqual("mp3", command[command.index("--audio-format") + 1])
        self.assertEqual("https://www.youtube.com/watch?v=dQw4w9WgXcQ", command[-1])

    def test_audio_command_extracts_m4b_as_m4a_before_finalization(self):
        server = load_server_module()

        command = server.build_ytdlp_command(
            "https://www.youtube.com/watch?v=dQw4w9WgXcQ",
            "audio",
            "m4b",
            "audiobook",
        )

        self.assertEqual("m4a", command[command.index("--audio-format") + 1])
        self.assertIn("--embed-metadata", command)

    def test_video_command_uses_merge_output_format(self):
        server = load_server_module()

        command = server.build_ytdlp_command(
            "https://youtu.be/dQw4w9WgXcQ",
            "720",
            None,
            "video",
        )

        self.assertIn("--merge-output-format", command)
        self.assertEqual("mp4", command[command.index("--merge-output-format") + 1])
        self.assertEqual("https://youtu.be/dQw4w9WgXcQ", command[-1])

    def test_archive_directory_and_library_name_follow_target(self):
        server = load_server_module()

        self.assertEqual(server.MUSIC_DOWNLOAD_DIR, server.archive_directory_for_target("music"))
        self.assertEqual(server.PODCAST_DOWNLOAD_DIR, server.archive_directory_for_target("podcast"))
        self.assertEqual(server.AUDIOBOOK_DOWNLOAD_DIR, server.archive_directory_for_target("book"))
        self.assertEqual(server.OTHER_DOWNLOAD_DIR, server.archive_directory_for_target("video"))
        self.assertEqual(server.JELLYFIN_MUSIC_LIBRARY_NAME, server.library_name_for_target("music"))
        self.assertEqual(server.JELLYFIN_PODCAST_LIBRARY_NAME, server.library_name_for_target("podcast"))
        self.assertEqual(server.JELLYFIN_AUDIOBOOK_LIBRARY_NAME, server.library_name_for_target("audiobook"))
        self.assertEqual(server.JELLYFIN_OTHER_LIBRARY_NAME, server.library_name_for_target("video"))
        self.assertEqual("", server.normalize_target("unknown"))

    def test_m4b_helpers_find_and_finalize_downloads(self):
        server = load_server_module()
        with tempfile.TemporaryDirectory() as temp_root:
            temp_dir = Path(temp_root)
            media_path = temp_dir / "Audiobook.m4a"
            media_path.write_text("", encoding="utf-8")
            media_path.with_suffix(".info.json").write_text('{"duration":100}', encoding="utf-8")

            self.assertEqual(media_path, server.find_newest_m4a(temp_dir, 0))

            server.finalize_m4b(temp_dir, 0)

            self.assertFalse(media_path.exists())
            self.assertTrue(media_path.with_suffix(".m4b").exists())

    def test_m4b_helpers_handle_missing_inputs(self):
        server = load_server_module()
        with tempfile.TemporaryDirectory() as temp_root:
            temp_dir = Path(temp_root)

            self.assertIsNone(server.find_newest_m4a(temp_dir, 0))
            server.finalize_m4b(temp_dir, 0)

    def test_parse_and_validate_download_request_accepts_supported_targets(self):
        server = load_server_module()
        handler = make_handler(server, "POST", "/download", {
            "url": "https://youtu.be/dQw4w9WgXcQ",
            "quality": "audio",
            "audioFormat": "m4b",
            "target": "book",
            "chapterPercent": 20,
        }, token="secret")

        request_data, error = server.parse_download_payload(handler)

        self.assertIsNone(error)
        self.assertEqual("audiobook", request_data["target"])
        self.assertIsNone(server.validate_download_request(request_data))
        self.assertEqual("chapterPercent must be 10 or 20", server.validate_download_request({
            **request_data,
            "chapter_percent": 30,
        }))


    def test_handler_requires_browser_token(self):
        server = load_server_module()
        server.BROWSER_API_TOKEN = "secret"

        handler = make_handler(server, "GET", "/ping")
        handler.do_GET()

        self.assertEqual(HTTPStatus.UNAUTHORIZED, handler.status)
        self.assertEqual("Missing or invalid YtdlArchive browser API token", handler.json_body["error"])

    def test_handler_reports_ping_save_types_and_status(self):
        server = load_server_module()
        server.BROWSER_API_TOKEN = "secret"
        server.active["https://youtu.be/dQw4w9WgXcQ"] = {"status": "done", "title": "Video"}

        ping = make_handler(server, "GET", "/ping", token="secret", origin="https://www.youtube.com")
        ping.do_GET()
        self.assertEqual(HTTPStatus.OK, ping.status)
        self.assertTrue(ping.json_body["ok"])
        self.assertEqual("https://www.youtube.com", ping.headers_sent["Access-Control-Allow-Origin"])

        save_types = make_handler(server, "GET", "/save-types", token="secret")
        save_types.do_GET()
        self.assertEqual(HTTPStatus.OK, save_types.status)
        self.assertGreaterEqual(len(save_types.json_body["saveTypes"]), 11)
        self.assertIn("audiobook", {save_type["target"] for save_type in save_types.json_body["saveTypes"]})

        status = make_handler(server, "GET", "/status", token="secret")
        status.do_GET()
        self.assertEqual("done", status.json_body["https://youtu.be/dQw4w9WgXcQ"]["status"])

    def test_handler_rejects_bad_download_requests(self):
        server = load_server_module()
        server.BROWSER_API_TOKEN = "secret"

        cases = [
            (b"{not json", "Bad JSON"),
            ({}, "Missing url"),
            ({"url": "https://example.com/watch?v=dQw4w9WgXcQ"}, "Only YouTube URLs"),
            ({"url": "https://youtu.be/dQw4w9WgXcQ", "quality": "4k"}, "Unsupported quality"),
            ({"url": "https://youtu.be/dQw4w9WgXcQ", "quality": "audio", "audioFormat": "wav"}, "Unsupported audio format"),
            ({"url": "https://youtu.be/dQw4w9WgXcQ", "target": "unknown"}, "Unsupported target"),
            ({"url": "https://youtu.be/dQw4w9WgXcQ", "chapterPercent": 30}, "chapterPercent"),
        ]

        for payload, expected_error in cases:
            with self.subTest(expected_error=expected_error):
                handler = make_handler(server, "POST", "/download", payload, token="secret")
                handler.do_POST()
                self.assertEqual(HTTPStatus.BAD_REQUEST, handler.status)
                self.assertIn(expected_error, handler.json_body["error"])

    def test_handler_reports_missing_ytdlp_and_already_downloading(self):
        server = load_server_module()
        server.BROWSER_API_TOKEN = "secret"
        payload = {"url": "https://youtu.be/dQw4w9WgXcQ", "quality": "best", "target": "video"}

        server.YTDLP = None
        missing = make_handler(server, "POST", "/download", payload, token="secret")
        missing.do_POST()
        self.assertEqual(HTTPStatus.INTERNAL_SERVER_ERROR, missing.status)
        self.assertIn("yt-dlp not found", missing.json_body["error"])

        server.YTDLP = "yt-dlp"
        server.active[payload["url"]] = {"status": "downloading"}
        duplicate = make_handler(server, "POST", "/download", payload, token="secret")
        duplicate.do_POST()
        self.assertEqual(HTTPStatus.OK, duplicate.status)
        self.assertEqual("already downloading", duplicate.json_body["reason"])

    def test_handler_returns_not_found_for_unknown_routes(self):
        server = load_server_module()
        server.BROWSER_API_TOKEN = "secret"

        get_handler = make_handler(server, "GET", "/missing", token="secret")
        get_handler.do_GET()
        self.assertEqual(HTTPStatus.NOT_FOUND, get_handler.status)

        post_handler = make_handler(server, "POST", "/missing", {}, token="secret")
        post_handler.do_POST()
        self.assertEqual(HTTPStatus.NOT_FOUND, post_handler.status)


class HeaderMap(dict):
    def get(self, key, default=None):
        return super().get(key, default)


def make_handler(server, method, path, payload=None, token=None, origin=None):
    class TestHandler(server.Handler):
        @property
        def json_body(self):
            return json.loads(self.wfile.getvalue().decode())

        def send_response(self, code, message=None):
            self.status = HTTPStatus(code)

        def send_header(self, keyword, value):
            self.headers_sent[keyword] = value

        def end_headers(self):
            self.ended_headers = True

    handler = object.__new__(TestHandler)
    handler.command = method
    handler.path = path
    handler.headers = HeaderMap()
    handler.headers_sent = {}
    handler.ended_headers = False
    if token:
        handler.headers["X-YtdlArchive-Token"] = token
    if origin:
        handler.headers["Origin"] = origin

    if payload is None:
        body = b""
    elif isinstance(payload, bytes):
        body = payload
    else:
        body = json.dumps(payload).encode()
    handler.headers["Content-Length"] = str(len(body))
    handler.rfile = io.BytesIO(body)
    handler.wfile = io.BytesIO()
    return handler
