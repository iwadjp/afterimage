param([Parameter(Mandatory = $true)][string]$Version, [Parameter(Mandatory = $true)][string]$OutDir)
# Build a portable release zip (exe + LICENSE + README) from sources identical to tag $Version.
$ErrorActionPreference = 'Stop'
$sources = @('afterimage.cs', 'query.cs', 'build.ps1')
& git -C $PSScriptRoot diff --quiet $Version -- @sources
if ($LASTEXITCODE -ne 0) { throw "Sources differ from tag $Version (or the tag is missing); refusing to package." }
if (Test-Path -LiteralPath $OutDir) { throw "OutDir must be a new path: $OutDir" }
$name = "afterimage-$Version-windows"
$stage = Join-Path $OutDir $name
New-Item -ItemType Directory -Path $stage | Out-Null
& (Join-Path $PSScriptRoot 'build.ps1') -Output (Join-Path $stage 'afterimage.exe')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE'), (Join-Path $PSScriptRoot 'README.md') -Destination $stage
$zip = Join-Path $OutDir "$name.zip"
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$hash  $name.zip`n")
"$hash  $zip"
