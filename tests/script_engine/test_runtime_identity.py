import hashlib
import importlib.util
from pathlib import Path
import sys
import types
import uuid
from unittest.mock import patch
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('runtime_identity', Path(__file__).resolve().parents[2]/'src/adapters/PlcMcp.Inovance/Scripts/runtime_identity.py')
identity = importlib.util.module_from_spec(spec); spec.loader.exec_module(identity)

class RuntimeIdentityTests(unittest.TestCase):
    def setUp(self):
        self.work = tempfile.TemporaryDirectory(); self.root = Path(self.work.name)
        self.exe = self.root/'ide.exe'; self.exe.write_bytes(b'ide')
        self.engine = self.root/'ScriptEngine.dll'; self.engine.write_bytes(b'engine')
        self.expected = dict(engineName=self.engine.name, executableSha256=identity.sha256(str(self.exe)), engineSha256=identity.sha256(str(self.engine)))
        self.assemblies = [('ScriptEngine', str(self.engine), '3.5.11.0')]
    def tearDown(self): self.work.cleanup()
    def test_observed_process_and_engine_match(self):
        self.assertEqual('matched', identity.verify(self.expected, str(self.exe), self.assemblies)['status'])
    def test_changed_engine_and_process_rejected(self):
        self.engine.write_bytes(b'changed')
        with self.assertRaises(ValueError): identity.verify(self.expected, str(self.exe), self.assemblies)
        self.engine.write_bytes(b'engine'); self.exe.write_bytes(b'changed')
        with self.assertRaises(ValueError): identity.verify(self.expected, str(self.exe), self.assemblies)
    def test_missing_or_multiple_engines_rejected(self):
        for assemblies in [[],self.assemblies*2]:
            with self.assertRaises(ValueError): identity.verify(self.expected, str(self.exe), assemblies)

    def test_worker_checks_identity_before_touching_project(self):
        folder=Path(__file__).resolve().parents[2]/'src/adapters/PlcMcp.Inovance/Scripts'
        sys.path.insert(0,str(folder))
        try:
            spec=importlib.util.spec_from_file_location('identity_guard_worker',folder/'engineering_worker.py')
            worker=importlib.util.module_from_spec(spec); spec.loader.exec_module(worker)
            request=dict(protocolVersion=1,executionMode='real',operation='project.open',requestId=str(uuid.uuid4()),jobId=str(uuid.uuid4()),workspaceId=str(uuid.uuid4()),
                deadlineUtc='2099-01-01T00:00:00Z', payload=dict(projectPath=str(self.root/'never-open.project'),runtimeIdentityExpected=self.expected))
            def failed(_): raise ValueError('mismatch')
            with patch.dict(sys.modules,{'runtime_identity':types.SimpleNamespace(observe=failed)}):
                result=worker.execute(request,{})
            self.assertEqual('runtime_identity_mismatch',result['errorCode'])
            self.assertEqual('failed',result['outcome'])
        finally: sys.path.remove(str(folder))
