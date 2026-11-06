"""Invocation-level ScriptEngine logic tests; not vendor compatibility evidence."""
import datetime
import hashlib
import importlib.util
import pathlib
import tempfile
import types
import unittest
import uuid

SCRIPT = pathlib.Path(__file__).resolve().parents[2] / 'src/adapters/PlcMcp.Inovance/Scripts/engineering_worker.py'
spec = importlib.util.spec_from_file_location('engineering_worker', SCRIPT)
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


class Text:
    def __init__(self, value): self.value, self.length = value, len(value)
    def get_text(self, offset, length): return self.value[offset:offset + length]


class Node:
    def __init__(self, name, children=(), application=False):
        self.type = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
        self.guid, self.name, self.children = str(uuid.uuid4()), name, list(children)
        self.is_application = application
        self.has_textual_declaration = self.has_textual_implementation = False
        self.build_action = lambda: None
    def get_children(self, recursive):
        assert recursive is False
        return self.children
    def get_name(self): return self.name
    def build(self): self.build_action()


class Project(Node):
    dirty = False
    closed = False
    fail_close = False
    def close(self):
        if self.fail_close: raise RuntimeError('close failed')
        self.closed = True
    def save(self): raise AssertionError('must not save')


class Projects:
    def __init__(self, project): self.project, self.primary, self.calls = project, None, []
    def open(self, path, **options):
        self.calls.append((path, options))
        return self.project


class System:
    def __init__(self): self.messages = {'build': []}
    def get_message_categories(self): return list(self.messages)
    def clear_messages(self, category): self.messages[category] = []
    def get_message_objects(self, category): return self.messages[category]


class EngineeringWorkerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = pathlib.Path(self.temp.name) / 'sample.project'
        self.path.write_bytes(b'test fixture, not a PLC project')
        self.program = Node('PLC_PRG')
        self.program.has_textual_declaration = self.program.has_textual_implementation = True
        self.program.textual_declaration = Text('PROGRAM PLC_PRG\nVAR\n计数: INT;\nEND_VAR')
        self.program.textual_implementation = Text('计数 := 计数 + 1;')
        self.app = Node('Application', [self.program], True)
        self.project = Project('Project', [self.app])
        self.projects, self.system = Projects(self.project), System()
        self.bindings = dict(projects=self.projects, system=self.system, VersionUpdateFlags=types.SimpleNamespace(NoUpdates='no-updates'))
    def request(self, operation='project.open', **changes):
        request = dict(protocolVersion=1, requestId=str(uuid.uuid4()), jobId=str(uuid.uuid4()), workspaceId=str(uuid.uuid4()),
                       operation=operation, executionMode='real', expectedRevision=hashlib.sha256(self.path.read_bytes()).hexdigest(),
                       deadlineUtc=(datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(minutes=1)).isoformat(),
                       payload=dict(projectPath=str(self.path), maxObjects=256, objectId=self.program.guid, applicationId=self.app.guid))
        request.update(changes)
        return request
    def run_operation(self, operation='project.open', **changes): return worker.execute(self.request(operation, **changes), self.bindings)
    def test_source_view_uses_actual_markers_and_does_not_guess_language(self):
        result = self.run_operation('object.source.read')
        self.assertEqual('succeeded', result['outcome'])
        self.assertEqual('pou', result['payload']['kind'])
        self.assertEqual('textual', result['payload']['implementationKind'])
        self.assertEqual(self.program.guid, result['payload']['objectId'])
        self.assertNotIn('language', result['payload'])
        self.assertTrue(self.project.closed)
    def test_graphical_source_keeps_declaration_without_inventing_text(self):
        self.program.has_textual_implementation = False
        result = self.run_operation('object.source.read')['payload']
        self.assertEqual('pou', result['kind'])
        self.assertIsNone(result['implementation'])
        self.assertEqual('graphical_or_unavailable', result['implementationKind'])
    def test_source_kind_classification_preserves_unknown_objects(self):
        for source, kind in [('VAR_GLOBAL\nA: BOOL;\nEND_VAR', 'gvl'), ('TYPE PLC_PRG : INT; END_TYPE', 'dut'), ('METHOD Run : BOOL', 'object')]:
            self.program.textual_declaration = Text(source)
            self.program.has_textual_implementation = False
            self.assertEqual(kind, worker.source_view(self.program)['kind'])
    def test_source_for_nontextual_configuration_retains_identity(self):
        source = worker.source_view(self.app)
        self.assertEqual('application', source['kind'])
        self.assertEqual(self.app.guid, source['objectId'])
        self.assertIsNone(source['declaration'])
        self.assertEqual('none', source['implementationKind'])
    def test_open_uses_no_updates_readonly_and_closes_its_owned_session(self):
        result = self.run_operation()
        self.assertEqual('succeeded', result['outcome'])
        self.assertEqual(dict(primary=True, update_flags='no-updates', allow_readonly=True), self.projects.calls[0][1])
        self.assertTrue(self.project.closed)
        self.assertTrue(result['payload']['sessionClosedAfterOperation'])
    def test_existing_user_session_is_not_closed_or_replaced(self):
        self.projects.primary = self.project
        self.assertEqual('ide_session_busy', self.run_operation()['errorCode'])
        self.assertEqual([], self.projects.calls)
        self.assertFalse(self.project.closed)
    def test_dirty_opened_project_is_rejected_without_saving(self):
        self.project.dirty = True
        self.assertEqual('workspace_session_dirty', self.run_operation()['errorCode'])
        self.assertTrue(self.project.closed)
    def test_text_read_uses_documented_offset_length_overload_and_preserves_unicode(self):
        result = self.run_operation('pou.read')
        self.assertEqual('succeeded', result['outcome'])
        self.assertEqual('计数 := 计数 + 1;', result['payload']['implementation'])
    def test_graphical_object_is_not_misreported_as_empty_text(self):
        self.program.has_textual_implementation = False
        self.assertEqual('textual_source_unavailable', self.run_operation('pou.read')['errorCode'])
    def test_structure_limit_reports_truncation(self):
        request = self.request('project.tree.read'); request['payload']['maxObjects'] = 1
        result = worker.execute(request, self.bindings)
        self.assertEqual(1, len(result['payload']['objects']))
        self.assertTrue(result['payload']['truncated'])
    def test_previous_errors_are_cleared_and_only_current_build_errors_are_returned(self):
        self.system.messages['build'] = ['old error, not a message object']
        def fail_build():
            self.system.messages['new-build'] = [types.SimpleNamespace(severity=2, prefix='C', number=123,
                text='current error', object=self.program.guid, position_text='Line 3', position_offset=7)]
        self.app.build_action = fail_build
        result = self.run_operation('project.compile')
        self.assertEqual('failed', result['outcome'])
        self.assertEqual('compile_failed', result['errorCode'])
        self.assertEqual(['current error'], [x['message'] for x in result['payload']['diagnostics']])
        self.assertIsNone(result['payload']['diagnostics'][0]['line'])
        self.assertTrue(self.project.closed)
    def test_successful_build_is_not_based_on_an_exit_code(self):
        result = self.run_operation('project.compile')
        self.assertEqual('succeeded', result['outcome'])
        self.assertTrue(result['payload']['succeeded'])
    def test_external_change_during_build_rejects_snapshot(self):
        self.app.build_action = lambda: self.path.write_bytes(b'changed externally')
        result = self.run_operation('project.compile')
        self.assertEqual('workspace_revision_conflict', result['errorCode'])
        self.assertIsNone(result['payload'])
    def test_close_failure_reports_unknown_outcome(self):
        self.project.fail_close = True
        result = self.run_operation()
        self.assertEqual('unknown', result['outcome'])
        self.assertEqual('project_close_unconfirmed', result['errorCode'])
    def test_missing_engine_and_invalid_request_never_open_a_project(self):
        self.assertEqual('scriptengine_api_unavailable', worker.execute(self.request(), {})['errorCode'])
        self.assertEqual('invalid_worker_request', self.run_operation(protocolVersion=2)['errorCode'])
        self.assertEqual('invalid_operation', self.run_operation('execute_python')['errorCode'])
        self.assertEqual('deadline_expired', self.run_operation(deadlineUtc='2000-01-01T00:00:00Z')['errorCode'])
        self.assertEqual([], self.projects.calls)
    def test_revision_conflict_prevents_opening(self):
        self.assertEqual('workspace_revision_conflict', self.run_operation(expectedRevision='wrong')['errorCode'])
        self.assertEqual([], self.projects.calls)


if __name__ == '__main__': unittest.main()
