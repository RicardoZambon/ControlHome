#Requires -Version 7.2
<#
.SYNOPSIS
  Drops and re-creates a ControlHome development/test database.

.DESCRIPTION
  The SQL server also hosts other databases, so this script refuses any name that does not match
  ^ControlHome_(Dev|Test|Compose)(_[A-Za-z0-9]+)?$ (case-sensitive) before it connects anywhere.

  Server, login and password come from the API's user-secrets (ConnectionStrings:ControlHome; see
  ./scripts/setup-dev.ps1 -PromptConnectionString) and are never printed. The password reaches sqlcmd through the
  SQLCMDPASSWORD environment variable, never the command line. The script connects to [master], drops the database
  (rolling back open sessions) and creates it again with RECOVERY SIMPLE and COMPATIBILITY_LEVEL 170
  (SQL Server 2025 is required). The app applies its migrations on the next start.

  Asks for confirmation; pass -Confirm:$false to skip it, -WhatIf to only validate.

.PARAMETER Name
  Database to reset, e.g. ControlHome_Dev or ControlHome_Dev_feature1.

.EXAMPLE
  ./scripts/reset-dev-db.ps1 -Name ControlHome_Dev

.EXAMPLE
  ./scripts/reset-dev-db.ps1 -Name ControlHome_Dev -WhatIf
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [string]$Name
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

if (-not (Test-ControlHomeDatabaseName $Name)) {
    Write-Error "Refusing '$Name': only databases matching $script:ControlHomeDatabaseNamePattern (case-sensitive) may be dropped or created by tooling." -ErrorAction Continue
    exit 1
}

if (-not (Test-CommandAvailable 'sqlcmd')) {
    Write-Error 'sqlcmd was not found on PATH (install the SQL Server command-line tools / go-sqlcmd).' -ErrorAction Continue
    exit 1
}

$secrets = Get-UserSecrets
$connectionString = $secrets['ConnectionStrings:ControlHome']
$secrets = $null
if (-not $connectionString) {
    Write-Error 'ConnectionStrings:ControlHome is not set in user-secrets. Run ./scripts/setup-dev.ps1 -PromptConnectionString first.' -ErrorAction Continue
    exit 1
}

$builder = ConvertTo-ConnectionStringBuilder $connectionString
$connectionString = $null
$server = Get-ConnectionStringValue -Builder $builder -Keys @('Server', 'Data Source', 'Address', 'Addr', 'Network Address')
$user = Get-ConnectionStringValue -Builder $builder -Keys @('User ID', 'UID', 'User', 'UserID')
$password = Get-ConnectionStringValue -Builder $builder -Keys @('Password', 'PWD')
$integrated = Test-TruthyConnectionValue (Get-ConnectionStringValue -Builder $builder -Keys @('Integrated Security', 'Trusted_Connection'))
$trustCertificate = Test-TruthyConnectionValue (Get-ConnectionStringValue -Builder $builder -Keys @('TrustServerCertificate', 'Trust Server Certificate'))
$encrypt = Test-TruthyConnectionValue (Get-ConnectionStringValue -Builder $builder -Keys @('Encrypt'))
$builder = $null

if (-not $server) {
    Write-Error 'The stored connection string has no Server / Data Source.' -ErrorAction Continue
    exit 1
}
if (-not $integrated -and (-not $user -or -not $password)) {
    Write-Error 'The stored connection string needs User ID and Password (or Integrated Security).' -ErrorAction Continue
    exit 1
}

$sqlcmdArgs = [System.Collections.Generic.List[string]]::new()
$sqlcmdArgs.AddRange([string[]]@('-S', $server, '-d', 'master', '-b', '-I'))
if ($integrated) { $sqlcmdArgs.Add('-E') } else { $sqlcmdArgs.AddRange([string[]]@('-U', $user)) }
if ($trustCertificate) { $sqlcmdArgs.Add('-C') }
if ($encrypt) { $sqlcmdArgs.Add('-N') }

# $Name is validated above (letters, digits and underscores only), so it is safe inside [ ] and N' '.
$sql = "SET NOCOUNT ON; " +
"IF DB_ID(N'$Name') IS NOT NULL BEGIN ALTER DATABASE [$Name] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Name]; END; " +
"CREATE DATABASE [$Name]; " +
"ALTER DATABASE [$Name] SET RECOVERY SIMPLE; " +
"ALTER DATABASE [$Name] SET COMPATIBILITY_LEVEL = 170;"
$sqlcmdArgs.AddRange([string[]]@('-Q', $sql))

if (-not $PSCmdlet.ShouldProcess($Name, 'DROP and re-CREATE database')) {
    $password = $null
    exit 0
}

$previousPassword = $env:SQLCMDPASSWORD
try {
    if (-not $integrated) { $env:SQLCMDPASSWORD = $password }
    $password = $null
    & sqlcmd @sqlcmdArgs
    $exit = $LASTEXITCODE
}
finally {
    $env:SQLCMDPASSWORD = $previousPassword
}

if ($exit -ne 0) {
    Write-Error "sqlcmd failed (exit $exit) while resetting '$Name' - see the messages above." -ErrorAction Continue
    exit $exit
}
Write-Host "Recreated $Name (RECOVERY SIMPLE, COMPATIBILITY_LEVEL 170). The app migrates it on next start." -ForegroundColor Green
