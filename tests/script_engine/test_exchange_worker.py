"""Original exchange API-call and failure tests; doubles do not establish IDE compatibility."""
import hashlib
import pathlib
import types
import unittest
import xml.etree.ElementTree as ET
from test_engineering_worker import Node, worker
import test_editing_worker as editing
from test_editing_worker import WritableText
from exchange_worker import canonical, parse_xml

P = '{http://www.plcopen.org/xml/tc6_0201}'
XML = '<project xmlns="http://www.plcopen.org/xml/tc6_0201"><fileHeader creationDateTime="2000-01-01T00:00:00Z"/><types><dataTypes/><pous><pou name="Graph" pouType="program"><interface/><body><LD><leftPowerRail localId="1"><position x="0" y="0"/><connectionPointOut formalParameter=""/></leftPowerRail><contact localId="2"><position x="50" y="0"/><connectionPointIn><connection refLocalId="1"/></connectionPointIn><connectionPointOut/><variable>Start</variable></contact></LD></body></pou></pous></types><instances><configurations/></instances></project>'


class ExchangeWorkerTests(unittest.TestCase):
    def setUp(self):
        self.harness = editing.EditingWorkerTests(); self.harness.setUp(); self.addCleanup(self.harness.doCleanups)
        self.h = self.harness.h
        self.h.bindings.update(ConflictResolve=types.SimpleNamespace(Replace='replace', Skip='skip'),
            NativeImportResolve=types.SimpleNamespace(replace='replace',skip='skip',cancel='cancel'),
            NativeImportResult=types.SimpleNamespace(ok='ok',no_changes='no_changes',skipped='skipped',errors='errors'))
        self.exports, self.imports = [], []
        self.export_error = self.export_warning = self.native_cancel = self.native_skip = self.partial_error = self.import_warning = False
        self.corrupt = False
        self.vendor_exception = False
        self.h.program.exchange_xml = XML.replace('name="Graph"','name="PLC_PRG"')
        def export_xml(**kwargs):
            self.exports.append(kwargs)
            reporter=kwargs['reporter']; nodes=kwargs['objects']
            if self.export_error: reporter.nonexportable(nodes[0])
            if self.export_warning: reporter.warning(nodes[0],'Export warning')
            root=ET.fromstring(nodes[0].exchange_xml); pous=root.find(P+'types/'+P+'pous')
            for node in nodes[1:]: pous.append(ET.fromstring(node.exchange_xml).find(P+'types/'+P+'pous/'+P+'pou'))
            return ET.tostring(root,encoding='unicode')
        self.h.project.export_xml=export_xml
        def perform_import(xml, reporter, native=False):
            root=ET.fromstring(xml); self.imports.append(xml)
            for pou in root.findall(P+'types/'+P+'pous/'+P+'pou'):
                name=pou.get('name'); existing=next((n for n in self.h.app.children if n.name.lower()==name.lower()),None)
                if existing:
                    policy=reporter.conflict(name,existing,'new-guid') if native else reporter.resolve_conflict(existing)
                    if policy in ('skip','cancel'):
                        reporter.skipped(name); continue
                    self.h.app.children.remove(existing)
                n=Node(name); n.exchange_xml=xml
                n.has_textual_declaration=True; n.textual_declaration=WritableText('PROGRAM '+name)
                self.h.app.children.append(n)
                if native: reporter.progress(name,n,None)
                elif existing: reporter.replaced(n)
                else: reporter.added(n)
            if self.partial_error: reporter.error('Second object failed after the first was added')
            if self.import_warning: reporter.warning('Vendor extension was normalized')
            if self.vendor_exception: raise RuntimeError('SDK failed after reporting an imported object')
        self.h.app.import_xml=lambda **kw:perform_import(kw['dataOrPath'],kw['reporter'])
        def export_native(**kw):
            xml=export_xml(objects=kw['objects'],reporter=kw['reporter'])
            if self.native_cancel: kw['reporter'].cancel('Incomplete native stream')
            if self.native_skip: kw['reporter'].skipped('SomeType','SomeProperty')
            wrap=ET.Element('NativeDouble'); ET.SubElement(wrap,'payload').text=xml
            pathlib.Path(kw['destination']).write_text(ET.tostring(wrap,encoding='unicode'),encoding='utf-8')
        self.h.project.export_native=export_native
        def import_native(**kw):
            raw=pathlib.Path(kw['filenames'][0]).read_text(encoding='utf-8')
            perform_import(ET.fromstring(raw).find('payload').text,kw['handler'],True)
            return 'errors' if self.partial_error else 'ok'
        self.h.app.import_native=import_native
        saved_open=self.h.projects.open
        def reopen(*a,**kw):
            project=saved_open(*a,**kw)
            if self.corrupt and self.harness.saved is not None:
                for n in project.children[0].children:
                    if hasattr(n,'exchange_xml'): n.exchange_xml=n.exchange_xml.replace('Start','Lost')
            return project
        self.h.projects.open=reopen

    def request(self, op='project.import', xml=XML, format_name='plcopen', policy='reject', warnings=False, **extra):
        request=self.h.request(op)
        if op in ('project.import','pou.graph.write'):
            change=dict(artifactId=hashlib.sha256((format_name+'\0'+xml).encode()).hexdigest(),format=format_name,xml=xml,conflictPolicy=policy,allowWarnings=warnings)
            request['payload'].update(candidate=True, requirements=dict(controller=dict(type=4096,id='test-controller',version='1.0'),requiredLibraries=['TestLibrary, 1.0 (Fixture)']),
                edit=dict(workspaceName='example',parentId=self.h.app.guid,exchange=change,**extra))
        else: request['payload']['exchange']=dict(format=format_name,objectIds=[self.h.program.guid],recursive=False)
        return request

    def run_request(self,*args,**kwargs): return worker.execute(self.request(*args,**kwargs),self.h.bindings)
    def test_plcopen_export_uses_explicit_objects_and_does_not_save(self):
        r=self.run_request('project.export')
        self.assertEqual('succeeded',r['outcome']); self.assertEqual(0,self.harness.save_count)
        self.assertFalse(self.exports[0]['recursive']); self.assertFalse(self.exports[0]['declarations_as_plaintext'])
        self.assertEqual([self.h.program.guid],r['payload']['exchange']['objectIds'])
    def test_import_saves_reopens_reexports_and_retains_source_artifact(self):
        r=self.run_request()
        self.assertEqual('succeeded',r['outcome'],r)
        self.assertTrue(r['payload']['exchange']['reexportVerified'])
        self.assertEqual((1,1,2),(self.harness.save_count,self.harness.reopens,self.harness.close_count))
        self.assertEqual(2,len(self.exports))
    def test_native_exchange_calls_documented_file_overloads(self):
        exported=self.run_request('project.export',format_name='codesys-native')
        self.assertEqual('succeeded',exported['outcome'])
        xml=exported['payload']['exchange']['xml'].replace('PLC_PRG','NativeCopy')
        r=self.run_request(xml=xml,format_name='codesys-native')
        self.assertEqual('succeeded',r['outcome'],r)
        self.assertTrue(r['payload']['exchange']['reexportVerified'])
    def test_conflict_reject_fails_without_save_and_keeps_report(self):
        r=self.run_request(xml=XML.replace('Graph','PLC_PRG'))
        self.assertEqual('exchange_reported_error',r['errorCode']); self.assertEqual(0,self.harness.save_count)
        self.assertTrue(r['payload']['exchange']['report'])
    def test_explicit_skip_preserves_existing_object(self):
        r=self.run_request(xml=XML.replace('Graph','PLC_PRG'),policy='skip')
        self.assertEqual('succeeded',r['outcome'],r)
        self.assertEqual([self.h.program.guid],r['payload']['exchange']['objectIds'])
        self.assertTrue(any('Skipped' in x['message'] for x in r['payload']['exchange']['report']))
    def test_replace_reports_new_identity_and_old_identity(self):
        r=self.run_request(xml=XML.replace('Graph','PLC_PRG'),policy='replace')
        self.assertEqual('succeeded',r['outcome'],r)
        self.assertNotEqual(self.h.program.guid,r['payload']['exchange']['objectIds'][0])
        self.assertEqual(self.h.program.guid,r['payload']['exchange']['report'][0]['oldObjectId'])
    def test_partial_import_and_unacknowledged_warning_never_save(self):
        self.partial_error=True; r=self.run_request()
        self.assertEqual('exchange_reported_error',r['errorCode']); self.assertEqual(0,self.harness.save_count)
        self.setUp(); self.import_warning=True; r=self.run_request()
        self.assertEqual('exchange_warning_requires_acknowledgement',r['errorCode']); self.assertEqual(0,self.harness.save_count)
    def test_acknowledged_import_warning_is_retained(self):
        self.import_warning=True; r=self.run_request(warnings=True)
        self.assertEqual('succeeded',r['outcome'],r)
        self.assertTrue(any(x['severity']=='warning' for x in r['payload']['exchange']['report']))
    def test_native_cancel_and_skipped_member_never_return_export_success(self):
        self.native_cancel=True
        self.assertEqual('exchange_reported_error',self.run_request('project.export',format_name='codesys-native')['errorCode'])
        self.native_cancel=False; self.native_skip=True
        self.assertEqual('exchange_reported_error',self.run_request('project.export',format_name='codesys-native')['errorCode'])
    def test_nonexportable_selection_fails_even_if_api_returns_xml(self):
        self.export_error=True
        self.assertEqual('exchange_reported_error',self.run_request('project.export')['errorCode'])
    def test_changed_content_after_reopen_does_not_publish(self):
        self.corrupt=True; r=self.run_request()
        self.assertEqual('exchange_reopen_mismatch',r['errorCode'])
        self.assertEqual(1,self.harness.save_count)
        self.assertTrue(any('Imported object' in x['message'] for x in r['payload']['exchange']['report']))
    def test_unexpected_sdk_exception_retains_partial_import_evidence(self):
        self.vendor_exception=True; r=self.run_request()
        self.assertEqual('script_execution_failed',r['errorCode']); self.assertEqual(0,self.harness.save_count)
        self.assertFalse(r['payload']['published'])
        self.assertTrue(any('Imported object' in x['message'] for x in r['payload']['exchange']['report']))
    def test_graph_create_uses_same_lifecycle(self):
        r=self.run_request('pou.graph.write',name='Graph')
        self.assertEqual('succeeded',r['outcome'],r)
        self.assertTrue(r['payload']['readBackVerified'])
    def test_graph_replacement_checks_identity_before_import(self):
        r=self.run_request('pou.graph.write',name='Graph',objectId=self.h.program.guid,policy='replace')
        self.assertEqual('graph_identity_conflict',r['errorCode']); self.assertEqual([],self.imports)
    def test_xml_entities_and_artifact_tampering_fail_before_import(self):
        xml='<!DOCTYPE x [<!ENTITY a "secret">]><x>&a;</x>'
        self.assertEqual('invalid_exchange_xml',self.run_request(xml=xml)['errorCode'])
        request=self.request();request['payload']['edit']['exchange']['xml']=XML.replace('Start','Other')
        self.assertEqual('artifact_integrity_mismatch',worker.execute(request,self.h.bindings)['errorCode'])
        self.assertEqual([],self.imports)
    def test_proof_ignores_only_header_timestamp_and_not_unknown_extension(self):
        def fail(code):raise ValueError(code)
        self.assertEqual(canonical(XML,'plcopen',fail),canonical(XML.replace('2000-01-01','2026-01-01'),'plcopen',fail))
        altered=XML.replace('</pou>','<addData><vendor value="changed"/></addData></pou>')
        self.assertNotEqual(canonical(XML,'plcopen',fail),canonical(altered,'plcopen',fail))


if __name__=='__main__': unittest.main()
