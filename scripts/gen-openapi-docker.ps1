#Requires -Version 7
<#
.SYNOPSIS
    Regenerates web/packages/api-client/openapi.json by building ControlHome.Api inside a Linux .NET SDK container,
    then regenerates the TypeScript client (npm run gen:api).

.DESCRIPTION
    For hosts where Windows Smart App Control blocks the build-time OpenAPI step. The working tree (tracked + untracked,
    minus ignored output) is streamed into the container with tar, so no Linux bin/obj land in the repository; the
    generated document comes back base64-encoded on stdout. The CI 'api-client-drift' job performs the same build.
#>
[CmdletBinding()]
param(
    [string] $SdkImage = 'mcr.microsoft.com/dotnet/sdk:10.0.302-noble',
    [switch] $SkipClient
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$target = Join-Path $repo 'web/packages/api-client/openapi.json'

Push-Location $repo
try {
    $files = git ls-files --cached --others --exclude-standard
    $tarList = New-TemporaryFile
    Set-Content -Path $tarList -Value ($files -join "`n") -NoNewline
    $inner = 'mkdir -p /src && tar -x -C /src && cd /src && ' +
        'dotnet build src/ControlHome.Api -c Release -warnaserror -v q 1>&2 && ' +
        'base64 -w0 web/packages/api-client/openapi.json'
    $b64 = tar -c -f - -T $tarList.FullName |
        docker run --rm -i -v controlhome-nuget:/root/.nuget/packages -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 `
            $SdkImage bash -c $inner
    $exit = $LASTEXITCODE
    Remove-Item $tarList -Force
    if ($exit -ne 0 -or -not $b64) { throw "Container build failed (exit $exit)." }
    [System.IO.File]::WriteAllBytes($target, [Convert]::FromBase64String(($b64 -join '').Trim()))
    Write-Host "openapi.json regenerated ($((Get-Item $target).Length) bytes)."
    if (-not $SkipClient) {
        npm run gen:api --prefix web
        if ($LASTEXITCODE -ne 0) { throw 'npm run gen:api failed.' }
    }
}
finally { Pop-Location }
