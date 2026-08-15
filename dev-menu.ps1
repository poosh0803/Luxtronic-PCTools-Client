<#
    Dev launcher menu for Luxtronic-PCTools-Client.

    Stand-in for a proper installer/shortcut while the project is still in active
    development: build, test, sanity-check prerequisites, and launch the WPF app -
    elevated (real sensor access) or unelevated (fast iteration, degraded sensors).

    This is a developer convenience only. The actual technician-facing UI is the WPF
    app itself (MainWindow) - this script does not reimplement it.

    Usage:
        powershell -ExecutionPolicy Bypass -File .\dev-menu.ps1
#>

$ErrorActionPreference = 'Stop'

$RepoRoot     = $PSScriptRoot
$SlnPath      = Join-Path $RepoRoot 'LuxtronicPCTools.sln'
$AppCsproj    = Join-Path $RepoRoot 'src\Luxtronic.PCTools\Luxtronic.PCTools.csproj'
$AppSettings  = Join-Path $RepoRoot 'src\Luxtronic.PCTools\appsettings.json'
$ExeDir       = Join-Path $RepoRoot 'src\Luxtronic.PCTools\bin\Debug\net8.0-windows'
$ExePath      = Join-Path $ExeDir 'Luxtronic.PCTools.exe'
$ApiKeyPath   = Join-Path $ExeDir 'apikey.txt'
$Prime95Dir   = Join-Path $RepoRoot 'tools\prime95'
$Prime95Exe   = Join-Path $Prime95Dir 'prime95.exe'
$HwiDir       = Join-Path $RepoRoot 'tools\hwi'
$HwiExe       = Join-Path $HwiDir 'HWiNFO64.exe'
$FurMarkDir   = Join-Path $RepoRoot 'tools\FurMark_win64'
$FurMarkExe   = Join-Path $FurMarkDir 'furmark.exe'
$TM5Dir       = Join-Path $RepoRoot 'tools\TestMem5'
$TM5Exe       = Join-Path $TM5Dir 'TM5.exe'
$DiskSpdDir   = Join-Path $RepoRoot 'tools\DiskSpd'
$DiskSpdExe   = Join-Path $DiskSpdDir 'DiskSpd64.exe'
$ToolsDir     = Join-Path $RepoRoot 'tools'
$PublishDir   = Join-Path $RepoRoot 'publish\win-x64'
$PublishAssetsDir = Join-Path $RepoRoot 'publish-assets'

# CPU test duration is server-owned config (CONTRACT.md section 3 - "config flows one direction:
# from server to client"), not a client setting. Assumes Luxtronic-PCTools-Server checked out as
# a sibling directory next to this repo, matching this machine's layout under \Documents\Github.
$ServerRepoRoot   = Join-Path $RepoRoot '..\Luxtronic-PCTools-Server'
$ServerConfigPath = Join-Path $ServerRepoRoot 'config\default.json'

# Live LAN deploy target - same server/credentials/pattern as the other Luxtronic services
# (see the lan-portal-deploy skill). luxtronic-pctools-server is the pm2 process name (confirmed
# via `pm2 list` on the box), not something guessed - re-check with `pm2 jlist` if this ever stops
# matching (e.g. the service gets renamed).
$LanServerHost     = '192.168.68.255'
$LanServerUser     = 'root'
$LanSshKeyPath     = 'C:\Users\Admin\.ssh\lan-portal-ssh.txt'
$LanServerRepoPath = '/root/Luxtronic-PCTools-Server'
$LanPm2Name        = 'luxtronic-pctools-server'

function Write-Header {
    Clear-Host
    Write-Host '========================================================' -ForegroundColor Cyan
    Write-Host '  Luxtronic PCTools - Client Dev Menu' -ForegroundColor Cyan
    Write-Host '  (dev convenience only - not the technician-facing UI)' -ForegroundColor DarkCyan
    Write-Host '========================================================' -ForegroundColor Cyan
    Write-Host ''
}

function Test-DotnetSdk {
    try {
        $sdks = dotnet --list-sdks 2>$null
    } catch {
        return $false
    }
    if (-not $sdks) { return $false }
    return ($sdks | Where-Object { $_ -match '^8\.0\.' }) -ne $null
}

function Test-HwInfoSharedMemoryActive {
    # Directly checks whether HWiNFO's shared-memory block exists right now (Global\HWiNFO_SENS_SM2),
    # rather than just whether HWiNFO64.exe is present on disk - this is the only thing that
    # actually answers "would the CPU temp/clock fallback work if invoked right now", since the
    # exe can be present but not running, running without Shared Memory Support enabled, or
    # running with that setting enabled but not yet restarted since (see HwInfoSensorReader.cs
    # class remarks - the mapping only gets created at HWiNFO startup, not retroactively).
    try {
        Add-Type -AssemblyName System.Core -ErrorAction SilentlyContinue
        # Explicit Read rights, not the default ReadWrite: OpenExisting's default request fails
        # with "Access to the path is denied" on this mapping when the calling process isn't
        # elevated, even though the mapping is genuinely readable and the app's own fallback
        # (HwInfoSensorReader, which opens read-only) works fine unelevated - confirmed against a
        # live HWiNFO64 process. Requesting ReadWrite here was a false negative, not a real
        # unavailability signal.
        $mmf = [System.IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting(
            'Global\HWiNFO_SENS_SM2', [System.IO.MemoryMappedFiles.MemoryMappedFileRights]::Read)
        $mmf.Dispose()
        return $true
    } catch {
        return $false
    }
}

function Get-CpuDurationMinutes {
    if (-not (Test-Path $ServerConfigPath)) { return $null }
    try {
        $cfg = Get-Content $ServerConfigPath -Raw | ConvertFrom-Json
        return $cfg.cpu.duration_minutes
    } catch {
        return $null
    }
}

function Get-GpuDurationMinutes {
    if (-not (Test-Path $ServerConfigPath)) { return $null }
    try {
        $cfg = Get-Content $ServerConfigPath -Raw | ConvertFrom-Json
        return $cfg.gpu.duration_minutes
    } catch {
        return $null
    }
}

function Show-Status {
    Write-Host 'Environment status:' -ForegroundColor Yellow
    Write-Host ''

    if (Test-DotnetSdk) {
        Write-Host '  [OK]   .NET 8 SDK found' -ForegroundColor Green
    } else {
        Write-Host '  [FAIL] .NET 8 SDK not found - install from https://dotnet.microsoft.com/download/dotnet/8.0' -ForegroundColor Red
    }

    if (Test-Path $ExePath) {
        Write-Host "  [OK]   Built exe present: $ExePath" -ForegroundColor Green
    } else {
        Write-Host '  [--]   Not built yet (run Build first)' -ForegroundColor DarkYellow
    }

    if (Test-Path $Prime95Exe) {
        Write-Host "  [OK]   prime95.exe found: $Prime95Exe" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] prime95.exe missing - drop the real binary into $Prime95Dir" -ForegroundColor Red
        Write-Host '         (see tools\prime95\README.md - not auto-downloaded)' -ForegroundColor DarkYellow
    }

    # HWiNFO is the sole CPU/GPU sensor source now (see README "Known risk") - not required for
    # the exe to launch at all (unlike prime95, still [FAIL]/red), but without it CPU/GPU sensors
    # come up unhealthy and the CPU test gets disabled, so still worth a clear signal here.
    if (Test-Path $HwiExe) {
        Write-Host "  [OK]   HWiNFO64.exe found: $HwiExe" -ForegroundColor Green
    } else {
        Write-Host "  [--]   HWiNFO64.exe not found at $HwiDir - required for CPU/GPU sensors" -ForegroundColor DarkYellow
        Write-Host '         to work at all now (no LHM fallback - see README "Known risk")' -ForegroundColor DarkYellow
    }
    if (Test-HwInfoSharedMemoryActive) {
        Write-Host '  [OK]   HWiNFO shared memory is active right now - CPU/GPU sensors will work' -ForegroundColor Green
    } else {
        Write-Host '  [--]   HWiNFO shared memory not active - CPU/GPU sensors will come up unhealthy until HWiNFO is' -ForegroundColor DarkYellow
        Write-Host '         running with Shared Memory Support enabled (and restarted after enabling it - see HwInfoSensorReader.cs)' -ForegroundColor DarkYellow
    }

    if (Test-Path $ApiKeyPath) {
        Write-Host "  [OK]   apikey.txt found: $ApiKeyPath" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] apikey.txt missing at $ApiKeyPath (option 6 to set one)" -ForegroundColor Red
    }

    if (Test-Path $AppSettings) {
        try {
            $settings = Get-Content $AppSettings -Raw | ConvertFrom-Json
            Write-Host "  [OK]   ServerBaseUrl = $($settings.ServerBaseUrl)" -ForegroundColor Green
        } catch {
            Write-Host "  [FAIL] appsettings.json is present but not valid JSON" -ForegroundColor Red
        }
    } else {
        Write-Host "  [FAIL] appsettings.json missing at $AppSettings" -ForegroundColor Red
    }

    $cpuDuration = Get-CpuDurationMinutes
    if ($null -ne $cpuDuration) {
        Write-Host "  [OK]   CPU test duration (server config) = $cpuDuration minute(s)" -ForegroundColor Green
    } else {
        Write-Host "  [--]   CPU test duration unknown - server config not found at $ServerConfigPath" -ForegroundColor DarkYellow
    }

    $gpuDuration = Get-GpuDurationMinutes
    if ($null -ne $gpuDuration) {
        Write-Host "  [OK]   GPU test duration (server config) = $gpuDuration minute(s)" -ForegroundColor Green
    } else {
        Write-Host "  [--]   GPU test duration unknown - server config not found at $ServerConfigPath" -ForegroundColor DarkYellow
    }

    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if ($isAdmin) {
        Write-Host '  [OK]   This shell is running elevated (Administrator)' -ForegroundColor Green
    } else {
        Write-Host '  [--]   This shell is NOT elevated - option 4 will prompt UAC for you' -ForegroundColor DarkYellow
    }

    Write-Host ''
}

function Invoke-Build {
    Write-Host "Building $SlnPath ..." -ForegroundColor Yellow
    & dotnet build $SlnPath
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'Build failed.' -ForegroundColor Red
    } else {
        Write-Host 'Build succeeded.' -ForegroundColor Green
    }
}

function Invoke-Tests {
    Write-Host "Running tests for $SlnPath ..." -ForegroundColor Yellow
    & dotnet test $SlnPath
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'Tests failed.' -ForegroundColor Red
    } else {
        Write-Host 'All tests passed.' -ForegroundColor Green
    }
}

function Start-AppElevated {
    if (-not (Test-Path $ExePath)) {
        Write-Host 'Exe not built yet - building first...' -ForegroundColor Yellow
        Invoke-Build
        if (-not (Test-Path $ExePath)) {
            Write-Host 'Build did not produce the exe - aborting launch.' -ForegroundColor Red
            return
        }
    }

    if (-not (Test-Path $Prime95Exe)) {
        Write-Host "Warning: prime95.exe not found at $Prime95Exe - Start will fail in the app until it's dropped in." -ForegroundColor DarkYellow
    }
    if (-not (Test-Path $ApiKeyPath)) {
        Write-Host "Warning: apikey.txt not found at $ApiKeyPath - session creation will fail until it's set (option 6)." -ForegroundColor DarkYellow
    }

    Write-Host 'Launching elevated (UAC prompt expected) - required for real sensor access...' -ForegroundColor Yellow
    try {
        Start-Process -FilePath $ExePath -WorkingDirectory $ExeDir -Verb RunAs
    } catch {
        Write-Host "Failed to launch: $($_.Exception.Message)" -ForegroundColor Red
    }
}

function Start-AppDev {
    Write-Host 'Launching via dotnet run (unelevated - sensors will likely come up degraded, see README).' -ForegroundColor DarkYellow
    Write-Host 'This is for fast UI/logic iteration only, not for validating real sensor access.' -ForegroundColor DarkYellow
    & dotnet run --project $AppCsproj
}

function Set-ApiKey {
    Write-Host "This writes $ApiKeyPath (git-ignored - never committed)." -ForegroundColor Yellow
    $key = Read-Host -Prompt 'Paste the technician API key (input will be visible)'
    if ([string]::IsNullOrWhiteSpace($key)) {
        Write-Host 'No key entered - nothing written.' -ForegroundColor DarkYellow
        return
    }
    New-Item -ItemType Directory -Force -Path $ExeDir | Out-Null
    Set-Content -Path $ApiKeyPath -Value $key.Trim() -NoNewline -Encoding utf8
    Write-Host "Wrote $ApiKeyPath." -ForegroundColor Green
}

function Set-ServerUrl {
    if (-not (Test-Path $AppSettings)) {
        Write-Host "appsettings.json not found at $AppSettings" -ForegroundColor Red
        return
    }
    $settings = Get-Content $AppSettings -Raw | ConvertFrom-Json
    Write-Host "Current ServerBaseUrl: $($settings.ServerBaseUrl)" -ForegroundColor Yellow
    $newUrl = Read-Host -Prompt 'New ServerBaseUrl (blank to cancel)'
    if ([string]::IsNullOrWhiteSpace($newUrl)) {
        Write-Host 'Cancelled.' -ForegroundColor DarkYellow
        return
    }
    $settings.ServerBaseUrl = $newUrl.Trim()
    ($settings | ConvertTo-Json -Depth 10) | Set-Content -Path $AppSettings -Encoding utf8
    Write-Host "Updated $AppSettings. Rebuild so it's copied to the output directory." -ForegroundColor Green
}

# Real gotcha hit live (2026-08-15): editing the local checkout's config/default.json has ZERO
# effect on the actual LAN server the app talks to (ServerBaseUrl in appsettings.json, normally
# http://192.168.68.255:7777) - that's a separately-running deployed process (pm2, service name
# luxtronic-pctools-server) that only picks up changes via its own git pull + pm2 restart, same
# pattern as Luxtronic-Portal/etc. A technician relaunching the app after using this menu option
# will keep seeing the OLD duration until someone commits+pushes this repo's change and deploys it
# (see lan-portal-deploy skill / CLAUDE.md) - this menu option alone does not do that.
function Write-ServerConfigDeployWarning {
    Write-Host 'IMPORTANT: this only changes YOUR LOCAL checkout. The live LAN server' -ForegroundColor Red
    Write-Host '(usually http://192.168.68.255:7777 - check appsettings.json ServerBaseUrl) is a' -ForegroundColor Red
    Write-Host 'separate running process that reads its OWN copy of this file - it will keep using' -ForegroundColor Red
    Write-Host 'the old value until this change is committed, pushed, and deployed there. You will' -ForegroundColor Red
    Write-Host 'be asked below whether to do that now (or use option 10 to deploy later).' -ForegroundColor Red
}

# Commits config/default.json (only that file - never -A, so this can't accidentally sweep up
# someone else's unrelated WIP sitting in the sibling Server checkout), pushes, then SSHes into
# the live LAN box to git pull + pm2 restart the real running service - the same manual sequence
# used to deploy the CPU duration change live (2026-08-15), now available from the menu itself
# instead of by hand. This touches a shared production server other people may be relying on
# (the technician-facing dashboard, any PC client currently mid-session against it) - always
# confirmed interactively before doing anything, never called silently.
function Deploy-ServerConfigToLive {
    if (-not (Test-Path $ServerRepoRoot)) {
        Write-Host "Server repo not found at $ServerRepoRoot - expected as a sibling directory next to this repo." -ForegroundColor Red
        return
    }
    if (-not (Test-Path $LanSshKeyPath)) {
        Write-Host "SSH key not found at $LanSshKeyPath - can't reach the live server without it." -ForegroundColor Red
        return
    }

    Push-Location $ServerRepoRoot
    try {
        $uncommittedDiff = git diff -- config/default.json
        $stagedDiff = git diff --cached -- config/default.json
        $hasLocalChange = [bool]$uncommittedDiff -or [bool]$stagedDiff

        if ($hasLocalChange) {
            Write-Host ''
            Write-Host 'Uncommitted config/default.json change:' -ForegroundColor Yellow
            git --no-pager diff -- config/default.json
            Write-Host ''
        } else {
            Write-Host ''
            Write-Host 'No uncommitted config/default.json change - checking whether there are already' -ForegroundColor DarkYellow
            Write-Host 'committed-but-unpushed commits, or the live server just needs a re-pull...' -ForegroundColor DarkYellow
        }

        $confirm = Read-Host -Prompt "This will commit+push config/default.json (if changed) and run 'git pull && pm2 restart $LanPm2Name' on the LIVE server ($LanServerHost) via SSH. Continue? (y/N)"
        if ($confirm -ne 'y' -and $confirm -ne 'Y') {
            Write-Host 'Cancelled - nothing pushed, live server untouched.' -ForegroundColor DarkYellow
            return
        }

        if ($hasLocalChange) {
            git add config/default.json
            git commit -m 'Update server config (via dev-menu)' | Out-Null
            Write-Host 'Committed locally.' -ForegroundColor Green
        }

        Write-Host 'Pushing to origin/master...' -ForegroundColor Yellow
        git push origin master
        if ($LASTEXITCODE -ne 0) {
            Write-Host 'git push failed - stopping before touching the live server. See error above.' -ForegroundColor Red
            return
        }

        Write-Host ''
        Write-Host "Deploying on $LanServerHost (git pull + pm2 restart $LanPm2Name)..." -ForegroundColor Yellow
        $sshArgs = @('-i', $LanSshKeyPath, '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=10',
            "$LanServerUser@$LanServerHost",
            "cd $LanServerRepoPath && git pull && pm2 restart $LanPm2Name")
        & ssh @sshArgs
        if ($LASTEXITCODE -ne 0) {
            Write-Host 'SSH deploy command failed or returned non-zero - see output above.' -ForegroundColor Red
            Write-Host 'If auth failed with "error in libcrypto", the key file may have picked up' -ForegroundColor DarkYellow
            Write-Host 'CRLF line endings - see the lan-portal-deploy skill for the fix.' -ForegroundColor DarkYellow
            return
        }

        Write-Host ''
        Write-Host 'Verifying deployed cpu/gpu duration values...' -ForegroundColor Yellow
        & ssh -i $LanSshKeyPath -o BatchMode=yes -o ConnectTimeout=10 "$LanServerUser@$LanServerHost" `
            "grep -A3 '`"cpu`"' $LanServerRepoPath/config/default.json; grep -A3 '`"gpu`"' $LanServerRepoPath/config/default.json"

        Write-Host ''
        Write-Host 'Deployed - the live server is now running the updated config.' -ForegroundColor Green
    } finally {
        Pop-Location
    }
}

function Set-CpuTestDuration {
    if (-not (Test-Path $ServerConfigPath)) {
        Write-Host "Server config not found at $ServerConfigPath" -ForegroundColor Red
        Write-Host 'Expected Luxtronic-PCTools-Server checked out as a sibling directory next to this repo.' -ForegroundColor DarkYellow
        return
    }

    $text = Get-Content $ServerConfigPath -Raw
    $cfg = $text | ConvertFrom-Json
    Write-Host "Current CPU test duration: $($cfg.cpu.duration_minutes) minute(s)" -ForegroundColor Yellow
    Write-Host 'This edits config/default.json in the LOCAL Luxtronic-PCTools-Server checkout only -' -ForegroundColor DarkYellow
    Write-Host 'only cpu.duration_minutes is touched.' -ForegroundColor DarkYellow
    Write-ServerConfigDeployWarning
    $raw = Read-Host -Prompt 'New CPU test duration in minutes (blank to cancel)'
    if ([string]::IsNullOrWhiteSpace($raw)) {
        Write-Host 'Cancelled.' -ForegroundColor DarkYellow
        return
    }

    $minutes = 0
    if (-not [int]::TryParse($raw, [ref]$minutes) -or $minutes -le 0) {
        Write-Host 'Enter a positive whole number of minutes.' -ForegroundColor Red
        return
    }

    # Targeted regex substitution instead of a full ConvertTo-Json round-trip, so the rest of the
    # file's hand-written formatting (2-space indent, etc.) is left untouched - PowerShell 5.1's
    # ConvertTo-Json indents nested objects in a way that's hard to read after a round-trip.
    $pattern = '("cpu"\s*:\s*\{[^{}]*"duration_minutes"\s*:\s*)\d+'
    if ($text -notmatch $pattern) {
        Write-Host 'Could not find cpu.duration_minutes in the expected shape - leaving the file untouched.' -ForegroundColor Red
        return
    }
    $newText = $text -replace $pattern, "`${1}$minutes"
    Set-Content -Path $ServerConfigPath -Value $newText -NoNewline -Encoding utf8
    Write-Host "Set CPU test duration to $minutes minute(s) in $ServerConfigPath." -ForegroundColor Green

    $deployNow = Read-Host -Prompt 'Deploy this to the live LAN server now? (y/N)'
    if ($deployNow -eq 'y' -or $deployNow -eq 'Y') {
        Deploy-ServerConfigToLive
    } else {
        Write-Host 'Not deployed - use option 10 whenever you''re ready.' -ForegroundColor DarkYellow
    }
}

# Same shape as Set-CpuTestDuration, for gpu.duration_minutes instead of cpu.duration_minutes -
# not folded into one shared function, matching the rest of this codebase's preference for
# explicit per-component duplication (see TestSessionController's RunCpuTestSessionAsync/
# RunGpuTestSessionAsync/etc.) over a generic "component name" parameter for a handful of lines.
function Set-GpuTestDuration {
    if (-not (Test-Path $ServerConfigPath)) {
        Write-Host "Server config not found at $ServerConfigPath" -ForegroundColor Red
        Write-Host 'Expected Luxtronic-PCTools-Server checked out as a sibling directory next to this repo.' -ForegroundColor DarkYellow
        return
    }

    $text = Get-Content $ServerConfigPath -Raw
    $cfg = $text | ConvertFrom-Json
    Write-Host "Current GPU test duration: $($cfg.gpu.duration_minutes) minute(s)" -ForegroundColor Yellow
    Write-Host 'This edits config/default.json in the LOCAL Luxtronic-PCTools-Server checkout only -' -ForegroundColor DarkYellow
    Write-Host 'only gpu.duration_minutes is touched.' -ForegroundColor DarkYellow
    Write-ServerConfigDeployWarning
    $raw = Read-Host -Prompt 'New GPU test duration in minutes (blank to cancel)'
    if ([string]::IsNullOrWhiteSpace($raw)) {
        Write-Host 'Cancelled.' -ForegroundColor DarkYellow
        return
    }

    $minutes = 0
    if (-not [int]::TryParse($raw, [ref]$minutes) -or $minutes -le 0) {
        Write-Host 'Enter a positive whole number of minutes.' -ForegroundColor Red
        return
    }

    $pattern = '("gpu"\s*:\s*\{[^{}]*"duration_minutes"\s*:\s*)\d+'
    if ($text -notmatch $pattern) {
        Write-Host 'Could not find gpu.duration_minutes in the expected shape - leaving the file untouched.' -ForegroundColor Red
        return
    }
    $newText = $text -replace $pattern, "`${1}$minutes"
    Set-Content -Path $ServerConfigPath -Value $newText -NoNewline -Encoding utf8
    Write-Host "Set GPU test duration to $minutes minute(s) in $ServerConfigPath." -ForegroundColor Green

    $deployNow = Read-Host -Prompt 'Deploy this to the live LAN server now? (y/N)'
    if ($deployNow -eq 'y' -or $deployNow -eq 'Y') {
        Deploy-ServerConfigToLive
    } else {
        Write-Host 'Not deployed - use option 10 whenever you''re ready.' -ForegroundColor DarkYellow
    }
}

function Publish-SelfContained {
    Write-Host "Publishing a self-contained, single-file build to $PublishDir ..." -ForegroundColor Yellow
    Write-Host 'This bundles the .NET 8 runtime into the exe itself (PROJECT_PLAN.md section 4''s' -ForegroundColor DarkYellow
    Write-Host 'original "single self-contained executable" goal) - target PCs need nothing' -ForegroundColor DarkYellow
    Write-Host 'installed, not even the .NET runtime, only this repo''s own dev/build machine needs' -ForegroundColor DarkYellow
    Write-Host 'the SDK. Regular Build/dotnet run stay framework-dependent (faster, smaller) -' -ForegroundColor DarkYellow
    Write-Host 'self-contained is opt-in per publish via command-line flags, not a project default.' -ForegroundColor DarkYellow
    Write-Host ''

    & dotnet publish $AppCsproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $PublishDir
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'Publish failed.' -ForegroundColor Red
        return
    }

    # appsettings.json is marked <None Update> with CopyToOutputDirectory in the csproj, which
    # normally handles this - but that only fires when MSBuild's incremental build decides the
    # copy step needs to (re)run. Confirmed reproducible: delete just appsettings.json from an
    # existing $PublishDir and republish with no other change (bin/obj caches intact) - dotnet
    # reports "up-to-date for restore", skips the copy entirely, and the file stays missing. Copied
    # explicitly here instead, same reasoning as tools\prime95/tools\hwi/publish-assets below - not
    # relying on MSBuild's content pipeline for a file that must reliably end up in the publish
    # folder every time, regardless of what the incremental build cache thinks already happened.
    if (Test-Path $AppSettings) {
        Copy-Item -Path $AppSettings -Destination $PublishDir -Force
        Write-Host 'Copied appsettings.json into the published folder.' -ForegroundColor Green
    } else {
        Write-Host "WARNING: appsettings.json not found at $AppSettings - published folder has none." -ForegroundColor Red
    }

    # Copies the ENTIRE tools\ folder wholesale, not a per-subfolder allowlist. This used to copy
    # only tools\prime95 and tools\hwi individually, hardcoded - which meant tools\FurMark_win64,
    # tools\TestMem5, and tools\DiskSpd (added in later sessions, wiring up GPU/RAM/SSD)
    # were SILENTLY never copied to a published build, because nobody remembered to add a third,
    # fourth, fifth copy block here to match. Every tool subfolder is resolved relative to the exe
    # first, then by walking up parent directories as a dev-mode convenience (see README) - that
    # walk-up only finds anything because this repo checkout is nearby; a published folder copied
    # to another PC has no such parent repo, so every tool has to ship directly inside the
    # published folder instead. Copying tools\ as one unit means a future sixth wrapper ships
    # automatically too, with no fourth copy block to remember to add.
    if (Test-Path $ToolsDir) {
        $publishToolsDir = Join-Path $PublishDir 'tools'
        New-Item -ItemType Directory -Force -Path $publishToolsDir | Out-Null
        Copy-Item -Path (Join-Path $ToolsDir '*') -Destination $publishToolsDir -Recurse -Force
        Write-Host "Copied tools\ (prime95, hwi, FurMark_win64, TestMem5, DiskSpd - everything under tools\) into the published folder." -ForegroundColor Green
    } else {
        Write-Host "WARNING: $ToolsDir not found - published folder has no tools\ at all. Nothing will work." -ForegroundColor Red
    }

    # Per-tool presence check AFTER the bulk copy above - same source paths, so "present in the
    # repo" and "present in the publish output" are equivalent here. Kept as individual checks
    # (rather than folding into the bulk-copy step) purely so a technician publishing a build gets
    # a clear, specific "X is missing" list instead of having to go hunting through the published
    # folder themselves to find out which test won't work.
    $expectedTools = @(
        @{ Name = 'prime95.exe (CPU)'; Path = $Prime95Exe },
        @{ Name = 'HWiNFO64.exe (CPU/GPU sensors)'; Path = $HwiExe },
        @{ Name = 'furmark.exe (GPU)'; Path = $FurMarkExe },
        @{ Name = 'TM5.exe (RAM)'; Path = $TM5Exe },
        @{ Name = 'DiskSpd64.exe (SSD)'; Path = $DiskSpdExe }
    )
    foreach ($tool in $expectedTools) {
        if (Test-Path $tool.Path) {
            Write-Host "  [OK]   $($tool.Name) found." -ForegroundColor Green
        } else {
            Write-Host "  [WARN] $($tool.Name) NOT found at $($tool.Path) - that test won't work until it's dropped in." -ForegroundColor DarkYellow
        }
    }

    # Launch.ps1/Launch.bat (publish-assets\) run a pre-flight check (exe/prime95/apikey/
    # appsettings/server reachability/HWiNFO status) before starting the app, so a technician who
    # double-clicks Launch.bat on the target PC gets a clear "here's what's missing" instead of the
    # app either failing cryptically or - worse, the case that motivated adding this - coming up as
    # a blank unresponsive window with no diagnostic shown at all. Not part of dotnet publish's
    # output, so copied in explicitly, same as tools\ above.
    if (Test-Path $PublishAssetsDir) {
        Copy-Item -Path (Join-Path $PublishAssetsDir '*') -Destination $PublishDir -Recurse -Force
        Write-Host 'Copied Launch.ps1/Launch.bat (pre-flight check + launcher) into the published folder.' -ForegroundColor Green
    } else {
        Write-Host "WARNING: publish-assets\ not found at $PublishAssetsDir - published folder has no Launch.bat." -ForegroundColor DarkYellow
    }

    Write-Host ''
    Write-Host "Published to $PublishDir." -ForegroundColor Green
    Write-Host 'Before copying that folder to another PC, still needed there (not part of publish,' -ForegroundColor Yellow
    Write-Host 'same as every other build - see README "One-time local setup"):' -ForegroundColor Yellow
    Write-Host "  - apikey.txt - THAT technician's own key, never copy one technician's key folder-to-folder" -ForegroundColor Yellow
    Write-Host '  - appsettings.json ServerBaseUrl - already copied, but double-check it points at the real server' -ForegroundColor Yellow
    Write-Host '  - If HWiNFO was bundled: it still needs launching separately on the target PC, with' -ForegroundColor Yellow
    Write-Host '    Shared Memory Support enabled and restarted after enabling - copying the exe alone' -ForegroundColor Yellow
    Write-Host '    does not configure or start it (option 3 on this menu shows whether it''s active)' -ForegroundColor Yellow
    Write-Host '  - prime95/FurMark/TM5/DiskSpd need no separate setup beyond being present on disk -' -ForegroundColor Yellow
    Write-Host '    the [OK]/[WARN] list above already told you if any of those are missing from THIS' -ForegroundColor Yellow
    Write-Host '    machine''s tools\ folder (fix that here before publishing again, not on the target PC)' -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'Then copy the whole publish\win-x64 folder to the target PC and double-click' -ForegroundColor Green
    Write-Host 'Launch.bat from there - it checks prerequisites, then launches the app (which' -ForegroundColor Green
    Write-Host 'elevates itself via UAC) - no .NET install needed on that PC.' -ForegroundColor Green
}

function Open-ToolsFolder {
    New-Item -ItemType Directory -Force -Path $Prime95Dir | Out-Null
    New-Item -ItemType Directory -Force -Path $HwiDir | Out-Null
    Start-Process explorer.exe $ToolsDir
}

$running = $true
while ($running) {
    Write-Header
    Show-Status

    Write-Host 'What do you want to do?' -ForegroundColor Yellow
    Write-Host '  1) Build solution'
    Write-Host '  2) Run tests'
    Write-Host '  3) Refresh status (re-check prerequisites)'
    Write-Host '  4) Launch app - elevated (recommended, real sensor access)'
    Write-Host '  5) Launch app - dev mode (dotnet run, unelevated, fast iteration)'
    Write-Host '  6) Set/update technician API key'
    Write-Host '  7) Set server URL'
    Write-Host '  8) Set CPU test duration (server config)'
    Write-Host '  9) Set GPU test duration (server config)'
    Write-Host '  10) Deploy server config to live LAN server (push + restart)'
    Write-Host '  11) Open tools folder (prime95, hwi, FurMark, TestMem5, DiskSpd)'
    Write-Host '  12) Publish self-contained build (for PCs without .NET installed)'
    Write-Host '  13) Exit'
    Write-Host ''

    $choice = Read-Host -Prompt 'Choice'
    Write-Host ''

    switch ($choice) {
        '1' { Invoke-Build }
        '2' { Invoke-Tests }
        '3' { }
        '4' { Start-AppElevated }
        '5' { Start-AppDev }
        '6' { Set-ApiKey }
        '7' { Set-ServerUrl }
        '8' { Set-CpuTestDuration }
        '9' { Set-GpuTestDuration }
        '10' { Deploy-ServerConfigToLive }
        '11' { Open-ToolsFolder }
        '12' { Publish-SelfContained }
        '13' { $running = $false }
        default { Write-Host 'Not a valid choice.' -ForegroundColor Red }
    }

    if ($running) {
        Write-Host ''
        Read-Host -Prompt 'Press Enter to continue' | Out-Null
    }
}

Write-Host 'Bye.' -ForegroundColor Cyan
