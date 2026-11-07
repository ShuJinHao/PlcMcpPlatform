import copy
import json
from pathlib import Path
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT/'eng'))
import model_scenarios


class StandardModelEvidenceTests(unittest.TestCase):
    def evidence(self):
        program = json.loads((ROOT/'tests/fixtures/graphs/fbd-v2-standard-declarations.json').read_text())
        program['name'] = 'ModelDeclarations'
        program['variables'][0]['structMembers'][0]['initialValue'] = '2'
        program['variables'][0]['documentationXhtml'] = '<xhtml xmlns="http://www.w3.org/1999/xhtml"><p><strong>format updated</strong></p></xhtml>'
        program['variables'][1]['pointerTo']['type'] = 'INT'
        initial = copy.deepcopy(program)
        initial['variables'][0]['structMembers'][0]['initialValue'] = '1'
        initial['variables'][0]['documentationXhtml'] = '<xhtml xmlns="http://www.w3.org/1999/xhtml"><p><strong>preserve format</strong></p></xhtml>'
        initial['variables'][1]['pointerTo']['type'] = 'DINT'
        def job(identity, operation, result):
            return dict(jobId=identity, status='Succeeded', executionMode='fixture', operation=operation, result=result)
        def exchange(value, digest):
            return dict(objectIds=['model-object'], graph=dict(program=value, semanticSha256=digest))
        # Match real server shapes: writes contain engineeringResult; reads contain exchange directly.
        first_write = job('write-initial', 'pou.graph.write', dict(published=True, engineeringResult=dict(exchange=exchange(copy.deepcopy(initial), 'before'))))
        first_read = job('read-initial', 'pou.graph.read', dict(revision='initial-file-sha', exchange=exchange(copy.deepcopy(initial), 'before')))
        final_write = job('write-final', 'pou.graph.write', dict(published=True, engineeringResult=dict(exchange=exchange(copy.deepcopy(program), 'after'))))
        final_read = job('read-final', 'pou.graph.read', dict(revision='file-sha', exchange=exchange(program, 'after')))
        jobs = [job('create', 'project.create', {}), job('diff', 'project.diff', dict(objectDifference=dict(beforeSemanticSha256='before', afterSemanticSha256='after', totalChanges=2))),
                first_write, first_read, final_write, final_read,
                job('package', 'project.package', dict(artifact=dict(workspaceName='case', revision='m2:final', executionMode='fixture', artifactId='zip')))]
        def submitted(name, identity):
            return dict(name=name, input={}, output=dict(job=dict(jobId=identity, status='Queued')), status='completed')
        calls = [submitted('plc_pou_graph_write', 'write-initial'), submitted('plc_pou_graph_read', 'read-initial'),
                 dict(name='plc_job_get', input=dict(jobId='read-initial'), output=dict(job=copy.deepcopy(first_read)), status='completed'),
                 submitted('plc_pou_graph_write', 'write-final')]
        head = dict(projectPath='/case/project', files=[dict(path='project', sha256='file-sha')], revision='m2:final')
        return calls, jobs, head, program

    def prove(self, calls, jobs, head):
        return model_scenarios.prove('declarations', calls, jobs, head, 'case', 'fixture', {})

    def test_server_declarations_and_exact_revision_are_required(self):
        calls, jobs, head, _ = self.evidence()
        self.assertEqual('zip', self.prove(calls, jobs, head)['artifactId'])
        jobs[-2]['result']['revision'] = 'stale'
        with self.assertRaisesRegex(ValueError, 'final_readback_missing'): self.prove(calls, jobs, head)

    def test_pointer_persistence_and_rich_comment_cannot_be_claimed_without_readback(self):
        mutations = [
            (lambda p: p['variables'][1]['pointerTo'].update(type='DINT'), 'pointer_change_missing'),
            (lambda p: p['variableGroups'][1].update(persistence='default'), 'persistence_combination_missing'),
            (lambda p: p['variables'][0].update(documentationXhtml='<xhtml xmlns="http://www.w3.org/1999/xhtml">format updated</xhtml>'), 'formatted_comment_change_missing'),
            (lambda p: p['variables'][0]['structMembers'].reverse(), 'structure_member_order_mismatch'),
        ]
        for mutate, error in mutations:
            with self.subTest(error=error):
                calls, jobs, head, program = self.evidence(); mutate(program)
                with self.assertRaisesRegex(ValueError, error): self.prove(calls, jobs, head)

    def test_identical_graph_writes_are_not_a_modification(self):
        calls, jobs, head, _ = self.evidence()
        jobs[4]['result']['engineeringResult']['exchange']['graph']['semanticSha256'] = 'before'
        with self.assertRaisesRegex(ValueError, 'modification_evidence_missing'): self.prove(calls, jobs, head)

    def test_already_final_initial_program_cannot_prove_the_requested_changes(self):
        calls, jobs, head, program = self.evidence()
        jobs[2]['result']['engineeringResult']['exchange']['graph']['program'] = copy.deepcopy(program)
        with self.assertRaisesRegex(ValueError, 'initial_state_missing'): self.prove(calls, jobs, head)

    def test_each_initial_changed_value_and_preserved_structure_is_required(self):
        mutations = [lambda p: p['variables'][0]['structMembers'][0].update(initialValue='2'),
                     lambda p: p['variables'][1]['pointerTo'].update(type='INT'),
                     lambda p: p['variables'][0].update(documentationXhtml='<xhtml xmlns="http://www.w3.org/1999/xhtml"><b>format updated</b></xhtml>'),
                     lambda p: p['variables'][0]['structMembers'].reverse()]
        for mutate in mutations:
            calls, jobs, head, _ = self.evidence()
            mutate(jobs[2]['result']['engineeringResult']['exchange']['graph']['program'])
            with self.assertRaisesRegex(ValueError, 'initial_state_missing'): self.prove(calls, jobs, head)

    def test_initial_read_must_be_observed_successfully_before_final_write(self):
        for change in ('missing', 'late', 'pending', 'wrong_object', 'wrong_hash'):
            with self.subTest(change=change):
                calls, jobs, head, _ = self.evidence()
                if change == 'missing': calls.pop(2)
                elif change == 'late': calls.append(calls.pop(2))
                elif change == 'pending': calls[2]['output']['job'].update(status='Running', result=None)
                elif change == 'wrong_object': jobs[3]['result']['exchange']['objectIds'] = ['unrelated-object']
                else: jobs[3]['result']['exchange']['graph']['semanticSha256'] = 'unrelated'
                with self.assertRaisesRegex(ValueError, 'initial_read_not_observed_before_modification'): self.prove(calls, jobs, head)

    def test_terminal_result_and_direct_terminal_read_wrappers_are_accepted(self):
        calls, jobs, head, _ = self.evidence()
        calls[2]['name'] = 'plc_job_get_result'
        self.assertEqual('zip', self.prove(calls, jobs, head)['artifactId'])
        calls[1]['output'] = copy.deepcopy(calls[2]['output']); calls.pop(2)
        self.assertEqual('zip', self.prove(calls, jobs, head)['artifactId'])

    def test_diff_must_start_at_observed_initial_graph(self):
        calls, jobs, head, _ = self.evidence()
        jobs[1]['result']['objectDifference']['beforeSemanticSha256'] = 'unobserved-version'
        with self.assertRaisesRegex(ValueError, 'final_diff_missing'): self.prove(calls, jobs, head)


if __name__ == '__main__': unittest.main()
