$ErrorActionPreference = 'Stop'
$work = Join-Path ([IO.Path]::GetTempPath()) ('afterimage-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$exe = Join-Path $work 'query.tests.exe'
& (Join-Path $PSScriptRoot 'build.ps1') -Tests -Output $exe
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Afterimage logic tests failed.' }
# Keep the temporary binary for inspection; no recursive cleanup or journal mutation.
