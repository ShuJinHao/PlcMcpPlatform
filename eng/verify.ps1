$ErrorActionPreference = 'Stop'
python (Join-Path $PSScriptRoot 'verify.py') @args
if ($LASTEXITCODE -ne 0) { throw 'Repository verification failed' }
