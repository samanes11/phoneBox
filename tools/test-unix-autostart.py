"""Exercise generated Unix login configuration without touching real services."""
import os
import pathlib
import plistlib
import subprocess
import sys
import tempfile
import unittest

INSTALL = pathlib.Path(__file__).resolve().parents[1] / 'scripts/install.sh'


class AutostartTests(unittest.TestCase):
    def install(self, os_name, root):
        commands = root / 'bin'
        commands.mkdir()

        def executable(name, program):
            path = commands / name
            path.write_text('#!/bin/sh\n' + program, encoding='utf-8')
            path.chmod(0o755)

        executable('uname', f'case "$1" in -s) echo {os_name};; -m) echo arm64;; esac\n')
        for name in ('pkill', 'codesign', 'systemctl', 'launchctl'):
            executable(name, 'exit 0\n')
        executable('dotnet', """
while [ "$#" -gt 0 ]; do
  if [ "$1" = -o ]; then shift; output="$1"; fi
  shift
done
mkdir -p "$output"
printf '%s\n' '#!/bin/sh' 'printf "%s\\n" "$PHONEBOX_TEST_INSTALL_DIR"' > "$output/PhoneBoxAgent"
chmod +x "$output/PhoneBoxAgent"
""")
        app = root / ('PhoneBox & extras' if os_name == 'Darwin' else 'PhoneBox 100%')
        env = dict(
            os.environ,
            PATH=str(commands) + os.pathsep + os.environ['PATH'],
            HOME=str(root),
            TMPDIR=str(root),
            DOTNET_ROOT=str(root / 'dotnet & runtime'),
            XDG_DATA_HOME=str(root / 'data directory'),
            XDG_CONFIG_HOME=str(root / 'config directory'),
            PHONEBOX_TEST_INSTALL_DIR=str(app),
        )
        bash = sys.argv[1] if len(sys.argv) > 1 else '/bin/bash'
        result = subprocess.run(
            [bash, str(INSTALL), '--autostart'],
            env=env, capture_output=True, text=True, timeout=20,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        return app, env

    def test_linux_service_quotes_paths_and_preserves_xdg(self):
        with tempfile.TemporaryDirectory(prefix='phonebox ') as tmp:
            root = pathlib.Path(tmp)
            app, env = self.install('Linux', root)
            unit = (root / '.config/systemd/user/phonebox.service').read_text(encoding='utf-8')
            self.assertIn(f'ExecStart="{app / "PhoneBoxAgent"}"'.replace('%', '%%'), unit)
            for name in ('DOTNET_ROOT', 'XDG_DATA_HOME', 'XDG_CONFIG_HOME'):
                self.assertIn(f'Environment="{name}={env[name]}"', unit)

    def test_macos_launch_agent_keeps_xml_metacharacters_in_paths(self):
        with tempfile.TemporaryDirectory(prefix='phonebox ') as tmp:
            root = pathlib.Path(tmp)
            app, env = self.install('Darwin', root)
            plist = root / 'Library/LaunchAgents/net.oxog.phonebox.agent.plist'
            with plist.open('rb') as stream:
                config = plistlib.load(stream)
            self.assertEqual(config['ProgramArguments'], [str(app / 'PhoneBoxAgent')])
            self.assertEqual(config['EnvironmentVariables']['DOTNET_ROOT'], env['DOTNET_ROOT'])


if __name__ == '__main__':
    unittest.main(argv=[sys.argv[0]])
