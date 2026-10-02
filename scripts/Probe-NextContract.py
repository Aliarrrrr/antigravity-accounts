"""No-login, fresh-directory contract probe. Never reads authentication files.

Only package sources, binary hashes and a fixed token-store log marker are read.
Output is restricted to booleans/hashes, never raw vendor output or argv.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import tempfile
import time
import uuid


def package_sources(archive):
    with archive.open('rb') as stream:
        header = struct.unpack('<4I', stream.read(16))
        if header[3] > 32 * 1024 * 1024:
            raise ValueError('ASAR header too large')
        tree = json.loads(stream.read(header[3]))
        def member(name):
            node = tree
            for part in name.split('/'):
                node = node['files'][part]
            if node.get('unpacked') or node['size'] > 8 * 1024 * 1024:
                raise ValueError('Unsupported source member')
            stream.seek(8 + header[1] + int(node['offset']))
            return stream.read(node['size']).decode('utf-8')
        package = json.loads(member('package.json'))
        return package, {name: member(name) for name in ['dist/main.js', 'dist/paths.js', 'dist/languageServer.js']}


def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def run_server(server, version, root, extra, seconds):
    for folder in ['home/.gemini', 'roaming', 'local', 'temp']:
        (root / folder).mkdir(parents=True, exist_ok=True)
    env = dict(os.environ)
    for key in ['WSL_DISTRO_NAME', 'WSL_INTEROP', 'SSH_CONNECTION', 'SSH_CLIENT', 'SSH_TTY', 'container',
                'NODE_OPTIONS', 'ELECTRON_RUN_AS_NODE', 'ANTIGRAVITY_LS_ADDRESS', 'ANTIGRAVITY_CSRF_TOKEN']:
        env.pop(key, None)
    home = root / 'home'
    env.update(USERPROFILE=str(home), HOME=str(home), HOMEDRIVE=home.drive,
               HOMEPATH=str(home)[2:], APPDATA=str(root / 'roaming'), LOCALAPPDATA=str(root / 'local'),
               TEMP=str(root / 'temp'), TMP=str(root / 'temp'))
    env.update(extra)
    args = [str(server), '--standalone', '--override_ide_name', 'antigravity', '--subclient_type', 'hub',
            '--override_ide_version', version, '--override_user_agent_name', 'antigravity',
            '--http_server_port', '0', '--https_server_port', '0', '--csrf_token', uuid.uuid4().hex,
            '--gemini_dir', str(home / '.gemini'), '--app_data_dir', 'antigravity',
            '--disable_telemetry', '--use_mocked_data']
    with subprocess.Popen(args, env=env, cwd=root, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                          stderr=subprocess.PIPE, creationflags=subprocess.CREATE_NO_WINDOW) as proc:
        timed_out = False
        try:
            output, error = proc.communicate(timeout=seconds)
        except subprocess.TimeoutExpired:
            timed_out = True
            # Popen owns this exact live process handle; do not search or kill by name/PID tree.
            proc.kill()
            output, error = proc.communicate(timeout=10)
        text = (output + error).decode('utf-8', 'replace')
        # Only this fresh probe's LS log is eligible. No token/database files are read.
        logs = list((home / '.gemini').glob('**/language_server*.log'))
        marker = 'Using file-based token storage' in text
        for log in logs:
            if log.stat().st_size <= 8 * 1024 * 1024:
                marker |= 'Using file-based token storage' in log.read_text(encoding='utf-8', errors='replace')
        return dict(fileTokenStoreMarker=marker, survivedObservation=timed_out, exitCode=proc.returncode,
                    homeCreated=(home / '.gemini').is_dir(), rawOutputPersisted=False)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--executable', default=str(Path(os.environ['LOCALAPPDATA']) / 'Programs/antigravity/Antigravity.exe'))
    parser.add_argument('--report', required=True)
    parser.add_argument('--seconds', type=int, default=12)
    args = parser.parse_args()
    exe = Path(args.executable).resolve(strict=True)
    archive = exe.parent / 'resources/app.asar'
    server = exe.parent / 'resources/bin/language_server.exe'
    package, sources = package_sources(archive)
    root = Path(tempfile.mkdtemp(prefix='agprofile-next-probe-'))
    report = dict(schemaVersion=1, strategyRevision=2, version=package['version'], scratchRoot=str(root),
                  fingerprints={str(p.relative_to(exe.parent)): sha(p) for p in [exe, archive, server]},
                  sourceHashes={name: hashlib.sha256(source.encode()).hexdigest() for name, source in sources.items()},
                  workspaceContract='unsupported-until-proven', manualAcceptance=False, runs={})
    for name, env in [('control', {}), ('wsl', {'WSL_DISTRO_NAME': 'AGPHNextProbe'}),
                      ('ssh', {'SSH_CONNECTION': '127.0.0.1 50000 127.0.0.1 22', 'SSH_CLIENT': '127.0.0.1 50000 22'})]:
        report['runs'][name] = run_server(server, package['version'], root / name, env, args.seconds)
    Path(args.report).write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(report, indent=2))


if __name__ == '__main__':
    main()
