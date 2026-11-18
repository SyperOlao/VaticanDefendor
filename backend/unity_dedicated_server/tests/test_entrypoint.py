"""POSIX supervisor contract; this fixture is not a Unity/Linux Player test."""
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import time
import unittest


ENTRYPOINT = Path(__file__).resolve().parents[1] / "entrypoint.sh"


@unittest.skipUnless(os.name == "posix", "POSIX signals required")
class EntrypointTests(unittest.TestCase):
    def test_stop_signals_request_clean_child_exit_and_reap_it(self):
        for stop_signal in (signal.SIGTERM, signal.SIGINT):
            with self.subTest(signal=stop_signal), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                fixture = root / "server"
                fixture.write_text("""#!/bin/sh
set -eu
stop_file=
while [ "$#" -gt 0 ]; do
    if [ "$1" = -vdShutdownFile ]; then shift; stop_file=$1; fi
    shift
done
printf '%s' "$$" > "$VD_SUPERVISOR_TEST_READY"
while [ ! -f "$stop_file" ]; do sleep 0.05; done
printf '%s' clean-stop > "$VD_SUPERVISOR_TEST_STOPPED"
exit 0
""")
                fixture.chmod(0o755)
                ready = root / "ready"
                stopped = root / "stopped"
                env = dict(os.environ, VD_SERVER_EXECUTABLE=str(fixture),
                           VD_SUPERVISOR_TEST_READY=str(ready),
                           VD_SUPERVISOR_TEST_STOPPED=str(stopped))
                process = subprocess.Popen(["/bin/sh", str(ENTRYPOINT)], env=env,
                                           stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                           start_new_session=True)
                try:
                    deadline = time.monotonic() + 5
                    while not ready.exists() and process.poll() is None and time.monotonic() < deadline:
                        time.sleep(0.02)
                    self.assertTrue(ready.exists(), "child did not start")
                    child_pid = int(ready.read_text())
                    process.send_signal(stop_signal)
                    stdout, stderr = process.communicate(timeout=5)
                    self.assertEqual(process.returncode, 0, (stdout, stderr))
                    self.assertEqual(stopped.read_text(), "clean-stop")
                    with self.assertRaises(ProcessLookupError):
                        os.kill(child_pid, 0)
                finally:
                    if process.poll() is None:
                        os.killpg(process.pid, signal.SIGKILL)
                    process.communicate(timeout=5)

    def test_child_failure_is_not_reported_as_success(self):
        with tempfile.TemporaryDirectory() as directory:
            fixture = Path(directory) / "server"
            fixture.write_text("#!/bin/sh\nexit 7\n")
            fixture.chmod(0o755)
            result = subprocess.run(["/bin/sh", str(ENTRYPOINT)],
                                    env=dict(os.environ, VD_SERVER_EXECUTABLE=str(fixture)),
                                    capture_output=True, timeout=5)
            self.assertEqual(result.returncode, 7)


if __name__ == "__main__":
    unittest.main()
