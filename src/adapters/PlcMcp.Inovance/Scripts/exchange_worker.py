# -*- coding: utf-8 -*-
"""Original official-API exchange worker. Python 2.7 syntax; no third-party MCP code.

Only engineering_worker owns open/save/reopen/close. This module exports to private
temporary files and returns bounded XML over the existing internal protocol.
"""
from __future__ import print_function
import hashlib
import os
import re
import tempfile
import uuid
import xml.etree.ElementTree as ET

try:
    text_type = unicode
except NameError:
    text_type = str

READS = ('project.export', 'pou.graph.read')
WRITES = ('project.import', 'pou.graph.write')
MAX_XML = int(os.environ.get('PLCMCP_EXCHANGE_MAX_BYTES', 8 * 1024 * 1024))
if not 65536 <= MAX_XML <= 64 * 1024 * 1024: raise ValueError('Invalid exchange limit')
P = '{http://www.plcopen.org/xml/tc6_0201}'


def parse_xml(xml, fail):
    if not isinstance(xml, text_type) or not xml.strip() or len(xml.encode('utf-8')) > MAX_XML:
        fail('exchange_size_invalid')
    if re.search(r'<!\s*(DOCTYPE|ENTITY)', xml, re.I) or '\0' in xml:
        fail('invalid_exchange_xml')
    try:
        root = ET.fromstring(re.sub(r'(<\?xml\s[^?]*?)encoding\s*=\s*[\"\'][^\"\']*[\"\']', r'\1', xml, count=1, flags=re.I).encode('utf-8'))
    except (ValueError, ET.ParseError):
        fail('invalid_exchange_xml')
    queue, count = [(root, 0)], 0
    while queue:
        node, depth = queue.pop(); count += 1
        if depth > 32 or count > 100000: fail('exchange_structure_too_large')
        queue.extend((child, depth + 1) for child in node)
    return root


def read_transport(request, directory):
    change = (request.get('payload', {}).get('edit') or {}).get('exchange') or {}
    ref = change.get('file')
    if ref is None: return request
    if change.get('xml') is not None or ref.get('name') != 'exchange-input.xml': raise ValueError('Invalid exchange transport')
    size = ref.get('byteLength')
    if type(size) is not int or not 0 < size <= MAX_XML: raise ValueError('Invalid exchange length')
    path = os.path.join(directory, 'exchange-input.xml')
    if os.path.islink(path) or os.path.getsize(path) != size: raise ValueError('Invalid exchange file')
    with open(path, 'rb') as source: raw = source.read(size + 1)
    if len(raw) != size or hashlib.sha256(raw).hexdigest() != ref.get('sha256'): raise ValueError('Exchange integrity mismatch')
    change['xml'] = raw.decode('utf-8'); del change['file']
    return request


def write_transport(response, directory):
    exchange = (response.get('payload') or {}).get('exchange')
    if not exchange or not isinstance(exchange.get('xml'), text_type): return response
    if exchange.get('file') is not None: raise ValueError('Ambiguous exchange transport')
    raw = exchange['xml'].encode('utf-8')
    if len(raw) > MAX_XML: raise ValueError('Exchange limit exceeded')
    if len(raw) <= 65536: return response
    path = os.path.join(directory, 'exchange-output.xml')
    # Each invocation has a fresh directory. Never overwrite a pre-existing file or link.
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, 'wb') as target:
        target.write(raw); target.flush(); os.fsync(target.fileno())
    exchange['file'] = dict(name='exchange-output.xml', sha256=hashlib.sha256(raw).hexdigest(), byteLength=len(raw))
    exchange['xml'] = None
    return response


def canonical(xml, format_name, fail):
    root = parse_xml(xml, fail)
    def visit(node):
        # Header timestamps are export metadata. All object/extension data remains in the proof.
        attrs = sorted((key, value) for key, value in node.attrib.items()
                       if not (format_name == 'plcopen' and node.tag in (P + 'fileHeader', P + 'contentHeader')
                               and key in ('creationDateTime', 'modificationDateTime')))
        text = node.text or ''
        if len(node) and not text.strip(): text = ''
        children = tuple((visit(child), child.tail if (child.tail or '').strip() else '') for child in node)
        return (node.tag, tuple(attrs), text.replace('\r\n', '\n'), children)
    return visit(root)


class ExchangeReporter(object):
    def __init__(self, engine, policy='reject', requirements=None):
        self.engine, self.policy, self.requirements = engine, policy, requirements
        self.aborting = False
        self.objects, self.conflicts = {}, {}
        self.failed = False

    def entry(self, severity, message, node=None, old=None):
        if len(self.engine.report) >= 256: self.engine.fail('exchange_report_too_large')
        item = {'severity': severity, 'message': text_type(message)[:2048]}
        if node is not None:
            key = self.engine.identity(node)
            item.update(objectId=key, name=text_type(node.get_name()))
        if old is not None: item['oldObjectId'] = old
        self.engine.report.append(item)

    def error(self, *args):
        self.failed, self.aborting = True, True
        self.entry('error', args[-1])

    def warning(self, *args): self.entry('warning', args[-1])
    def warn(self, message): self.warning(message)
    def cancel(self, message): self.error(message)
    def nonexportable(self, obj): self.error('Object is not exportable: ' + text_type(obj.get_name()))

    def resolve_conflict(self, obj):
        key = self.engine.identity(obj)
        self.conflicts[text_type(obj.get_name()).lower()] = key
        self.objects[key] = obj
        if self.policy == 'replace': self.engine.configuration.protect(key, True, self.requirements)
        if self.policy == 'reject':
            self.error('Object name conflict: ' + text_type(obj.get_name()))
        kind = self.engine.binding('ConflictResolve')
        return kind.Replace if self.policy == 'replace' else kind.Skip

    def added(self, obj):
        self.objects[self.engine.identity(obj)] = obj
        self.entry('information', 'Imported object', obj)

    def replaced(self, obj):
        old = self.conflicts.get(text_type(obj.get_name()).lower())
        if old is not None: self.objects.pop(old, None)
        self.objects[self.engine.identity(obj)] = obj
        self.entry('information', 'Replaced object', obj, old)

    def skipped(self, *args):
        # Native export has (type_name, member_name); this denotes data loss, never an allowed import skip.
        if len(args) != 1:
            self.error('Native serialization skipped data: ' + ', '.join(text_type(a) for a in args)); return
        name = args[0]
        if isinstance(name, (tuple, list)): name = name[-1] if name else ''
        if self.policy != 'skip' or text_type(name).lower() not in self.conflicts:
            self.error('Unexpected skipped object: ' + text_type(name)); return
        self.entry('information', 'Skipped conflicting object: ' + text_type(name))

    def conflict(self, name, existingObject, newguid):
        self.resolve_conflict(existingObject)
        kind = self.engine.binding('NativeImportResolve')
        return kind.replace if self.policy == 'replace' else kind.skip if self.policy == 'skip' else kind.cancel

    def progress(self, name, pastedObject, exception):
        if exception is not None: self.error(text_type(exception)); return
        if pastedObject is None: self.error('Native import did not identify its object'); return
        old = self.conflicts.get(text_type(pastedObject.get_name()).lower())
        if old: self.replaced(pastedObject)
        else: self.added(pastedObject)

    def check(self, allow_warnings=False):
        if self.failed: self.engine.fail('exchange_reported_error')
        if not allow_warnings and any(x['severity'] == 'warning' for x in self.engine.report):
            self.engine.fail('exchange_warning_requires_acknowledgement')


class ExchangeEngine(object):
    def __init__(self, project, bindings, error_type, configuration):
        self.project, self.bindings, self.error_type, self.configuration = project, bindings, error_type, configuration
        self.report = []

    def fail(self, code): raise self.error_type(code)
    def binding(self, key):
        if self.bindings.get(key) is None: self.fail('scriptengine_api_unavailable')
        return self.bindings[key]
    def identity(self, node): return str(uuid.UUID(text_type(node.guid)))

    def select(self, ids, recursive):
        self.configuration.refresh()
        if not isinstance(ids, list) or not 1 <= len(ids) <= 2000 or len(set(ids)) != len(ids): self.fail('invalid_exchange_selection')
        if 'project' in ids:
            if ids != ['project']: self.fail('invalid_exchange_selection')
            ids = [self.identity(n) for n in self.project.get_children(False)]
        found = []
        for key in ids:
            for child_key in self.configuration.descendants(key) if recursive else [key]:
                if child_key not in found: found.append(child_key)
        if not found: self.fail('exchange_selection_empty')
        return [self.configuration.node(k) for k in found]

    def export(self, format_name, ids, recursive=True, strict_warnings=False):
        if format_name not in ('plcopen', 'codesys-native'): self.fail('invalid_exchange_format')
        objects = self.select(ids, recursive)
        reporter = ExchangeReporter(self)
        if format_name == 'plcopen':
            # Pass every selected descendant explicitly: the API silently ignores non-exportable
            # recursive children, whereas explicit selections receive nonexportable callbacks.
            xml = self.project.export_xml(objects=tuple(objects), reporter=reporter, recursive=False,
                                          export_folder_structure=False, declarations_as_plaintext=False)
        else:
            fd, path = tempfile.mkstemp(suffix='.export'); os.close(fd)
            try:
                self.project.export_native(objects=objects, destination=path, recursive=False,
                                           one_file_per_subtree=False, reporter=reporter)
                with open(path, 'rb') as source: raw = source.read(MAX_XML + 1)
                if len(raw) > MAX_XML: self.fail('exchange_size_invalid')
                xml = raw.decode('utf-16') if raw.startswith((b'\xff\xfe', b'\xfe\xff')) else raw.decode('utf-8-sig')
            finally:
                if os.path.exists(path): os.remove(path)
        reporter.check(allow_warnings=not strict_warnings)
        parse_xml(xml, self.fail)
        return {'format': format_name, 'xml': xml, 'objectIds': [self.identity(n) for n in objects],
                'report': list(self.report), 'selectionScope': 'explicit_objects_including_requested_descendants',
                'sourceToExportLosslessness': 'not_proven'}

    def read_graph(self, key):
        node = self.configuration.node(key)
        # Member children are not safely replaceable by a body-only graph. Keep them opaque.
        if list(node.get_children(False)): self.fail('graph_object_has_children')
        return self.export('plcopen', [key], False, strict_warnings=True)

    def apply(self, operation, edit, requirements):
        change = edit.get('exchange') or {}
        format_name, xml, policy = change.get('format'), change.get('xml'), change.get('conflictPolicy')
        if format_name not in ('plcopen', 'codesys-native') or policy not in ('reject', 'replace', 'skip'): self.fail('invalid_exchange_request')
        root = parse_xml(xml, self.fail)
        digest = hashlib.sha256((format_name + '\0' + xml).encode('utf-8')).hexdigest()
        if digest != change.get('artifactId'): self.fail('artifact_integrity_mismatch')
        parent = self.project if edit.get('parentId') == 'project' else self.configuration.node(edit['parentId'])
        if operation == 'pou.graph.write':
            if format_name != 'plcopen' or root.tag != P + 'project' or parent is self.project: self.fail('invalid_graph_request')
            pous = root.findall(P + 'types/' + P + 'pous/' + P + 'pou')
            if len(pous) != 1 or pous[0].get('name') != edit.get('name'): self.fail('graph_name_mismatch')
            existing = [n for n in parent.get_children(False) if text_type(n.get_name()).lower() == edit['name'].lower()]
            if edit.get('objectId'):
                if len(existing) != 1 or self.identity(existing[0]) != edit['objectId']: self.fail('graph_identity_conflict')
                if list(existing[0].get_children(False)): self.fail('graph_object_has_children')
            elif existing: self.fail('object_name_conflict')
        reporter = ExchangeReporter(self, policy, requirements)
        if format_name == 'plcopen':
            if root.tag != P + 'project': self.fail('exchange_namespace_unsupported')
            # Project import has no conflictResolve argument. Reporter chooses every conflict.
            parent.import_xml(dataOrPath=xml, reporter=reporter, import_folder_structure=False)
        else:
            if root.tag == P + 'project': self.fail('exchange_format_mismatch')
            fd, path = tempfile.mkstemp(suffix='.export'); os.close(fd)
            try:
                with open(path, 'wb') as target:
                    # String artifacts are UTF-8; normalize only the XML encoding declaration for the private SDK file.
                    normalized = re.sub(r'(<\?xml\s[^?]*?)encoding\s*=\s*[\"\'][^\"\']*[\"\']', r'\1encoding="utf-8"', xml, count=1, flags=re.I)
                    target.write(normalized.encode('utf-8'))
                outcome = parent.import_native(filenames=[path], handler=reporter)
                kinds = self.binding('NativeImportResult')
                if outcome not in (kinds.ok, kinds.no_changes, kinds.skipped): self.fail('native_import_failed')
                if outcome == kinds.skipped and policy != 'skip': self.fail('unexpected_import_skip')
            finally:
                if os.path.exists(path): os.remove(path)
        reporter.check(change.get('allowWarnings', False))
        self.configuration.refresh()
        ids = sorted(reporter.objects)
        if not ids: self.fail('import_no_objects_observed')
        if operation == 'pou.graph.write':
            matching = [n for n in parent.get_children(False) if text_type(n.get_name()) == edit['name']]
            if len(matching) != 1: self.fail('graph_identity_conflict')
            ids = [self.identity(matching[0])]
            # Single POU content includes an SFC body; child methods/properties remain outside both graph profiles.
            before_save = self.export(format_name, ids, False, strict_warnings=True)
        else: before_save = self.export(format_name, ids, True, strict_warnings=not change.get('allowWarnings', False))
        before_save.update(sourceArtifactId=change['artifactId'], reexportVerified=False,
                           verificationScope='post_import_vs_reopened_export', conflictPolicy=policy)
        return before_save

    def proof(self, saved):
        observed = self.export(saved['format'], saved['objectIds'], False, strict_warnings=True)
        if canonical(saved['xml'], saved['format'], self.fail) != canonical(observed['xml'], saved['format'], self.fail):
            self.fail('exchange_reopen_mismatch')
        result = dict(saved, xml=observed['xml'], reexportVerified=True)
        result['report'] = saved['report'] + self.report
        if len(result['report']) > 256: self.fail('exchange_report_too_large')
        return result
