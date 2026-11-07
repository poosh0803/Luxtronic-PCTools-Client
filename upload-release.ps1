<#
.SYNOPSIS
    Uploads the latest published client exe to the server's Publish page, with an automatic version.

.DESCRIPTION
    Takes publish\win-x64\Luxtronic.PCTools.exe (made by publish.bat) and uploads it as a new
    release (CONTRACT.md section 4, category "client"). The version is assigned automatically:
    the patch number of the newest release on the server plus one (2.0.3 -> 2.0.4), or the
    <Version> in the csproj when that is higher (bump it there for a minor/major release), or the
    csproj version when the server has no release yet. Nothing is uploaded when the exe is
    identical to a release already on the server.

.PARAMETER Server     Server base URL. Default: the live box.  e.g. -Server http://localhost:7777
.PARAMETER Version    Use this version instead of the automatic one.
.PARAMETER Notes      Release notes. Default: build date, git branch and commit.
.PARAMETER Build      Run publish.bat's dotnet publish first, so the upload is always the current code.
.PARAMETER DryRun     Show the version and notes that would be used, upload nothing.
.PARAMETER Force      Upload even if an identical exe is already published.
#>
[CmdletBinding()]
param(
    [string]$Server = 'http://192.168.68.255:7777',
    [string]$Version,
    [string]$Notes,
    [switch]$Build,
    [switch]$DryRun,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-RestMethod's progress bar is very slow in Windows PowerShell 5.1

$root = $PSScriptRoot
$csproj = Join-Path $root 'src\Luxtronic.PCTools\Luxtronic.PCTools.csproj'
$exe = Join-Path $root 'publish\win-x64\Luxtronic.PCTools.exe'
$Server = $Server.TrimEnd('/')

function Fail($message) {
    Write-Host "ERROR: $message" -ForegroundColor Red
    exit 1
}

function ConvertTo-Triple($text) {
    if ($text -match '^(\d+)\.(\d+)\.(\d+)$') {
        return [version]::new([int]$Matches[1], [int]$Matches[2], [int]$Matches[3])
    }
    return $null
}

# --- build (optional) -------------------------------------------------------------------------
if ($Build) {
    Write-Host 'Publishing the client...'
    & dotnet publish $csproj -c Release -r win-x64 --nologo -p:PublishProfile=Single
    if ($LASTEXITCODE -ne 0) { Fail 'dotnet publish failed (is the app still running? close it first).' }
}

if (-not (Test-Path $exe)) { Fail "Not found: $exe`nRun publish.bat first, or use -Build." }

$file = Get-Item $exe
$sha = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host ("Build:  {0}  ({1:N1} MB, built {2:yyyy-MM-dd HH:mm})" -f $file.FullName, ($file.Length / 1MB), $file.LastWriteTime)

# --- what is already on the server ------------------------------------------------------------
try {
    # Windows PowerShell 5.1 hands a JSON array back as ONE nested array object; the ForEach-Object
    # unrolls it so $releases is a flat list whatever the number of releases.
    $releases = @(Invoke-RestMethod -Uri "$Server/api/apps?category=client" -TimeoutSec 15 | ForEach-Object { $_ })
} catch {
    $hint = ''
    if ($_.Exception.Message -match '400') { $hint = "`nThe server probably does not have the Publish feature yet - deploy the latest server first." }
    Fail "Could not read the release list from $Server : $($_.Exception.Message)$hint"
}

$same = $releases | Where-Object { $_.file_sha256 -eq $sha } | Select-Object -First 1
if ($same -and -not $Force) {
    Write-Host "This exact build is already published as v$($same.version) - nothing to upload. (-Force to upload anyway)" -ForegroundColor Yellow
    exit 0
}

# --- version ----------------------------------------------------------------------------------
if (-not $Version) {
    $latest = $releases | Where-Object { $_.file_name } | Select-Object -First 1   # newest first
    $latestTriple = $null
    if ($latest) { $latestTriple = ConvertTo-Triple $latest.version }

    $projectText = $null
    $propertyGroups = ([xml](Get-Content $csproj -Raw)).Project.PropertyGroup
    foreach ($group in $propertyGroups) { if ($group.Version) { $projectText = [string]$group.Version; break } }
    $projectTriple = $null
    if ($projectText) { $projectTriple = ConvertTo-Triple $projectText }

    if ($latestTriple) {
        $next = [version]::new($latestTriple.Major, $latestTriple.Minor, $latestTriple.Build + 1)
        if ($projectTriple -and $projectTriple -gt $next) { $next = $projectTriple }
        $Version = $next.ToString()
    } elseif ($latest) {
        # The newest release has a custom (non x.y.z) version: fall back to a date-based one.
        $Version = (Get-Date).ToString('yyyy.MM.dd.HHmm')
    } elseif ($projectTriple) {
        $Version = $projectTriple.ToString()
    } else {
        $Version = '1.0.0'
    }
}

if ($releases | Where-Object { $_.version -ieq $Version }) { Fail "Version $Version is already published. Pass a different -Version." }

# --- notes ------------------------------------------------------------------------------------
if (-not $Notes) {
    $Notes = 'Built ' + $file.LastWriteTime.ToString('yyyy-MM-dd HH:mm')
    $git = Get-Command git -ErrorAction SilentlyContinue
    if ($git) {
        Push-Location $root
        try {
            $branch = (& git rev-parse --abbrev-ref HEAD 2>$null)
            $commit = (& git rev-parse --short HEAD 2>$null)
            $dirty = (& git status --porcelain --untracked-files=no 2>$null)
            if ($commit) {
                $Notes += " from $branch@$commit"
                if ($dirty) { $Notes += ' (with uncommitted changes)' }
            }
        } finally { Pop-Location }
    }
}

Write-Host "Server: $Server"
Write-Host "Version: $Version"
Write-Host "Notes:   $Notes"
if ($DryRun) { Write-Host 'Dry run - nothing uploaded.' -ForegroundColor Yellow; exit 0 }

# --- upload -----------------------------------------------------------------------------------
$body = @{ category = 'client'; name = 'Luxtronic PCTools'; version = $Version; description = $Notes } | ConvertTo-Json
try {
    $release = Invoke-RestMethod -Uri "$Server/api/apps" -Method Post -ContentType 'application/json' -Body $body
} catch {
    Fail "Could not create the release: $($_.Exception.Message)"
}

Write-Host 'Uploading...'
$url = "$Server/api/apps/$($release.id)/file?filename=$([uri]::EscapeDataString($file.Name))"
$responseFile = [System.IO.Path]::GetTempFileName()
try {
    # curl.exe (ships with Windows 10/11) streams the file and shows progress.
    & curl.exe --fail-with-body -sS -X PUT -H 'Content-Type: application/octet-stream' -T $exe -o $responseFile --progress-bar $url
    if ($LASTEXITCODE -ne 0) { throw "curl failed with exit code $LASTEXITCODE" }
    $uploaded = Get-Content $responseFile -Raw | ConvertFrom-Json
    if ($uploaded.file_sha256 -ne $sha) { throw 'the server stored a file with a different checksum' }
} catch {
    # Don't leave a file-less release holding the version number.
    try { Invoke-RestMethod -Uri "$Server/api/apps/$($release.id)" -Method Delete | Out-Null } catch { }
    Fail "Upload failed: $($_.Exception.Message)"
} finally {
    Remove-Item $responseFile -ErrorAction SilentlyContinue
}

Write-Host "Published v$Version ($('{0:N1}' -f ($file.Length / 1MB)) MB, checksum verified)." -ForegroundColor Green
Write-Host "$Server/publish.html"
