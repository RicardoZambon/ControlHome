#Requires -Version 7.2
<#
.SYNOPSIS
    Runs the Playwright end-to-end specs (or the screenshots) against the ControlHome Docker image.

.DESCRIPTION
    1. Builds the image from deploy/Dockerfile (controlhome:e2e) - the API runs in Docker, never on the host (Windows
       Smart App Control can block freshly built DLLs there).
    2. Points it at the dev SQL Server from user-secrets (ConnectionStrings:ControlHome) with the database replaced by
       ControlHome_Test_E2e (the app creates and migrates it). A host name the container cannot resolve (mDNS *.local,
       single-label names) is resolved here and passed with --add-host. The connection string travels in a temporary
       --env-file that is deleted afterwards; it is never printed.
    3. Starts the container detached in the Testing environment (POST /api/_test/reset exists) with the fixed setup code
       E2E-SETUP-CODE-1, a tmpfs /data and port 18711, and waits for /health/ready (at most 3 minutes).
    4. Runs `npm run e2e` (or `npm run screenshots`) in web/ with E2E_BASE_URL=http://localhost:18711.
    5. Always: removes the container (printing its log when something failed) and drops the database - only when the
       name matches ^ControlHome_(Dev|Test|Compose)(_[A-Za-z0-9]+)?$. The database is also dropped before the run, so
       data (and Data Protection keys) from an earlier run never mix with the new container's fresh /data.

    Needs Docker, Node 24 (nvm's v24.21.0 is used when it is installed), the API's user-secrets and sqlcmd (to drop the
    database; without it the database is left behind with a warning). Install the browser once:
    `cd web; npx playwright install chromium`.

.PARAMETER Screenshots
    Capture the screenshots (npm run screenshots -> web/tests/e2e/screenshots) instead of running the specs.

.PARAMETER NoBuild
    Reuse the existing image instead of building it.

.PARAMETER PlaywrightArgs
    Passed to Playwright, e.g. `--project desktop`, `--grep login`, `--headed`, `--debug`.

.EXAMPLE
    ./scripts/e2e.ps1
    ./scripts/e2e.ps1 -Screenshots
    ./scripts/e2e.ps1 -NoBuild --project desktop --grep "last admin"
#>
[CmdletBinding()]
param(
    [switch] $Screenshots,
    [switch] $NoBuild,
    [int] $Port = 18711,
    [string] $Database = 'ControlHome_Test_E2e',
    [string] $Image = 'controlhome:e2e',
    [string] $SetupCode = 'E2E-SETUP-CODE-1',
    [Parameter(ValueFromRemainingArguments)]
    [string[]] $PlaywrightArgs = @()
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '_common.ps1')

$repo = Get-RepoRoot
$webDir = Join-Path $repo 'web'
$container = "controlhome-e2e-$Port"
$baseUrl = "http://localhost:$Port"

if (-not (Test-ControlHomeDatabaseName $Database)) {
    Write-Error "Refusing '$Database': only databases matching $script:ControlHomeDatabaseNamePattern (case-sensitive) may be created or dropped by tooling." -ErrorAction Continue
    exit 1
}
if (-not (Test-CommandAvailable 'docker')) {
    Write-Error 'docker was not found on PATH.' -ErrorAction Continue
    exit 1
}
if (-not (Use-Node24)) { exit 1 }

# --- Connection string: user-secrets, database replaced, host resolved for the container ------------------------------
$secrets = Get-UserSecrets
$connectionString = $secrets['ConnectionStrings:ControlHome']
$secrets = $null
if (-not $connectionString) {
    Write-Error 'ConnectionStrings:ControlHome is not set in user-secrets. Run ./scripts/setup-dev.ps1 -PromptConnectionString first.' -ErrorAction Continue
    exit 1
}
$builder = ConvertTo-ConnectionStringBuilder $connectionString
$connectionString = $null
foreach ($key in @('Database', 'Initial Catalog')) { if ($builder.ContainsKey($key)) { [void]$builder.Remove($key) } }
$builder['Database'] = $Database

$server = Get-ConnectionStringValue -Builder $builder -Keys @('Server', 'Data Source', 'Address', 'Addr', 'Network Address')
if (-not $server) {
    Write-Error 'The stored connection string has no Server / Data Source.' -ErrorAction Continue
    exit 1
}

# Containers cannot resolve names only the host can (mDNS *.local, NetBIOS/LLMNR single-label names): resolve on the host
# and map the name with --add-host, so the connection string (and TLS name checks) keep the name.
$addHost = @()
$hostPart = (($server -replace '^(?i:tcp):', '') -split '[,\\]', 2)[0].Trim()
$parsedIp = $null
if ($hostPart -and $hostPart -notin @('localhost', '.', '(local)') -and
    -not [System.Net.IPAddress]::TryParse($hostPart, [ref]$parsedIp)) {
    $ip = [System.Net.Dns]::GetHostAddresses($hostPart) |
        Where-Object { $_.AddressFamily -eq 'InterNetwork' } | Select-Object -First 1
    if ($ip) { $addHost = @('--add-host', "$($hostPart):$($ip.IPAddressToString)") }
}

# --- Dropping the test database (host-side sqlcmd; the password goes through SQLCMDPASSWORD, never the command line) ---
function Remove-E2eDatabase {
    if (-not (Test-ControlHomeDatabaseName $Database)) { return }
    if (-not (Test-CommandAvailable 'sqlcmd')) {
        Write-Warning "sqlcmd was not found on PATH: database $Database was not dropped."
        return
    }
    $user = Get-ConnectionStringValue -Builder $builder -Keys @('User ID', 'UID', 'User', 'UserID')
    $integrated = Test-TruthyConnectionValue (Get-ConnectionStringValue -Builder $builder -Keys @('Integrated Security', 'Trusted_Connection'))
    $trust = Test-TruthyConnectionValue (Get-ConnectionStringValue -Builder $builder -Keys @('TrustServerCertificate', 'Trust Server Certificate'))
    $encrypt = Test-TruthyConnectionValue (Get-ConnectionStringValue -Builder $builder -Keys @('Encrypt'))

    $sqlcmdArgs = [System.Collections.Generic.List[string]]::new()
    $sqlcmdArgs.AddRange([string[]]@('-S', $server, '-d', 'master', '-b', '-I'))
    if ($integrated) { $sqlcmdArgs.Add('-E') } else { $sqlcmdArgs.AddRange([string[]]@('-U', $user)) }
    if ($trust) { $sqlcmdArgs.Add('-C') }
    if ($encrypt) { $sqlcmdArgs.Add('-N') }
    # $Database passed the guard (letters, digits and underscores only), so it is safe inside [ ] and N' '.
    $sqlcmdArgs.AddRange([string[]]@('-Q',
            "SET NOCOUNT ON; IF DB_ID(N'$Database') IS NOT NULL BEGIN ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Database]; END;"))

    $previous = $env:SQLCMDPASSWORD
    try {
        if (-not $integrated) { $env:SQLCMDPASSWORD = Get-ConnectionStringValue -Builder $builder -Keys @('Password', 'PWD') }
        & sqlcmd @sqlcmdArgs
        if ($LASTEXITCODE -ne 0) { Write-Warning "sqlcmd could not drop $Database (exit $LASTEXITCODE)." }
        else { Write-Note "Dropped $Database (if it existed)." }
    }
    finally { $env:SQLCMDPASSWORD = $previous }
}

$envFile = $null
$started = $false
$exitCode = 1
$previousBaseUrl = $env:E2E_BASE_URL
$previousSetupCode = $env:E2E_SETUP_CODE
try {
    if (-not $NoBuild) {
        Write-Step "Building $Image (deploy/Dockerfile)"
        docker build -f (Join-Path $repo 'deploy/Dockerfile') -t $Image $repo
        if ($LASTEXITCODE -ne 0) { throw "docker build failed (exit $LASTEXITCODE)." }
    }

    Write-Step "Dropping a leftover $Database"
    Remove-E2eDatabase

    # Only the connection string is secret: it goes in the env file, everything else on the command line.
    $envFile = New-TemporaryFile
    Set-Content -Path $envFile -Value "ConnectionStrings__ControlHome=$($builder.get_ConnectionString())" -NoNewline

    docker rm -f $container 2>$null | Out-Null
    Write-Step "Starting $container on $baseUrl (Testing environment, database $Database)"
    $runArgs = @('run', '--detach', '--name', $container,
        '--env-file', $envFile.FullName,
        '-e', 'ASPNETCORE_ENVIRONMENT=Testing',
        '-e', "Setup__Code=$SetupCode",
        # Every test completes the setup and signs in again: lift the per-IP limits (5 setups / 10 min, 10 logins / min).
        '-e', 'RateLimiting__Setup__PermitLimit=10000',
        '-e', 'RateLimiting__Login__PermitLimit=10000',
        '-p', "$($Port):8711",
        '--tmpfs', '/data:uid=1654') + $addHost + @($Image)
    docker @runArgs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "docker run failed (exit $LASTEXITCODE)." }
    $started = $true

    Write-Step 'Waiting for /health/ready (up to 3 minutes)'
    $deadline = (Get-Date).AddMinutes(3)
    $ready = $false
    while ((Get-Date) -lt $deadline) {
        $running = docker inspect --format '{{.State.Running}}' $container 2>$null
        if ($running -ne 'true') { throw "$container stopped while starting." }
        try {
            $response = Invoke-WebRequest -Uri "$baseUrl/health/ready" -TimeoutSec 5 -SkipHttpErrorCheck
            if ($response.StatusCode -eq 200) { $ready = $true; break }
        }
        catch { } # Not listening yet.
        Start-Sleep -Seconds 2
    }
    if (-not $ready) { throw "$baseUrl/health/ready did not answer 200 within 3 minutes." }

    $script = if ($Screenshots) { 'screenshots' } else { 'e2e' }
    Write-Step "npm run $script $($PlaywrightArgs -join ' ')"
    $env:E2E_BASE_URL = $baseUrl
    $env:E2E_SETUP_CODE = $SetupCode
    Push-Location $webDir
    try {
        if ($PlaywrightArgs.Count -gt 0) { npm run $script -- @PlaywrightArgs } else { npm run $script }
        $exitCode = $LASTEXITCODE
    }
    finally { Pop-Location }
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
}
finally {
    $env:E2E_BASE_URL = $previousBaseUrl
    $env:E2E_SETUP_CODE = $previousSetupCode
    if ($started) {
        if ($exitCode -ne 0) {
            Write-Step "Last 200 log lines of $container"
            docker logs --tail 200 $container 2>&1 | Out-Host
        }
        docker rm -f $container 2>$null | Out-Null
    }
    if ($envFile) { Remove-Item $envFile -Force -ErrorAction SilentlyContinue }
    Write-Step "Dropping $Database"
    Remove-E2eDatabase
}

if ($exitCode -eq 0) { Write-Host 'E2E run passed.' -ForegroundColor Green }
exit $exitCode
