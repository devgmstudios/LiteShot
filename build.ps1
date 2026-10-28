# Builds LiteShot into .\dist
#   dist\LiteShot.exe               portable and self-contained: runs without .NET installed
#   dist\LiteShot-Setup-<ver>.msi   per-user installer published by Devgm, LLC
#
#   .\build.ps1 -CertThumbprint <sha1>   also code-signs the exe and the installer with a certificate
#                                        from your CurrentUser\My store (avoids SmartScreen warnings)
#   .\build.ps1 -SkipInstaller           only the portable exe
#   .\build.ps1 -UpdateServer <url>      the update server built into the app (overrides LiteShot.csproj)
param([string]$CertThumbprint, [switch]$SkipInstaller, [string]$UpdateServer)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$app = Join-Path $dist 'app'
$version = ([xml](Get-Content (Join-Path $root 'src\LiteShot\LiteShot.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

# A running copy from dist would lock the exe.
if (Test-Path (Join-Path $dist 'LiteShot.exe')) { & (Join-Path $dist 'LiteShot.exe') --exit | Out-Null }
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

$publish = @('-c', 'Release', '-o', $app, '-p:SelfContained=true', '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-p:DebugType=none')
if ($UpdateServer) { $publish += "-p:UpdateServer=$UpdateServer" }
dotnet publish (Join-Path $root 'src\LiteShot\LiteShot.csproj') @publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

function Sign([string]$file) {
    if (-not $CertThumbprint) { return }
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe |
        Where-Object FullName -match '\\x64\\' | Select-Object -Last 1
    if (-not $signtool) { throw 'signtool.exe not found; install the Windows SDK.' }
    & $signtool.FullName sign /sha1 $CertThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /d LiteShot $file
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $file" }
}

Sign (Join-Path $app 'LiteShot.exe')
Copy-Item (Join-Path $app 'LiteShot.exe') (Join-Path $dist 'LiteShot.exe')

if (-not $SkipInstaller) {
    $wixproj = Join-Path $root 'installer\LiteShot.Installer.wixproj'
    dotnet build $wixproj -c Release -p:Version=$version -p:PublishDir=$app
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $msi = Join-Path $dist "LiteShot-Setup-$version.msi"
    $built = Get-ChildItem (Join-Path $root 'installer\bin') -Recurse -Filter LiteShot-Setup.msi | Sort-Object LastWriteTime | Select-Object -Last 1
    Copy-Item $built.FullName $msi
    Sign $msi
}

Get-ChildItem $dist -File | Format-Table Name, @{ n = 'Size (MB)'; e = { [math]::Round($_.Length / 1MB, 1) } }
