#Requires -Version 7.2
<#
.SYNOPSIS
  Prepares a ControlHome development clone. Safe to run again at any time.

.DESCRIPTION
  1. Checks for Node.js 24 (switches this session to nvm's v24.21.0 when it is installed).
  2. Enables the secret-scanning pre-commit hook: git config core.hooksPath .githooks
  3. dotnet tool restore, dotnet restore, npm ci --prefix web
     (each step is skipped while that part of the repository does not exist yet).
  4. Optionally stores the development database connection string in the API's user-secrets
     (ConnectionStrings:ControlHome). The value is never echoed and never passed on a command line.

.PARAMETER ConnectionString
  Development connection string, for example
  "Server=<host>,1433;Database=ControlHome_Dev;User ID=<login>;Password=<password>;TrustServerCertificate=True".
  The database name must match ^ControlHome_(Dev|Test|Compose)(_[A-Za-z0-9]+)?$.
  Prefer -PromptConnectionString, which keeps the value out of your shell history.

.PARAMETER PromptConnectionString
  Prompt for the connection string with hidden input.

.EXAMPLE
  ./scripts/setup-dev.ps1

.EXAMPLE
  ./scripts/setup-dev.ps1 -PromptConnectionString
#>
[CmdletBinding()]
param(
    [string]$ConnectionString,
    [switch]$PromptConnectionString
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

$root = Get-RepoRoot
$failures = [System.Collections.Generic.List[string]]::new()

function Invoke-Checked([string]$Description, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "$Description failed (exit $LASTEXITCODE)."
        $failures.Add($Description)
    }
}

Push-Location $root
try {
    Write-Step 'Node.js 24'
    $nodeOk = Use-Node24
    if ($nodeOk) { Write-Note "node $(Get-NodeVersion)" }

    Write-Step 'Pre-commit secret check (core.hooksPath = .githooks)'
    if (Test-CommandAvailable 'git') {
        Invoke-Checked 'git config core.hooksPath' { git config core.hooksPath .githooks }
    }
    else {
        Write-Warning 'git was not found on PATH.'
        $failures.Add('git config core.hooksPath')
    }

    $hasDotnet = Test-CommandAvailable 'dotnet'
    if (-not $hasDotnet) {
        Write-Warning 'dotnet was not found on PATH - install the .NET SDK pinned in global.json.'
    }

    Write-Step '.NET local tools'
    if (-not (Test-Path -LiteralPath '.config/dotnet-tools.json')) { Write-Note 'skipped (.config/dotnet-tools.json not found)' }
    elseif (-not $hasDotnet) { $failures.Add('dotnet tool restore') }
    else { Invoke-Checked 'dotnet tool restore' { dotnet tool restore } }

    Write-Step '.NET packages'
    if (-not (Test-Path -LiteralPath 'ControlHome.slnx')) { Write-Note 'skipped (ControlHome.slnx not found)' }
    elseif (-not $hasDotnet) { $failures.Add('dotnet restore') }
    else { Invoke-Checked 'dotnet restore' { dotnet restore } }

    Write-Step 'Web packages (npm ci --prefix web)'
    if (-not (Test-Path -LiteralPath 'web/package-lock.json')) { Write-Note 'skipped (web/package-lock.json not found)' }
    elseif (-not $nodeOk) { $failures.Add('npm ci (Node 24 required)') }
    else { Invoke-Checked 'npm ci' { npm ci --prefix web } }

    if ($PromptConnectionString) {
        $secure = Read-Host -AsSecureString -Prompt 'Development connection string (input hidden)'
        $ConnectionString = [System.Net.NetworkCredential]::new('', $secure).Password
        Remove-Variable secure
    }

    if ($ConnectionString) {
        Write-Step 'Development connection string -> user-secrets (ConnectionStrings:ControlHome)'
        $builder = ConvertTo-ConnectionStringBuilder $ConnectionString
        $database = Get-ConnectionStringValue -Builder $builder -Keys @('Database', 'Initial Catalog')
        if (-not (Test-ControlHomeDatabaseName $database)) {
            throw "The connection string's database must match $script:ControlHomeDatabaseNamePattern (found: '$database'). The server hosts other databases; ControlHome tooling only touches its own."
        }
        if (-not $hasDotnet) { throw 'dotnet is required to store user-secrets.' }

        $selector = Get-UserSecretsArguments
        # JSON on stdin keeps the value off the process command line.
        $payload = @{ ConnectionStrings = @{ ControlHome = $ConnectionString } } | ConvertTo-Json -Compress
        $payload | dotnet user-secrets set @selector | Out-Null
        $exit = $LASTEXITCODE
        Remove-Variable payload, builder
        $ConnectionString = $null
        if ($exit -ne 0) {
            $failures.Add('dotnet user-secrets set')
        }
        else {
            Write-Note "stored for database '$database' ($($selector -join ' '))."
        }
    }

    Write-Host ''
    if ($failures.Count -gt 0) {
        Write-Warning "Setup finished with problems: $($failures -join '; ')"
        exit 1
    }
    Write-Host 'Setup complete. Next: ./scripts/dev.ps1 (API on http://localhost:5080, web on http://localhost:5173).' -ForegroundColor Green
}
finally {
    Pop-Location
}
