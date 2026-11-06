# -*- coding: utf-8 -*-
"""Original ScriptEngine v1 worker. Python 2.7 syntax; target IDE validation pending.

Each invocation owns a fresh project session. Writes require a managed candidate; no online PLC access.
API sources and execution limitations are recorded in docs/12-execution.md.
"""
from __future__ import print_function
import calendar
import datetime
import hashlib
import json
import os
import re
import time
import uuid
from collections import deque

try:
    text_type = unicode
except NameError:
    text_type = str

EDIT_OPERATIONS = ("project.verify", "pou.create", "pou.update", "gvl.create", "gvl.update", "dut.create", "dut.update")
OPERATIONS = ("project.open", "project.tree.read", "pou.read", "gvl.read", "dut.read", "project.compile") + EDIT_OPERATIONS
MAX_REQUEST = 131072
MAX_RESULT = 1048576


class WorkerError(Exception):
    pass


def revision(path):
    digest = hashlib.sha256()
    with open(path, "rb") as source:
        while True:
            chunk = source.read(65536)
            if not chunk:
                break
            digest.update(chunk)
    return digest.hexdigest()


def validate(request):
    if type(request.get("protocolVersion")) is not int or request.get("protocolVersion") != 1 or request.get("executionMode") != "real":
        raise WorkerError("invalid_worker_request")
    if request.get("operation") not in OPERATIONS:
        raise WorkerError("invalid_operation")
    for key in ("requestId", "jobId", "workspaceId"):
        if uuid.UUID(request[key]).int == 0:
            raise WorkerError("invalid_worker_request")
    payload = request["payload"]
    if not os.path.isabs(payload["projectPath"]) or type(payload.get("maxObjects", 256)) is not int or not 1 <= payload.get("maxObjects", 256) <= 2000:
        raise WorkerError("invalid_worker_request")
    if request["operation"] in ("pou.read", "gvl.read", "dut.read"):
        uuid.UUID(payload["objectId"])
    if request["operation"] == "project.compile":
        uuid.UUID(payload["applicationId"])
    if request["operation"] in EDIT_OPERATIONS:
        if payload.get("candidate") is not True or not isinstance(payload.get("edit"), dict):
            raise WorkerError("managed_candidate_required")
        edit = payload["edit"]
        for key in ("declaration", "implementation"):
            value = edit.get(key)
            if value is not None and (not isinstance(value, text_type) or len(value) > 65536 or "\0" in value):
                raise WorkerError("invalid_source")
        if request["operation"].endswith(".create"):
            uuid.UUID(edit["parentId"])
            if not re.match(r"^[A-Za-z_][A-Za-z0-9_]{0,127}$", edit["name"]):
                raise WorkerError("invalid_object_identity")
        if request["operation"].endswith(".update"):
            uuid.UUID(edit["objectId"])
    deadline = request["deadlineUtc"]
    if not (deadline.endswith("Z") or deadline.endswith("+00:00")):
        raise WorkerError("invalid_worker_request")
    instant = calendar.timegm(datetime.datetime.strptime(deadline[:19], "%Y-%m-%dT%H:%M:%S").timetuple())
    if time.time() >= instant:
        raise WorkerError("deadline_expired")


def tree(project, limit):
    queue = deque([(project, None, 0)])
    seen, rows, objects = set(), [], {}
    truncated = False
    while queue:
        node, parent, depth = queue.popleft()
        identity = "project" if node is project else str(uuid.UUID(text_type(node.guid)))
        if identity in seen:
            truncated = True
            continue
        seen.add(identity)
        objects[identity] = node
        rows.append({"id": identity, "parentId": parent,
                     "name": "Project" if node is project else text_type(node.get_name()),
                     "kind": "application" if getattr(node, "is_application", False) else "object"})
        for child in node.get_children(False):
            if depth >= 64 or len(rows) + len(queue) >= limit:
                truncated = True
                break
            queue.append((child, identity, depth + 1))
    return rows, objects, truncated


def get_object(project, object_id):
    unused, objects, truncated = tree(project, 2000)
    canonical = str(uuid.UUID(object_id))
    if canonical in objects:
        return objects[canonical]
    raise WorkerError("object_search_incomplete" if truncated else "object_not_found")


def read_source(node):
    if not getattr(node, "has_textual_declaration", False) or not getattr(node, "has_textual_implementation", False):
        raise WorkerError("textual_source_unavailable")
    declaration = node.textual_declaration
    implementation = node.textual_implementation
    # The CHM baseline documents get_text(offset, length), not a parameterless overload.
    if declaration.length + implementation.length > 400000:
        raise WorkerError("result_too_large")
    return text_type(declaration.get_text(0, declaration.length)), text_type(implementation.get_text(0, implementation.length))


def build(application, system):
    if not getattr(application, "is_application", False):
        raise WorkerError("application_required")
    # Only the invocation's owned IDE session is used. Clear old diagnostics before build.
    for category in system.get_message_categories():
        system.clear_messages(category)
    application.build()
    messages, failed = [], False
    for category in system.get_message_categories():
        for message in system.get_message_objects(category):
            if len(messages) >= 500:
                raise WorkerError("diagnostics_too_large")
            severity = int(message.severity)
            if severity <= 0 or severity & ~31:
                raise WorkerError("diagnostic_severity_unknown")
            failed = failed or bool(severity & 3)
            messages.append({"severity": "error" if severity & 3 else "warning" if severity & 4 else "information",
                             "code": text_type(message.prefix) + text_type(message.number),
                             "message": text_type(message.text),
                             "objectId": text_type(message.object) if message.object is not None else None,
                             "positionText": text_type(message.position_text),
                             "positionOffset": message.position_offset,
                             "line": None, "column": None})
    return not failed, messages


def normalize_source(value):
    return value.replace("\r\n", "\n").replace("\r", "\n")


def declaration_header(source):
    value = source.lstrip()
    for unused in range(64):
        if value.startswith("(*"):
            depth, cursor = 1, 2
            while depth and cursor < len(value):
                if value[cursor:cursor + 2] == "(*": depth, cursor = depth + 1, cursor + 2
                elif value[cursor:cursor + 2] == "*)": depth, cursor = depth - 1, cursor + 2
                else: cursor += 1
            if depth: raise WorkerError("object_kind_unverified")
            value = value[cursor:].lstrip()
        elif value.startswith("{") and "}" in value:
            value = value.split("}", 1)[1].lstrip()
        elif value.startswith("//") and "\n" in value:
            value = value.split("\n", 1)[1].lstrip()
        else: return value
    raise WorkerError("object_kind_unverified")


def object_text(node):
    if not getattr(node, "has_textual_declaration", False): raise WorkerError("textual_source_unavailable")
    declaration = node.textual_declaration
    implementation = node.textual_implementation if getattr(node, "has_textual_implementation", False) else None
    if declaration.length + (implementation.length if implementation else 0) > 400000: raise WorkerError("result_too_large")
    return {"objectId": str(uuid.UUID(text_type(node.guid))), "name": text_type(node.get_name()),
            "declaration": text_type(declaration.get_text(0, declaration.length)),
            "implementation": text_type(implementation.get_text(0, implementation.length)) if implementation else None}


def check_kind(source, operation, name):
    header = declaration_header(source)
    kind = operation.split(".")[0]
    pattern = r"^VAR_GLOBAL\b" if kind == "gvl" else r"^TYPE\s+" + re.escape(name) + r"\b" if kind == "dut" else r"^(PROGRAM|FUNCTION_BLOCK|FUNCTION)\s+" + re.escape(name) + r"\b"
    if not re.match(pattern, header, re.IGNORECASE): raise WorkerError("object_kind_mismatch")


def verify_requirements(project, requirements):
    if not isinstance(requirements, dict) or not isinstance(requirements.get("controller"), dict):
        raise WorkerError("dependency_requirements_missing")
    unused, objects, truncated = tree(project, 2000)
    if truncated: raise WorkerError("dependency_search_incomplete")
    expected = requirements["controller"]
    matched, libraries = False, set()
    for node in objects.values():
        if getattr(node, "is_device", False):
            identity = node.get_device_identification()
            if int(identity.type) == expected["type"] and text_type(identity.id) == expected["id"] and text_type(identity.version) == expected["version"]:
                matched = True
        if getattr(node, "is_libman", False):
            libraries.update(text_type(name) for name in node.get_libraries(False))
    if not matched: raise WorkerError("controller_requirement_mismatch")
    if not set(requirements["requiredLibraries"]).issubset(libraries): raise WorkerError("library_reference_missing")
    return {"controllerMatched": True, "libraryReferencesMatched": True, "installedDependenciesVerified": False}


def apply_edit(project, operation, edit, bindings):
    if operation == "project.verify": return None
    if operation.endswith(".create"):
        parent = get_object(project, edit["parentId"])
        if any(text_type(child.get_name()).lower() == edit["name"].lower() for child in parent.get_children(False)):
            raise WorkerError("object_name_conflict")
        if operation == "pou.create":
            types, languages = bindings.get("PouType"), bindings.get("ImplementationLanguages")
            if types is None or languages is None: raise WorkerError("scriptengine_api_unavailable")
            language = {"ST": "st", "IL": "instruction_list", "LD": "ladder", "FBD": "fbd", "SFC": "sfc", "CFC": "cfc"}.get(edit.get("language"))
            if language is None or edit.get("pouType") not in ("Program", "FunctionBlock", "Function"): raise WorkerError("invalid_pou_definition")
            if edit["language"] == "SFC" and edit["pouType"] == "Function": raise WorkerError("language_not_applicable")
            if edit["language"] not in ("ST", "IL") and edit.get("implementation") is not None: raise WorkerError("graphical_content_requires_specialized_editor")
            node = parent.create_pou(name=edit["name"], type=getattr(types, edit["pouType"]), language=getattr(languages, language),
                                     return_type=edit.get("baseType") if edit["pouType"] == "Function" else None)
        elif operation == "gvl.create": node = parent.create_gvl(edit["name"])
        else:
            types = bindings.get("DutType")
            if types is None: raise WorkerError("scriptengine_api_unavailable")
            if edit.get("dutType") not in ("Structure", "Enumeration", "Alias", "Union"): raise WorkerError("invalid_dut_definition")
            node = parent.create_dut(name=edit["name"], type=getattr(types, edit["dutType"]), baseType=edit.get("baseType"))
    else: node = get_object(project, edit["objectId"])
    before = object_text(node)
    check_kind(before["declaration"], operation, before["name"])
    if edit.get("declaration") is not None:
        check_kind(edit["declaration"], operation, before["name"])
        node.textual_declaration.replace(new_text=edit["declaration"])
    if edit.get("implementation") is not None:
        if operation.startswith(("gvl.", "dut.")) or before["implementation"] is None: raise WorkerError("textual_implementation_unavailable")
        node.textual_implementation.replace(new_text=edit["implementation"])
    after = object_text(node)
    for key in ("declaration", "implementation"):
        if edit.get(key) is not None and normalize_source(after[key]) != normalize_source(edit[key]): raise WorkerError("readback_mismatch")
    return after


def execute(request, bindings):
    response = {"protocolVersion": 1, "requestId": request.get("requestId"), "jobId": request.get("jobId"),
                "executionMode": "real", "outcome": "failed", "errorCode": None, "payload": None}
    project = None
    try:
        validate(request)
        projects, system = bindings.get("projects"), bindings.get("system")
        flags = bindings.get("VersionUpdateFlags")
        if projects is None or system is None or flags is None:
            raise WorkerError("scriptengine_api_unavailable")
        if projects.primary is not None:
            raise WorkerError("ide_session_busy")
        path = request["payload"]["projectPath"]
        before = revision(path)
        if before != request["expectedRevision"]:
            raise WorkerError("workspace_revision_conflict")
        editing = request["operation"] in EDIT_OPERATIONS
        project = projects.open(path, primary=True, update_flags=flags.NoUpdates, allow_readonly=not editing)
        if bool(project.dirty):
            raise WorkerError("workspace_session_dirty")
        operation, payload = request["operation"], request["payload"]
        result = {"revision": before, "revisionScope": "project_file_sha256"}
        if editing:
            dependency_check = verify_requirements(project, payload.get("requirements"))
            before_save = apply_edit(project, operation, payload["edit"], bindings)
            if revision(path) != before: raise WorkerError("workspace_revision_conflict")
            project.save()
            saved_revision = revision(path)
            project.close()
            project = None
            project = projects.open(path, primary=True, update_flags=flags.NoUpdates, allow_readonly=True)
            if bool(project.dirty): raise WorkerError("workspace_session_dirty")
            if verify_requirements(project, payload.get("requirements")) != dependency_check: raise WorkerError("dependency_readback_mismatch")
            result["dependencies"] = dependency_check
            if before_save is not None:
                after_reopen = object_text(get_object(project, before_save["objectId"]))
                if after_reopen != before_save: raise WorkerError("reopen_readback_mismatch")
                result.update(after_reopen)
            if revision(path) != saved_revision: raise WorkerError("workspace_revision_conflict")
            result.update({"baseRevision": before, "revision": saved_revision, "saved": True, "reopened": True,
                           "readBackVerified": True, "sessionClosedAfterOperation": True})
        elif operation == "project.open":
            result.update({"opened": True, "sessionClosedAfterOperation": True})
        elif operation == "project.tree.read":
            rows, unused, truncated = tree(project, payload.get("maxObjects", 256))
            result.update({"objects": rows, "truncated": truncated})
        elif operation == "pou.read":
            node = get_object(project, payload["objectId"])
            declaration, implementation = read_source(node)
            result.update({"objectId": payload["objectId"], "language": None,
                           "declaration": declaration, "implementation": implementation})
        elif operation in ("gvl.read", "dut.read"):
            node = get_object(project, payload["objectId"])
            source = object_text(node)
            check_kind(source["declaration"], operation, source["name"])
            result.update({"objectId": payload["objectId"], "kind": operation.split(".")[0], "declaration": source["declaration"]})
        else:
            app = get_object(project, payload["applicationId"])
            succeeded, messages = build(app, system)
            result.update({"applicationId": payload["applicationId"], "succeeded": succeeded,
                           "diagnostics": messages, "sessionDirtyAfterBuild": bool(project.dirty)})
            if not succeeded:
                response["errorCode"] = "compile_failed"
        if not editing and revision(path) != before:
            raise WorkerError("workspace_revision_conflict")
        response["payload"] = result
        response["outcome"] = "failed" if response["errorCode"] else "succeeded"
    except WorkerError as error:
        response["errorCode"] = str(error)
    except (KeyError, TypeError, ValueError):
        response["errorCode"] = "invalid_request_or_vendor_result"
    except Exception:
        response["errorCode"] = "script_execution_failed"
    finally:
        if project is not None:
            try:
                project.close()  # Only owned sessions are closed; no online PLC action is performed.
            except Exception:
                response.update({"outcome": "unknown", "errorCode": "project_close_unconfirmed", "payload": None})
    return response


def main(bindings):
    request_path = os.environ["PLCMCP_REQUEST_PATH"]
    result_path = os.environ["PLCMCP_RESULT_PATH"]
    if not os.path.isabs(request_path) or not os.path.isabs(result_path) or os.path.dirname(request_path) != os.path.dirname(result_path):
        raise ValueError("Invalid worker paths")
    with open(request_path, "rb") as source:
        raw = source.read(MAX_REQUEST + 1)
    if len(raw) > MAX_REQUEST:
        raise ValueError("Request too large")
    request = json.loads(raw.decode("utf-8"))
    available = dict(bindings)
    try:
        import scriptengine
        for name in ("projects", "system", "VersionUpdateFlags", "PouType", "DutType", "ImplementationLanguages"):
            if available.get(name) is None:
                available[name] = getattr(scriptengine, name, None)
    except ImportError:
        pass
    response = execute(request, available)
    raw = json.dumps(response, ensure_ascii=True, separators=(",", ":")).encode("utf-8")
    if len(raw) > MAX_RESULT:
        response.update({"outcome": "failed", "errorCode": "result_too_large", "payload": None})
        raw = json.dumps(response, ensure_ascii=True).encode("utf-8")
    temporary = result_path + ".tmp"
    with open(temporary, "wb") as target:
        target.write(raw)
        target.flush()
        os.fsync(target.fileno())
    os.rename(temporary, result_path)


if __name__ == "__main__":
    main(globals())
