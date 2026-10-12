#Requires -Version 7
<#
.SYNOPSIS
    Runs `dotnet test` inside a Linux .NET SDK container (fallback when Windows Smart App Control blocks
    freshly built DLLs on the host, and a way to reproduce CI locally).

.DESCRIPTION
    The repository (without bin/obj/node_modules) is streamed into the container with tar, so host build
    output never mixes with the Linux build. NuGet packages are cached in the `controlhome-nuget` volume.

    SQL Server for integration tests:
      * default: Testcontainers inside the container talks to Docker Desktop through the mounted socket;
      * -UseDevServer: uses the dev SQL Server from user-secrets (ConnectionStrings:ControlHome) as
        CONTROLHOME_TEST_SQLSERVER (database name removed; tests create ControlHome_Test_* databases).
        The value is passed via an env file that is deleted afterwards and is never printed.

.EXAMPLE
    ./scripts/test-docker.ps1
    ./scripts/test-docker.ps1 -Filter "FullyQualifiedName~Setup" -UseDevServer
#>
[CmdletBinding()]
param(
    [string] $Filter,
    [switch] $UseDevServer,
    [string] $SdkImage = 'mcr.microsoft.com/dotnet/sdk:10.0.302-noble'
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

$envFile = $null
$dockerArgs = @('run', '--rm', '-i',
    '-v', 'controlhome-nuget:/root/.nuget/packages',
    '-v', '/var/run/docker.sock:/var/run/docker.sock',
    '-e', 'TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal',
    '-e', 'DOTNET_CLI_TELEMETRY_OPTOUT=1',
    '-e', 'DOTNET_NOLOGO=1')

try {
    if ($UseDevServer) {
        $raw = dotnet user-secrets list --project (Join-Path $repo 'src/ControlHome.Api') |
            Where-Object { $_ -like 'ConnectionStrings:ControlHome = *' } |
            Select-Object -First 1
        if (-not $raw) { throw 'ConnectionStrings:ControlHome is not set in user-secrets (run scripts/setup-dev.ps1).' }
        $cs = $raw.Substring('ConnectionStrings:ControlHome = '.Length)
        $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
        $builder.ConnectionString = $cs
        foreach ($k in @('Database', 'Initial Catalog')) { if ($builder.ContainsKey($k)) { [void]$builder.Remove($k) } }
        # Containers cannot resolve mDNS (*.local) names: resolve on the host.
        $server = [string]$builder['Server']
        $hostPart, $portPart = $server -split ',', 2
        if ($hostPart -like '*.local') {
            $ip = [System.Net.Dns]::GetHostAddresses($hostPart) |
                Where-Object { $_.AddressFamily -eq 'InterNetwork' } | Select-Object -First 1
            if ($ip) { $builder['Server'] = if ($portPart) { "$($ip.IPAddressToString),$portPart" } else { $ip.IPAddressToString } }
        }
        $envFile = New-TemporaryFile
        Set-Content -Path $envFile -Value "CONTROLHOME_TEST_SQLSERVER=$($builder.ConnectionString)" -NoNewline
        $dockerArgs += @('--env-file', $envFile.FullName)
    }

    $testCmd = 'dotnet test -c Release'
    if ($Filter) { $testCmd += " --filter `"$Filter`"" }
    $dockerArgs += @('-w', '/src', $SdkImage, 'bash', '-c', "mkdir -p /src && tar -x -C /src && cd /src && $testCmd")

    Push-Location $repo
    try {
        # Stream the working tree (tracked + untracked, minus ignored build output) into the container.
        $files = git ls-files --cached --others --exclude-standard
        $tarList = New-TemporaryFile
        Set-Content -Path $tarList -Value ($files -join "`n")
        tar -c -f - -T $tarList.FullName | docker @dockerArgs
        $exit = $LASTEXITCODE
        Remove-Item $tarList -Force
    }
    finally { Pop-Location }
    exit $exit
}
finally {
    if ($envFile) { Remove-Item $envFile -Force -ErrorAction SilentlyContinue }
}
