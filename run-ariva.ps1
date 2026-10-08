<#
.SYNOPSIS
    Runs Ariva on this machine (every host, the web app and the simulator, through Ariva.AppHost) and shows the
    sign-in details of every demo user, with the live authenticator code.

.DESCRIPTION
    1. Checks the tools: .NET SDK, Node.js and a running Docker engine (Docker Desktop or Rancher Desktop with dockerd).
    2. Installs the web app's packages once (npm ci) and prepares the demo accounts once (node scripts/demo-local.mjs
       prepare): random passwords in .demo\accounts.json, which git ignores. Delete .demo to get new ones.
    3. Starts Ariva.AppHost in its own window and waits until Ariva answers.
    4. Opens the web app and keeps a table of every user: user name, password, role and, for the administrator, the
       current authenticator code. Press 1 to 4 to copy that user's password, C to copy the code, Q to quit (Ariva keeps
       running in its window; close that window to stop it).

    Local only: these accounts and the sign-in without a second factor for the operational roles exist only in
    vm-local on this machine. Never use this on a shared or production environment.

.PARAMETER Demo
    Also starts the scripted demo evening (node scripts/demo-local.mjs start) once Ariva is up.

.PARAMETER NoBrowser
    Does not open the browser.

.EXAMPLE
    .\run-ariva.ps1
    .\run-ariva.ps1 -Demo
#>
[CmdletBinding()]
param(
    [switch] $Demo,
    [switch] $NoBrowser
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$Accounts = Join-Path $Root '.demo\accounts.json'
$AppHostSettings = Join-Path $Root '.demo\apphost-environment.json'
$WebUrl = 'http://localhost:51010'
$MainHealth = 'http://localhost:51001/health/readiness'
$Dashboard = 'http://localhost:15880'

function Step([string] $Text) { Write-Host "`n== $Text" -ForegroundColor Cyan }
function Fail([string] $Text) { Write-Host "`n$Text" -ForegroundColor Red; exit 1 }

#region Tools

Step 'Checking the tools'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'The .NET SDK is missing: install .NET 10 SDK (winget install Microsoft.DotNet.SDK.10).' }
if (-not (Get-Command node -ErrorAction SilentlyContinue)) { Fail 'Node.js is missing: install Node 22 or later (winget install OpenJS.NodeJS.LTS).' }
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { Fail 'The docker command is missing: install Rancher Desktop (container engine dockerd) or Docker Desktop.' }
# Through cmd: Windows PowerShell turns a native command's stderr (Docker's 'No swap limit support' warning) into a
# terminating error when it is redirected under ErrorActionPreference Stop; only the exit code matters here.
cmd /c 'docker info >nul 2>&1'
if ($LASTEXITCODE -ne 0) { Fail 'Docker is not running: start Rancher Desktop (or Docker Desktop) and wait until it is ready, then run this again.' }
Write-Host "dotnet $(dotnet --version), node $(node --version), docker ready"

#endregion

#region Once

$Web = Join-Path $Root 'Platform\Frontplane\Ariva.Web'
if (-not (Test-Path (Join-Path $Web 'node_modules'))) {
    Step 'Installing the web app packages (first run only)'
    Push-Location $Web
    try { npm ci; if ($LASTEXITCODE -ne 0) { Fail 'npm ci failed (see above).' } } finally { Pop-Location }
}

Step 'Preparing the demo accounts'
node (Join-Path $Root 'scripts\demo-local.mjs') prepare | Out-Null
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $Accounts)) { Fail 'node scripts/demo-local.mjs prepare failed.' }
Write-Host 'Accounts in .demo\accounts.json (git-ignored).'

#endregion

#region Start

function Test-Up([string] $Url) {
    try { (Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200 } catch { $false }
}

if (Test-Up $MainHealth) {
    Step 'Ariva is already running'
} else {
    Step 'Starting Ariva.AppHost in its own window (the first build takes a few minutes)'
    $command = "Set-Location '$Root'; dotnet run --project Platform/Cloud/Ariva.AppHost -- '--AppHost:HostEnvironmentFile=$AppHostSettings'"
    Start-Process powershell -ArgumentList '-NoExit', '-NoProfile', '-Command', $command | Out-Null

    $deadline = (Get-Date).AddMinutes(15)
    while (-not ((Test-Up $MainHealth) -and (Test-Up $WebUrl))) {
        if ((Get-Date) -gt $deadline) { Fail "Ariva did not come up within 15 minutes. Look at the AppHost window and the dashboard ($Dashboard)." }
        Write-Host '.' -NoNewline
        Start-Sleep -Seconds 5
    }
    Write-Host ' up.'
}

if ($Demo) {
    Step 'Starting the scripted demo evening'
    node (Join-Path $Root 'scripts\demo-local.mjs') start
}

if (-not $NoBrowser) { Start-Process $WebUrl | Out-Null }

#endregion

#region Accounts and codes

function Get-Totp([string] $Secret) {
    $alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'
    $bits = ($Secret.ToUpperInvariant().TrimEnd('=').ToCharArray() | ForEach-Object { [Convert]::ToString($alphabet.IndexOf($_), 2).PadLeft(5, '0') }) -join ''
    $key = [byte[]]::new([math]::Floor($bits.Length / 8))
    for ($i = 0; $i -lt $key.Length; $i++) { $key[$i] = [Convert]::ToByte($bits.Substring($i * 8, 8), 2) }

    $counter = [BitConverter]::GetBytes([long][math]::Floor([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() / 30))
    if ([BitConverter]::IsLittleEndian) { [array]::Reverse($counter) }
    $hmac = [System.Security.Cryptography.HMACSHA1]::new($key)
    try { $hash = $hmac.ComputeHash($counter) } finally { $hmac.Dispose() }
    $offset = $hash[$hash.Length - 1] -band 0x0f
    # As [long]: PowerShell shifts a [byte] within its own width and would drop the high bits.
    $binary = (([long]$hash[$offset] -band 0x7f) -shl 24) -bor ([long]$hash[$offset + 1] -shl 16) -bor ([long]$hash[$offset + 2] -shl 8) -bor [long]$hash[$offset + 3]
    ($binary % 1000000).ToString('000000')
}

$people = (Get-Content $Accounts -Raw | ConvertFrom-Json).people
$admin = $people | Where-Object { $_.totpSecret } | Select-Object -First 1

while ($true) {
    Clear-Host
    Write-Host "Ariva is running: $WebUrl   (Aspire dashboard: $Dashboard)`n" -ForegroundColor Green
    $rows = for ($i = 0; $i -lt $people.Count; $i++) {
        $p = $people[$i]
        [pscustomobject]@{
            Key      = $i + 1
            User     = $p.userName
            Password = $p.password
            Code     = if ($p.totpSecret) { Get-Totp $p.totpSecret } else { '(none needed)' }
            Role     = ($p.roles -join ', ')
            Sees     = $p.shows
        }
    }
    $rows | Format-Table -AutoSize | Out-String -Width 220 | Write-Host
    $left = 30 - ([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() % 30)
    Write-Host "Authenticator code changes in $left s.  Press 1-$($people.Count) to copy a password, C to copy the code, Q to quit."
    Write-Host 'Ariva keeps running in its own window; close that window to stop it.' -ForegroundColor DarkGray

    $until = (Get-Date).AddSeconds(1)
    while ((Get-Date) -lt $until) {
        if ([Console]::KeyAvailable) {
            $key = [Console]::ReadKey($true).KeyChar
            if ($key -in 'q', 'Q') { exit 0 }
            if ($key -in 'c', 'C' -and $admin) { Set-Clipboard (Get-Totp $admin.totpSecret); Write-Host "Code copied for $($admin.userName)." -ForegroundColor Yellow; Start-Sleep -Milliseconds 700 }
            $index = [int][char]$key - [int][char]'1'
            if ($index -ge 0 -and $index -lt $people.Count) { Set-Clipboard $people[$index].password; Write-Host "Password copied for $($people[$index].userName)." -ForegroundColor Yellow; Start-Sleep -Milliseconds 700 }
        }
        Start-Sleep -Milliseconds 100
    }
}

#endregion
