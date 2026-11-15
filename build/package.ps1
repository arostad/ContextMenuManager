<#
  Builds the Context Menu Manager release assets into -OutDir (outside the repo):
    ContextMenuManager.zip (+ .sha256)          portable copy, also the update artifact for portable copies
    ContextMenuManagerSetup.exe (+ .sha256)     per-user installer, also the update artifact for installed copies
    version.txt
  Usage: powershell -File build\package.ps1 -OutDir C:\Users\admin\cmm-build\0.1.2 [-Version 0.1.1] [-UpdateTestOverride]
  -Version and -UpdateTestOverride exist for local update testing only. Never publish their output.
#>
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [string]$Version = "",
    [switch]$UpdateTestOverride
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo "src\ContextMenuManager\ContextMenuManager.csproj"
$iscc = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
) | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) was not found." }

if (-not $Version) {
    $Version = ([xml](Get-Content -LiteralPath $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')." }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path
$app = Join-Path $OutDir "app"
if (Test-Path -LiteralPath $app) { Remove-Item -LiteralPath $app -Recurse -Force }

# MSBuild skips work when the clock looks behind the sources (carbon's clock runs ahead), so touch first.
Get-ChildItem (Join-Path $repo "src\ContextMenuManager") -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | ForEach-Object { $_.LastWriteTime = Get-Date }

$publish = @("publish", $project, "-c", "Release", "-r", "win-x64", "-o", $app, "-nologo", "-p:Version=$Version")
if ($UpdateTestOverride) { $publish += "-p:UpdateTestOverride=true" }
& dotnet @publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }
foreach ($required in "ContextMenuManager.exe", "ContextMenuManager.pri", "App.xbf", "MainWindow.xbf", "AboutDialog.xbf") {
    if (-not (Test-Path -LiteralPath (Join-Path $app $required))) { throw "Publish output is missing $required." }
}
$exe = Join-Path $app "ContextMenuManager.exe"
$productVersion = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
if ($productVersion -ne $Version) { throw "Exe ProductVersion is '$productVersion', expected '$Version'." }

function Write-Sidecar([string]$file) {
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    $line = "$hash  $(Split-Path -Leaf $file)`n"
    [IO.File]::WriteAllText("$file.sha256", $line, (New-Object Text.UTF8Encoding $false))
}

$zip = Join-Path $OutDir "ContextMenuManager.zip"
Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($app, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
Write-Sidecar $zip

$iss = Join-Path $repo "installer\ContextMenuManager.iss"
& $iscc "/Q" "/DAppVersion=$Version" "/DSourceDir=$app" "/DOutputDir=$OutDir" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed." }
$setup = Join-Path $OutDir "ContextMenuManagerSetup.exe"
Write-Sidecar $setup

[IO.File]::WriteAllText((Join-Path $OutDir "version.txt"), "$Version`n", (New-Object Text.UTF8Encoding $false))

$appBytes = (Get-ChildItem -LiteralPath $app -Recurse -File | Measure-Object Length -Sum).Sum
"{0}: app folder {1:N1} MB ({2} files), zip {3:N1} MB, installer {4:N1} MB" -f $Version, ($appBytes / 1MB),
    (Get-ChildItem -LiteralPath $app -Recurse -File).Count, ((Get-Item $zip).Length / 1MB), ((Get-Item $setup).Length / 1MB)