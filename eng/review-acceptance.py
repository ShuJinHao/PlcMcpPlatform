"""Prepare a local review draft from real ValidationHost invocation records.
No auto-promotion, no online operation, and no vendor certification is produced.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import sys


def draft(records, output):
    output = Path(output).resolve()
    if output.exists(): raise ValueError('Choose a new review directory.')
    if not 0 < len(records) <= 256: raise ValueError('Select 1..256 invocation records for review.')
    parsed = []
    for path in map(Path, records):
        if path.stat().st_size > 2*1024*1024: raise ValueError('Invocation record is too large.')
        value = json.loads(path.read_text(encoding='utf-8'))
        if value.get('schemaVersion') != 1 or value.get('executionMode') != 'real' or value.get('simulated') is not False or value.get('response',{}).get('outcome') != 'succeeded' or value.get('request',{}).get('executionMode') != 'real' or value.get('response',{}).get('executionMode') != 'real':
            raise ValueError('Only successful real invocation records can be reviewed.')
        runtime = value.get('runtimeIdentity', {}); identity = value['identity']
        if runtime.get('status') != 'matched' or runtime.get('executableSha256') != identity['executable']['sha256'] or runtime.get('engineSha256') != identity['engine']['sha256']:
            raise ValueError('Observed process/engine identity is missing or mismatched.')
        if parsed and identity != parsed[0][1]['identity']: raise ValueError('Do not combine different IDE/executor identities.')
        parsed.append((path,value))
    output.mkdir(parents=True); (output/'evidence').mkdir()
    files=[]; candidates=[]
    # Pin observable request values by default. A reviewer must consciously decide coverage.
    fields=('objectId','applicationId','edit.parentId','edit.objectId','edit.language','edit.pouType','edit.dutType',
            'edit.configuration.member.kind','edit.configuration.member.language','edit.configuration.member.expectedTypeId',
            'edit.exchange.artifactId','edit.exchange.format','query.memberKind','query.expectedTypeId','exchange.format')
    for index,(path,value) in enumerate(parsed):
        key='case-'+str(index+1); target=output/'evidence'/(key+'.json'); shutil.copyfile(path,target)
        files.append(dict(id=key,path=target.relative_to(output).as_posix(),sha256=hashlib.sha256(target.read_bytes()).hexdigest()))
        conditions={}
        for field in fields:
            item=value['request']['payload']
            for part in field.split('.'): item=item.get(part) if isinstance(item,dict) else None
            if isinstance(item,(str,int)) and not isinstance(item,bool): conditions[field]=str(item)
        candidates.append(dict(operation=value['request']['operation'],coverage='constrained' if conditions else 'operation',conditions=conditions,evidenceIds=[key]))
    document=dict(schemaVersion=1,acceptanceId='',reviewedBy='',reviewedAt=None,reviewStatement='',identity=parsed[0][1]['identity'],evidence=files,grants=candidates)
    (output/'acceptance.draft.json').write_text(json.dumps(document,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    return dict(draft=str(output/'acceptance.draft.json'),accepted=False,records=len(files),next='Review evidence, narrow grants, fill reviewer/time/statement, then save as acceptance.json outside the release bundle.')


def main():
    parser=argparse.ArgumentParser(); parser.add_argument('--record',action='append',required=True); parser.add_argument('--output',required=True); args=parser.parse_args()
    print(json.dumps(draft(args.record,args.output),ensure_ascii=False)); return 0

if __name__=='__main__': sys.exit(main())
