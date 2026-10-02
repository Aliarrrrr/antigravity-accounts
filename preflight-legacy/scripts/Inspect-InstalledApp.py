"""Read only packaged application source; never read a user profile or credentials.

Print evidence booleans and package hashes instead of distributing vendor code.
Usage: python scripts/Inspect-InstalledApp.py [path/to/app.asar]
"""
import hashlib
import json
import os
import struct
import sys
from pathlib import Path

archive = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(os.environ['LOCALAPPDATA']) / 'Programs/antigravity/resources/app.asar'
with archive.open('rb') as stream:
    prefix = stream.read(16)
    if len(prefix) != 16:
        raise ValueError('Incomplete ASAR header')
    header = struct.unpack('<4I', prefix)
    if header[3] > 32 * 1024 * 1024:
        raise ValueError('ASAR header exceeds inspection limit')
    entries = json.loads(stream.read(header[3]))
    payload = 8 + header[1]

    def read_member(name):
        node = entries
        for component in name.split('/'):
            node = node['files'][component]
        if node.get('unpacked') or node['size'] > 4 * 1024 * 1024:
            raise ValueError('Unsupported source member')
        stream.seek(payload + int(node['offset']))
        return stream.read(node['size']).decode('utf-8')

    package = json.loads(read_member('package.json'))
    main = read_member('dist/main.js')
    paths = read_member('dist/paths.js')
    backend = read_member('dist/languageServer.js')
    utils = read_member('dist/utils.js')
    loading_overlay = read_member('dist/loadingOverlay.js')
    print(json.dumps({
        'schemaVersion': 1,
        'package': {key: package.get(key) for key in ['name', 'version', 'description']},
        'staticEvidence': {
            'electronSingleton': 'requestSingleInstanceLock()' in main,
            'homeGeminiStorage': "homedir(), '.gemini'" in paths,
            'electronUserDataStorage': "getPath('userData')" in paths,
            'sharedProtocolRegistration': 'setAsDefaultProtocolClient(PROTOCOL)' in main,
            'backendNamedAppData': "'--app_data_dir'" in backend,
            'windowHasDidFailLoadHandler': "'did-fail-load'" in utils,
            'windowHasLoadRetry': '.reload(' in utils or 'loadURL(' in utils[utils.find("'did-fail-load'"):],
            'windowHasLoadingOverlay': 'LoadingOverlay' in utils or 'loadingOverlay' in utils,
        },
        'sourceHashes': {name: hashlib.sha256(content.encode()).hexdigest() for name, content in [('dist/main.js', main), ('dist/paths.js', paths), ('dist/languageServer.js', backend), ('dist/utils.js', utils), ('dist/loadingOverlay.js', loading_overlay)]},
        'authIsolation': 'not-evaluated-by-static-inspection',
        'note': 'Static inspection does not evaluate authentication. Manual A/B verification found concurrent Google-account isolation incompatible on Desktop 2.12.2.'
    }, indent=2))
