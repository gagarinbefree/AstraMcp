<#
.SYNOPSIS
Builds, tests and publishes the self-contained Windows x86 MCP server.
.PARAMETER SkipInspector
Skips installing and invoking the pinned Node-based MCP Inspector.
#>
[CmdletBinding()]
param([switch] $SkipInspector)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $env:DOTNET_CLI_HOME = Join-Path $root '.cache/dotnet'
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    $env:npm_config_cache = Join-Path $root '.cache/npm'
    dotnet restore AstraMcp.sln --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed.' }
    dotnet test tests/Tests/Tests.csproj --no-restore --logger 'trx;LogFileName=tests.trx' --results-directory .cache/verification
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    dotnet publish src/Mcp/Mcp.csproj -c Release -r win-x86 --self-contained true -o publish/win-x86 -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw 'Windows x86 publication failed.' }
    if (-not $SkipInspector) {
        npm ci --offline=false --prefer-online --ignore-scripts --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'Inspector dependency restore failed.' }
        node scripts/inspect.mjs
        if ($LASTEXITCODE -ne 0) { throw 'MCP Inspector verification failed.' }
    }
}
finally { Pop-Location }
