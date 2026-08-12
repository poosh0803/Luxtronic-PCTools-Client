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
$ToolsDir     = Join-Path $RepoRoot 'tools'
$PublishDir   = Join-Path $RepoRoot 'publish\win-x64'

# CPU test duration is server-owned config (CONTRACT.md section 3 - "config flows one direction:
# from server to client"), not a client setting. Assumes Luxtronic-PCTools-Server checked out as
# a sibling directory next to this repo, matching this machine's layout under \Documents\Github.
$ServerConfigPath = Join-Path $RepoRoot '..\Luxtronic-PCTools-Server\config\default.json'

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
        $mmf = [System.IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting('Global\HWiNFO_SENS_SM2')
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

    # HWiNFO is optional (CPU temp/clock fallback for hardware where LHM's own reads fail - see
    # README "Known risk") - "not found"/"not active" are yellow, not red, unlike prime95 which is
    # required for the CPU test to run at all.
    if (Test-Path $HwiExe) {
        Write-Host "  [OK]   HWiNFO64.exe found: $HwiExe" -ForegroundColor Green
    } else {
        Write-Host "  [--]   HWiNFO64.exe not found at $HwiDir (optional - only needed as a CPU" -ForegroundColor DarkYellow
        Write-Host '         temp/clock fallback on hardware where LHM''s own reads fail)' -ForegroundColor DarkYellow
    }
    if (Test-HwInfoSharedMemoryActive) {
        Write-Host '  [OK]   HWiNFO shared memory is active right now - fallback would work if LHM''s own CPU temp/clock reads failed' -ForegroundColor Green
    } else {
        Write-Host '  [--]   HWiNFO shared memory not active - fallback unavailable until HWiNFO is running with' -ForegroundColor DarkYellow
        Write-Host '         Shared Memory Support enabled (and restarted after enabling it - see HwInfoSensorReader.cs)' -ForegroundColor DarkYellow
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

function Set-CpuTestDuration {
    if (-not (Test-Path $ServerConfigPath)) {
        Write-Host "Server config not found at $ServerConfigPath" -ForegroundColor Red
        Write-Host 'Expected Luxtronic-PCTools-Server checked out as a sibling directory next to this repo.' -ForegroundColor DarkYellow
        return
    }

    $text = Get-Content $ServerConfigPath -Raw
    $cfg = $text | ConvertFrom-Json
    Write-Host "Current CPU test duration: $($cfg.cpu.duration_minutes) minute(s)" -ForegroundColor Yellow
    Write-Host 'This edits config/default.json in the Server repo directly - only cpu.duration_minutes' -ForegroundColor DarkYellow
    Write-Host 'is touched (gpu/ram/ssd are not wired up client-side yet). The server re-reads this' -ForegroundColor DarkYellow
    Write-Host 'file from disk on every request, so no server restart is needed.' -ForegroundColor DarkYellow
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

    # tools\prime95 is resolved relative to the exe first, then by walking up parent directories
    # as a dev-mode convenience (see README) - that walk-up only finds anything because this repo
    # checkout is nearby. A published folder copied to another PC has no such parent repo, so
    # prime95.exe has to ship directly inside the published folder instead.
    if (Test-Path $Prime95Exe) {
        $publishPrime95Dir = Join-Path $PublishDir 'tools\prime95'
        New-Item -ItemType Directory -Force -Path $publishPrime95Dir | Out-Null
        Copy-Item -Path (Join-Path $Prime95Dir '*') -Destination $publishPrime95Dir -Recurse -Force
        Write-Host "Copied tools\prime95\ (including prime95.exe) into the published folder." -ForegroundColor Green
    } else {
        Write-Host "prime95.exe not found at $Prime95Exe - published folder has no tools\prime95\." -ForegroundColor DarkYellow
        Write-Host "Drop prime95.exe into $PublishDir\tools\prime95\ before copying to another PC." -ForegroundColor DarkYellow
    }

    # HWiNFO is a standalone exe the technician launches separately (not something
    # Luxtronic.PCTools.exe resolves a path to itself, unlike prime95.exe) - still copied into the
    # published folder for convenience so the whole CPU temp/clock fallback setup travels with one
    # folder copy, same reasoning as prime95. Optional: only needed on hardware where LHM's own
    # reads fail, so its absence is a warning, not an error.
    if (Test-Path $HwiExe) {
        $publishHwiDir = Join-Path $PublishDir 'tools\hwi'
        New-Item -ItemType Directory -Force -Path $publishHwiDir | Out-Null
        Copy-Item -Path (Join-Path $HwiDir '*') -Destination $publishHwiDir -Recurse -Force
        Write-Host "Copied tools\hwi\ (including HWiNFO64.exe) into the published folder." -ForegroundColor Green
    } else {
        Write-Host "HWiNFO64.exe not found at $HwiExe - published folder has no tools\hwi\ (optional - see README ""Known risk"")." -ForegroundColor DarkYellow
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
    Write-Host ''
    Write-Host 'Then copy the whole publish\win-x64 folder to the target PC and run' -ForegroundColor Green
    Write-Host 'Luxtronic.PCTools.exe from there (elevated) - no .NET install needed on that PC.' -ForegroundColor Green
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
    Write-Host '  9) Open tools folder (prime95, hwi)'
    Write-Host '  10) Publish self-contained build (for PCs without .NET installed)'
    Write-Host '  11) Exit'
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
        '9' { Open-ToolsFolder }
        '10' { Publish-SelfContained }
        '11' { $running = $false }
        default { Write-Host 'Not a valid choice.' -ForegroundColor Red }
    }

    if ($running) {
        Write-Host ''
        Read-Host -Prompt 'Press Enter to continue' | Out-Null
    }
}

Write-Host 'Bye.' -ForegroundColor Cyan
