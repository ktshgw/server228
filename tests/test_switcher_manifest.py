from pathlib import Path
import unittest
from unittest.mock import patch

import main


class SwitcherManifestTests(unittest.TestCase):
    def test_compatibility_payload_only_publishes_complete_versions(self) -> None:
        static_root = Path(__file__).resolve().parents[1] / "static"
        with patch.object(main, "STATIC_DIR", static_root):
            payload = main._switcher_compatibility_payload()

        assert "2026.804.2" in payload
        assert int(payload["2026.804.2"]["enhanced_auth"]["size"]) > 1_000_000
        assert int(payload["2026.804.2"]["startup_hook"]["size"]) > 0
        assert len(str(payload["2026.804.2"]["startup_hook"]["sha256"])) == 64


if __name__ == "__main__":
    unittest.main()
