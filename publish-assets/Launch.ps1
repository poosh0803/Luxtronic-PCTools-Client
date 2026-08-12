<#
    Pre-flight check + launch script for a published Luxtronic PCTools build.

    This is what a technician double-clicks on a target PC (via Launch.bat, which bypasses
    PowerShell's default execution policy the same way dev-menu.bat does) - NOT dev-menu.ps1,
    which is a repo-only dev tool and isn't copied into the publish folder.

    Checks the specific things known to make Start fail, or the app come up unusable, before
    launching: the exe itself, prime95.exe, apikey.txt, appsettings.json validity, and whether the
    configured server is actually reachable right now. HWiNFO is actively started here (not just
    checked) before the server check, so its shared memory has time to come up before the app
    does - see the HWiNFO section below. It's still only ever reported as [--]/[OK], never a
    [FAIL] - it's an optional CPU temp/clock fallback for some hardware (see README "Known risk"),
    not something every machine needs.

    Deliberately does NOT elevate itself or pass -Verb RunAs when launching the exe - app.manifest
    already requests requireAdministrator, so Windows shows the UAC prompt on its own when the exe
    is launched directly. Doing both would either double-prompt or fight over which prompt wins.
#>

$ErrorActionPreference = 'Stop'

$Dir          = $PSScriptRoot
$ExePath      = Join-Path $Dir 'Luxtronic.PCTools.exe'
$AppSettings  = Join-Path $Dir 'appsettings.json'
$ApiKeyPath   = Join-Path $Dir 'apikey.txt'
$Prime95Exe   = Join-Path $Dir 'tools\prime95\prime95.exe'
$HwiExe       = Join-Path $Dir 'tools\hwi\HWiNFO64.exe'

$script:HardFail = $false

function Write-CheckOk($label) {
    Write-Host "  [OK]   $label" -ForegroundColor Green
}

function Write-CheckFail($label, $fix) {
    Write-Host "  [FAIL] $label" -ForegroundColor Red
    Write-Host "         $fix" -ForegroundColor Yellow
    $script:HardFail = $true
}

function Write-CheckWarn($label, $note) {
    Write-Host "  [WARN] $label" -ForegroundColor DarkYellow
    Write-Host "         $note" -ForegroundColor DarkYellow
}

function Write-CheckInfo($label) {
    Write-Host "  [--]   $label" -ForegroundColor DarkYellow
}

Write-Host '========================================================' -ForegroundColor Cyan
Write-Host '  Luxtronic PCTools - Pre-flight Check' -ForegroundColor Cyan
Write-Host '========================================================' -ForegroundColor Cyan
Write-Host ''

if (Test-Path $ExePath) {
    Write-CheckOk "App found: $ExePath"
} else {
    Write-CheckFail "App exe not found at $ExePath" `
        'This publish folder is incomplete or was copied wrong - re-copy the whole published folder.'
}

if (Test-Path $Prime95Exe) {
    Write-CheckOk 'prime95.exe found'
} else {
    Write-CheckFail 'prime95.exe not found' `
        "Drop the real prime95.exe into $(Join-Path $Dir 'tools\prime95\') before running a CPU test (see tools/prime95/README.md - not auto-downloaded)."
}

if (Test-Path $ApiKeyPath) {
    Write-CheckOk 'apikey.txt found'
} else {
    Write-CheckFail 'apikey.txt not found' `
        "Create $ApiKeyPath containing this technician's own API key (issued server-side) as its only contents. Never copy another technician's key."
}

$settings = $null
if (Test-Path $AppSettings) {
    try {
        $settings = Get-Content $AppSettings -Raw | ConvertFrom-Json
        Write-CheckOk "appsettings.json is valid (ServerBaseUrl = $($settings.ServerBaseUrl))"
    } catch {
        Write-CheckFail 'appsettings.json exists but is not valid JSON' `
            'Fix or replace appsettings.json - it must be valid JSON with at least a ServerBaseUrl field.'
    }
} else {
    Write-CheckFail 'appsettings.json not found' `
        'This publish folder is incomplete - re-copy the whole published folder.'
}

# HWiNFO is optional (CPU temp/clock fallback on hardware where LibreHardwareMonitorLib's own
# reads fail - see README "Known risk") - never a [FAIL], never blocks launch. Actively started
# here (not just checked) rather than left for the technician to remember: its shared-memory block
# only gets created once it's actually running, and tools\hwi\HWiNFO64.INI now has
# ShowWelcomeAndProgress=0 set, so it starts straight to the sensor window with nothing to click
# through - safe to launch unattended ahead of the app. Done before the server check below so its
# shared memory has a few seconds to come up while that check (and its own network round trip)
# runs, rather than racing the app's own startup.
if (Test-Path $HwiExe) {
    Write-CheckOk 'HWiNFO64.exe present'

    if (Get-Process -Name 'HWiNFO64' -ErrorAction SilentlyContinue) {
        Write-CheckOk 'HWiNFO64 already running'
    } else {
        try {
            Start-Process -FilePath $HwiExe -WorkingDirectory (Split-Path $HwiExe)
            Write-CheckOk 'Started HWiNFO64.exe'
        } catch {
            Write-CheckWarn 'Could not start HWiNFO64.exe' $_.Exception.Message
        }
    }

    # Poll rather than check once immediately - a freshly-started process needs a moment before
    # its first sensor pass creates the shared-memory block.
    $hwiActive = $false
    for ($i = 0; $i -lt 15 -and -not $hwiActive; $i++) {
        try {
            Add-Type -AssemblyName System.Core -ErrorAction SilentlyContinue
            $mmf = [System.IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting(
                'Global\HWiNFO_SENS_SM2', [System.IO.MemoryMappedFiles.MemoryMappedFileRights]::Read)
            $mmf.Dispose()
            $hwiActive = $true
        } catch {
            Start-Sleep -Milliseconds 300
        }
    }

    if ($hwiActive) {
        Write-CheckOk 'HWiNFO shared memory active (fallback available if CPU temp/clock ever needs it)'
    } else {
        Write-CheckInfo 'HWiNFO shared memory not active yet - only matters if this app''s own CPU temp/clock reads come up blank. If so, check HWiNFO''s own window for errors, or that Settings > Shared Memory Support is enabled.'
    }
} else {
    Write-CheckInfo 'HWiNFO64.exe not present - optional, only needed as a CPU temp/clock fallback on some hardware.'
}

# Best-effort TCP reachability check, not a hard requirement - the app itself will report a clear
# error if Start is clicked and the server can't be reached, so an unreachable server here is a
# warning (something to notice before starting a test), not a reason to block launch entirely.
if ($settings -and $settings.ServerBaseUrl) {
    try {
        $uri = [Uri]$settings.ServerBaseUrl
        $port = if ($uri.Port -gt 0) { $uri.Port } else { 80 }
        $tcp = Test-NetConnection -ComputerName $uri.Host -Port $port -WarningAction SilentlyContinue -InformationLevel Quiet
        if ($tcp) {
            Write-CheckOk "Server reachable at $($uri.Host):$port"
        } else {
            Write-CheckWarn "Server NOT reachable at $($uri.Host):$port right now" `
                'Starting a test will fail until the server is reachable - check the LAN connection or whether the server is running.'
        }
    } catch {
        Write-CheckWarn 'Could not test server reachability' $_.Exception.Message
    }
}

Write-Host ''

if ($script:HardFail) {
    Write-Host 'One or more required items are missing - fix the [FAIL] item(s) above, then run this again.' -ForegroundColor Red
    Read-Host -Prompt 'Press Enter to exit' | Out-Null
    exit 1
}

Write-Host 'All required checks passed. Launching Luxtronic PCTools...' -ForegroundColor Green
Write-Host '(A UAC prompt is expected - the app always requests administrator rights for sensor access.)' -ForegroundColor DarkGray
try {
    Start-Process -FilePath $ExePath -WorkingDirectory $Dir
} catch {
    Write-Host "Failed to launch: $($_.Exception.Message)" -ForegroundColor Red
    Read-Host -Prompt 'Press Enter to exit' | Out-Null
    exit 1
}
