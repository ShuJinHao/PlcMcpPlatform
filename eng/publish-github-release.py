"""Validate all candidate bundles, then publish an immutable GitHub prerelease from verified main."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import subprocess
import zipfile
from release_metadata import metadata


def validate(directory, target):
    root=Path(directory); release=metadata(); report=json.loads((root/'prerelease.json').read_text())
    if any(report.get(k) != v for k,v in release.items()): raise ValueError('release_metadata_mismatch')
    rows=report['archives']
    if len(rows)!=3 or {r['mode'] for r in rows}!={'production','fixture','validation'}: raise ValueError('three_modes_required')
    if not re.fullmatch('[a-f0-9]{40}',target): raise ValueError('exact_source_commit_required')
    sums=''.join(r['sha256']+'  '+r['file']+'\n' for r in rows)
    if (root/'SHA256SUMS').read_text()!=sums: raise ValueError('checksum_inventory_mismatch')
    shared=[]; trees=set()
    for row in rows:
        name='PlcMcpPlatform-'+release['version']+'-'+row['mode']
        if row['file']!=name+'.zip' or row['sourceCommit']!=target: raise ValueError('archive_source_mismatch')
        archive=root/row['file']
        if archive.is_symlink() or archive.stat().st_size>512*1024*1024: raise ValueError('archive_limit')
        if hashlib.sha256(archive.read_bytes()).hexdigest()!=row['sha256']: raise ValueError('archive_checksum_mismatch')
        with zipfile.ZipFile(archive) as bundle:
            infos=[i for i in bundle.infolist() if not i.is_dir()]
            if len(infos)>4001 or sum(i.file_size for i in infos)>1024*1024*1024 or len({i.filename for i in infos})!=len(infos): raise ValueError('archive_inventory_invalid')
            for info in infos:
                path=PurePosixPath(info.filename)
                if '\\' in info.filename or ':' in info.filename or path.parts[0]!=name or any(p in ('..','.') for p in path.parts): raise ValueError('archive_path_invalid')
            manifest=json.loads(bundle.read(name+'/release.json'))
            if manifest.get('sourceCommit')!=target or manifest.get('sourceDirty') is not False or manifest.get('version')!=release['version'] or manifest.get('mode')!=row['mode'] or manifest.get('engineeringReady') is not False:
                raise ValueError('bundle_source_or_mode_invalid')
            tree=manifest.get('sourceTree','')
            if not re.fullmatch('[a-f0-9]{40}',tree): raise ValueError('source_tree_missing')
            trees.add(tree)
            files=manifest['files']
            if len({f['path'] for f in files})!=len(files) or {i.filename for i in infos}!={name+'/'+f['path'] for f in files}|{name+'/release.json'}: raise ValueError('bundle_inventory_mismatch')
            for entry in files:
                info=bundle.getinfo(name+'/'+entry['path'])
                with bundle.open(info) as content:
                    digest=hashlib.sha256()
                    for block in iter(lambda:content.read(65536),b''):digest.update(block)
                if info.file_size!=entry['byteLength'] or digest.hexdigest()!=entry['sha256']: raise ValueError('bundle_file_checksum_mismatch')
            shared.append({f['path']:f['sha256'] for f in files if f['path'].startswith('bin/Scripts/') or
                           re.fullmatch(r'bin/PlcMcp\..+\.dll',f['path']) and not any(x in f['path'] for x in ('TestHost','ValidationHost'))})
    if len(trees)!=1 or not shared[0] or any(s!=shared[0] for s in shared[1:]): raise ValueError('bundle_execution_code_differs')
    return release, [root/r['file'] for r in rows]+[root/'SHA256SUMS',root/'prerelease.json']


def verify_existing(existing, tag, assets):
    if existing.get('tag_name') != tag or not existing.get('prerelease') or existing.get('draft'):
        raise ValueError('existing_release_is_not_the_expected_prerelease')
    uploaded=existing.get('assets',[])
    if len(uploaded)!=len(assets) or {a.get('name') for a in uploaded}!={p.name for p in assets} or any(
            a.get('state')!='uploaded' or not isinstance(a.get('size'),int) or a['size']<=0 for a in uploaded):
        raise ValueError('existing_release_incomplete_requires_review')


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--directory',required=True);parser.add_argument('--target',required=True)
    parser.add_argument('--repository');parser.add_argument('--publish',action='store_true');args=parser.parse_args()
    release,assets=validate(args.directory,args.target); tag='v'+release['version']
    if not args.publish: print(json.dumps(dict(verified=True,tag=tag,sourceCommit=args.target)));return 0
    if not args.repository or not re.fullmatch('[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+',args.repository): raise ValueError('repository_required')
    found=subprocess.run(['gh','api',f'repos/{args.repository}/releases/tags/{tag}'],capture_output=True,text=True)
    if found.returncode==0:
        existing=json.loads(found.stdout)
        verify_existing(existing,tag,assets)
        print(json.dumps(dict(status='existing_version_preserved',url=existing['html_url'])));return 0
    if 'HTTP 404' not in found.stderr: raise ValueError('release_lookup_failed')
    # A pre-existing tag without a release requires review; never repoint it implicitly.
    tagged=subprocess.run(['gh','api',f'repos/{args.repository}/git/ref/tags/{tag}'],capture_output=True,text=True)
    if tagged.returncode==0 and json.loads(tagged.stdout).get('object',{}).get('sha')!=args.target: raise ValueError('existing_tag_target_mismatch')
    if tagged.returncode!=0 and 'HTTP 404' not in tagged.stderr: raise ValueError('tag_lookup_failed')
    notes=Path(__file__).resolve().parents[1]/'release/notes.md'
    subprocess.run(['gh','release','create',tag,*[str(p) for p in assets],'--repo',args.repository,'--target',args.target,
                    '--prerelease','--title','PLC MCP '+release['version'],'--notes-file',str(notes)],check=True)
    return 0


if __name__=='__main__': raise SystemExit(main())
