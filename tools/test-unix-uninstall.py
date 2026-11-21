"""Exercise Unix uninstall options in an isolated home."""
import os
import pathlib
import subprocess
import tempfile
import unittest

UNINSTALLER = pathlib.Path(__file__).resolve().parents[1] / 'scripts/uninstall.sh'


class UninstallTests(unittest.TestCase):
    def run_uninstaller(self, *options, code, removes_agent):
        with tempfile.TemporaryDirectory(prefix='phonebox removal ') as directory:
            root = pathlib.Path(directory)
            home = root / 'home'
            app = home / 'Library/Application Support/PhoneBox/app'
            app.mkdir(parents=True)
            settings = app.parent / 'settings.json'
            settings.write_text('{}', encoding='utf-8')
            (app / 'PhoneBoxAgent').write_text('fixture', encoding='utf-8')
            fake_bin = root / 'bin'
            fake_bin.mkdir()
            calls = root / 'calls'
            for name, command in (
                ('uname', 'echo Darwin'),
                ('launchctl', 'echo launchctl >> "$PHONEBOX_TEST_CALLS"'),
                ('pkill', 'echo pkill >> "$PHONEBOX_TEST_CALLS"'),
            ):
                path = fake_bin / name
                path.write_text(f'#!/bin/sh\n{command}\n', encoding='utf-8')
                path.chmod(0o755)
            env = dict(os.environ, HOME=str(home),
                       PATH=str(fake_bin) + os.pathsep + os.environ['PATH'],
                       PHONEBOX_TEST_CALLS=str(calls))
            with UNINSTALLER.open('rb') as stream:
                result = subprocess.run(['/bin/bash', '-s', '--', *options], stdin=stream,
                                        env=env, capture_output=True, text=True, timeout=10)
            self.assertEqual(app.exists(), not removes_agent, result.stdout + result.stderr)
            self.assertEqual(calls.exists(), removes_agent)
            self.assertTrue(settings.exists())
            self.assertEqual(result.returncode, code)
            return result

    def test_help_never_removes_the_install(self):
        result = self.run_uninstaller('--help', code=0, removes_agent=False)
        self.assertIn('Usage:', result.stdout)

    def test_unknown_option_never_removes_the_install(self):
        result = self.run_uninstaller('--pugre', code=2, removes_agent=False)
        self.assertIn('unknown option: --pugre', result.stderr)

    def test_default_removal_keeps_settings(self):
        self.run_uninstaller(code=0, removes_agent=True)


if __name__ == '__main__':
    unittest.main()
