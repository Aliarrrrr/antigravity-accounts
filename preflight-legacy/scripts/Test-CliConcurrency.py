"""Exercise real independent CLI processes against disposable, non-authenticated metadata."""
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import json
import shutil
import subprocess
import uuid

repo = Path(__file__).resolve().parent.parent
local_sdk = repo / '.tools/dotnet/dotnet.exe'
sdk = str(local_sdk) if local_sdk.exists() else shutil.which('dotnet')
if not sdk:
    raise RuntimeError('.NET SDK is required')
cli = repo / 'src/AGProfileHub.Cli/bin/Release/net10.0/AGProfileHub.Cli.dll'
root = repo / 'artifacts/cli-concurrency' / uuid.uuid4().hex
root.mkdir(parents=True)
checks = []

def call(*args, expected=0):
    result = subprocess.run([sdk, str(cli), '--data-root', str(root), '--json', *args], capture_output=True, text=True, encoding='utf-8', timeout=45)
    if expected == 0 and result.returncode != 0:
        raise AssertionError(f'{args[0]} failed with exit {result.returncode}: {result.stderr[:300]}')
    if expected != 0 and result.returncode == 0:
        raise AssertionError(f'{args[0]} unexpectedly succeeded')
    return json.loads(result.stdout) if result.stdout.strip() else None

def check(name, condition):
    checks.append({'name': name, 'passed': bool(condition)})
    if not condition:
        raise AssertionError(name)
    print('PASS', name)

try:
    with ThreadPoolExecutor(max_workers=6) as pool:
        created = list(pool.map(lambda i: call('create', f'并发 Profile {i}'), range(6)))
    profiles = call('list')
    check('six concurrent creates retained', len(profiles) == 6)
    check('JSON uses camelCase contract', all('id' in p and 'Id' not in p for p in profiles))
    check('UUIDs unique', len({p['id'] for p in profiles}) == 6)
    selected = created[0]['id']
    directory = root / 'profiles' / selected
    (directory / 'keep.txt').write_text('ordinary test fixture', encoding='utf-8')
    renamed = call('rename', selected, '新名字 with spaces')
    check('rename retains UUID and data', renamed['id'] == selected and (directory / 'keep.txt').exists())
    workspace = call('workspace', selected, str(repo))
    check('workspace metadata stored', workspace['workspacePath'] == str(repo))
    cleared = call('workspace', selected, 'none')
    check('workspace metadata clears', cleared['workspacePath'] is None)
    config = root / 'config/profiles.json'
    original = config.read_bytes()
    for label, bad in [('future schema', b'{"schemaVersion":999,"profiles":[]}'), ('corrupt JSON', b'{oops'), ('null profiles', b'{"schemaVersion":1,"profiles":null}'), ('null profile entry', b'{"schemaVersion":1,"profiles":[null]}')]:
        config.write_bytes(bad)
        call('list', expected=1)
        check(label + ' rejected without overwrite', config.read_bytes() == bad)
    config.write_bytes(original)
    call('show', '../outside', expected=1)
    check('invalid ID rejected', True)
    call('remove', selected)
    check('unregister preserves fixture', (directory / 'keep.txt').read_text(encoding='utf-8') == 'ordinary test fixture')
finally:
    (root / 'test-report.json').write_text(json.dumps({'schemaVersion': 1, 'checks': checks}, indent=2), encoding='utf-8')
    print('Evidence:', root)
