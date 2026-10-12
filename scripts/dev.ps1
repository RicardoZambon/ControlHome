#Requires -Version 7.2
<#
.SYNOPSIS
  Runs the ControlHome API and the web dev server together. Ctrl+C stops both.

.DESCRIPTION
  - API:  dotnet watch run --project src/ControlHome.Api --launch-profile http   -> http://localhost:5080
  - Web:  npm run dev --prefix web (Vite)                                         -> http://localhost:5173
  Open http://localhost:5173: Vite proxies /api, /hubs and /health to the API (same origin, so the
  Origin / X-ControlHome request checks behave exactly as in production).

  Both run as background processes sharing this console. Ctrl+C - or either process exiting - stops the other
  one too, including child processes (dotnet watch -> app, npm -> node/vite), so nothing keeps the ports busy.
  A part of the repository that does not exist yet is skipped with a warning.

.PARAMETER ApiOnly
  Start only the API.

.PARAMETER WebOnly
  Start only the web dev server.

.EXAMPLE
  ./scripts/dev.ps1
#>
[CmdletBinding()]
param(
    [switch]$ApiOnly,
    [switch]$WebOnly
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

if ($ApiOnly -and $WebOnly) { throw 'Use either -ApiOnly or -WebOnly, not both.' }

$root = Get-RepoRoot
$apiPort = 5080
$webPort = 5173

function Test-PortFree([int]$Port) {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
    try { $listener.Start(); return $true }
    catch { return $false }
    finally { $listener.Stop() }
}

function Start-DevProcess([string]$Name, [string]$FilePath, [string[]]$Arguments) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($FilePath)
    foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add($argument) }
    $startInfo.WorkingDirectory = $root
    $startInfo.UseShellExecute = $false
    $process = [System.Diagnostics.Process]::Start($startInfo)
    Write-Note "$Name started (pid $($process.Id))."
    [pscustomobject]@{ Name = $Name; Process = $process }
}

# Resolves how to run npm without a .cmd/.ps1 shim, so the whole process tree can be stopped cleanly.
function Get-NpmCommand {
    $node = Get-Command node -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($node) {
        $npmCli = Join-Path (Split-Path -Parent $node.Source) 'node_modules/npm/bin/npm-cli.js'
        if (Test-Path -LiteralPath $npmCli) { return @($node.Source, $npmCli) }
    }
    if ($IsWindows) { return @($env:ComSpec, '/d', '/s', '/c', 'npm') }
    $npm = Get-Command npm -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($npm) { return @($npm.Source) }
    return $null
}

$startApi = -not $WebOnly
$startWeb = -not $ApiOnly

if ($startApi) {
    if (-not (Test-Path -LiteralPath (Join-Path $root 'src/ControlHome.Api/ControlHome.Api.csproj'))) {
        Write-Warning 'src/ControlHome.Api does not exist yet - skipping the API.'
        $startApi = $false
    }
    elseif (-not (Test-CommandAvailable 'dotnet')) {
        Write-Warning 'dotnet was not found on PATH - skipping the API.'
        $startApi = $false
    }
    elseif (-not (Test-PortFree $apiPort)) {
        throw "Port $apiPort is already in use (another API instance?). Stop it first."
    }
}

$npmCommand = $null
if ($startWeb) {
    if (-not (Test-Path -LiteralPath (Join-Path $root 'web/package.json'))) {
        Write-Warning 'web/ does not exist yet - skipping the web dev server.'
        $startWeb = $false
    }
    elseif (-not (Use-Node24)) {
        throw 'Node 24 is required for the web dev server.'
    }
    elseif (-not (Test-Path -LiteralPath (Join-Path $root 'web/node_modules'))) {
        throw 'web/node_modules is missing - run ./scripts/setup-dev.ps1 (or npm ci --prefix web) first.'
    }
    elseif (-not (Test-PortFree $webPort)) {
        throw "Port $webPort is already in use (another Vite instance?). Stop it first."
    }
    else {
        $npmCommand = Get-NpmCommand
        if (-not $npmCommand) { throw 'npm was not found on PATH.' }
    }
}

if (-not $startApi -and -not $startWeb) {
    Write-Warning 'Nothing to start.'
    exit 1
}

$running = [System.Collections.Generic.List[object]]::new()
$exitCode = 0
try {
    if ($startApi) {
        Write-Step "API -> http://localhost:$apiPort"
        $running.Add((Start-DevProcess 'api' 'dotnet' @('watch', 'run', '--project', 'src/ControlHome.Api', '--launch-profile', 'http')))
    }
    if ($startWeb) {
        Write-Step "Web -> http://localhost:$webPort"
        $npmArgs = @($npmCommand | Select-Object -Skip 1) + @('run', 'dev', '--prefix', 'web')
        $running.Add((Start-DevProcess 'web' $npmCommand[0] $npmArgs))
    }
    Write-Host 'Press Ctrl+C to stop.' -ForegroundColor Green

    while ($true) {
        $exited = $running | Where-Object { $_.Process.HasExited } | Select-Object -First 1
        if ($exited) {
            $exitCode = $exited.Process.ExitCode
            Write-Warning "$($exited.Name) exited with code $exitCode - stopping the rest."
            break
        }
        Start-Sleep -Milliseconds 500
    }
}
finally {
    foreach ($entry in $running) {
        if (-not $entry.Process.HasExited) {
            try {
                $entry.Process.Kill($true) # entire process tree
                Write-Note "$($entry.Name) stopped."
            }
            catch {
                Write-Warning "Could not stop $($entry.Name) (pid $($entry.Process.Id)): $($_.Exception.Message)"
            }
        }
    }
}
exit $exitCode
