"""Credential safety checks; no Docker or service dependencies required."""
import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("infra_stack", Path(__file__).resolve().parents[1] / "stack.py")
stack = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(stack)


class ConfigurationSafetyTests(unittest.TestCase):
    def test_fresh_install_generates_distinct_secrets_and_restart_preserves_them(self):
        example = (stack.INFRA / ".env.example").read_text()
        with tempfile.TemporaryDirectory() as directory:
            infra = Path(directory)
            (infra / ".env.example").write_text(example)
            with patch.object(stack, "INFRA", infra):
                stack.configure()
                first = (infra / ".env").read_bytes()
                values = dict(line.split("=", 1) for line in first.decode().splitlines()
                              if "=" in line and not line.startswith("#"))
                secrets = [values[key] for key in stack.SECRET_KEYS]
                self.assertEqual(len(secrets), len(set(secrets)))
                self.assertTrue(all(len(value) >= 48 and "replace-with-" not in value for value in secrets))
                self.assertEqual((infra / ".env").stat().st_mode & 0o777, 0o600)
                stack.configure()
                self.assertEqual(first, (infra / ".env").read_bytes())

    def test_existing_credentials_ports_and_data_namespace_survive_upgrade(self):
        original = "# Keep local settings\nVD_AUTH_DB_PASSWORD='existing-password'\nVD_GATEWAY_HOST_PORT=38087\nVD_DATA_NAMESPACE=legacy\n"
        with tempfile.TemporaryDirectory() as directory:
            infra = Path(directory)
            (infra / ".env").write_text(original)
            with patch.object(stack, "INFRA", infra):
                stack.configure()
                result = (infra / ".env").read_text()
                self.assertTrue(result.startswith(original))
                self.assertEqual(result.count("VD_AUTH_DB_PASSWORD="), 1)
                self.assertIn("VD_SUPERSET_ADMIN_PASSWORD=", result)
                stack.configure()
                self.assertEqual(result, (infra / ".env").read_text())


if __name__ == "__main__":
    unittest.main()
