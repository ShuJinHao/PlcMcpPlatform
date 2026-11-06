"""Original ScriptEngine API doubles. No IDE/PLC or graphical networks are simulated as real evidence."""
import copy
import types
import unittest
import uuid
import test_engineering_worker as foundation
from test_editing_worker import WritableText
from configuration_worker import ConfigurationEngine

worker = foundation.worker


class Calls(list):
    def add(self, name, comment=None): self.append((name, comment or ''))
    def remove(self, index): del self[index]


class Reference:
    def __init__(self, name, default=None):
        self.name, self.namespace, self.system_library = name, 'Test', False
        self.is_placeholder = name.startswith('#')
        self.default_resolution, self.redirect = default, ''
    @property
    def effective_resolution(self): return self.redirect or self.default_resolution
    def get_redirection(self): return self.redirect
    def set_redirection(self, value): self.redirect = value


class Mapping:
    variable = ''
    @property
    def mapping_creates_variable(self): return bool(self.variable) and '.' not in self.variable
    @property
    def maps_to_existing_variable(self): return bool(self.variable) and '.' in self.variable


class Parameter:
    def __init__(self, key=1, base='INT', access=3, elements=None):
        self.id, self.identifier, self.base_type, self.offline_access_rights = key, 'field' + str(key), base, access
        self.elements = elements or []
        self.has_sub_elements = bool(self.elements)
        self.is_range_type = self.is_enumeration = False
        self.value, self.io_mapping = '1', Mapping()
        self.is_mappable_io = True
    def __iter__(self): return iter(self.elements)


class ConfigNode(foundation.Node):
    def __init__(self, name, kind='object', parent=None):
        super().__init__(name, application=kind == 'application')
        self.kind, self.parent = kind, parent
        for marker in ('task', 'task_configuration', 'device', 'libman', 'folder'): setattr(self, 'is_' + marker, kind == marker)
        if parent: parent.children.append(self)
        self.references = []
        self.device_id = types.SimpleNamespace(type=4096, id='test-controller', version='1.0')
        self.module, self.device_parameters, self.connectors = '', [Parameter()], []
        self.kind_of_task, self.priority, self.interval, self.interval_unit = 'Cyclic', '1', '10', 'ms'
        self.event, self.external_event, self.parent_synchron_task, self.core_binding = '', '', '', 0
        self.watchdog = types.SimpleNamespace(enabled=False, time='10', time_unit='ms', sensitivity='1')
        self.pous = Calls()
    def source(self, declaration=None, implementation=None):
        self.has_textual_declaration = declaration is not None
        self.has_textual_implementation = implementation is not None
        if declaration is not None: self.textual_declaration = WritableText(declaration)
        if implementation is not None: self.textual_implementation = WritableText(implementation)
        return self
    def create_task_configuration(self): return ConfigNode('Task Configuration', 'task_configuration', self)
    def create_task(self, name): return ConfigNode(name, 'task', self)
    def create_folder(self, name): ConfigNode(name, 'folder', self)  # documented void return
    def create_method(self, name, return_type=None, language=None):
        return ConfigNode(name, 'method', self).source('METHOD ' + name + ((' : ' + return_type) if return_type else ''), '' if language in ('st','il') else None)
    def create_property(self, name, return_type='int', language=None):
        prop = ConfigNode(name, 'property', self).source('PROPERTY ' + name + ' : ' + return_type)
        for accessor in ('Get', 'Set'): ConfigNode(accessor, 'accessor', prop).source('VAR\nEND_VAR', '' if language in ('st','il') else None)
        return prop
    def create_action(self, name, language=None): return ConfigNode(name, 'action', self).source(None, '' if language in ('st','il') else None)
    def rename(self, name): self.name = name
    def move(self, parent, index=-1):
        self.parent.children.remove(self); self.parent = parent
        if index == -1: parent.children.append(self)
        else: parent.children.insert(index, self)
    def remove(self): self.parent.children.remove(self)
    def get_device_identification(self): return self.device_id
    def get_module_identification(self): return self.module
    def add(self, name, type, id, version, module=None):
        child = ConfigNode(name, 'device', self)
        child.device_id = types.SimpleNamespace(type=type, id=id, version=version); child.module = module or ''
    def plug(self, **args): self.add(**args)
    def get_libraries(self, recursive): return [r.name for r in self.references]
    def add_library(self, lib): self.references.append(Reference(lib.displayname))
    def remove_library(self, name): self.references = [r for r in self.references if r.name != name]
    def add_placeholder(self, name, default): self.references.append(Reference('#' + name, default.displayname))


class ConfigurationTests(unittest.TestCase):
    def setUp(self):
        self.h = foundation.EngineeringWorkerTests(); self.h.setUp(); self.addCleanup(self.h.doCleanups)
        h = self.h
        h.project.children = []
        self.device = ConfigNode('Controller', 'device', h.project)
        self.app = ConfigNode('Application', 'application', self.device)
        self.program = ConfigNode('PLC_PRG', parent=self.app).source('PROGRAM PLC_PRG', 'x := 1;')
        self.manager = ConfigNode('LibraryManager', 'libman', self.app)
        self.manager.references = [Reference('Base, 1.0 (Test)')]
        self.saved, self.saves, self.corrupt, self.before_save = None, 0, None, None
        def attach(node):
            for child in node.children: child.parent = node; attach(child)
        def save():
            self.saves += 1
            if self.before_save: self.before_save()
            self.saved = copy.deepcopy(h.project.children)
            h.path.write_bytes(('saved:' + str(self.saves)).encode())
        def open_project(path, **kwargs):
            if self.saved is not None:
                h.project.children = copy.deepcopy(self.saved); attach(h.project)
                if self.corrupt: self.corrupt(self.engine())
            h.projects.primary = h.project
            return h.project
        h.project.save = save; h.projects.open = open_project; h.project.close = lambda:setattr(h.projects, 'primary', None)
        libs = [types.SimpleNamespace(displayname=n + ', 1.0 (Test)', title=n, version='1.0', company='Test', default_namespace=n) for n in ('Base','Extra')]
        h.bindings.update(KindOfTask=types.SimpleNamespace(**dict((k,k) for k in ('Cyclic','Freewheeling','Event','ExternalEvent','Status','ParentSynchron'))),
            ImplementationLanguages=types.SimpleNamespace(st='st', instruction_list='il', ladder='ld', fbd='fbd', sfc='sfc', cfc='cfc'),
            librarymanager=types.SimpleNamespace(get_all_libraries=lambda:libs, find_library=lambda name:next(((x, 'local') for x in libs if x.displayname == name), None)),
            device_repository=types.SimpleNamespace(create_device_identification=lambda t,i,v:(t,i,v),get_device=lambda key:key if key[1] == 'io' else None))
        self.requirements = dict(controller=dict(type=4096,id='test-controller',version='1.0'), requiredLibraries=['Base, 1.0 (Test)'])
    def engine(self): return ConfigurationEngine(self.h.project,self.h.bindings,worker.WorkerError,worker.tree,worker.declaration_header)
    def run_op(self, op, key=None, parent=None, name=None, configuration=None, deps=None, query=None, **source):
        r = self.h.request(op)
        r['payload'].update(objectId=key, query=query, candidate=True, requirements=self.requirements, currentDependencies=deps,
            edit=dict(workspaceName='test',objectId=key,parentId=parent,name=name,configuration=configuration,**source))
        return worker.execute(r,self.h.bindings)
    def ok(self, op, **args):
        result = self.run_op(op,**args); self.assertEqual('succeeded',result['outcome'],result)
        return result['payload']['configuration']
    def task(self):
        config = self.ok('task.configuration.create',parent=self.app.guid)
        return self.ok('task.create',parent=config['objectId'],name='Cycle',configuration=dict(task=dict(kind='Cyclic',priority='2',interval='10',intervalUnit='ms',watchdog=dict(enabled=True,time='20',timeUnit='ms',sensitivity='2'))))
    def test_task_settings_and_call_order_persist_and_unbind(self):
        task = self.task(); self.assertEqual('ms',task['data']['settings']['intervalUnit'])
        calls = [dict(pouId=self.program.guid,pouName='PLC_PRG',comment='first')]
        saved = self.ok('task.calls.set',key=task['objectId'],configuration=dict(calls=calls))
        self.assertEqual(calls[0]['comment'],saved['data']['calls'][0]['comment'])
        read = self.ok('task.read',key=task['objectId']); self.assertEqual(saved['data'],read['data'])
        self.ok('task.update',key=task['objectId'],configuration=dict(task=dict(kind='Freewheeling',priority='3')))
        self.assertEqual([],self.ok('task.calls.set',key=task['objectId'],configuration=dict(calls=[]))['data']['calls'])
        self.ok('task.delete',key=task['objectId'],configuration=dict(object=dict(expectedName='Cycle')))
    def test_ambiguous_program_and_function_calls_do_not_save(self):
        task = self.task(); engine = self.engine()
        duplicate = ConfigNode('PLC_PRG',parent=engine.node('project')).source('PROGRAM PLC_PRG','')
        self.saved = copy.deepcopy(self.h.project.children)
        calls = [dict(pouId=self.program.guid,pouName='PLC_PRG',comment='')]
        self.assertEqual('task_program_resolution_conflict',self.run_op('task.calls.set',key=task['objectId'],configuration=dict(calls=calls))['errorCode'])
        self.assertEqual(2,self.saves)
    def test_unknown_call_representation_is_explicit(self):
        config=self.app.create_task_configuration(); task=config.create_task('Cycle'); task.pous.append(object())
        self.assertEqual('task_call_representation_unverified',self.run_op('task.read',key=task.guid)['errorCode'])
    def test_method_property_and_action_source_and_accessors(self):
        method=self.ok('member.create',parent=self.program.guid,name='Calc',configuration=dict(member=dict(kind='Method',language='ST',returnType='INT')),declaration='METHOD PUBLIC Calc : INT',implementation='Calc := 4;')
        changed=self.ok('member.update',key=method['objectId'],configuration=dict(member=dict(kind='Method')),implementation='Calc := 5;')
        self.assertEqual('Calc := 5;',changed['data']['implementation'])
        prop=self.ok('member.create',parent=self.program.guid,name='Value',configuration=dict(member=dict(kind='Property',returnType='INT',getter=dict(implementation='Value := x;'),setter=dict(implementation='x := Value;'))))
        self.assertEqual('Value := x;',prop['data']['getter']['implementation'])
        self.assertEqual('x := Value;',prop['data']['setter']['implementation'])
        action=self.ok('member.create',parent=self.program.guid,name='Reset',configuration=dict(member=dict(kind='Action')),implementation='x := 0;')
        read=self.ok('member.read',key=action['objectId'],query=dict(memberKind='Action',expectedTypeId=action['data']['typeId']))
        self.assertEqual('x := 0;',read['data']['implementation'])
        self.assertEqual('member_type_evidence_required',self.run_op('member.update',key=action['objectId'],configuration=dict(member=dict(kind='Action')),implementation='bad') ['errorCode'])
    def test_property_accessor_loss_and_parent_function_are_rejected(self):
        self.program.textual_declaration=WritableText('FUNCTION PLC_PRG : INT')
        self.assertEqual('member_parent_not_supported',self.run_op('member.create',parent=self.program.guid,name='Bad',configuration=dict(member=dict(kind='Method')))['errorCode'])
        self.assertEqual(0,self.saves)
    def test_graphical_member_creates_only_empty_object(self):
        m=self.ok('member.create',parent=self.program.guid,name='Graph',configuration=dict(member=dict(kind='Method',language='LD')))
        self.assertIsNone(m['data']['implementation'])
    def test_folder_move_rename_delete_and_identity(self):
        folder=self.ok('folder.create',parent=self.app.guid,name='Group',configuration=dict(object={}))
        other=self.ok('folder.create',parent=self.app.guid,name='Other',configuration=dict(object={}))
        m=self.ok('member.create',parent=self.program.guid,name='Calc',configuration=dict(member=dict(kind='Method')))
        self.ok('object.rename',key=folder['objectId'],name='Renamed',configuration=dict(object=dict(expectedName='Group')))
        move=self.ok('object.move',key=folder['objectId'],parent=other['objectId'],configuration=dict(object=dict(expectedName='Renamed',index=-1)))
        self.assertEqual(folder['objectId'],move['objectId'])
        self.ok('object.delete',key=m['objectId'],configuration=dict(object=dict(expectedName='Calc',recursive=False)))
    def test_task_reference_protects_program_and_controller_is_protected(self):
        t=self.task(); self.ok('task.calls.set',key=t['objectId'],configuration=dict(calls=[dict(pouId=self.program.guid,pouName='PLC_PRG',comment='')]))
        self.assertEqual('task_reference_conflict',self.run_op('object.rename',key=self.program.guid,name='New',configuration=dict(object=dict(expectedName='PLC_PRG')))['errorCode'])
        self.assertIn(self.run_op('device.remove',key=self.device.guid,configuration=dict(object=dict(expectedName='Controller',recursive=True)))['errorCode'],('protected_controller','protected_object'))
    def test_library_inventory_add_placeholder_resolution_and_remove_template_reference(self):
        self.assertEqual(2,len(self.ok('library.installed')['data']['libraries']))
        self.ok('library.add',key=self.manager.guid,configuration=dict(library=dict(name='Extra, 1.0 (Test)')))
        placeholder=self.ok('library.placeholder.set',key=self.manager.guid,configuration=dict(library=dict(name='Common',defaultResolution='Base, 1.0 (Test)',redirection='Extra, 1.0 (Test)')))
        self.assertEqual('Extra, 1.0 (Test)',placeholder['data']['reference']['effectiveResolution'])
        deps=self.engine().dependencies()
        removed=self.run_op('library.remove',key=self.manager.guid,deps=deps,configuration=dict(library=dict(name='Base, 1.0 (Test)')))
        self.assertEqual('succeeded',removed['outcome'],removed)
        new_deps=removed['payload']['dependencySnapshot']
        self.ok('folder.create',parent=self.app.guid,name='AfterRemoval',deps=new_deps,configuration=dict(object={}))
        self.assertEqual('dependency_snapshot_conflict',self.run_op('folder.create',parent=self.app.guid,name='Stale',deps=deps,configuration=dict(object={}))['errorCode'])
        self.assertEqual(['Base, 1.0 (Test)'],self.requirements['requiredLibraries'])
    def test_missing_library_and_system_library_have_explicit_errors(self):
        self.assertEqual('library_not_installed',self.run_op('library.add',key=self.manager.guid,configuration=dict(library=dict(name='Missing')))['errorCode'])
        self.manager.references[0].system_library=True
        self.assertEqual('protected_system_library',self.run_op('library.remove',key=self.manager.guid,configuration=dict(library=dict(name='Base, 1.0 (Test)')))['errorCode'])
    def test_device_add_parameter_nested_address_mapping_and_remove(self):
        d=self.ok('device.add',parent=self.device.guid,name='Input',configuration=dict(device=dict(identification=dict(type=5,id='io',version='1.0'),module=None,plug=False)))
        key=d['objectId']
        self.assertEqual('io',d['data']['identification']['id'])
        param=self.ok('device.parameter.write',key=key,configuration=dict(parameter=dict(address=dict(parameterId=1),value='12')))
        self.assertEqual('12',param['data']['value'])
        mapping=self.ok('device.io.map',key=key,configuration=dict(mapping=dict(address=dict(parameterId=1),variable='GVL.Input',createVariable=False)))
        self.assertTrue(mapping['data']['mapsExistingVariable'])
        self.ok('device.io.map',key=key,configuration=dict(mapping=dict(address=dict(parameterId=1),variable='NewInput',createVariable=True)))
        self.ok('device.io.map',key=key,configuration=dict(mapping=dict(address=dict(parameterId=1),variable='',createVariable=False)))
        self.ok('device.remove',key=key,configuration=dict(object=dict(expectedName='Input',recursive=False)))
    def test_parameter_range_access_enum_and_unknown_types(self):
        p=self.device.device_parameters[0]; p.is_range_type=True; p.min_value='0'; p.max_value='10'
        def write(value): return self.run_op('device.parameter.write',key=self.device.guid,configuration=dict(parameter=dict(address=dict(parameterId=1),value=value)))['errorCode']
        self.assertEqual('parameter_out_of_range',write('11'))
        p.offline_access_rights=1; self.assertEqual('parameter_not_read_write',write('2'))
        p.offline_access_rights=3; p.is_range_type=False; p.is_enumeration=True; p.allowed_values=[types.SimpleNamespace(identifier='Enabled')]
        self.assertEqual('invalid_parameter_enum',write('1'))
        p.is_enumeration=False; p.base_type='VendorStruct'; self.assertEqual('parameter_type_unverified',write('2'))
        self.assertEqual(0,self.saves)
    def test_mapping_uses_channel_capability_not_parameter_value_write_rights(self):
        p=self.device.device_parameters[0]; p.offline_access_rights=1
        result=self.ok('device.io.map',key=self.device.guid,configuration=dict(mapping=dict(address=dict(parameterId=1),variable='GVL.Input',createVariable=False)))
        self.assertTrue(result['data']['mapsExistingVariable'])
        current=self.engine().node(self.device.guid).device_parameters[0]; current.is_mappable_io=False
        self.saved=copy.deepcopy(self.h.project.children)
        self.assertEqual('io_mapping_unavailable',self.run_op('device.io.map',key=self.device.guid,configuration=dict(mapping=dict(address=dict(parameterId=1),variable='GVL.Input',createVariable=False)))['errorCode'])
    def test_reordered_calls_and_reopen_source_corruption_are_detected(self):
        second=ConfigNode('Other',parent=self.app).source('PROGRAM Other','')
        t=self.task()
        calls=[dict(pouId=self.program.guid,pouName='PLC_PRG',comment='first'),dict(pouId=second.guid,pouName='Other',comment='second')]
        self.ok('task.calls.set',key=t['objectId'],configuration=dict(calls=calls))
        reversed_calls=self.ok('task.calls.set',key=t['objectId'],configuration=dict(calls=list(reversed(calls))))
        self.assertEqual('Other',reversed_calls['data']['calls'][0]['pouName'])
        def corrupt(e):
            for n in e.nodes.values():
                if n.get_name()=='Calc': n.textual_implementation=WritableText('lost')
        self.corrupt=corrupt
        result=self.run_op('member.create',parent=self.program.guid,name='Calc',configuration=dict(member=dict(kind='Method')),implementation='x := 1;')
        self.assertEqual('reopen_readback_mismatch',result['errorCode'])
    def test_connector_compound_leaf_is_resolved_by_stable_identifiers(self):
        leaf=Parameter(4); compound=Parameter(20,elements=[leaf])
        self.device.connectors=[types.SimpleNamespace(connector_id=3,host_parameters=[compound])]
        result=self.ok('device.parameter.write',key=self.device.guid,configuration=dict(parameter=dict(address=dict(parameterId=20,connectorId=3,elements=['field4']),value='8')))
        self.assertEqual(['field4'],result['data']['address']['elements'])
    def test_reopen_corruption_fails_instead_of_publishing(self):
        self.corrupt=lambda e:setattr(e.node(self.manager.guid).references[0], 'namespace', 'changed-after-save')
        self.assertEqual('dependency_readback_mismatch',self.run_op('folder.create',parent=self.app.guid,name='Lost',configuration=dict(object={}))['errorCode'])
    def test_name_conflict_move_cycle_and_missing_device_package(self):
        folder=self.ok('folder.create',parent=self.app.guid,name='Group',configuration=dict(object={}))
        self.assertEqual('object_move_cycle',self.run_op('object.move',key=folder['objectId'],parent=folder['objectId'],configuration=dict(object=dict(expectedName='Group')))['errorCode'])
        self.assertEqual('object_name_conflict',self.run_op('folder.create',parent=self.app.guid,name='group',configuration=dict(object={}))['errorCode'])
        self.assertEqual('device_description_not_installed',self.run_op('device.add',parent=self.device.guid,name='Missing',configuration=dict(device=dict(identification=dict(type=1,id='missing',version='1'))))['errorCode'])


if __name__ == '__main__': unittest.main()
