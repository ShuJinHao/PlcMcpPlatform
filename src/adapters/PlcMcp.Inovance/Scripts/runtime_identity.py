# -*- coding: utf-8 -*-
"""Original runtime identity check. CLR observations only; no project/API mutation."""
import hashlib
import os


def sha256(path):
    digest = hashlib.sha256()
    with open(path, "rb") as source:
        while True:
            block = source.read(1024 * 1024)
            if not block:
                break
            digest.update(block)
    return digest.hexdigest()


def verify(expected, executable, assemblies):
    """assemblies contains observed (name, location, version) tuples, never declared versions."""
    matches = [item for item in assemblies if os.path.basename(item[1]).lower() == expected["engineName"].lower()]
    if len(matches) != 1:
        raise ValueError("runtime_engine_ambiguous_or_missing")
    result = {"executableSha256": sha256(executable), "engineSha256": sha256(matches[0][1]),
              "engineAssemblyVersion": matches[0][2], "status": "matched"}
    if result["executableSha256"] != expected["executableSha256"] or result["engineSha256"] != expected["engineSha256"]:
        raise ValueError("runtime_identity_mismatch")
    return result


def observe(expected):
    from System import AppDomain
    from System.Diagnostics import Process
    assemblies = []
    for assembly in AppDomain.CurrentDomain.GetAssemblies():
        if not assembly.IsDynamic:
            name = assembly.GetName()
            assemblies.append((str(name.Name), str(assembly.Location), str(name.Version)))
    process = Process.GetCurrentProcess()
    try:
        return verify(expected, str(process.MainModule.FileName), assemblies)
    finally:
        process.Dispose()
