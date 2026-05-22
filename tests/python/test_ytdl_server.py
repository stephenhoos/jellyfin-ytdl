import importlib.util
import unittest
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

    def test_archive_directory_and_library_name_follow_target(self):
        server = load_server_module()

        self.assertEqual(server.MUSIC_DOWNLOAD_DIR, server.archive_directory_for_target("music"))
        self.assertEqual(server.DOWNLOAD_DIR, server.archive_directory_for_target("video"))
        self.assertEqual(server.JELLYFIN_MUSIC_LIBRARY_NAME, server.library_name_for_target("music"))
        self.assertEqual(server.JELLYFIN_LIBRARY_NAME, server.library_name_for_target("video"))
