"""Original API-call tests against session doubles; not InoProShop compatibility proof."""
import copy
import pathlib
import types
import unittest
import test_engineering_worker as foundation
from test_engineering_worker import Node, Text, worker


class WritableText(Text):
    def replace(self, *, new_text):
        self.value = new_text.replace('\r\n', '\n').replace('\r', '\n')
        self.length = len(self.value)


class EditingWorkerTests(unittest.TestCase):
    def setUp(self):
        self.h = foundation.EngineeringWorkerTests()
        self.h.setUp()
        self.addCleanup(self.h.doCleanups)
        h = self.h
        self.saved, self.reopens = None, 0
        h.program.textual_declaration = WritableText('PROGRAM PLC_PRG\nVAR\n计数 : INT;\nEND_VAR')
        h.program.textual_implementation = WritableText('计数 := 计数 + 1;')
        self.save_count, self.close_count, self.created_args = 0, 0, None
        self.fail_save = self.corrupt_reopen = self.fail_reopen_close = False
        def save():
            self.save_count += 1
            if self.fail_save: raise RuntimeError('save failed')
            self.saved = copy.deepcopy(h.project.children)
            h.path.write_bytes(b'saved test project, not a PLC artifact')
        def close():
            self.close_count += 1
            if self.fail_reopen_close and self.close_count == 2: raise RuntimeError('close unconfirmed')
            h.projects.primary = None
        def open_project(path, **options):
            h.projects.calls.append((path, options))
            if self.saved is not None:
                self.reopens += 1
                h.project.children = copy.deepcopy(self.saved)
                if self.corrupt_reopen: h.project.children[0].children[0].textual_implementation.value = 'lost source'
            h.projects.primary = h.project
            return h.project
        h.project.save, h.project.close, h.projects.open = save, close, open_project
        def make(name, kind, textual=False):
            node = Node(name)
            node.has_textual_declaration = True
            node.has_textual_implementation = textual
            node.textual_declaration = WritableText(('PROGRAM ' + name) if kind == 'pou' else 'VAR_GLOBAL\nEND_VAR' if kind == 'gvl' else 'TYPE ' + name + ' : INT; END_TYPE')
            if textual: node.textual_implementation = WritableText('')
            h.app.children.append(node)
            return node
        def pou(**kwargs):
            self.created_args = kwargs
            node = make(kwargs['name'], 'pou', kwargs['language'] in ('st', 'il'))
            prefix = {'Program':'PROGRAM', 'FunctionBlock':'FUNCTION_BLOCK', 'Function':'FUNCTION'}[kwargs['type']]
            node.textual_declaration = WritableText(prefix + ' ' + kwargs['name'])
            return node
        def dut(**kwargs):
            self.created_args = kwargs
            return make(kwargs['name'], 'dut')
        device = Node('Controller'); device.is_device = True
        device.get_module_identification = lambda:''
        device.get_device_identification = lambda:types.SimpleNamespace(type=4096, id='test-controller', version='1.0')
        library = Node('LibraryManager'); library.is_libman = True; library.get_libraries = lambda recursive:['TestLibrary, 1.0 (Fixture)']
        library.references = [types.SimpleNamespace(name='TestLibrary, 1.0 (Fixture)', namespace='TestLibrary', is_placeholder=False, system_library=False)]
        h.project.children.extend([device, library])
        h.app.create_pou, h.app.create_gvl, h.app.create_dut = pou, lambda name:make(name, 'gvl'), dut
        h.bindings.update(PouType=types.SimpleNamespace(Program='Program', FunctionBlock='FunctionBlock', Function='Function'),
                          DutType=types.SimpleNamespace(Structure='Structure', Enumeration='Enumeration', Alias='Alias', Union='Union'),
                          ImplementationLanguages=types.SimpleNamespace(st='st', instruction_list='il', ladder='ld', fbd='fbd', sfc='sfc', cfc='cfc'))
    def run_edit(self, op='pou.update', **edit):
        request = self.h.request(op)
        request['payload'].update(candidate=True, requirements=dict(controller=dict(type=4096,id='test-controller',version='1.0'),requiredLibraries=['TestLibrary, 1.0 (Fixture)']), edit=dict(workspaceName='example', objectId=self.h.program.guid, parentId=self.h.app.guid, **edit))
        return worker.execute(request, self.h.bindings)
    def test_update_preserves_unicode_and_reads_back_after_two_owned_sessions(self):
        result = self.run_edit(implementation='计数 := 计数 + 2;\r\n')
        self.assertEqual('succeeded', result['outcome'])
        self.assertEqual('计数 := 计数 + 2;\n', result['payload']['implementation'])
        self.assertEqual((1, 1, 2), (self.save_count, self.reopens, self.close_count))
        self.assertFalse(self.h.projects.calls[0][1]['allow_readonly'])
        self.assertTrue(self.h.projects.calls[1][1]['allow_readonly'])
    def test_function_creation_uses_documented_keyword_overload(self):
        result = self.run_edit('pou.create', name='Compute', pouType='Function', language='ST', baseType='INT', declaration='FUNCTION Compute : INT', implementation='Compute := 1;')
        self.assertEqual('succeeded', result['outcome'])
        self.assertEqual(dict(name='Compute', type='Function', language='st', return_type='INT'), self.created_args)
    def test_documented_graphical_identifiers_create_empty_objects_without_text_networks(self):
        for language in ('LD','FBD','SFC','CFC'):
            with self.subTest(language=language):
                self.setUp()
                result = self.run_edit('pou.create', name='Graph', pouType='Program', language=language, declaration='PROGRAM Graph')
                self.assertEqual('succeeded', result['outcome'])
                self.assertIsNone(result['payload']['implementation'])
    def test_graphical_network_text_and_sfc_function_are_rejected(self):
        result = self.run_edit('pou.create', name='Graph', pouType='Program', language='LD', declaration='PROGRAM Graph', implementation='<xml/>')
        self.assertEqual('graphical_content_requires_specialized_editor', result['errorCode'])
        result = self.run_edit('pou.create', name='Bad', pouType='Function', language='SFC', declaration='FUNCTION Bad : INT', baseType='INT')
        self.assertEqual('language_not_applicable', result['errorCode'])
        self.assertEqual(0, self.save_count)
    def test_gvl_and_dut_declarations_persist(self):
        result = self.run_edit('gvl.create', name='Globals', declaration='VAR_GLOBAL\nvalue : INT;\nEND_VAR')
        self.assertEqual('succeeded', result['outcome'])
        request = self.h.request('gvl.read'); request['payload']['objectId'] = result['payload']['objectId']
        read = worker.execute(request, self.h.bindings)
        self.assertEqual('succeeded', read['outcome'])
        self.assertEqual(result['payload']['declaration'], read['payload']['declaration'])
        self.setUp()
        result = self.run_edit('dut.create', name='Values', dutType='Alias', baseType='ARRAY[0..3] OF INT', declaration='TYPE Values : ARRAY[0..3] OF INT; END_TYPE')
        self.assertEqual('succeeded', result['outcome'])
        self.assertEqual('ARRAY[0..3] OF INT', self.created_args['baseType'])
        request = self.h.request('dut.read'); request['payload']['objectId'] = result['payload']['objectId']
        read = worker.execute(request, self.h.bindings)
        self.assertEqual('succeeded', read['outcome'])
        self.assertEqual(result['payload']['declaration'], read['payload']['declaration'])
    def test_type_mismatch_never_saves(self):
        result = self.run_edit('gvl.update', declaration='VAR_GLOBAL\nx : INT;\nEND_VAR')
        self.assertEqual('object_kind_mismatch', result['errorCode'])
        self.assertEqual(0, self.save_count)
    def test_duplicate_name_is_not_a_second_create(self):
        result = self.run_edit('pou.create', name='plc_prg', pouType='Program', language='ST', declaration='PROGRAM plc_prg')
        self.assertEqual('object_name_conflict', result['errorCode'])
        self.assertEqual(0, self.save_count)
    def test_save_failure_is_not_a_successful_edit(self):
        self.fail_save = True
        result = self.run_edit(implementation='changed')
        self.assertEqual('failed', result['outcome'])
        self.assertIsNone(result['payload'])
        self.assertEqual(1, self.close_count)
    def test_lost_source_after_reopen_is_rejected(self):
        self.corrupt_reopen = True
        self.assertEqual('reopen_readback_mismatch', self.run_edit(implementation='changed')['errorCode'])
    def test_unconfirmed_final_close_is_unknown(self):
        self.fail_reopen_close = True
        self.assertEqual('unknown', self.run_edit(implementation='changed')['outcome'])
    def test_editing_requires_candidate_flag_and_existing_user_session_is_rejected(self):
        request = self.h.request('pou.update')
        self.assertEqual('managed_candidate_required', worker.execute(request, self.h.bindings)['errorCode'])
        self.h.projects.primary = self.h.project
        self.assertEqual('ide_session_busy', self.run_edit(implementation='changed')['errorCode'])
        self.assertEqual(0, self.save_count)
    def test_controller_version_and_required_library_are_checked_before_save(self):
        self.h.project.children[1].get_device_identification = lambda:types.SimpleNamespace(type=4096, id='test-controller', version='wrong')
        self.assertEqual('controller_requirement_mismatch', self.run_edit(implementation='changed')['errorCode'])
        self.assertEqual(0, self.save_count)
        self.setUp()
        self.h.project.children[2].get_libraries = lambda recursive:[]
        self.assertEqual('library_reference_missing', self.run_edit(implementation='changed')['errorCode'])
        self.assertEqual(0, self.save_count)
    def test_project_verification_saves_reopens_and_reports_no_compilation(self):
        result = self.run_edit('project.verify')
        self.assertEqual('succeeded', result['outcome'])
        self.assertTrue(result['payload']['reopened'])
        self.assertNotIn('diagnostics', result['payload'])


if __name__ == '__main__': unittest.main()
