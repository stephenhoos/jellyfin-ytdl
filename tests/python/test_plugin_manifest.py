import json
import re
import unittest
from pathlib import Path
from urllib.parse import urlparse


ROOT = Path(__file__).resolve().parents[2]


class PluginManifestTests(unittest.TestCase):
    def test_repository_manifest_has_jellyfin_catalog_shape(self):
        manifest = json.loads((ROOT / "manifest.json").read_text(encoding="utf-8"))

        self.assertIsInstance(manifest, list)
        self.assertGreaterEqual(len(manifest), 1)

        plugin = manifest[0]
        for key in ["guid", "name", "description", "overview", "owner", "category", "versions"]:
            self.assertIn(key, plugin)

        self.assertEqual("499ef0fc-f2b9-4469-aac1-bee74c3b1cfd", plugin["guid"])
        self.assertEqual("YtdlArchive", plugin["name"])
        self.assertIsInstance(plugin["versions"], list)
        self.assertGreaterEqual(len(plugin["versions"]), 1)

        version = plugin["versions"][0]
        for key in ["version", "changelog", "targetAbi", "sourceUrl", "checksum", "timestamp"]:
            self.assertIn(key, version)

        parsed_url = urlparse(version["sourceUrl"])
        self.assertEqual("https", parsed_url.scheme)
        self.assertTrue(parsed_url.path.endswith(".zip"))
        self.assertRegex(version["checksum"], r"^[0-9a-f]{32}$")
        self.assertRegex(version["targetAbi"], r"^\d+\.\d+\.\d+\.\d+$")

    def test_packaged_meta_matches_plugin_identity(self):
        manifest = json.loads((ROOT / "manifest.json").read_text(encoding="utf-8"))[0]
        meta = json.loads((ROOT / "packaging" / "YtdlArchive" / "meta.json").read_text(encoding="utf-8"))

        self.assertEqual(manifest["guid"], meta["guid"])
        self.assertEqual(manifest["name"], meta["name"])
        self.assertEqual(manifest["category"], meta["category"])
        self.assertTrue(re.match(r"^\d+\.\d+\.\d+\.\d+$", meta["targetAbi"]))


if __name__ == "__main__":
    unittest.main()
