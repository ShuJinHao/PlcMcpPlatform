$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
    dotnet restore PlcMcpPlatform.sln --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    dotnet build PlcMcpPlatform.sln -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    dotnet test PlcMcpPlatform.sln -c Release --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
} finally { Pop-Location }
