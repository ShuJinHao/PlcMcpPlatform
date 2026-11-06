import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('review_acceptance', Path(__file__).resolve().parents[2]/'eng/review-acceptance.py')
review = importlib.util.module_from_spec(spec); spec.loader.exec_module(review)

class AcceptanceReviewTests(unittest.TestCase):
    def setUp(self):
        self.work=tempfile.TemporaryDirectory(); self.root=Path(self.work.name); self.record=self.root/'run.json'
        self.value=dict(schemaVersion=1,executionMode='real',simulated=False,identity=dict(executable=dict(sha256='a'),engine=dict(sha256='b')),
            runtimeIdentity=dict(status='matched',executableSha256='a',engineSha256='b'),
            request=dict(operation='pou.create',executionMode='real',payload=dict(edit=dict(language='ST',pouType='Program'))),
            response=dict(outcome='succeeded',executionMode='real'))
    def tearDown(self): self.work.cleanup()
    def write(self): self.record.write_text(json.dumps(self.value),encoding='utf-8')
    def test_draft_remains_unapproved_and_preserves_scope(self):
        self.write(); result=review.draft([self.record],self.root/'review')
        draft=json.loads(Path(result['draft']).read_text())
        self.assertFalse(result['accepted']); self.assertEqual('',draft['reviewedBy']); self.assertIsNone(draft['reviewedAt'])
        self.assertEqual({'edit.language':'ST','edit.pouType':'Program'},draft['grants'][0]['conditions'])
        self.assertTrue((self.root/'review/evidence/case-1.json').is_file())
    def test_fixture_and_missing_runtime_refused(self):
        for change in [dict(simulated=True),dict(executionMode='fixture'),dict(runtimeIdentity={})]:
            value=dict(self.value);self.value.update(change);self.write()
            with self.assertRaises(ValueError):review.draft([self.record],self.root/'review')
            self.value=value
        self.assertFalse((self.root/'review').exists())
