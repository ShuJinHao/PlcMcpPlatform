"""Start the Release Host, exercise MCP, then stop only this test's process."""
from pathlib import Path
import argparse
import os
import secrets
import subprocess
import sys
import tempfile
import time
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument("--dotnet", default="dotnet")
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
host = root / "src/hosts/PlcMcp.McpHost/bin/Release/net8.0/PlcMcp.McpHost.dll"
if not host.exists():
    sys.exit("Build Release before running MCP verification.")
try:
    urllib.request.urlopen("http://127.0.0.1:5088/health/live", timeout=1).close()
except OSError:
    pass
else:
    sys.exit("Port 5088 is occupied; stop the existing Host before running this test.")
env = dict(os.environ, PlcMcp__ApiKey=secrets.token_hex(32))
with tempfile.TemporaryFile(mode="w+") as log:
    process = subprocess.Popen([args.dotnet, str(host)], cwd=host.parent, env=env, stdout=log, stderr=log)
    try:
        for attempt in range(100):
            if process.poll() is not None:
                raise RuntimeError("Host exited during startup")
            try:
                urllib.request.urlopen("http://127.0.0.1:5088/health/live", timeout=1).close()
                break
            except OSError:
                time.sleep(0.1)
        else:
            raise RuntimeError("Host readiness timeout")
        subprocess.run([sys.executable, str(root / "eng/mcp-smoke.py")], env=env, check=True)
    except Exception:
        log.seek(0)
        print(log.read(), file=sys.stderr)
        raise
    finally:
        if process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=5)
