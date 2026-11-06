$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    dotnet restore PlcMcpPlatform.sln --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    dotnet build PlcMcpPlatform.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    dotnet test PlcMcpPlatform.sln -c Release --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
    python eng/verify-mcp.py
    if ($LASTEXITCODE -ne 0) { throw 'Production MCP checks failed' }
    python eng/verify-execution.py
    if ($LASTEXITCODE -ne 0) { throw 'Fixture MCP checks failed' }
    python -m unittest discover -s tests/script_engine -v
    if ($LASTEXITCODE -ne 0) { throw 'Script tests failed' }
} finally { Pop-Location }
