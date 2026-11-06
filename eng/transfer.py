"""Local CLI for bounded MCP artifact upload/download. Never imports or edits a project."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import sys
from mcp_client import McpClient


def upload_xml(client, path, format_name, key):
    path = Path(path)
    size = path.stat().st_size
    if not 0 < size <= 64 * 1024 * 1024: raise ValueError('XML must be nonempty and at most 64 MiB; the server may set a smaller limit.')
    with path.open('rb') as source:
        checksum = hashlib.sha256()
        for block in iter(lambda: source.read(65536), b''): checksum.update(block)
        digest = checksum.hexdigest()
    session = client.call('plc_exchange_upload_begin', dict(uploadKey=key, format=format_name, byteLength=size, sha256=digest))['value']
    if session.get('artifact'): return session['artifact']
    offset = session['nextOffset']
    with path.open('rb') as source:
        source.seek(offset)
        while offset < size:
            data = source.read(min(32768, size-offset))
            if not data: raise ValueError('Source file changed during upload.')
            reply = client.call('plc_exchange_upload_append', dict(uploadId=session['uploadId'], offset=offset, base64=base64.b64encode(data).decode()))['value']
            if reply['nextOffset'] != offset + len(data): raise ValueError('Unexpected upload offset; inspect the upload before retrying.')
            offset = reply['nextOffset']
        if source.read(1): raise ValueError('Source file grew during upload.')
    return client.call('plc_exchange_upload_complete', dict(uploadId=session['uploadId']))['value']['artifact']


def download_delivery(client, artifact_id, destination, expected_sha256):
    if artifact_id != expected_sha256 or len(expected_sha256) != 64: raise ValueError('Use the exact artifact ID and SHA-256 from delivery_create.')
    target = Path(destination).resolve()
    if target.exists(): raise FileExistsError('Destination already exists; choose a new filename.')
    target.parent.mkdir(parents=True, exist_ok=True)
    temporary = target.with_name(target.name + '.part')
    digest = hashlib.sha256(); offset = 0; length = None
    # An existing partial download is preserved rather than silently truncated.
    with temporary.open('xb') as output:
        while True:
            chunk = client.call('plc_delivery_read', dict(artifactId=artifact_id, offset=offset, maxBytes=32768))['value']
            artifact = chunk['artifact']
            if artifact['artifactId'] != artifact_id or artifact['sha256'] != expected_sha256: raise ValueError('Delivery identity changed.')
            if length is None: length = artifact['byteLength']
            if length != artifact['byteLength'] or not 0 < length <= 272 * 1024 * 1024: raise ValueError('Invalid delivery length.')
            data = base64.b64decode(chunk['base64'], validate=True)
            if len(data) > 32768 or chunk['offset'] != offset or chunk['nextOffset'] != offset+len(data) or offset+len(data) > length:
                raise ValueError('Invalid delivery chunk range.')
            if hashlib.sha256(data).hexdigest() != chunk['chunkSha256']: raise ValueError('Delivery chunk integrity mismatch.')
            output.write(data); digest.update(data); offset += len(data)
            if chunk['complete']:
                if offset != length: raise ValueError('Incomplete delivery.')
                break
            if not data: raise ValueError('Empty nonfinal delivery chunk.')
        if digest.hexdigest() != expected_sha256: raise ValueError('Complete ZIP integrity mismatch; partial file was retained.')
        output.flush(); os.fsync(output.fileno())
    # Atomic no-overwrite publication on the same filesystem.
    os.link(temporary, target); temporary.unlink()
    return dict(path=str(target), sha256=expected_sha256, byteLength=length)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--url', default='http://127.0.0.1:5088/mcp')
    sub = parser.add_subparsers(dest='command', required=True)
    upload = sub.add_parser('upload'); upload.add_argument('path'); upload.add_argument('--format', choices=['plcopen', 'codesys-native'], required=True); upload.add_argument('--key', required=True)
    download = sub.add_parser('download'); download.add_argument('artifact_id'); download.add_argument('--sha256', required=True); download.add_argument('--output', required=True)
    args = parser.parse_args(); key = os.environ.get('PLCMCP_API_KEY')
    if not key: parser.error('Set PLCMCP_API_KEY in the local environment.')
    client = McpClient(args.url, key)
    result = upload_xml(client, args.path, args.format, args.key) if args.command == 'upload' else download_delivery(client, args.artifact_id, args.output, args.sha256)
    print(json.dumps(result, ensure_ascii=False)); return 0


if __name__ == '__main__': sys.exit(main())
