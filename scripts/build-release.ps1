param(
    [switch]$SkipRelease
)

# Full release build: compile, stage (root cleanup), build installer, build portable zip.
# Optionally publish to GitHub Releases (requires GITHUB_TOKEN env var; use -SkipRelease to skip).
$ErrorActionPreference = 'Stop'

$Root      = Split-Path -Parent $PSScriptRoot
$MsBuild   = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
$InnoISCC  = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
$SevenZip  = '7z.exe'

$Version    = '1.1.1'
$OutDir     = Join-Path $Root 'dist'
$BuildOut   = Join-Path $Root 'src\BiliShare\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64'
$StageDir   = Join-Path $OutDir 'BiliShare'
$Csproj     = Join-Path $Root 'src\BiliShare\BiliShare.csproj'
$IssFile    = Join-Path $Root 'scripts\BiliShare.iss'

# GitHub release target (must match UpdateService.cs Owner/Repo)
$GitHubOwner = 'baize520mc'
$GitHubRepo  = 'BiliShare'
$NotesFile   = Join-Path $PSScriptRoot 'release-notes.md'

# =============================================================
# Publish to GitHub Releases (create release + upload installer & zip)
# =============================================================
function Publish-GitHubRelease {
    param(
        [string]$Owner,
        [string]$Repo,
        [string]$Version,
        [string]$OutDir,
        [string]$NotesFile
    )

    $Tag = "v$Version"
    $Token = $env:GITHUB_TOKEN
    if (-not $Token) { $Token = $env:GH_TOKEN }
    if (-not $Token) {
        Write-Warning 'GITHUB_TOKEN is not set; skipped publishing release.'
        return
    }

    $Api = "https://api.github.com/repos/$Owner/$Repo"
    $Headers = @{
        'Authorization' = "Bearer $Token"
        'Accept'        = 'application/vnd.github+json'
        'User-Agent'    = 'BiliShare-Build'
    }

    $Body = if (Test-Path $NotesFile) { [System.IO.File]::ReadAllText($NotesFile) } else { "BiliShare v$Version" }

    # Reuse an existing release tag, otherwise create a new one
    try {
        $release = Invoke-RestMethod -Uri "$Api/releases/tags/$Tag" -Headers $Headers -Method Get -ErrorAction Stop
        Write-Host ("  reuse existing release #{0}" -f $release.id)
    }
    catch {
        $payload = @{
            tag_name    = $Tag
            name        = "v$Version"
            body        = $Body
            draft       = $false
            prerelease  = $false
        } | ConvertTo-Json
        # Send UTF-8 bytes: Invoke-RestMethod would otherwise encode the string as Latin-1, mangling Chinese
        $createBytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
        $release = Invoke-RestMethod -Uri "$Api/releases" -Headers $Headers -Method Post -Body $createBytes -ContentType 'application/json'
        Write-Host ("  created release #{0}" -f $release.id)
    }

    # Always sync the body (self-heals a garbled body left by an earlier Latin-1 run)
    $patchJson = @{ body = $Body } | ConvertTo-Json
    $patchBytes = [System.Text.Encoding]::UTF8.GetBytes($patchJson)
    Invoke-RestMethod -Uri "$Api/releases/$($release.id)" -Headers $Headers -Method Patch -Body $patchBytes -ContentType 'application/json' | Out-Null

    # Upload two artifacts: installer + portable zip
    $assets = @(
        (Join-Path $OutDir ("BiliShare_Setup_{0}.exe" -f $Version)),
        (Join-Path $OutDir ("BiliShare_{0}_portable.zip" -f $Version))
    )

    foreach ($file in $assets) {
        if (-not (Test-Path $file)) {
            Write-Warning ("  missing asset, skip: {0}" -f (Split-Path $file -Leaf))
            continue
        }
        $name = Split-Path $file -Leaf

        # Delete an existing asset with the same name so the script is re-runnable
        try {
            $existing = Invoke-RestMethod -Uri "$Api/releases/$($release.id)/assets" -Headers $Headers -Method Get
            $dup = $existing | Where-Object { $_.name -eq $name }
            if ($dup) {
                Invoke-RestMethod -Uri $dup.url -Headers $Headers -Method Delete | Out-Null
                Write-Host ("  removed old asset {0}" -f $name)
            }
        }
        catch { }

        # Use WebClient.UploadFile to post the binary (works on Windows PowerShell 5.1)
        $uploadUrl = ($release.upload_url -split '\{')[0] + '?name=' + [uri]::EscapeDataString($name)
        $wc = New-Object System.Net.WebClient
        $wc.Headers.Add('Authorization', "Bearer $Token")
        $wc.Headers.Add('Accept', 'application/vnd.github+json')
        $wc.Headers.Add('User-Agent', 'BiliShare-Build')
        try {
            $wc.UploadFile($uploadUrl, 'POST', $file) | Out-Null
            Write-Host ("  uploaded {0}" -f $name)
        }
        finally {
            $wc.Dispose()
        }
    }
}

# 1) Compile Release
Write-Host '[1/4] Building Release ...'
& $MsBuild $Csproj /p:Configuration=Release /p:Platform=x64 /restore /t:Build /m /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "MSBuild failed (exit $LASTEXITCODE)" }

# 2) Stage clean folder
Write-Host '[2/4] Staging clean BiliShare folder ...'
# Deletes are extremely slow on this volume; rename the old folder (instant) and build fresh.
if (Test-Path $StageDir) {
    $trash = Join-Path $OutDir ('.trash_' + (Get-Date -Format 'yyyyMMdd_HHmmss_fff'))
    Rename-Item $StageDir $trash
}
New-Item -ItemType Directory -Force -Path $StageDir | Out-Null

# keep only zh/en language satellites (exclude the rest by directory name)
$keepDirs = @('zh-CN','zh-TW','en-us','en-GB','Assets','Views','wwwroot','Microsoft.UI.Xaml','NpuDetect')
$excludeDirs = Get-ChildItem $BuildOut -Directory | Where-Object { $_.Name -notin $keepDirs } | Select-Object -ExpandProperty Name

$robocopyArgs = @($BuildOut, $StageDir, '/E', '/MT:32', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/NC', '/R:1', '/W:1', '/XD') + $excludeDirs
& robocopy @robocopyArgs | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed (exit $LASTEXITCODE)" }

# remove debug symbols
Get-ChildItem $StageDir -Recurse -Filter '*.pdb' -ErrorAction SilentlyContinue | Remove-Item -Force

# 3) Build installer (default install dir = setup.exe folder\BiliShare via {src})
Write-Host '[3/4] Building installer (Inno Setup) ...'
& $InnoISCC $IssFile
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit $LASTEXITCODE)" }

# 4) Build portable zip
Write-Host '[4/4] Building portable zip ...'
$Zip = Join-Path $OutDir ("BiliShare_{0}_portable.zip" -f $Version)
if (Test-Path $Zip) { Remove-Item $Zip -Force }
& $SevenZip a -tzip $Zip (Join-Path $StageDir '*') | Out-Null
if ($LASTEXITCODE -ne 0) { throw "7z failed (exit $LASTEXITCODE)" }

# 5) Publish to GitHub Releases (optional)
if (-not $SkipRelease) {
    Write-Host '[5/5] Publishing GitHub release ...'
    Publish-GitHubRelease -Owner $GitHubOwner -Repo $GitHubRepo -Version $Version -OutDir $OutDir -NotesFile $NotesFile
}
else {
    Write-Host '[5/5] Skipping GitHub release (-SkipRelease).'
}

Write-Host ''
Write-Host 'DONE. Outputs:'
Write-Host ("  installer : {0}\BiliShare_Setup_{1}.exe" -f $OutDir, $Version)
Write-Host ("  zip       : {0}\BiliShare_{1}_portable.zip" -f $OutDir, $Version)
Write-Host ("  app folder: {0}" -f $StageDir)