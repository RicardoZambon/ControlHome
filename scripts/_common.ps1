# Shared helpers for the ControlHome developer scripts (dot-sourced; not meant to be run directly).
# Requires PowerShell 7.2+.

Set-StrictMode -Version Latest
# Native commands report failure through $LASTEXITCODE, which the scripts check explicitly.
$PSNativeCommandUseErrorActionPreference = $false

$script:ControlHomeNodeVersion = '24.21.0'
$script:ControlHomeApiProject = 'src/ControlHome.Api'
$script:ControlHomeDefaultUserSecretsId = 'controlhome-api'
# The SQL server also hosts other databases: tooling may only create/drop names matching this (case-sensitive).
$script:ControlHomeDatabaseNamePattern = '^ControlHome_(Dev|Test|Compose)(_[A-Za-z0-9]+)?$'

function Get-RepoRoot {
    Split-Path -Parent $PSScriptRoot
}

function Write-Step([string]$Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Note([string]$Message) {
    Write-Host "    $Message" -ForegroundColor DarkGray
}

function Test-CommandAvailable([string]$Name) {
    [bool](Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue)
}

function Get-NodeVersion {
    if (-not (Test-CommandAvailable 'node')) { return $null }
    $version = & node -v 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    "$version".Trim()
}

function Test-Node24 {
    (Get-NodeVersion) -match '^v24\.'
}

<#
.SYNOPSIS
  Makes sure `node` is 24.x for this PowerShell session. If it is not, prepends nvm's v24.21.0 folder to PATH
  (nvm-windows: $env:NVM_HOME or %LOCALAPPDATA%\nvm; nvm-sh: $env:NVM_DIR or ~/.nvm) when it is installed.
  Returns $true when Node 24 is active.
#>
function Use-Node24 {
    if (Test-Node24) { return $true }

    $exe = if ($IsWindows) { 'node.exe' } else { 'node' }
    $candidates = [System.Collections.Generic.List[string]]::new()
    if ($env:NVM_HOME) { $candidates.Add((Join-Path $env:NVM_HOME "v$script:ControlHomeNodeVersion")) }
    if ($env:LOCALAPPDATA) { $candidates.Add((Join-Path $env:LOCALAPPDATA "nvm/v$script:ControlHomeNodeVersion")) }
    if ($env:NVM_DIR) { $candidates.Add((Join-Path $env:NVM_DIR "versions/node/v$script:ControlHomeNodeVersion/bin")) }
    if ($HOME) { $candidates.Add((Join-Path $HOME ".nvm/versions/node/v$script:ControlHomeNodeVersion/bin")) }

    foreach ($dir in $candidates) {
        if (Test-Path -LiteralPath (Join-Path $dir $exe)) {
            $env:PATH = $dir + [System.IO.Path]::PathSeparator + $env:PATH
            if (Test-Node24) {
                Write-Note "Using Node $(Get-NodeVersion) from $dir for this session."
                return $true
            }
        }
    }

    $found = Get-NodeVersion
    if (-not $found) { $found = 'not installed' }
    Write-Warning "Node 24 is required (found: $found). Install it with nvm and run: nvm use $script:ControlHomeNodeVersion"
    return $false
}

function Test-ControlHomeDatabaseName([string]$Name) {
    -not [string]::IsNullOrEmpty($Name) -and ($Name -cmatch $script:ControlHomeDatabaseNamePattern)
}

<#
.SYNOPSIS
  Arguments that select the API's user-secrets store: $env:CONTROLHOME_USER_SECRETS_ID if set, else the API project
  (when it exists), else the id "controlhome-api" (the project's UserSecretsId).
#>
function Get-UserSecretsArguments {
    if ($env:CONTROLHOME_USER_SECRETS_ID) {
        return @('--id', $env:CONTROLHOME_USER_SECRETS_ID)
    }
    $projectDir = Join-Path (Get-RepoRoot) $script:ControlHomeApiProject
    if (Test-Path -LiteralPath (Join-Path $projectDir 'ControlHome.Api.csproj')) {
        return @('--project', $projectDir)
    }
    return @('--id', $script:ControlHomeDefaultUserSecretsId)
}

<#
.SYNOPSIS
  Reads the API's user-secrets into a hashtable (flattened keys, e.g. 'ConnectionStrings:ControlHome').
  The values are secrets: never print them.
#>
function Get-UserSecrets {
    if (-not (Test-CommandAvailable 'dotnet')) { throw 'dotnet was not found on PATH.' }
    $selector = Get-UserSecretsArguments
    $output = & dotnet user-secrets list --json @selector 2>$null
    if ($LASTEXITCODE -ne 0) { throw "Could not read user-secrets ($($selector -join ' '))." }
    $json = ($output | Where-Object { $_ -notmatch '^//(BEGIN|END)\s*$' }) -join "`n"
    if ([string]::IsNullOrWhiteSpace($json)) { return @{} }
    $json | ConvertFrom-Json -AsHashtable
}

<#
.SYNOPSIS
  First value present in a parsed connection string for any of the given keys (synonyms), else $null.
#>
function Get-ConnectionStringValue {
    param(
        [Parameter(Mandatory)][System.Data.Common.DbConnectionStringBuilder]$Builder,
        [Parameter(Mandatory)][string[]]$Keys
    )
    foreach ($key in $Keys) {
        if ($Builder.ContainsKey($key)) {
            $value = "$($Builder[$key])".Trim()
            if ($value) { return $value }
        }
    }
    return $null
}

function ConvertTo-ConnectionStringBuilder([string]$ConnectionString) {
    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    try {
        # Call the setter explicitly: on a dictionary type, `$builder.ConnectionString = ...` would add a key instead.
        $builder.set_ConnectionString($ConnectionString)
    }
    catch {
        # Do not surface the parser message: it may quote part of the (secret) string.
        throw 'The connection string could not be parsed.'
    }
    # The builder is a dictionary: wrap it so PowerShell does not unroll it into its entries.
    return , $builder
}

function Test-TruthyConnectionValue([string]$Value) {
    $Value -and ($Value.Trim().ToLowerInvariant() -in @('true', 'yes', 'sspi', 'mandatory', 'strict'))
}
