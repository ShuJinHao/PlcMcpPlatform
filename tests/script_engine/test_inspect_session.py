"""Probe boundary tests. Fixtures are not evidence of vendor compatibility."""
import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / "src/adapters/PlcMcp.Inovance/Scripts/inspect_session.py"
SPEC = importlib.util.spec_from_file_location("inspect_session", SCRIPT)
probe = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(probe)


def forbidden(*args, **kwargs):
    raise AssertionError("Probe invoked a mutating operation")


class Node:
    def __init__(self, name, children=()):
        self.name = name
        self.children = children
        self.guid = name
        self.dirty = True
        self.save = self.close = self.build = self.create_pou = forbidden

    def get_name(self):
        return self.name

    def get_children(self, recursive=False):
        assert not recursive
        return self.children

    @property
    def textual_implementation(self):
        raise AssertionError("Probe should not fetch program source")


def bindings(project):
    return {"projects": SimpleNamespace(primary=project, open=forbidden, create=forbidden),
            "system": SimpleNamespace(get_messages=forbidden, get_message_objects=forbidden),
            "ImplementationLanguages": SimpleNamespace(st="st-id", ladder="ld-id")}


class ProbeTests(unittest.TestCase):
    def test_plain_python_is_not_reported_as_an_ide(self):
        result = probe.inspect_session({})
        self.assertEqual("unavailable", result["status"])
        self.assertEqual("scriptengine_bindings_missing", result["reason"])
        self.assertFalse(result["engineeringReady"])

    def test_no_open_project_is_reported_without_opening_one(self):
        result = probe.inspect_session(bindings(None))
        self.assertEqual("no_primary_project", result["reason"])
        self.assertEqual([], result["objects"])

    def test_inspection_preserves_dirty_project_and_does_not_fetch_source(self):
        project = Node("root", [Node("计数程序")])
        result = probe.inspect_session(bindings(project))
        self.assertEqual("observed", result["status"])
        self.assertEqual("计数程序", result["objects"][1]["name"])
        self.assertTrue(project.dirty)
        self.assertFalse(result["engineeringReady"])
        self.assertEqual("api_presence_only", result["evidenceKind"])
        self.assertEqual("not_guaranteed", result["snapshotConsistency"])
        self.assertEqual(result, json.loads(json.dumps(result)))

    def test_task_binding_api_is_inspected_without_invoking_it(self):
        task = Node("task")
        task.pous = SimpleNamespace(add=forbidden, insert=forbidden, replace=forbidden, remove=forbidden)
        result = probe.inspect_session(bindings(Node("root", [task])))
        self.assertTrue(result["objects"][1]["taskCallApi"]["add"]["callable"])

    def test_node_limit_is_explicit(self):
        result = probe.inspect_session(bindings(Node("root", [Node(str(i)) for i in range(10)])), max_nodes=2)
        self.assertEqual(2, len(result["objects"]))
        self.assertTrue(result["truncated"])
        self.assertIn("max_nodes", result["truncationReasons"])

    def test_depth_limit_is_explicit(self):
        result = probe.inspect_session(bindings(Node("root", [Node("child")])), max_depth=0)
        self.assertEqual(1, len(result["objects"]))
        self.assertIn("max_depth", result["truncationReasons"])

    def test_cycle_is_bounded_and_reported(self):
        project = Node("root")
        project.children = [project]
        result = probe.inspect_session(bindings(project))
        self.assertEqual(1, len(result["objects"]))
        self.assertIn("repeated_object", result["truncationReasons"])

    def test_child_enumeration_failure_is_partial_and_retains_known_nodes(self):
        class Broken(Node):
            def get_children(self, recursive=False):
                yield Node("known")
                raise RuntimeError("private implementation detail")
        result = probe.inspect_session(bindings(Broken("root")))
        self.assertEqual("partial", result["status"])
        self.assertEqual(2, len(result["objects"]))
        self.assertEqual("RuntimeError", result["issues"][0]["errorType"])
        self.assertNotIn("private implementation detail", json.dumps(result))

    def test_getter_failure_is_not_reported_as_unsupported(self):
        class Broken(Node):
            @property
            def device_parameters(self):
                raise RuntimeError("cannot inspect")
        result = probe.inspect_session(bindings(Broken("root")))
        self.assertEqual("partial", result["status"])
        self.assertEqual("inspection_failed", result["objects"][0]["members"]["device_parameters"]["availability"])

    def test_invalid_limits_fail_before_reading_the_project(self):
        for limits in [(0, 1), (2001, 1), (1, -1), (1, 65)]:
            with self.assertRaises(ValueError):
                probe.inspect_session({}, *limits)


if __name__ == "__main__":
    unittest.main()
