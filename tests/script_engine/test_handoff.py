import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ENG = Path(__file__).resolve().parents[2]/'eng'
sys.path.insert(0, str(ENG))
import dev


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, ENG/path)
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    return module


handoff = load('windows_handoff_test', 'windows-regression.py')
model = load('model_handoff_test', 'model-regression.py')


class HandoffTests(unittest.TestCase):
    def test_launcher_refuses_reused_pid_even_when_process_is_running(self):
        with tempfile.TemporaryDirectory() as tmp, patch.object(dev, 'DEV', Path(tmp)):
            identity = 'stable-start dotnet host --PlcMcpDevOwner=owner'
            (Path(tmp)/'host.json').write_text(json.dumps(dict(pid=123, owner='owner', identitySha256=hashlib.sha256(identity.encode()).hexdigest())))
            with patch.object(dev, 'process_identity', return_value=identity), patch.object(os, 'kill') as kill:
                self.assertEqual(dev.owned_state()['pid'], 123)
                kill.assert_not_called()
            with patch.object(dev, 'process_identity', return_value='different process'), patch.object(os, 'kill') as kill:
                with self.assertRaisesRegex(ValueError, 'identity'): dev.stop()
                kill.assert_not_called()

    @unittest.skipIf(os.name != 'posix', 'POSIX local credential permissions')
    def test_provider_file_requires_private_permissions_and_rejects_symlink(self):
        with tempfile.TemporaryDirectory() as tmp:
            key = Path(tmp)/'key'; dev.private_write(key, 'private-placeholder\n')
            self.assertEqual(dev.read_private(key), 'private-placeholder')
            link = Path(tmp)/'link'; link.symlink_to(key)
            with self.assertRaises(ValueError): dev.read_private(link)
            key.chmod(0o644)
            with self.assertRaisesRegex(ValueError, '600'): dev.read_private(key)

    def test_object_discovery_does_not_guess_ambiguous_ids_or_accept_stale_ids(self):
        objects = [dict(id='device', kind='device', parentId='project', name='Controller'),
                   dict(id='app', kind='application', parentId='device', name='Application'),
                   dict(id='pou', kind='object', parentId='app', name='PLC_PRG'),
                   dict(id='libs', kind='library_manager', parentId='app', name='LibraryManager')]
        values, evidence = handoff.discover_ids(objects, dict(programName='PLC_PRG'))
        self.assertEqual(values['applicationId'], 'app'); self.assertEqual(values['programId'], 'pou')
        self.assertEqual(values['controllerId'], 'device'); self.assertEqual(values['libraryManagerId'], 'libs')
        with self.assertRaisesRegex(ValueError, 'programId'):
            handoff.discover_ids(objects, dict(programName='PLC_PRG', programId='stale'))
        values, evidence = handoff.discover_ids(objects+[dict(objects[1], id='app2')], dict(programName='PLC_PRG'))
        self.assertNotIn('applicationId', values)
        self.assertEqual(evidence['applicationId']['source'], 'manual_selection_required')
        nested = [dict(o, parentId='logic') if o['id']=='app' else dict(o, parentId='folder') if o['id']=='pou' else o for o in objects]
        nested += [dict(id='logic', kind='object', parentId='device'), dict(id='folder', kind='object', parentId='app')]
        values, _ = handoff.discover_ids(nested, dict(programName='PLC_PRG'))
        self.assertEqual(values['controllerId'], 'device'); self.assertEqual(values['programId'], 'pou')

    def test_cli_compatibility_is_not_version_equality(self):
        with patch.object(model.subprocess, 'check_output', side_effect=['1.18.2\n', 'run --format json --model provider/model']):
            result = model.check_cli('opencode', Path('.'), {})
        self.assertEqual(result['openCodeVersion'], '1.18.2'); self.assertFalse(result['matchesCiBaseline'])
        with patch.object(model.subprocess, 'check_output', side_effect=['2.0.0\n', 'different protocol']):
            with self.assertRaisesRegex(ValueError, 'protocol'): model.check_cli('opencode', Path('.'), {})

    def test_missing_provider_credentials_is_skipped_not_passed(self):
        import argparse
        args = argparse.Namespace(execute=True, model='deepseek/deepseek-flash', provider_env=['HANDOFF_TEST_API_KEY'])
        with patch.dict(os.environ, {}, clear=True):
            report = {}; result = model.run(args, report)
        self.assertEqual(result, 3); self.assertEqual(report['status'], 'skipped')


if __name__ == '__main__': unittest.main()
