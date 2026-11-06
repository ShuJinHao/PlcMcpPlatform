# -*- coding: utf-8 -*-
"""Original offline ScriptEngine operations. Python 2.7 compatible; version validation required.

No arbitrary scripts, online access, reference refactoring, package installation or PDO editing.
All mutations run in a disposable managed candidate owned by engineering_worker.
"""
import math
from decimal import Decimal, InvalidOperation
import re
import uuid
try:
    text_type = unicode
    string_types = (basestring,)
except NameError:
    text_type = str
    string_types = (str,)

READS = ('task.list', 'task.read', 'member.read', 'object.inspect', 'library.list',
         'library.installed', 'device.tree.read', 'device.parameters.read', 'device.io.read')
WRITES = ('task.configuration.create', 'task.create', 'task.update', 'task.delete', 'task.calls.set',
          'member.create', 'member.update', 'folder.create', 'object.rename', 'object.move', 'object.delete',
          'library.add', 'library.remove', 'library.placeholder.set', 'device.add', 'device.remove',
          'device.parameter.write', 'device.io.map')
LANGUAGES = {'ST': 'st', 'IL': 'instruction_list', 'LD': 'ladder', 'FBD': 'fbd', 'SFC': 'sfc', 'CFC': 'cfc'}
TASK_FIELDS = {'kind': 'kind_of_task', 'priority': 'priority', 'interval': 'interval', 'intervalUnit': 'interval_unit',
               'event': 'event', 'externalEvent': 'external_event', 'parentSynchronTask': 'parent_synchron_task', 'coreBinding': 'core_binding'}
WATCHDOG_FIELDS = {'enabled': 'enabled', 'time': 'time', 'timeUnit': 'time_unit', 'sensitivity': 'sensitivity'}


def identity(node):
    return str(uuid.UUID(text_type(node.guid)))


def normalized(value):
    return text_type(value).replace('\r\n', '\n').replace('\r', '\n')


class ConfigurationEngine(object):
    def __init__(self, project, bindings, error, tree, header):
        self.project, self.bindings, self.Error = project, bindings, error
        self.tree, self.header = tree, header
        self.refresh()

    def fail(self, code):
        raise self.Error(code)

    def refresh(self):
        rows, self.nodes, truncated = self.tree(self.project, 2000)
        if truncated: self.fail('object_search_incomplete')
        self.rows = dict((r['id'], r) for r in rows)
        self.child_ids = {}
        for row in rows: self.child_ids.setdefault(row['parentId'], []).append(row['id'])

    def node(self, key):
        key = 'project' if key == 'project' else str(uuid.UUID(key))
        if key not in self.nodes: self.fail('object_not_found')
        return self.nodes[key]

    def require(self, node, marker):
        if not hasattr(node, marker): self.fail('scriptengine_api_unavailable')
        if not getattr(node, marker): self.fail('object_kind_mismatch')
        return node

    def api(self, node, name):
        if not hasattr(node, name): self.fail('scriptengine_api_unavailable')
        return getattr(node, name)

    def info(self, key):
        node = self.node(key)
        row = dict(self.rows[key])
        row.update(objectId=key, typeId=None if key == 'project' else text_type(node.type),
                   kind='project' if key == 'project' else 'application' if getattr(node, 'is_application', False)
                   else 'task_configuration' if getattr(node, 'is_task_configuration', False)
                   else 'task' if getattr(node, 'is_task', False) else 'device' if getattr(node, 'is_device', False)
                   else 'library_manager' if getattr(node, 'is_libman', False) else 'folder' if getattr(node, 'is_folder', False) else 'object')
        row['index'] = -1 if key == 'project' else self.child_ids[row['parentId']].index(key)
        return row

    def children(self, key):
        return list(self.child_ids.get(key, []))

    def descendants(self, key):
        found, pending = [key], [key]
        while pending:
            children = self.children(pending.pop())
            found.extend(children); pending.extend(children)
        return found

    def unique(self, parent, name, except_id=None):
        if any(self.rows[k]['name'].lower() == name.lower() for k in self.children(parent) if k != except_id):
            self.fail('object_name_conflict')

    def new_child(self, parent, before, name=None):
        self.refresh()
        created = [k for k in self.children(parent) if k not in before and (name is None or self.rows[k]['name'] == name)]
        if len(created) != 1: self.fail('created_object_identity_unconfirmed')
        return created[0]

    def declaration(self, node):
        if not getattr(node, 'has_textual_declaration', False): return None
        source = node.textual_declaration
        if source.length > 400000: self.fail('result_too_large')
        return normalized(source.get_text(0, source.length))

    def sources(self, node):
        result = {'declaration': self.declaration(node), 'implementation': None}
        if getattr(node, 'has_textual_implementation', False):
            source = node.textual_implementation
            if source.length > 400000: self.fail('result_too_large')
            result['implementation'] = normalized(source.get_text(0, source.length))
        return result

    def source_write(self, node, source):
        for key, api in (('declaration', 'textual_declaration'), ('implementation', 'textual_implementation')):
            value = source.get(key)
            if value is not None:
                if not isinstance(value, text_type) or len(value) > 65536 or '\0' in value: self.fail('invalid_source')
                if not getattr(node, 'has_' + api, False): self.fail('textual_source_unavailable')
                self.api(node, api).replace(new_text=value)
        actual = self.sources(node)
        if any(source.get(k) is not None and normalized(source[k]) != actual[k] for k in ('declaration', 'implementation')):
            self.fail('readback_mismatch')

    def application(self, key):
        while key is not None:
            if getattr(self.node(key), 'is_application', False): return key
            key = self.rows[key]['parentId']
        return None

    def calls(self, task):
        result = []
        for call in self.api(task, 'pous'):
            # The sequence element representation is version-specific. Never silently drop comments.
            if isinstance(call, string_types): name, comment = call, ''
            elif isinstance(call, (list, tuple)) and len(call) == 2: name, comment = call
            else: self.fail('task_call_representation_unverified')
            if len(result) >= 128: self.fail('task_calls_too_large')
            matches = self.programs(text_type(name), self.application(identity(task)))
            result.append({'pouId': matches[0] if len(matches) == 1 else None, 'pouName': text_type(name),
                           'comment': text_type(comment or ''), 'resolved': len(matches) == 1})
        return result

    def programs(self, name, application):
        result = []
        for key, node in self.nodes.items():
            if self.rows[key]['name'].lower() != name.lower() or self.application(key) not in (None, application): continue
            declaration = self.declaration(node)
            if declaration and re.match(r'^PROGRAM\s+' + re.escape(name) + r'\b', self.header(declaration), re.I): result.append(key)
        return result

    def task(self, key):
        node = self.require(self.node(key), 'is_task')
        kind = text_type(self.api(node, 'kind_of_task'))
        keys = ['kind', 'priority']
        if kind in ('Cyclic', 'ExternalEvent'): keys += ['interval', 'intervalUnit']
        if kind in ('Event', 'Status'): keys += ['event']
        if kind == 'ExternalEvent': keys += ['externalEvent']
        if kind == 'ParentSynchron': keys += ['parentSynchronTask']
        settings = dict((k, text_type(self.api(node, TASK_FIELDS[k]))) for k in keys)
        if hasattr(node, 'core_binding'): settings['coreBinding'] = int(node.core_binding)
        watchdog = self.api(node, 'watchdog')
        settings['watchdog'] = {'enabled': bool(watchdog.enabled)}
        if watchdog.enabled:
            for k in ('time', 'timeUnit', 'sensitivity'): settings['watchdog'][k] = text_type(self.api(watchdog, WATCHDOG_FIELDS[k]))
        result = self.info(key); result.update(settings=settings, calls=self.calls(node))
        return result

    def set_task(self, node, settings):
        enum = self.bindings.get('KindOfTask')
        if enum is None: self.fail('scriptengine_api_unavailable')
        node.kind_of_task = self.api(enum, settings['kind'])
        for key in TASK_FIELDS:
            if key != 'kind' and settings.get(key) is not None:
                self.api(node, TASK_FIELDS[key]); setattr(node, TASK_FIELDS[key], settings[key])
        if settings.get('watchdog') is not None:
            watchdog = self.api(node, 'watchdog')
            for key, value in settings['watchdog'].items():
                if value is not None:
                    self.api(watchdog, WATCHDOG_FIELDS[key]); setattr(watchdog, WATCHDOG_FIELDS[key], value)
        actual = self.task(identity(node))['settings']
        if not supplied(settings, actual): self.fail('task_settings_readback_mismatch')

    def member(self, key, kind, expected_type=None):
        node = self.node(key); result = self.info(key); result.update(self.sources(node))
        declaration = result['declaration']
        if expected_type is not None and str(uuid.UUID(expected_type)) != str(uuid.UUID(text_type(node.type))): self.fail('member_type_evidence_required')
        if kind in ('Method', 'Property'):
            if declaration is None or not re.match(r'^' + kind.upper() + r'\s+(?:(?:PUBLIC|PRIVATE|PROTECTED|INTERNAL|FINAL|ABSTRACT)\s+)*' + re.escape(text_type(node.get_name())) + r'\b', self.header(declaration), re.I): self.fail('object_kind_mismatch')
        elif kind == 'Action':
            # Action has no declaration marker. Caller must pin the observed vendor type GUID.
            if expected_type is None or str(uuid.UUID(expected_type)) != str(uuid.UUID(text_type(node.type))): self.fail('member_type_evidence_required')
            if declaration is not None: self.fail('object_kind_mismatch')
            parent = self.rows[key]['parentId']
            if parent not in self.nodes or not re.match(r'^(PROGRAM|FUNCTION_BLOCK)\b', self.header(self.declaration(self.node(parent)) or ''), re.I):
                self.fail('member_parent_not_supported')
        else: self.fail('invalid_member_kind')
        result['kind'] = kind
        if kind == 'Property':
            for name, field in (('Get', 'getter'), ('Set', 'setter')):
                children = [n for n in node.get_children(False) if text_type(n.get_name()) == name]
                if len(children) > 1: self.fail('property_accessor_ambiguous')
                value = None
                if children:
                    value = self.sources(children[0])
                    value.update(objectId=identity(children[0]), parentId=key, ownerId=key, accessor=name, typeId=text_type(children[0].type))
                result[field] = value
        return result

    def reference(self, manager_id, ref):
        placeholder = bool(ref.is_placeholder)
        return {'managerId': manager_id, 'name': text_type(ref.name), 'namespace': text_type(getattr(ref, 'namespace')),
                'isPlaceholder': placeholder, 'systemLibrary': bool(ref.system_library),
                'defaultResolution': text_type(ref.default_resolution) if placeholder and ref.default_resolution is not None else None,
                'effectiveResolution': text_type(ref.effective_resolution) if placeholder and ref.effective_resolution is not None else None,
                'redirection': text_type(ref.get_redirection() or '') if placeholder else None}

    def references(self, key):
        node = self.require(self.node(key), 'is_libman')
        values = []
        for ref in self.api(node, 'references'):
            if len(values) >= 1024: self.fail('library_list_too_large')
            values.append(self.reference(key, ref))
        return sorted(values, key=lambda x: x['name'])

    def find_reference(self, key, name):
        matches = [r for r in self.api(self.require(self.node(key), 'is_libman'), 'references') if text_type(r.name) == name]
        if len(matches) != 1: self.fail('library_reference_not_found' if not matches else 'library_reference_ambiguous')
        return matches[0]

    def installed(self, name=None):
        manager = self.bindings.get('librarymanager')
        if manager is None: self.fail('scriptengine_api_unavailable')
        if name is not None:
            found = manager.find_library(name)
            if found is None or text_type(found[0].displayname) != name: self.fail('library_not_installed')
            return found[0]
        result = []
        for lib in manager.get_all_libraries():
            if len(result) >= 2000: self.fail('library_list_too_large')
            result.append({'name': text_type(lib.displayname), 'title': text_type(lib.title), 'version': text_type(lib.version),
                           'company': text_type(lib.company), 'namespace': text_type(lib.default_namespace)})
        return sorted(result, key=lambda x: x['name'])

    def device(self, key):
        node = self.require(self.node(key), 'is_device'); ident = node.get_device_identification()
        result = self.info(key)
        result.update(identification={'type': int(ident.type), 'id': text_type(ident.id), 'version': text_type(ident.version)},
                      module=text_type(node.get_module_identification() or ''))
        return result

    def dependencies(self):
        devices, libraries = [], []
        for key in sorted(self.nodes):
            node = self.nodes[key]
            if getattr(node, 'is_device', False):
                d = self.device(key); devices.append(dict((k, d[k]) for k in ('objectId', 'identification', 'module')))
            if getattr(node, 'is_libman', False):
                for ref in self.references(key):
                    ref.pop('systemLibrary'); libraries.append(ref)
        if len(devices) > 512 or len(libraries) > 1024: self.fail('dependency_list_too_large')
        return {'devices': devices, 'libraries': libraries}

    def parameter_nodes(self, key):
        node = self.require(self.node(key), 'is_device')
        sets = [(None, self.api(node, 'device_parameters'))]
        sets += [(int(c.connector_id), c.host_parameters) for c in self.api(node, 'connectors')]
        result = []
        for connector, parameters in sets:
            pending = [(p, {'parameterId': int(p.id), 'connectorId': connector, 'elements': []}, p, 0) for p in parameters]
            while pending:
                element, address, root, depth = pending.pop(0)
                if len(result) + len(pending) >= 2000 or depth > 32: self.fail('parameter_list_too_large')
                if element.has_sub_elements:
                    for child in element:
                        pending.append((child, dict(address, elements=address['elements'] + [text_type(child.identifier)]), root, depth + 1))
                else: result.append((element, address, root))
        return result

    def parameter(self, key, address):
        expected = dict(address, connectorId=address.get('connectorId'), elements=address.get('elements') or [])
        found = [(e, a, r) for e, a, r in self.parameter_nodes(key) if a == expected]
        if len(found) != 1: self.fail('parameter_not_found' if not found else 'parameter_ambiguous')
        return found[0]

    def parameter_info(self, element, address, root):
        rights = int(root.offline_access_rights)
        result = {'address': address, 'access': rights, 'readable': bool(rights & 1), 'writable': bool(rights & 2),
                  'type': text_type(element.base_type), 'value': text_type(element.value) if rights & 1 else None}
        if element.is_range_type: result.update(minimum=text_type(element.min_value), maximum=text_type(element.max_value))
        if element.is_enumeration: result['allowedValues'] = [text_type(v.identifier) for v in element.allowed_values]
        return result

    def mapping_info(self, element, address, root):
        if not self.api(element, 'is_mappable_io'): self.fail('io_mapping_unavailable')
        mapping = self.api(element, 'io_mapping')
        if mapping is None: self.fail('io_mapping_unavailable')
        variable = text_type(mapping.variable or '')
        return {'address': address, 'variable': variable,
                'createsVariable': bool(variable) and bool(mapping.mapping_creates_variable),
                'mapsExistingVariable': bool(variable) and bool(mapping.maps_to_existing_variable),
                'sdkCreatesVariable': bool(mapping.mapping_creates_variable), 'sdkMapsExistingVariable': bool(mapping.maps_to_existing_variable)}

    def validate_value(self, element, root, value):
        if int(root.offline_access_rights) & 3 != 3: self.fail('parameter_not_read_write')
        if element.is_enumeration:
            if value not in [text_type(v.identifier) for v in element.allowed_values]: self.fail('invalid_parameter_enum')
            return
        kind = text_type(element.base_type).upper()
        widths = {'SINT': (8, True), 'INT': (16, True), 'DINT': (32, True), 'LINT': (64, True),
                  'USINT': (8, False), 'BYTE': (8, False), 'UINT': (16, False), 'WORD': (16, False),
                  'UDINT': (32, False), 'DWORD': (32, False), 'ULINT': (64, False), 'LWORD': (64, False)}
        number = None
        if kind in widths:
            if not re.match(r'^[+-]?[0-9]+$', value): self.fail('invalid_parameter_value')
            number = int(value); bits, signed = widths[kind]
            if not (-(2 ** (bits - 1)) if signed else 0) <= number <= 2 ** (bits - (1 if signed else 0)) - 1: self.fail('parameter_out_of_range')
        elif kind in ('REAL', 'LREAL'):
            if not re.match(r'^[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[Ee][+-]?[0-9]+)?$', value): self.fail('invalid_parameter_value')
            number = float(value)
            if math.isnan(number) or math.isinf(number) or kind == 'REAL' and abs(number) > 3.402823466e38: self.fail('parameter_out_of_range')
        elif kind == 'BOOL':
            if value.upper() not in ('TRUE', 'FALSE'): self.fail('invalid_parameter_value')
        elif kind in ('STRING', 'WSTRING'):
            quote = "'" if kind == 'STRING' else '"'
            if len(value) < 2 or value[0] != quote or value[-1] != quote or quote in value[1:-1] or '\n' in value or '\r' in value:
                self.fail('invalid_parameter_value')
        else: self.fail('parameter_type_unverified')
        if element.is_range_type:
            if number is None: self.fail('parameter_type_unverified')
            try:
                if not Decimal(text_type(element.min_value)) <= Decimal(value) <= Decimal(text_type(element.max_value)): self.fail('parameter_out_of_range')
            except InvalidOperation: self.fail('parameter_range_unverified')

    def read(self, operation, key, query=None, limit=256):
        query = query or {}
        if operation == 'task.read': return self.task(key)
        if operation == 'member.read': return self.member(key, query['memberKind'], query.get('expectedTypeId'))
        if operation == 'object.inspect':
            result = self.info(key); children = [self.info(k) for k in self.children(key)]
            result.update(children=children[:limit], truncated=len(children) > limit); return result
        if operation == 'task.list':
            self.require(self.node(key), 'is_application')
            self.api(self.node(key), 'is_task_configuration')
            values = [self.task(k) for k in self.descendants(key) if getattr(self.node(k), 'is_task', False)]
            return {'tasks': values[:limit], 'truncated': len(values) > limit,
                    'configurations': [self.info(k) for k in self.descendants(key) if getattr(self.node(k), 'is_task_configuration', False)]}
        if operation == 'library.list': field, values = 'references', self.references(key)
        elif operation == 'library.installed': field, values = 'libraries', self.installed()
        elif operation == 'device.tree.read': field, values = 'devices', [self.device(k) for k in sorted(self.nodes) if getattr(self.node(k), 'is_device', False)]
        elif operation in ('device.parameters.read', 'device.io.read'):
            field, values = ('parameters', []) if operation == 'device.parameters.read' else ('mappings', [])
            for element, address, root in self.parameter_nodes(key):
                if field == 'parameters': values.append(self.parameter_info(element, address, root))
                elif self.api(element, 'is_mappable_io') and self.api(element, 'io_mapping') is not None: values.append(self.mapping_info(element, address, root))
        else: self.fail('invalid_operation')
        return {field: values[:limit], 'truncated': len(values) > limit}

    def protect(self, key, recursive, requirements):
        info = self.info(key)
        if info['kind'] in ('project', 'application', 'task_configuration', 'library_manager'): self.fail('protected_object')
        descendants = self.descendants(key)
        if len(descendants) > 1 and not recursive: self.fail('recursive_confirmation_required')
        controller = (requirements or {}).get('controller')
        for k in descendants:
            if getattr(self.node(k), 'is_application', False): self.fail('protected_object')
            if getattr(self.node(k), 'is_device', False) and self.device(k)['identification'] == controller: self.fail('protected_controller')
        names = set(self.rows[k]['name'].lower() for k in descendants)
        for task in self.nodes.values():
            if getattr(task, 'is_task', False) and identity(task) not in descendants:
                for call in self.calls(task):
                    if call['pouName'].lower() in names: self.fail('task_reference_conflict')
        return descendants

    def apply(self, operation, edit, requirements):
        change = edit.get('configuration') or {}; key = edit.get('objectId'); parent = edit.get('parentId')
        name = edit.get('name'); settings = change.get('object') or {}
        if operation.endswith('.create') or operation == 'device.add':
            node = self.node(parent); before = self.children(parent)
            if name: self.unique(parent, name)
            if operation == 'task.configuration.create':
                self.require(node, 'is_application')
                if any(getattr(self.node(k), 'is_task_configuration', False) for k in before): self.fail('task_configuration_exists')
                self.api(node, 'create_task_configuration')()
            elif operation == 'task.create':
                self.require(node, 'is_task_configuration'); self.api(node, 'create_task')(name)
            elif operation == 'folder.create': self.api(node, 'create_folder')(name)
            elif operation == 'member.create':
                m = change['member']; kind = m['kind']; declaration = self.declaration(node) or ''
                expected = r'^(PROGRAM|FUNCTION_BLOCK|VAR_GLOBAL)\b' if kind == 'Property' else r'^(PROGRAM|FUNCTION_BLOCK)\b'
                if not re.match(expected, self.header(declaration), re.I): self.fail('member_parent_not_supported')
                language = m.get('language') or 'ST'; languages = self.bindings.get('ImplementationLanguages')
                if languages is None or language not in LANGUAGES: self.fail('scriptengine_api_unavailable')
                args = {'name': name, 'language': self.api(languages, LANGUAGES[language])}
                if kind != 'Action': args['return_type'] = m.get('returnType')
                self.api(node, 'create_' + kind.lower())(**args)
            elif operation == 'device.add':
                self.require(node, 'is_device'); d = change['device']; spec = d['identification']
                repository = self.bindings.get('device_repository')
                if repository is None: self.fail('scriptengine_api_unavailable')
                dev_id = repository.create_device_identification(spec['type'], spec['id'], spec['version'])
                if repository.get_device(dev_id) is None: self.fail('device_description_not_installed')
                self.api(node, 'plug' if d.get('plug', False) else 'add')(name=name, type=spec['type'], id=spec['id'], version=spec['version'], module=d.get('module'))
            key = self.new_child(parent, before, name)
        node = self.node(key)
        if operation in ('task.create', 'task.update'): self.set_task(self.require(node, 'is_task'), change['task'])
        elif operation == 'task.calls.set':
            self.require(node, 'is_task'); calls = change['calls']; app = self.application(key)
            for call in calls:
                if self.programs(call['pouName'], app) != [str(uuid.UUID(call['pouId']))]: self.fail('task_program_resolution_conflict')
            collection = self.api(node, 'pous')
            for index in range(len(collection) - 1, -1, -1): collection.remove(index)
            for call in calls: collection.add(call['pouName'], call.get('comment') or '')
            actual = self.calls(node)
            if len(actual) != len(calls) or not all(supplied(c, a) for c, a in zip(calls, actual)): self.fail('task_calls_readback_mismatch')
        elif operation in ('member.create', 'member.update'):
            m = change['member']; kind = m['kind']
            observed_type = text_type(node.type) if operation == 'member.create' else m.get('expectedTypeId')
            self.member(key, kind, observed_type)
            if edit.get('declaration') is not None and kind != 'Action':
                header = self.header(edit['declaration'])
                pattern = r'^' + kind.upper() + r'\s+(?:(?:PUBLIC|PRIVATE|PROTECTED|INTERNAL|FINAL|ABSTRACT)\s+)*' + re.escape(text_type(node.get_name())) + r'\b'
                if not re.match(pattern, header, re.I): self.fail('object_kind_mismatch')
            self.source_write(node, edit)
            if kind == 'Property':
                for accessor, field in (('Get', 'getter'), ('Set', 'setter')):
                    if m.get(field) is not None:
                        children = [n for n in node.get_children(False) if text_type(n.get_name()) == accessor]
                        if len(children) != 1: self.fail('property_accessor_unavailable')
                        self.source_write(children[0], m[field])
            edit = dict(edit); edit['configuration'] = dict(change, member=dict(m, expectedTypeId=observed_type))
        elif operation in ('object.rename', 'object.move', 'object.delete', 'task.delete', 'device.remove'):
            if text_type(node.get_name()) != settings['expectedName']: self.fail('object_name_conflict')
            if operation == 'task.delete': self.require(node, 'is_task')
            if operation == 'device.remove': self.require(node, 'is_device')
            removed = self.protect(key, settings.get('recursive', False) if operation in ('object.delete', 'device.remove') else True, requirements)
            if operation == 'object.rename':
                self.unique(self.rows[key]['parentId'], name, key); self.api(node, 'rename')(name)
            elif operation == 'object.move':
                target = self.node(parent)
                if parent in removed: self.fail('object_move_cycle')
                if self.application(key) != self.application(parent): self.fail('cross_application_move_not_supported')
                self.unique(parent, text_type(node.get_name()), key)
                index = settings.get('index', -1)
                count = len([k for k in self.children(parent) if k != key])
                if index < -1 or index > count: self.fail('invalid_move_index')
                self.api(node, 'move')(target, index)
                self.refresh()
                if self.rows[key]['parentId'] != parent or self.info(key)['index'] != (count if index == -1 else index): self.fail('object_move_readback_mismatch')
            else:
                self.api(node, 'remove')(); self.refresh()
                if any(k in self.nodes for k in removed): self.fail('object_removal_unconfirmed')
                return key, {'removedIds': removed, 'referenceCheckScope': 'task_calls_only'}, edit
            self.refresh()
        elif operation in ('library.add', 'library.remove', 'library.placeholder.set'):
            self.require(node, 'is_libman'); lib = change['library']; libname = lib['name']
            if operation == 'library.add':
                if any(r['name'] == libname for r in self.references(key)): self.fail('library_reference_exists')
                node.add_library(self.installed(libname))
            elif operation == 'library.remove':
                ref = self.find_reference(key, libname)
                if ref.system_library: self.fail('protected_system_library')
                node.remove_library(libname)
            else:
                libname = '#' + libname
                if lib.get('defaultResolution') is not None: self.installed(lib['defaultResolution'])
                if lib.get('redirection'): self.installed(lib['redirection'])
                if not any(r['name'] == libname for r in self.references(key)):
                    if not lib.get('defaultResolution'): self.fail('placeholder_default_required')
                    node.add_placeholder(lib['name'], self.installed(lib['defaultResolution']))
                ref = self.find_reference(key, libname)
                if lib.get('defaultResolution') is not None: ref.default_resolution = lib['defaultResolution']
                if lib.get('redirection') is not None: ref.set_redirection(lib['redirection'])
        elif operation == 'device.parameter.write':
            p = change['parameter']; element, address, root = self.parameter(key, p['address'])
            self.validate_value(element, root, p['value']); element.value = p['value']
            if text_type(element.value) != p['value']: self.fail('parameter_readback_mismatch')
        elif operation == 'device.io.map':
            m = change['mapping']; element, address, root = self.parameter(key, m['address'])
            if not self.api(element, 'is_mappable_io'): self.fail('io_mapping_unavailable')
            mapping = self.api(element, 'io_mapping')
            if mapping is None: self.fail('io_mapping_unavailable')
            mapping.variable = m['variable']
            actual = self.mapping_info(element, address, root)
            if actual['variable'] != m['variable'] or actual['createsVariable'] != m.get('createVariable', False): self.fail('io_mapping_readback_mismatch')
        return key, self.proof(operation, key, edit), edit

    def proof(self, operation, key, edit, before=None):
        change = edit.get('configuration') or {}
        if operation in ('object.delete', 'task.delete', 'device.remove'):
            if before is None or any(k in self.nodes for k in before['removedIds']): self.fail('object_removal_unconfirmed')
            return before
        if operation in ('task.create', 'task.update', 'task.calls.set'): return self.task(key)
        if operation in ('member.create', 'member.update'):
            m = change['member']; return self.member(key, m['kind'], m.get('expectedTypeId'))
        if operation == 'device.add': return self.device(key)
        if operation == 'device.parameter.write':
            return self.parameter_info(*self.parameter(key, change['parameter']['address']))
        if operation == 'device.io.map': return self.mapping_info(*self.parameter(key, change['mapping']['address']))
        if operation.startswith('library.'):
            name = change['library']['name']
            if operation == 'library.remove':
                if any(r['name'] == name for r in self.references(key)): self.fail('library_removal_unconfirmed')
                return {'removedReference': name, 'references': self.references(key)}
            if operation == 'library.placeholder.set': name = '#' + name
            return {'reference': self.reference(key, self.find_reference(key, name)), 'references': self.references(key)}
        result = self.info(key)
        if operation in ('object.rename', 'object.move'): result['referenceCheckScope'] = 'task_calls_only'
        return result


def supplied(expected, actual):
    if expected is None: return True
    if isinstance(expected, dict): return isinstance(actual, dict) and all(v is None or k in actual and supplied(v, actual[k]) for k, v in expected.items())
    if isinstance(expected, text_type): return isinstance(actual, text_type) and normalized(expected) == normalized(actual)
    return expected == actual
