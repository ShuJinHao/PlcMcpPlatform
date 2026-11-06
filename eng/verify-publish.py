"""Publish two modes, verify tamper detection, start production bundle and diagnose over HTTP."""
import argparse
import os
from pathlib import Path
import secrets
import subprocess
import sys
import tempfile
import time
import urllib.request
from bundle import verify_bundle
from publish import publish
from start import prepare

parser = argparse.ArgumentParser(); parser.add_argument('--dotnet', default='dotnet'); args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
try: urllib.request.urlopen('http://127.0.0.1:5088/health/live', timeout=1).close()
except OSError: pass
else: raise RuntimeError('Port 5088 is occupied; this check will not stop another host.')
with tempfile.TemporaryDirectory(prefix='plcmcp-publish-') as tmp, tempfile.TemporaryFile(mode='w+') as log:
    base = Path(tmp); bundle = base/'production'
    publish('production', bundle, args.dotnet)
    original = (bundle/'config/opencode.example.json').read_bytes()
    (bundle/'config/opencode.example.json').write_bytes(original+b' ')
    try: verify_bundle(bundle)
    except ValueError: pass
    else: raise AssertionError('Modified bundle was accepted.')
    (bundle/'config/opencode.example.json').write_bytes(original)
    key = secrets.token_hex(32)
    manifest, command, env = prepare(bundle, base/'data', base/'workspace', args.dotnet, dict(os.environ, PlcMcp__ApiKey=key))
    assert manifest['mode'] == 'production'
    process = subprocess.Popen(command, env=env, cwd=bundle/'bin', stdout=log, stderr=log)
    try:
        for _ in range(300):
            if process.poll() is not None: raise RuntimeError('Published host exited.')
            try: urllib.request.urlopen('http://127.0.0.1:5088/health/live', timeout=1).close(); break
            except OSError: time.sleep(0.1)
        else: raise TimeoutError('Published host startup timed out.')
        subprocess.run([sys.executable, str(bundle/'eng/doctor.py'), '--mode', 'production'], env=env, check=True)
    except Exception:
        log.seek(0); print(log.read()[-6000:]); raise
    finally:
        if process.poll() is None:
            process.terminate()
            try: process.wait(timeout=10)
            except subprocess.TimeoutExpired: process.kill(); process.wait(timeout=5)
    fixture = base/'fixture'; publish('fixture', fixture, args.dotnet)
    assert (fixture/'bin/PlcMcp.TestHost.dll').is_file()
    assert (fixture/'bin/Scripts/engineering_worker.py').is_file()
    subprocess.run([sys.executable, str(fixture/'eng/init-fixture.py'), '--workspace-root', str(base/'fixture-workspace')], check=True)
    _, command, env = prepare(fixture, base/'fixture-data', base/'fixture-workspace', args.dotnet, dict(os.environ, PlcMcp__ApiKey=key))
    process = subprocess.Popen(command, env=env, cwd=fixture/'bin', stdout=log, stderr=log)
    try:
        for _ in range(300):
            if process.poll() is not None: raise RuntimeError('Published fixture exited.')
            try: urllib.request.urlopen('http://127.0.0.1:5088/health/live', timeout=1).close(); break
            except OSError: time.sleep(0.1)
        else: raise TimeoutError('Published fixture startup timed out.')
        subprocess.run([sys.executable, str(fixture/'eng/doctor.py'), '--mode', 'fixture'], env=env, check=True)
        subprocess.run([sys.executable, str(fixture/'eng/run-regressions.py'), '--suite', 'p5', '--expect-mode', 'fixture',
                        '--variables', str(fixture/'config/regression/fixture-variables.json'), '--report', str(base/'p5.json')], env=env, check=True)
    except Exception:
        log.seek(0); print(log.read()[-6000:]); raise
    finally:
        if process.poll() is None:
            process.terminate()
            try: process.wait(timeout=10)
            except subprocess.TimeoutExpired: process.kill(); process.wait(timeout=5)
    if sys.platform == 'win32':
        validation = base/'validation'; publish('validation', validation, args.dotnet)
        assert (validation/'bin/Scripts/exchange_worker.py').is_file()
    print('Publish regression: immutable inventory, tamper rejection, production startup/doctor and fixture packaging passed; no IDE was started.')
