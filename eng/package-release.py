"""Build versioned prerelease archives with per-file and per-archive hashes."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
from publish import publish
from release_metadata import metadata


def main():
    parser = argparse.ArgumentParser(); parser.add_argument('--output', required=True); parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--mode', action='append', choices=['production','fixture','validation']); args=parser.parse_args()
    output=Path(args.output).resolve(); output.mkdir(parents=True,exist_ok=True)
    rows=[]; release=metadata()
    for mode in args.mode or ['production','fixture','validation']:
        name='PlcMcpPlatform-'+release['version']+'-'+mode; directory=output/name
        if directory.exists() or (output/(name+'.zip')).exists(): raise ValueError('Release output already exists.')
        report=publish(mode,directory,args.dotnet)
        if report['sourceDirty']: raise ValueError('Prerelease archives require a clean source commit.')
        archive=Path(shutil.make_archive(str(output/name),'zip',root_dir=output,base_dir=name))
        digest=hashlib.sha256(archive.read_bytes()).hexdigest(); rows.append(dict(file=archive.name,sha256=digest,mode=mode,sourceCommit=report['sourceCommit']))
        shutil.rmtree(directory)
    (output/'SHA256SUMS').write_text(''.join(r['sha256']+'  '+r['file']+'\n' for r in rows),encoding='utf-8')
    (output/'prerelease.json').write_text(json.dumps(dict(**release,archives=rows),indent=2)+'\n',encoding='utf-8')
    print(json.dumps(rows))

if __name__=='__main__': main()
