# -*- coding: utf-8 -*-
"""Original ScriptEngine v1 worker. Python 2.7 syntax; target IDE validation pending.

Each invocation owns a fresh project session, never saves and never accesses a PLC.
API sources and execution limitations are recorded in docs/12-execution.md.
"""
from __future__ import print_function
import calendar
import datetime
import hashlib
import json
import os
import time
import uuid
from collections import deque

try:
    text_type = unicode
except NameError:
    text_type = str

OPERATIONS = ("project.open", "project.tree.read", "pou.read", "project.compile")
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
    if request.get("protocolVersion") != 1 or request.get("executionMode") != "real":
        raise WorkerError("invalid_worker_request")
    if request.get("operation") not in OPERATIONS:
        raise WorkerError("invalid_operation")
    for key in ("requestId", "jobId", "workspaceId"):
        if uuid.UUID(request[key]).int == 0:
            raise WorkerError("invalid_worker_request")
    payload = request["payload"]
    if not os.path.isabs(payload["projectPath"]) or not 1 <= payload.get("maxObjects", 256) <= 2000:
        raise WorkerError("invalid_worker_request")
    if request["operation"] == "pou.read":
        uuid.UUID(payload["objectId"])
    if request["operation"] == "project.compile":
        uuid.UUID(payload["applicationId"])
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
        project = projects.open(path, primary=True, update_flags=flags.NoUpdates, allow_readonly=True)
        if bool(project.dirty):
            raise WorkerError("workspace_session_dirty")
        operation, payload = request["operation"], request["payload"]
        result = {"revision": before, "revisionScope": "project_file_sha256"}
        if operation == "project.open":
            result.update({"opened": True, "sessionClosedAfterOperation": True})
        elif operation == "project.tree.read":
            rows, unused, truncated = tree(project, payload.get("maxObjects", 256))
            result.update({"objects": rows, "truncated": truncated})
        elif operation == "pou.read":
            node = get_object(project, payload["objectId"])
            declaration, implementation = read_source(node)
            result.update({"objectId": payload["objectId"], "language": None,
                           "declaration": declaration, "implementation": implementation})
        else:
            app = get_object(project, payload["applicationId"])
            succeeded, messages = build(app, system)
            result.update({"applicationId": payload["applicationId"], "succeeded": succeeded,
                           "diagnostics": messages, "sessionDirtyAfterBuild": bool(project.dirty)})
            if not succeeded:
                response["errorCode"] = "compile_failed"
        if revision(path) != before:
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
                project.close()  # Owned session only. No save or online action is performed.
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
        for name in ("projects", "system", "VersionUpdateFlags"):
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
