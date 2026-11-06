import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
import zipfile

ENG=Path(__file__).resolve().parents[2]/'eng'
sys.path.insert(0,str(ENG))
spec=importlib.util.spec_from_file_location('release_candidate',ENG/'publish-github-release.py')
release=importlib.util.module_from_spec(spec);spec.loader.exec_module(release)


class ReleaseCandidateTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory();self.root=Path(self.tmp.name);self.target='a'*40
    def tearDown(self):self.tmp.cleanup()
    def packages(self,changed_mode=None,wrong_commit=False):
        metadata=release.metadata();rows=[]
        for mode in ['production','fixture','validation']:
            name='PlcMcpPlatform-'+metadata['version']+'-'+mode
            content=b'different' if mode==changed_mode else b'shared execution code'
            files=[dict(path='bin/PlcMcp.McpHost.dll',byteLength=len(content),sha256=hashlib.sha256(content).hexdigest())]
            manifest=dict(sourceCommit='b'*40 if wrong_commit else self.target,sourceTree='c'*40,sourceDirty=False,
                          mode=mode,version=metadata['version'],engineeringReady=False,files=files)
            path=self.root/(name+'.zip')
            with zipfile.ZipFile(path,'w') as archive:
                archive.writestr(name+'/bin/PlcMcp.McpHost.dll',content)
                archive.writestr(name+'/release.json',json.dumps(manifest))
            rows.append(dict(file=path.name,mode=mode,sourceCommit=self.target,sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
        (self.root/'prerelease.json').write_text(json.dumps(dict(**metadata,archives=rows)))
        (self.root/'SHA256SUMS').write_text(''.join(r['sha256']+'  '+r['file']+'\n' for r in rows))
    def test_same_source_and_execution_code_in_three_modes(self):
        self.packages();metadata,assets=release.validate(self.root,self.target)
        self.assertTrue(metadata['prerelease']);self.assertFalse(metadata['realIdeValidated']);self.assertEqual(5,len(assets))
    def test_different_commit_or_execution_code_is_rejected(self):
        self.packages(wrong_commit=True)
        with self.assertRaisesRegex(ValueError,'source_or_mode'):release.validate(self.root,self.target)
        self.packages(changed_mode='fixture')
        with self.assertRaisesRegex(ValueError,'execution_code'):release.validate(self.root,self.target)
    def test_archive_and_inventory_tampering_is_rejected(self):
        self.packages();p=next(self.root.glob('*.zip'));p.write_bytes(p.read_bytes()+b'tamper')
        with self.assertRaisesRegex(ValueError,'checksum'):release.validate(self.root,self.target)
        self.packages();(self.root/'SHA256SUMS').write_text('not the inventory')
        with self.assertRaisesRegex(ValueError,'checksum_inventory'):release.validate(self.root,self.target)
    def test_partial_existing_release_cannot_be_reported_as_success(self):
        self.packages();metadata,assets=release.validate(self.root,self.target);tag='v'+metadata['version']
        existing=dict(tag_name=tag,prerelease=True,draft=False,assets=[dict(name=p.name,state='uploaded',size=p.stat().st_size) for p in assets])
        release.verify_existing(existing,tag,assets)
        existing['assets'][0]['state']='starter'
        with self.assertRaisesRegex(ValueError,'incomplete'):release.verify_existing(existing,tag,assets)
        existing['assets'].pop(0)
        with self.assertRaisesRegex(ValueError,'incomplete'):release.verify_existing(existing,tag,assets)
