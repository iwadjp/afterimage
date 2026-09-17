param([string]$Output = (Join-Path $PSScriptRoot 'afterimage.exe'), [switch]$Tests)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Run with Windows PowerShell 5.1 and .NET Framework csc.exe available.' }
$sources = @((Join-Path $PSScriptRoot 'afterimage.cs'), (Join-Path $PSScriptRoot 'query.cs'))
$entry = '/main:Program'
if ($Tests) { $sources += (Join-Path $PSScriptRoot 'query.tests.cs'); $entry = '/main:QueryTests' }
& $compiler /nologo /optimize+ /target:exe $entry "/out:$Output" @sources
if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
