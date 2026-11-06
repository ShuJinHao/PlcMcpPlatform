"""Small standard-library HTTP client for this repository's MCP regression checks."""
import json
import time
import urllib.parse
import urllib.request


class McpClient:
    def __init__(self, url, key):
        parsed = urllib.parse.urlsplit(url)
        if parsed.scheme != 'http' or parsed.hostname not in ('127.0.0.1', 'localhost', '::1') or parsed.username or parsed.password:
            raise ValueError('Regression endpoint must be an explicit loopback HTTP URL.')
        self.url, self.key, self.sequence, self.protocol = url, key, 0, '2025-11-25'
        initialized = self.rpc('initialize', dict(protocolVersion=self.protocol, capabilities={}, clientInfo=dict(name='plcmcp-regression', version='1.2')))
        self.protocol = initialized['protocolVersion']
        self.rpc('notifications/initialized', notification=True)

    def rpc(self, method, params=None, notification=False):
        self.sequence += 1
        request = dict(jsonrpc='2.0', method=method)
        if not notification: request['id'] = self.sequence
        if params is not None: request['params'] = params
        headers = {'Authorization': 'Bearer ' + self.key, 'Content-Type': 'application/json',
                   'Accept': 'application/json, text/event-stream', 'MCP-Protocol-Version': self.protocol}
        with urllib.request.urlopen(urllib.request.Request(self.url, json.dumps(request, ensure_ascii=True).encode(), headers), timeout=30) as response:
            raw = response.read(4 * 1024 * 1024 + 1)
            if len(raw) > 4 * 1024 * 1024: raise ValueError('MCP response exceeds regression limit.')
            if notification: return None
            text = raw.decode('utf-8')
            if 'text/event-stream' in response.headers.get('Content-Type', ''):
                messages = [json.loads(line[5:].strip()) for line in text.splitlines() if line.startswith('data:')]
                envelope = next(x for x in messages if x.get('id') == self.sequence)
            else: envelope = json.loads(text)
        if envelope.get('id') != self.sequence or 'error' in envelope: raise ValueError('MCP protocol error: ' + json.dumps(envelope))
        return envelope['result']

    def call(self, name, arguments, allow_error=False):
        result = self.rpc('tools/call', dict(name=name, arguments=arguments))
        value = result.get('structuredContent')
        if value is None: value = json.loads(result['content'][0]['text'])
        if result.get('isError') and not allow_error: raise ValueError('Tool rejected request: ' + json.dumps(value, ensure_ascii=False))
        return value

    def wait(self, job_id, timeout):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            job = self.call('plc_job_get', dict(jobId=job_id))['job']
            if job['status'] not in ('Queued', 'Running', 'CancelRequested'): return job
            time.sleep(0.1)
        raise TimeoutError('Job polling timed out; inspect the job before retrying. No cancellation or replay was issued.')
