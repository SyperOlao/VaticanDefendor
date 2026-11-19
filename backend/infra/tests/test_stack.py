"""Credential safety checks; no Docker or service dependencies required."""
import importlib.util
from pathlib import Path
import json
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch

SPEC = importlib.util.spec_from_file_location("infra_stack", Path(__file__).resolve().parents[1] / "stack.py")
stack = importlib.util.module_from_spec(SPEC)
with patch.object(sys, "path", [str(Path(SPEC.origin).parent), *sys.path]):
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


class DedicatedConfigurationTests(unittest.TestCase):
    def test_first_start_adds_one_local_slot_and_restart_reuses_it(self):
        release = "sha256-" + "a" * 64
        remote_slot = {"serverId": "d678e4f6-9779-4d00-ad0d-fc566b3457ac",
                       "host": "192.0.2.10", "port": 7979, "secret": "other-server"}
        environment = {"VD_SERVER_POOL_JSON": json.dumps([remote_slot])}

        slot, changes = stack.dedicated.local_settings(environment, 7979, release)

        self.assertEqual(json.loads(changes["VD_SERVER_POOL_JSON"]), [remote_slot, slot])
        self.assertEqual(slot["host"], "127.0.0.1")
        self.assertEqual(slot["port"], 7979)
        self.assertGreaterEqual(len(slot["secret"]), 32)
        self.assertEqual(stack.dedicated.local_settings(environment | changes, 7979, release), (slot, {}))

    def test_custom_rewards_and_energy_are_preserved(self):
        release = "sha256-" + "a" * 64
        slot, initial = stack.dedicated.local_settings({}, 7979, release)
        environment = dict(initial, VD_ENERGY_RULESET_ID="custom")
        environment["VD_RUN_REWARDS_JSON"] = json.dumps({release: {"perChurchLevel": 2}})
        self.assertEqual(stack.dedicated.local_settings(environment, 7979, release), (slot, {}))

    def test_registration_requires_live_matching_release_and_protocol(self):
        release = "sha256-" + "a" * 64
        row = ["ready", release, "9", "timestamp", "t"]
        self.assertTrue(stack.dedicated.is_registered(row, release, "9"))
        self.assertFalse(stack.dedicated.is_registered(row, release, "8"))
        row[4] = "f"
        self.assertFalse(stack.dedicated.is_registered(row, release, "9"))

    def test_rebuild_does_not_stop_server_that_owns_a_match(self):
        server = stack.dedicated
        slot = {"serverId": "d678e4f6-9779-4d00-ad0d-fc566b3457ac"}
        with tempfile.TemporaryDirectory() as runtime, patch.object(server, "RUNTIME", Path(runtime)):
            with patch.object(server, "running_command", return_value=f"sh -vdServerId {slot['serverId']}"):
                with patch.object(server, "query", return_value="busy"), patch.object(server, "stop") as stop:
                    with self.assertRaisesRegex(RuntimeError, "has a match"):
                        server.replace_if_idle(None, None, slot, 123, Path("/new/server"))
                    stop.assert_not_called()


class DedicatedBuildTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name) / "source"
        self.runtime = Path(directory.name) / "runtime"
        self.runtime.mkdir()
        self.source = self.root / "Assets" / "Gameplay.cs"
        self.source.parent.mkdir(parents=True)
        self.source.write_text("old")

    def build_player(self, args, cwd, check):
        output = cwd / "Builds/OnlineMac/Server/VaticanDefendor.app"
        data = output / "Data"
        (data / "Managed").mkdir(parents=True, exist_ok=True)
        (data / "app.info").write_text("Vatican\nActive Combat\n")
        (data / "Managed/VaticanDefendor.Network.Contracts.dll").touch()
        (output / "UnityPlayer.dylib").touch()
        binary = output / "Active Combat"
        binary.write_text((cwd / "Assets/Gameplay.cs").read_text() if (cwd / "Assets/Gameplay.cs").exists() else "deleted")
        binary.chmod(0o700)
        Path(args[args.index("-logFile") + 1]).write_text("[NetworkBuild] Server: done")
        return Mock(returncode=0)

    def prepare(self, build):
        server_build = stack.server_build
        with patch.object(server_build.sys, "platform", "darwin"):
            with patch.object(server_build, "editor_for", return_value=Path("/Unity")):
                with patch.object(server_build.subprocess, "run", build):
                    return server_build.prepare(self.root, self.runtime, None)

    def test_missing_changed_and_deleted_inputs_rebuild_while_unchanged_inputs_reuse(self):
        build = Mock(side_effect=self.build_player)
        first = self.prepare(build)
        self.assertEqual(first, self.prepare(build))
        self.assertEqual(build.call_count, 1)
        self.source.write_text("new")
        second = self.prepare(build)
        self.assertEqual(first.read_text(), "old")
        self.assertEqual(second.read_text(), "new")
        self.source.unlink()
        (self.root / "Packages").mkdir()
        (self.root / "Packages/manifest.json").write_text("{}")
        third = self.prepare(build)
        self.assertEqual(build.call_count, 3)
        self.assertEqual(third.read_text(), "deleted")

        third.unlink()
        rebuilt = self.prepare(build)
        self.assertEqual(build.call_count, 4)
        self.assertTrue(rebuilt.is_file())

    def test_failed_build_keeps_previous_artifact_and_receipt(self):
        first = self.prepare(Mock(side_effect=self.build_player))
        receipt = self.runtime / "server-build.json"
        previous = receipt.read_bytes()
        self.source.write_text("invalid")
        with self.assertRaisesRegex(RuntimeError, "build failed"):
            self.prepare(Mock(return_value=Mock(returncode=1)))
        self.assertEqual(receipt.read_bytes(), previous)
        self.assertEqual(first.read_text(), "old")


if __name__ == "__main__":
    unittest.main()
