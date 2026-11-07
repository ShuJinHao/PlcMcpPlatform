"""Failure snapshots are evidence, never a reason to replay engineering writes."""
import base64
import copy
from contextlib import redirect_stdout
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT/'eng'))
spec = importlib.util.spec_from_file_location('model_suite_under_test', ROOT/'eng/model-suite.py')
suite = importlib.util.module_from_spec(spec)
spec.loader.exec_module(suite)

JOB_A = '00000000-0000-0000-0000-000000000001'
JOB_B = '00000000-0000-0000-0000-000000000002'
JOB_C = '00000000-0000-0000-0000-000000000003'
PRIVATE_ERROR = 'private-provider-detail-must-not-enter-evidence'
READ_TOOLS = {'plc_job_get', 'plc_workspace_get', 'plc_delivery_read'}


def job(identity, status, operation='pou.graph.write', result=None):
    return dict(jobId=identity, status=status, operation=operation, executionMode='fixture',
                evidenceKind='simulated', result=result)


class ReadOnlyClient:
    def __init__(self, jobs, workspace=None, failures=(), payload=None, artifact=None):
        self.jobs = jobs
        self.workspace = workspace or dict(workspaceName='case', revision='m2:final', files=[])
        self.failures = set(failures)
        self.payload = payload
        self.artifact = artifact
        self.calls = []

    def call(self, name, arguments):
        self.calls.append((name, copy.deepcopy(arguments)))
        if name not in READ_TOOLS:
            raise AssertionError('Evidence collector attempted a non-read tool: '+name)
        if name == 'plc_job_get':
            identity = arguments['jobId']
            if identity in self.failures: raise OSError(PRIVATE_ERROR)
            return dict(job=copy.deepcopy(self.jobs[identity]))
        if name == 'plc_workspace_get':
            if 'workspace' in self.failures: raise ConnectionError(PRIVATE_ERROR)
            return dict(value=copy.deepcopy(self.workspace))
        if self.payload is None or self.artifact is None:
            raise AssertionError('A failed run must not request a delivery.')
        offset = arguments['offset']
        data = self.payload[offset:offset+arguments['maxBytes']]
        return dict(value=dict(artifact=self.artifact, base64=base64.b64encode(data).decode(),
                               offset=offset, nextOffset=offset+len(data), complete=offset+len(data)==len(self.payload),
                               chunkSha256=hashlib.sha256(data).hexdigest()))


class ModelSuiteEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.directory = Path(self.temporary.name)

    def tearDown(self):
        self.temporary.cleanup()

    def assert_read_only_once(self, client):
        self.assertLessEqual({name for name, _ in client.calls}, READ_TOOLS)
        identities = [arguments['jobId'] for name, arguments in client.calls if name == 'plc_job_get']
        self.assertEqual(len(identities), len(set(identities)), 'Snapshot collection retried a job query.')
        self.assertLessEqual(sum(name == 'plc_workspace_get' for name, _ in client.calls), 1)

    def successful(self):
        stream = io.BytesIO()
        with zipfile.ZipFile(stream, 'w') as archive:
            archive.writestr('manifest.json', json.dumps(dict(workspaceName='case', revision='m2:final')))
        data = stream.getvalue()
        digest = hashlib.sha256(data).hexdigest()
        artifact = dict(artifactId=digest, sha256=digest, byteLength=len(data), workspaceName='case',
                        revision='m2:final', executionMode='fixture')
        client = ReadOnlyClient({JOB_A: job(JOB_A, 'Succeeded', 'project.package', dict(artifact=artifact))},
                                payload=data, artifact=artifact)
        report = dict(status='passed', workspaceName='case', jobIds=[JOB_A], evidenceKind='simulated',
                      executionMode='fixture', finalRevision='m2:final', deliverySha256=digest, toolCalls=1)
        return client, report, data

    def test_failed_and_timeout_reports_collect_observed_jobs_without_delivery_or_retry(self):
        for error_code in ('ValueError', 'TimeoutError'):
            with self.subTest(error_code=error_code):
                output = self.directory/error_code; output.mkdir()
                report = dict(status='failed', errorCode=error_code, workspaceName='case',
                              observedJobIds=[JOB_A, JOB_B, JOB_A], evidenceKind='simulated')
                original = copy.deepcopy(report)
                client = ReadOnlyClient({JOB_A: job(JOB_A, 'Running'), JOB_B: job(JOB_B, 'OutcomeUnknown')})
                snapshot = suite.collect_evidence(client, output, report)
                self.assertEqual(original, report)
                self.assertEqual(snapshot, json.loads((output/'server-evidence.json').read_text()))
                self.assertEqual({JOB_A, JOB_B}, set(snapshot['requestedJobIds']))
                self.assertEqual({JOB_A, JOB_B}, set(snapshot['unresolvedJobIds']))
                self.assertEqual({'Running', 'OutcomeUnknown'}, {row['status'] for row in snapshot['jobs']})
                self.assertFalse((output/'delivery.zip').exists())
                self.assertFalse(any(name == 'plc_delivery_read' for name, _ in client.calls))
                self.assert_read_only_once(client)

    def test_partial_job_and_workspace_read_failures_keep_other_results_without_private_details(self):
        report = dict(status='failed', workspaceName='case', jobIds=[JOB_A, JOB_B], observedJobIds=[JOB_B, JOB_C])
        client = ReadOnlyClient({JOB_A: job(JOB_A, 'Succeeded'), JOB_C: job(JOB_C, 'Failed', result=dict(diagnostics=['fixture compile failure']))},
                                failures={JOB_B, 'workspace'})
        snapshot = suite.collect_evidence(client, self.directory, report)
        self.assertEqual({JOB_A, JOB_B, JOB_C}, set(snapshot['requestedJobIds']))
        self.assertEqual({JOB_A, JOB_C}, {row['jobId'] for row in snapshot['jobs']})
        self.assertFalse(snapshot['complete'])
        self.assertIsNone(snapshot['workspace'])
        self.assertEqual({'job', 'workspace'}, {error['target'] for error in snapshot['readErrors']})
        self.assertTrue(all(isinstance(error.get('code'), str) for error in snapshot['readErrors']))
        self.assertNotIn(PRIVATE_ERROR, (self.directory/'server-evidence.json').read_text())
        self.assert_read_only_once(client)

    def test_failure_before_workspace_creation_still_leaves_a_snapshot(self):
        client = ReadOnlyClient({})
        snapshot = suite.collect_evidence(client, self.directory, dict(status='failed', errorCode='provider_failure'))
        self.assertTrue((self.directory/'server-evidence.json').is_file())
        self.assertEqual([], snapshot['jobs'])
        self.assertFalse(snapshot['complete'])
        self.assertFalse(client.calls)

    def test_success_snapshot_and_exact_delivery_are_preserved(self):
        client, report, expected = self.successful()
        snapshot = suite.collect_evidence(client, self.directory, report)
        self.assertTrue(snapshot['complete'])
        suite.collect_delivery(client, self.directory, report, snapshot)
        self.assertEqual(expected, (self.directory/'delivery.zip').read_bytes())
        self.assertEqual(report['deliverySha256'], suite.digest(self.directory/'delivery.zip'))
        self.assert_read_only_once(client)

    def test_delivery_requires_a_successful_complete_resolved_snapshot_and_exact_revision(self):
        for change in ('failed_report', 'incomplete', 'read_error', 'unresolved', 'stale_head', 'wrong_package_revision', 'wrong_package_digest'):
            with self.subTest(change=change):
                output = self.directory/change; output.mkdir()
                client, report, _ = self.successful()
                snapshot = suite.collect_evidence(client, output, report)
                if change == 'failed_report': report['status'] = 'failed'
                elif change == 'incomplete': snapshot['complete'] = False
                elif change == 'read_error': snapshot['readErrors'] = [dict(target='job', jobId=JOB_B, code='OSError')]
                elif change == 'unresolved':
                    snapshot['unresolvedJobIds'] = [JOB_B]; snapshot['jobs'].append(job(JOB_B, 'OutcomeUnknown'))
                elif change == 'stale_head': snapshot['workspace']['revision'] = 'm2:other'
                elif change == 'wrong_package_revision': snapshot['jobs'][0]['result']['artifact']['revision'] = 'm2:other'
                else: snapshot['jobs'][0]['result']['artifact']['sha256'] = '0'*64
                with self.assertRaises(ValueError): suite.collect_delivery(client, output, report, snapshot)
                self.assertFalse(any(name == 'plc_delivery_read' for name, _ in client.calls))
                self.assertFalse((output/'delivery.zip').exists())

    def run_main(self, report, returncode, client, evidence_error=None, runs=None):
        output = self.directory/'suite'
        expected_report = json.dumps(report, ensure_ascii=False, indent=2)+'\n'
        commands = []
        def model(command, **unused):
            commands.append(command)
            Path(command[command.index('--report')+1]).write_text(expected_report, encoding='utf-8')
            return SimpleNamespace(returncode=returncode)
        with patch.object(suite, 'RUNS', runs if runs is not None else [('first', 'repair'), ('must-not-run', 'graphics')]), \
             patch.object(suite, 'McpClient', return_value=client), \
             patch.object(suite.subprocess, 'run', side_effect=model), \
             patch.dict(os.environ, {'PLCMCP_API_KEY':'local-fixture-key'}), \
             patch.object(sys, 'argv', ['model-suite', '--execute', '--report-directory', str(output)]), \
             redirect_stdout(io.StringIO()):
            if evidence_error is None:
                result = suite.main()
            else:
                with patch.object(suite, 'collect_evidence', side_effect=evidence_error): result = suite.main()
        self.assertEqual(1, len(commands))
        self.assertEqual(expected_report, (output/'first/report.json').read_text())
        self.assertFalse((output/'must-not-run').exists())
        return result, output, json.loads((output/'summary.json').read_text())

    def test_main_collects_failure_snapshot_then_stops_without_requiring_a_zip(self):
        report = dict(status='failed', errorCode='TimeoutError', workspaceName='case', observedJobIds=[JOB_A])
        client = ReadOnlyClient({JOB_A:job(JOB_A, 'Running')}, failures={'workspace'})
        result, output, summary = self.run_main(report, 7, client)
        self.assertEqual(7, result)
        self.assertEqual('suite_stopped_at_first', summary['reason'])
        self.assertEqual('failed', summary['status'])
        self.assertFalse(summary['llmGenerationVerified'])
        row = summary['runs'][0]
        self.assertEqual('TimeoutError', row['errorCode'])
        self.assertEqual(7, row['exitCode'])
        self.assertEqual('failed', row['modelStatus'])
        self.assertEqual('first/server-evidence.json', row['serverEvidence'])
        self.assertEqual(suite.digest(output/'first/server-evidence.json'), row['serverEvidenceSha256'])
        self.assertFalse((output/'first/delivery.zip').exists())
        self.assertFalse(any(name == 'plc_delivery_read' for name, _ in client.calls))
        self.assert_read_only_once(client)

    def test_evidence_collection_exception_cannot_replace_original_model_failure(self):
        report = dict(status='failed', errorCode='TimeoutError', workspaceName='case', observedJobIds=[JOB_A])
        result, output, summary = self.run_main(report, 9, ReadOnlyClient({}), PermissionError(PRIVATE_ERROR))
        self.assertEqual(9, result)
        self.assertEqual('suite_stopped_at_first', summary['reason'])
        self.assertEqual('failed', summary['status'])
        self.assertEqual('TimeoutError', summary['runs'][0]['errorCode'])
        self.assertEqual(9, summary['runs'][0]['exitCode'])
        self.assertEqual('failed', summary['runs'][0]['modelStatus'])
        self.assertTrue(summary['runs'][0].get('evidenceError'))
        self.assertNotIn(PRIVATE_ERROR, (output/'summary.json').read_text())

    def test_nonzero_model_exit_cannot_be_reported_as_passed_or_start_delivery(self):
        client, report, _ = self.successful()
        result, output, summary = self.run_main(report, 4, client)
        self.assertEqual(4, result)
        self.assertEqual('failed', summary['status'])
        self.assertFalse(summary['llmGenerationVerified'])
        self.assertEqual('passed', summary['runs'][0]['modelStatus'])
        self.assertEqual(4, summary['runs'][0]['exitCode'])
        self.assertTrue((output/'first/server-evidence.json').exists())
        self.assertFalse((output/'first/delivery.zip').exists())
        self.assertFalse(any(name == 'plc_delivery_read' for name, _ in client.calls))

    def test_main_success_still_requires_snapshot_and_downloaded_exact_delivery(self):
        client, report, expected = self.successful()
        result, output, summary = self.run_main(report, 0, client, runs=[('first', 'repair')])
        self.assertEqual(0, result)
        self.assertEqual('passed', summary['status'])
        self.assertTrue(summary['llmGenerationVerified'])
        self.assertEqual(expected, (output/'first/delivery.zip').read_bytes())
        row = summary['runs'][0]
        self.assertEqual('passed', row['modelStatus'])
        self.assertEqual(0, row['exitCode'])
        self.assertEqual(suite.digest(output/'first/server-evidence.json'), row['serverEvidenceSha256'])
        self.assert_read_only_once(client)

    def test_successful_model_with_incomplete_snapshot_stops_without_next_model_or_zip(self):
        client, report, _ = self.successful()
        client.failures.add('workspace')
        result, output, summary = self.run_main(report, 0, client)
        self.assertEqual(2, result)
        self.assertEqual('failed', summary['status'])
        self.assertEqual('passed', summary['runs'][0]['modelStatus'])
        self.assertEqual(0, summary['runs'][0]['exitCode'])
        self.assertTrue((output/'first/server-evidence.json').exists())
        self.assertFalse((output/'first/delivery.zip').exists())
        self.assertFalse(any(name == 'plc_delivery_read' for name, _ in client.calls))



if __name__ == '__main__': unittest.main()
