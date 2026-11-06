"""Run against an already started P0 Host; uses Python standard library only."""
from pathlib import Path
import json
import os
import urllib.error
import urllib.request

BASE = "http://127.0.0.1:5088"
key = os.environ["PlcMcp__ApiKey"]
session = None
protocol = "2025-11-25"


def request(payload, auth=True, origin=None):
    headers = {"Content-Type": "application/json", "Accept": "application/json, text/event-stream"}
    if auth:
        headers["Authorization"] = "Bearer " + key
    if session:
        headers["Mcp-Session-Id"] = session
    headers["MCP-Protocol-Version"] = protocol
    if origin:
        headers["Origin"] = origin
    req = urllib.request.Request(BASE + "/mcp", json.dumps(payload).encode(), headers)
    with urllib.request.urlopen(req, timeout=15) as response:
        raw = response.read().decode()
        if not raw.strip():
            return None, response.headers
        if "text/event-stream" in response.headers.get("Content-Type", ""):
            items = [json.loads(line[5:].strip()) for line in raw.splitlines() if line.startswith("data:")]
            return items[-1], response.headers
        return json.loads(raw), response.headers


def tool_value(response):
    result = response["result"]
    assert not result.get("isError", False), result
    if result.get("structuredContent") is not None:
        return result["structuredContent"]
    texts = [c["text"] for c in result["content"] if c["type"] == "text"]
    return json.loads(texts[0])


initialize = {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
    "protocolVersion": protocol, "capabilities": {}, "clientInfo": {"name": "plcmcp-smoke", "version": "0.1"}}}
for auth, origin, expected in [(False, None, 401), (True, "https://example.invalid", 403)]:
    try:
        request(initialize, auth=auth, origin=origin)
        raise AssertionError("Expected rejection")
    except urllib.error.HTTPError as error:
        assert error.code == expected, error.code
response, headers = request(initialize)
assert response["result"]["serverInfo"]
session = headers.get("Mcp-Session-Id")
protocol = response["result"]["protocolVersion"]
request({"jsonrpc": "2.0", "method": "notifications/initialized"})
response, _ = request({"jsonrpc": "2.0", "id": 2, "method": "tools/list"})
names = {tool["name"] for tool in response["result"]["tools"]}
assert names == set(json.loads((Path(__file__).resolve().parents[1]/"contracts/tool-access.v1.json").read_text())["localTools"]), names
response, _ = request({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {
    "name": "plc_runtime_get_status", "arguments": {}}})
status = tool_value(response)
assert status["engineeringReady"] is False and status["ideConnected"] is False, status
response, _ = request({"jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": {
    "name": "plc_capabilities_list", "arguments": {}}})
catalog = tool_value(response)
assert catalog["capabilities"]
assert all(cap["status"] == "Unverified" for cap in catalog["capabilities"]), catalog
response, _ = request({"jsonrpc": "2.0", "id": 5, "method": "tools/call", "params": {
    "name": "plc_project_compile", "arguments": {}}})
assert "error" in response or response.get("result", {}).get("isError") is True, response
# Local graphical and XML processing is usable in production without an installed IDE.
program = json.loads((Path(__file__).resolve().parents[1]/"tests/fixtures/graphs/ld-start-stop.json").read_text())
response, _ = request({"jsonrpc":"2.0", "id":6, "method":"tools/call", "params":{"name":"plc_graph_generate", "arguments":{"program":program}}})
generated = tool_value(response)
assert generated["evidenceKind"] == "local" and generated["engineeringReady"] is False, generated
response, _ = request({"jsonrpc":"2.0", "id":7, "method":"tools/call", "params":{"name":"plc_graph_decode", "arguments":{"artifactId":generated["value"]["artifact"]["artifactId"]}}})
assert tool_value(response)["value"]["semanticSha256"] == generated["value"]["graph"]["semanticSha256"]
with urllib.request.urlopen(BASE + "/health/live", timeout=5) as health:
    assert json.load(health)["engineeringReady"] is False
print("PASS: auth, Origin rejection, initialize, discovery, tool results, unavailable engineering tool, liveness")
