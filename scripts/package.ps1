<#
.SYNOPSIS
    Builds a shareable mod bundle under dist\.
.DESCRIPTION
    Produces a Thunderstore-format zip, which is also exactly what Gale and r2modman accept
    as a local mod import - so one artifact serves both testers and a real release.

    Layout inside the zip (all paths at the root, payload mirroring the profile):
        manifest.json, icon.png, README.md, CHANGELOG.md
        BepInEx/plugins/BigScreen/BigScreen.dll

    The zip is written with System.IO.Compression rather than Compress-Archive: Windows
    PowerShell 5.1 writes backslash path separators for nested entries, which is invalid per
    the ZIP spec and which mod managers can refuse or extract as one oddly-named file.

    Everything is checked before packing - version agreement across the three places that
    carry it, icon dimensions, payload presence - because a bundle that is wrong in one of
    those ways still zips cleanly and only fails once a tester tries to install it.
.PARAMETER Configuration
    Release (default) or Debug.
.PARAMETER IncludeYtDlp
    Bundle yt-dlp.exe (~17 MB) instead of letting the mod download it on first use. Useful
    for a tester whose network blocks the GitHub release download.
.PARAMETER SkipLibmpv
    Do NOT bundle mpv-2.dll (~60-80 MB). Bundling is the default because the libmpv backend
    is the one that actually plays MKV/HEVC torrent streams, and "install mpv yourself" is
    the step that makes a movie night fail to start.
.PARAMETER LibmpvPath
    A specific mpv-2.dll to bundle, instead of looking one up. Overrides the normal search.
.EXAMPLE
    .\scripts\package.ps1
    .\scripts\package.ps1 -IncludeYtDlp
    .\scripts\package.ps1 -SkipLibmpv -IncludeYtDlp
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$IncludeYtDlp,
    [switch]$SkipBuild,
    [switch]$SkipLibmpv,
    [string]$LibmpvPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\common.ps1"

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repo = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $repo 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

if (-not $SkipBuild) { & "$PSScriptRoot\build.ps1" -Configuration $Configuration }

# --- Gather and cross-check the version -------------------------------------------------
# CLAUDE.md requires these three to agree. Packaging is the last point where a mismatch can
# be caught cheaply: after this it ships, and the mod would log a different version than the
# one the package claims.
[xml]$proj = Get-Content (Join-Path $repo 'src\BigScreen\BigScreen.csproj')
$version = ($proj.Project.PropertyGroup.Version | Where-Object { $_ }) -as [string]
$desc    = ($proj.Project.PropertyGroup.Description | Where-Object { $_ }) -as [string]
if (-not $version) { throw "No <Version> in BigScreen.csproj." }

$pluginSrc = Get-Content (Join-Path $repo 'src\BigScreen\Plugin.cs') -Raw
if ($pluginSrc -notmatch 'Version\s*=\s*"([^"]+)"') { throw "Could not find Plugin.Version in Plugin.cs." }
$pluginVersion = $Matches[1]

$manifestPath = Join-Path $repo 'thunderstore\manifest.json'
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

if ($pluginVersion -ne $version) {
    throw "Version mismatch: Plugin.Version is '$pluginVersion' but the csproj says '$version'. Make them agree before packaging."
}
if ($manifest.version_number -ne $version) {
    Write-Host "manifest.json said $($manifest.version_number); updating to $version." -ForegroundColor DarkGray
}

# Thunderstore caps the description at 250 characters.
if ($desc -and $desc.Length -gt 250) { $desc = $desc.Substring(0, 250) }

# --- Warn if the tree does not match what is committed ----------------------------------
# A bundle handed to testers should be traceable to a commit; otherwise a bug report cannot
# be tied back to source.
$commit = '(unknown)'
try {
    $commit = (& git -C $repo rev-parse --short HEAD 2>$null)
    $dirty = (& git -C $repo status --porcelain 2>$null)
    if ($dirty) {
        Write-Host "WARNING: uncommitted changes - this bundle will not match any commit." -ForegroundColor Yellow
    }
} catch { }

# --- Stage ------------------------------------------------------------------------------
$stage = Join-Path $dist 'stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$payload = Join-Path $stage 'BepInEx\plugins\BigScreen'
New-Item -ItemType Directory -Force $payload | Out-Null

$dll = Join-Path $repo "src\BigScreen\bin\$Configuration\BigScreen.dll"
if (-not (Test-Path $dll)) { throw "Built DLL missing: $dll  (drop -SkipBuild, or build first)" }
Copy-Item $dll $payload -Force

if ($IncludeYtDlp) {
    # Reuse the copy the mod already downloaded into the dev profile rather than fetching
    # another one; it is the same binary from the same release.
    $ytdlp = $null
    try {
        $bep = Get-BepInExPath
        if ($bep) { $ytdlp = Join-Path $bep 'plugins\BigScreen\yt-dlp.exe' }
    } catch { }

    if ($ytdlp -and (Test-Path $ytdlp)) {
        Copy-Item $ytdlp $payload -Force
        Write-Host "bundled yt-dlp.exe ($([int]((Get-Item $ytdlp).Length / 1MB)) MB)" -ForegroundColor DarkGray
    } else {
        Write-Host "WARNING: -IncludeYtDlp given but no yt-dlp.exe found; the mod will download it on first use." -ForegroundColor Yellow
    }
}

# --- libmpv ---------------------------------------------------------------------------
# Bundled by default. libmpv is the only backend here that can play what Torbox hands over,
# which is most often MKV + HEVC: Unity's VideoPlayer is limited to a muxed H.264/AAC MP4, so
# a player without it silently loses most of the library. The mod will also look on the
# system PATH, but shipping it is what makes the first run work for everyone.
$bundledLibmpv = $false
if ($SkipLibmpv) {
    Write-Host "-SkipLibmpv given: not bundling mpv-2.dll. Linux players still need system libmpv." -ForegroundColor DarkGray
} else {
    $mpv = ''
    if ($LibmpvPath) {
        if (-not (Test-Path $LibmpvPath)) { throw "-LibmpvPath '$LibmpvPath' does not exist." }
        $mpv = (Resolve-Path $LibmpvPath).Path
    } else {
        # 1. The repo-local stash. Keep the real binary out of git and download it once:
        #        deps\mpv-2.dll
        $localMpv = Join-Path $repo 'deps\mpv-2.dll'
        if (Test-Path $localMpv) { $mpv = $localMpv }
        if (-not $mpv) {
            # 2. A copy the mod already placed next to itself in the dev profile.
            try {
                $bep = Get-BepInExPath
                if ($bep) {
                    $profileMpv = Join-Path $bep 'plugins\BigScreen\mpv-2.dll'
                    if (Test-Path $profileMpv) { $mpv = $profileMpv }
                }
            } catch { }
        }
        if (-not $mpv) {
            # 3. This repo's own release asset, the same way CI gets build-refs.zip. Upload once:
            #        gh release create mpv --title "Windows libmpv" --notes "mpv-2.dll for packaging"
            #        gh release upload mpv deps\mpv-2.dll
            $ownerRepo = $env:GITHUB_REPOSITORY
            if (-not $ownerRepo) {
                # Outside Actions (where this is almost always run) GITHUB_REPOSITORY is empty,
                # so derive owner/repo from the origin remote instead.
                try {
                    $remote = (& git -C $repo remote get-url origin 2>$null)
                    if ($remote -match 'github\.com[:/]([^/]+/[^/]+?)(\.git)?/?$') { $ownerRepo = $Matches[1] }
                } catch { }
            }
            if ($ownerRepo) {
                $url = "https://github.com/$ownerRepo/releases/download/mpv/mpv-2.dll"
                $cache = Join-Path $dist 'mpv-2.dll'
                if (-not (Test-Path $cache)) {
                    Write-Host "mpv-2.dll not found locally; downloading from $url" -ForegroundColor DarkGray
                    try { Invoke-WebRequest -Uri $url -OutFile $cache } catch { }
                }
                if (Test-Path $cache) { $mpv = $cache }
            }
        }
    }

    if (-not $mpv) {
        throw @"
No mpv-2.dll found. Put one at deps\mpv-2.dll (or pass -LibmpvPath), or upload it as an
asset of the 'mpv' release:
    gh release create mpv --title "Windows libmpv" --notes "mpv-2.dll for packaging"
    gh release upload mpv deps\mpv-2.dll
Windows players can also get it themselves (see README); pass -SkipLibmpv to package without it.
"@
    }

    $mpvSize = (Get-Item $mpv).Length
    # A real mpv-2.dll is tens of MB. Anything tiny is an HTML error page, a stub, or a
    # truncated download, and it would load-fail at runtime in a way that looks like a bug.
    if ($mpvSize -lt 5MB) {
        throw "mpv-2.dll at $mpv is $([int]($mpvSize / 1MB)) MB - a real one is 50-100 MB. Refusing to bundle it."
    }
    Copy-Item $mpv $payload -Force
    $bundledLibmpv = $true
    Write-Host "bundled mpv-2.dll ($([int]($mpvSize / 1MB)) MB)" -ForegroundColor DarkGray
}

$manifest.version_number = $version
if ($desc) { $manifest.description = $desc }
# WriteAllText with an explicit no-BOM encoder, NOT Set-Content -Encoding UTF8: on Windows
# PowerShell 5.1 that flag means UTF-8 WITH a BOM, and Thunderstore rejects a manifest.json
# that starts with one. The rejection gives no reason, so this is expensive to rediscover.
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText(
    (Join-Path $stage 'manifest.json'),
    ($manifest | ConvertTo-Json -Depth 4),
    $utf8NoBom)

# The listing README is player-facing; the repo README is for developers.
Copy-Item (Join-Path $repo 'thunderstore\README.md') (Join-Path $stage 'README.md') -Force
if (Test-Path (Join-Path $repo 'CHANGELOG.md')) { Copy-Item (Join-Path $repo 'CHANGELOG.md') $stage -Force }

# Thunderstore requires the icon to be exactly 256x256; it rejects the upload otherwise.
$icon = Join-Path $repo 'thunderstore\icon.png'
if (-not (Test-Path $icon)) {
    & python (Join-Path $repo 'thunderstore\make-icon.py') $icon
    if ($LASTEXITCODE -ne 0) { throw "Icon generation failed (needs Python + Pillow, or drop a 256x256 icon.png in thunderstore\)." }
}
$img = [System.Drawing.Image]::FromFile($icon)
try {
    if ($img.Width -ne 256 -or $img.Height -ne 256) {
        throw "icon.png must be exactly 256x256; it is $($img.Width)x$($img.Height)."
    }
} finally { $img.Dispose() }
Copy-Item $icon (Join-Path $stage 'icon.png') -Force

# --- Pack -------------------------------------------------------------------------------
$zip = Join-Path $dist "BigScreen-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

# Entries are written one at a time with explicit forward-slash names. Neither
# Compress-Archive nor ZipFile.CreateFromDirectory can be used here: on .NET Framework, which
# is what Windows PowerShell 5.1 runs on, both emit Windows separators for nested entries.
# The ZIP spec requires '/', and a mod manager handed 'BepInEx\plugins\...' either rejects the
# package or extracts one file with a backslash in its name. The verification below fails the
# build if this ever regresses.
$stream = [System.IO.File]::Open($zip, [System.IO.FileMode]::Create)
try {
    $archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $stage -Recurse -File) {
            $relative = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
            $entry = $archive.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
            $target = $entry.Open()
            try {
                $source = [System.IO.File]::OpenRead($file.FullName)
                try { $source.CopyTo($target) } finally { $source.Dispose() }
            } finally { $target.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }

Remove-Item $stage -Recurse -Force

# --- Verify what was actually written ---------------------------------------------------
$required = @('manifest.json', 'icon.png', 'README.md', "BepInEx/plugins/BigScreen/BigScreen.dll")
if ($bundledLibmpv) { $required += "BepInEx/plugins/BigScreen/mpv-2.dll" }
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $names = $archive.Entries | ForEach-Object { $_.FullName }
    foreach ($r in $required) {
        if ($names -notcontains $r) { throw "Packaged zip is missing '$r'. Entries: $($names -join ', ')" }
    }
    # Backslashes here are the Compress-Archive bug this script exists to avoid.
    $bad = $names | Where-Object { $_ -like '*\*' }
    if ($bad) { throw "Zip contains backslash separators, which mod managers reject: $($bad -join ', ')" }
    $size = [int]((Get-Item $zip).Length / 1KB)
} finally { $archive.Dispose() }

Write-Host ""
if ($bundledLibmpv) {
    Write-Host ("Bundle is {0} MB - mpv-2.dll is most of it, and Thunderstore's own limit is 1 GB." -f `
        [int]((Get-Item $zip).Length / 1MB)) -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "packaged -> $zip  (${size} KB, commit $commit)" -ForegroundColor Green
Write-Host ""
Write-Host "Give testers the zip and these steps:" -ForegroundColor Cyan
Write-Host "  Gale      : Profile -> Import -> Local mod -> pick the zip"
Write-Host "  r2modman  : Settings -> Import local mod -> pick the zip"
Write-Host "  By hand   : unzip the BepInEx folder over the profile's BepInEx folder"
Write-Host ""
Write-Host "They need BepInExPack_IL2CPP in the profile; yt-dlp is$(if($IncludeYtDlp){' bundled'}else{' downloaded on first use'})." -ForegroundColor DarkGray
if (-not $bundledLibmpv) {
    Write-Host "mpv-2.dll is NOT in this bundle: Windows players without libmpv installed fall back to Unity's VideoPlayer (muxed MP4 only) and cannot play most Torbox streams." -ForegroundColor Yellow
}
Write-Host "To publish instead: https://thunderstore.io/c/big-walk/create/ (docs/MODDING-PRIMER.md, 'Publishing')."
