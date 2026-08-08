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

function Open-Prime95Folder {
    New-Item -ItemType Directory -Force -Path $Prime95Dir | Out-Null
    Start-Process explorer.exe $Prime95Dir
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
    Write-Host '  8) Open tools\prime95 folder'
    Write-Host '  9) Exit'
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
        '8' { Open-Prime95Folder }
        '9' { $running = $false }
        default { Write-Host 'Not a valid choice.' -ForegroundColor Red }
    }

    if ($running) {
        Write-Host ''
        Read-Host -Prompt 'Press Enter to continue' | Out-Null
    }
}

Write-Host 'Bye.' -ForegroundColor Cyan
