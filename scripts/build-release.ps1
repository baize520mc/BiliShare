# Full release build: compile, stage (root cleanup), build installer, build portable zip.
$ErrorActionPreference = 'Stop'

$Root      = Split-Path -Parent $PSScriptRoot
$MsBuild   = 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe'
$InnoISCC  = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
$SevenZip  = '7z.exe'

$Version    = '1.0.0'
$OutDir     = Join-Path $Root 'dist'
$BuildOut   = Join-Path $Root 'src\BiliShare\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64'
$StageDir   = Join-Path $OutDir 'BiliShare'
$Csproj     = Join-Path $Root 'src\BiliShare\BiliShare.csproj'
$IssFile    = Join-Path $Root 'scripts\BiliShare.iss'

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

Write-Host ''
Write-Host 'DONE. Outputs:'
Write-Host ("  installer : {0}\BiliShare_Setup_{1}.exe" -f $OutDir, $Version)
Write-Host ("  zip       : {0}\BiliShare_{1}_portable.zip" -f $OutDir, $Version)
Write-Host ("  app folder: {0}" -f $StageDir)