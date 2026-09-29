$ErrorActionPreference = 'Stop'
$work = Join-Path ([IO.Path]::GetTempPath()) ('afterimage-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$exe = Join-Path $work 'query.tests.exe'
& (Join-Path $PSScriptRoot 'build.ps1') -Tests -Output $exe
& $exe
if ($LASTEXITCODE -ne 0) { throw 'Afterimage logic tests failed.' }
# Keep the temporary binary for inspection; no recursive cleanup or journal mutation.
# CLI input errors must say which input was wrong, not only the generic INCONCLUSIVE reason.
$cli = Join-Path $work 'afterimage.exe'
& (Join-Path $PSScriptRoot 'build.ps1') -Output $cli
$cases = @(
    @(@('since', 'garbage', '--prefix', $work), 'not a valid timestamp'),
    @(@('between', '2026-09-17T10:02:00Z', '2026-09-17T10:00:00Z', '--prefix', $work), 'start .* is after end'),
    @(@('since', '2026-09-17T10:00:00Z', '--prefix'), 'missing value for --prefix'),
    @(@('since', '2026-09-17T10:00:00Z', '--prefix', $work, '--limit', 'x'), '--limit must be a positive integer'),
    @(@('since', '2026-09-17T10:00:00Z', '--prefix', $work, '--bogus'), 'unknown argument: --bogus'),
    @(@('since', '2026-09-17T10:00:00Z'), '--prefix is required'))
foreach ($c in $cases) {
    $err = & cmd /c "`"$cli`" $($c[0] -join ' ') 2>&1"
    if ($LASTEXITCODE -ne 2 -or ($err -join "`n") -notmatch 'INCONCLUSIVE' -or ($err -join "`n") -notmatch "detail  : .*$($c[1])") { throw "CLI input error case failed ($($c[0] -join ' ')): $err" }
}
Write-Output ('PASS cli-input-error-detail (' + $cases.Count + ')')
& (Join-Path $PSScriptRoot 'agent-window-cli.tests.ps1') -WorkRoot (Join-Path $work 'agent-window-cli')
