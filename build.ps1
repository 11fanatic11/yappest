# Builds the Yappest installers.
#   powershell -ExecutionPolicy Bypass -File build.ps1            -> full installer (includes the VB-Cable driver pack, for private sharing)
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Lite      -> public/update installer (no driver pack; the app fetches it from vb-audio.com when needed)
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Both
param([switch]$Lite, [switch]$Both)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$version = ([xml](Get-Content "$root\src\Yappest\Yappest.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "Yappest $version"

# 1. icons
& powershell -NoProfile -ExecutionPolicy Bypass -File "$root\streamdeck\tools\make-icons.ps1"
& powershell -NoProfile -ExecutionPolicy Bypass -File "$root\installer\make-ico.ps1"

# 2. self-contained build (no .NET install needed on the target PC)
dotnet publish "$root\src\Yappest\Yappest.csproj" -c Release -r win-x64 --self-contained true -o "$root\dist\Yappest" -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

# 3. installer(s) with Inno Setup
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found (winget install JRSoftware.InnoSetup)' }
if ($Lite -or $Both) { & $iscc "/DAppVersion=$version" /DLite "$root\installer\Yappest.iss"; if ($LASTEXITCODE -ne 0) { throw 'lite installer build failed' } }
if (-not $Lite -or $Both) { & $iscc "/DAppVersion=$version" "$root\installer\Yappest.iss"; if ($LASTEXITCODE -ne 0) { throw 'installer build failed' } }
Write-Host "Done: $root\installer\Output"
