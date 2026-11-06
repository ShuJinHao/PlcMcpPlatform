# -*- coding: utf-8 -*-
"""Original, read-only ScriptEngine API probe; run inside the target IDE.

Documentation: docs/10-official-api-baseline.md. No third-party MCP dependency.
Python 2.7-compatible syntax is intentional; real IronPython validation is pending.
"""
from __future__ import print_function

import json
import sys
from collections import deque

try:
    text_type = unicode
except NameError:
    text_type = str

PREFIX = "PLCMCP_PROBE_V1 "
OBJECT_MEMBERS = (
    "create_pou", "create_gvl", "create_dut", "create_method",
    "create_property", "create_action", "create_transition",
    "has_textual_declaration", "has_textual_implementation",
    "is_application", "create_task_configuration", "build", "rebuild",
    "is_task_configuration", "create_task", "is_task", "pous",
    "is_device", "device_parameters", "connectors",
    "export_io_mappings_as_csv", "import_io_mappings_from_csv",
    "get_libraries", "add_library", "remove_library",
    "import_xml", "export_xml", "import_native", "export_native",
)


def _read(obj, name, issues, location):
    try:
        return "present", getattr(obj, name)
    except AttributeError:
        return "absent", None
    except Exception as error:
        issues.append({"location": location, "member": name,
                       "errorType": type(error).__name__})
        return "inspection_failed", None


def _members(obj, names, issues, location):
    result = {}
    for name in names:
        availability, value = _read(obj, name, issues, location)
        result[name] = {"availability": availability}
        if availability == "present":
            result[name]["callable"] = callable(value)
    return result


def _assemblies():
    try:
        from System import AppDomain
        result = []
        for assembly in AppDomain.CurrentDomain.GetAssemblies():
            name = assembly.GetName()
            if text_type(name.Name) in ("ScriptEngine", "Core", "DeviceObject"):
                result.append({"name": text_type(name.Name),
                               "version": text_type(name.Version)})
        return {"status": "observed", "items": result}
    except Exception as error:
        return {"status": "unavailable", "items": [],
                "errorType": type(error).__name__}


def inspect_session(bindings, max_nodes=256, max_depth=16):
    if not 1 <= max_nodes <= 2000 or not 0 <= max_depth <= 64:
        raise ValueError("Invalid inspection bounds.")
    report = {
        "reportVersion": 1, "scriptVersion": "0.1.0",
        "evidenceKind": "api_presence_only", "engineeringReady": False,
        "status": "unavailable", "issues": [], "objects": [],
        "truncated": False, "truncationReasons": [],
        "snapshotConsistency": "not_guaranteed",
        "runtime": {"pythonVersion": sys.version, "assemblies": _assemblies(),
                    "inoproshopVersion": None, "profile": None},
    }
    issues = report["issues"]
    projects = bindings.get("projects")
    system = bindings.get("system")
    if projects is None or system is None:
        report["reason"] = "scriptengine_bindings_missing"
        return report
    report["status"] = "observed"
    report["projectsApi"] = _members(projects, ("open", "create", "primary"), issues, "projects")
    report["systemApi"] = _members(system, ("get_message_objects", "get_messages", "ui_present"), issues, "system")
    languages = bindings.get("ImplementationLanguages")
    report["languages"] = {}
    if languages is not None:
        for name in ("st", "ladder", "fbd", "sfc", "cfc", "page_oriented_cfc", "instruction_list"):
            availability, value = _read(languages, name, issues, "ImplementationLanguages")
            report["languages"][name] = {"availability": availability,
                                          "id": text_type(value) if value is not None else None}
    availability, project = _read(projects, "primary", issues, "projects")
    report["hasPrimaryProject"] = project is not None
    if project is None:
        report["reason"] = "no_primary_project" if availability == "present" else "primary_project_unreadable"
        report["status"] = "partial" if issues or availability != "present" else "observed"
        return report

    _, dirty_before = _read(project, "dirty", issues, "project")
    report["projectDirtyBefore"] = bool(dirty_before) if dirty_before is not None else None
    pending = deque([(project, None, 0)])
    seen = set()
    while pending:
        obj, parent_id, depth = pending.popleft()
        node_id = "n" + str(len(report["objects"]))
        _, guid = _read(obj, "guid", issues, node_id)
        identity = ("guid", text_type(guid)) if guid is not None else ("python_id", id(obj))
        if identity in seen:
            report["truncated"] = True
            report["truncationReasons"].append("repeated_object")
            continue
        seen.add(identity)
        node = {"id": node_id, "parentId": parent_id, "depth": depth,
                "guid": text_type(guid) if guid is not None else None,
                "name": None, "members": _members(obj, OBJECT_MEMBERS, issues, node_id)}
        state, get_name = _read(obj, "get_name", issues, node_id)
        if state == "present" and callable(get_name):
            try:
                node["name"] = text_type(get_name())
            except Exception as error:
                issues.append({"location": node_id, "member": "get_name",
                               "errorType": type(error).__name__})
        state, pous = _read(obj, "pous", issues, node_id)
        if state == "present" and pous is not None:
            node["taskCallApi"] = _members(pous, ("add", "insert", "replace", "remove"), issues, node_id + ".pous")
        report["objects"].append(node)
        state, get_children = _read(obj, "get_children", issues, node_id)
        if state != "present" or not callable(get_children):
            issues.append({"location": node_id, "member": "get_children",
                           "errorType": "UnreadableChildren"})
            continue
        try:
            for child in get_children(False):
                if depth >= max_depth or len(report["objects"]) + len(pending) >= max_nodes:
                    report["truncated"] = True
                    report["truncationReasons"].append("max_depth" if depth >= max_depth else "max_nodes")
                    break
                pending.append((child, node_id, depth + 1))
        except Exception as error:
            issues.append({"location": node_id, "member": "get_children",
                           "errorType": type(error).__name__})

    _, dirty_after = _read(project, "dirty", issues, "project")
    report["projectDirtyAfter"] = bool(dirty_after) if dirty_after is not None else None
    # A dirty flag is not a revision or a proof that manual edits did not occur.
    report["truncationReasons"] = sorted(set(report["truncationReasons"]))
    if issues or report["truncated"]:
        report["status"] = "partial"
    return report


def main(bindings):
    available = dict(bindings)
    if available.get("projects") is None or available.get("system") is None:
        try:
            import scriptengine
            for name in ("projects", "system", "ImplementationLanguages"):
                if available.get(name) is None:
                    available[name] = getattr(scriptengine, name, None)
        except ImportError:
            pass
    report = inspect_session(available)
    print(PREFIX + json.dumps(report, ensure_ascii=True, sort_keys=True))
    return report


if __name__ == "__main__":
    main(globals())
