"""Release bundle integrity helpers; a manifest is integrity evidence, not a signature."""
import hashlib
import json
from pathlib import Path, PurePosixPath

HOSTS = {
    'production': ('src/hosts/PlcMcp.McpHost', 'PlcMcp.McpHost.dll'),
    'fixture': ('tests/PlcMcp.TestHost', 'PlcMcp.TestHost.dll'),
    'validation': ('src/hosts/PlcMcp.ValidationHost', 'PlcMcp.ValidationHost.dll'),
}


def sha256(path):
    checksum = hashlib.sha256()
    with Path(path).open('rb') as source:
        for block in iter(lambda: source.read(65536), b''): checksum.update(block)
    return checksum.hexdigest()


def verify_bundle(directory):
    root = Path(directory).resolve()
    metadata = root/'release.json'
    if metadata.is_symlink() or metadata.stat().st_size > 1024*1024: raise ValueError('Invalid release manifest.')
    manifest = json.loads(metadata.read_text(encoding='utf-8'))
    if manifest.get('schemaVersion') != 1 or manifest.get('mode') not in HOSTS or manifest.get('engineeringReady') is not False:
        raise ValueError('Unknown release mode or evidence.')
    if manifest.get('entryPoint') != 'bin/'+HOSTS[manifest['mode']][1]: raise ValueError('Unexpected entry point.')
    rows = manifest.get('files', [])
    if not 0 < len(rows) <= 4000 or len({r['path'] for r in rows}) != len(rows): raise ValueError('Invalid release inventory.')
    listed = set()
    for row in rows:
        name = row['path']; relative = PurePosixPath(name)
        if relative.is_absolute() or '\\' in name or ':' in name or any(p in ('', '.', '..') for p in name.split('/')):
            raise ValueError('Invalid release path.')
        current = root
        for part in relative.parts:
            current = current/part
            if current.is_symlink(): raise ValueError('Release links are not supported.')
        if not current.is_file() or current.stat().st_size != row['byteLength'] or sha256(current) != row['sha256']:
            raise ValueError('Release integrity mismatch: '+name)
        listed.add(name)
    actual = {p.relative_to(root).as_posix() for p in root.rglob('*') if p.is_file() and p != metadata and '__pycache__' not in p.parts}
    if actual != listed or manifest['entryPoint'] not in listed: raise ValueError('Release file inventory changed.')
    return manifest
