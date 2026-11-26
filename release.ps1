# Publishes a new Yappest version: builds the public ("lite") installer, uploads it as a GitHub release and
# updates update.json on yappest.fanaticdev.xyz (GitHub Pages). Installed copies see it the next time they start.
#
#   powershell -ExecutionPolicy Bypass -File release.ps1 -Notes "Fixed X","Added Y"
#
# Bump <Version> in src\Yappest\Yappest.csproj first.
param([Parameter(Mandatory)][string[]]$Notes, [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
# `powershell -File` hands "a","b" over as one string, so notes can also be separated with |
$Notes = @($Notes | ForEach-Object { $_ -split '\|' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$repo = '11fanatic11/yappest.fanaticdev.xyz'
$site = Join-Path $root 'yappest.fanaticdev.xyz'

$version = ([xml](Get-Content "$root\src\Yappest\Yappest.csproj")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$tag = "v$version"
Write-Host "Releasing Yappest $version"

if (-not $SkipBuild) { & powershell -NoProfile -ExecutionPolicy Bypass -File "$root\build.ps1" -Lite; if ($LASTEXITCODE -ne 0) { throw 'build failed' } }
$exe = Join-Path $root 'installer\Output\Yappest-Setup.exe'
if (-not (Test-Path $exe)) { throw "missing $exe" }
$sha = (Get-FileHash $exe -Algorithm SHA256).Hash
$size = (Get-Item $exe).Length

# 1. GitHub release with the installer attached
gh release create $tag $exe --repo $repo --title "Yappest $version" --notes (($Notes | ForEach-Object { "- $_" }) -join "`n")
if ($LASTEXITCODE -ne 0) { throw 'gh release failed' }

# 2. update feed
$feed = [ordered]@{
  version   = $version
  published = (Get-Date -Format 'yyyy-MM-dd')
  notes     = $Notes
  url       = "https://github.com/$repo/releases/download/$tag/Yappest-Setup.exe"
  sha256    = $sha
  size      = $size
}
[IO.File]::WriteAllText((Join-Path $site 'update.json'), ($feed | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding($false)))

Push-Location $site
git add -A
git commit -m "Yappest $version" | Out-Null
git push
Pop-Location
Write-Host "Published $version ($([math]::Round($size/1MB,1)) MB, sha256 $sha)"
