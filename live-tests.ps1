param([Parameter(Mandatory=$true)][string]$Exe,
      [Parameter(Mandatory=$true)][string]$WorkRoot)
$ErrorActionPreference = 'Stop'
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)
if (-not $admin) { throw 'Privileged live tests require an elevated PowerShell. No fixture created.' }
if (Test-Path -LiteralPath $WorkRoot) { throw 'WorkRoot must not already exist.' }
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
New-Item -ItemType Directory -Path $WorkRoot | Out-Null
$summary = [ordered]@{ privileged=$true; cases=@(); allPass=$false }
function Check($condition, $label) {
    $script:summary.cases += [pscustomobject]@{name=$label;pass=[bool]$condition}
    if (-not $condition) { throw "FAIL $label" }
}
function WriteSynthetic($path, $text) { [IO.File]::WriteAllText($path, $text, [Text.Encoding]::ASCII) }
function InFixture($path) {
    $full=[IO.Path]::GetFullPath($path)
    if (-not $full.StartsWith($WorkRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Path outside owned fixture.' }
    return $full
}
function Query($scope,$start,$end,$name) {
    $output = & $Exe between $start.ToString('o') $end.ToString('o') --prefix $scope --limit 1000
    $code=$LASTEXITCODE
    $output | Set-Content -Encoding utf8 (Join-Path $WorkRoot "$name.txt")
    $script:summary["${name}Exit"]=$code
    $script:summary["${name}Status"]=@($output | Where-Object {$_ -match '^status|^reason|^scan|^events'})
    return ($output -join "`n")
}
try {
    $demo=Join-Path $WorkRoot 'demo'; New-Item -ItemType Directory $demo | Out-Null
    & git -C $demo init -q
    if ($LASTEXITCODE -ne 0) { throw 'git init failed' }
    WriteSynthetic (Join-Path $demo 'config.txt') 'timeout=30'
    WriteSynthetic (Join-Path $demo 'notes.txt') 'synthetic notes'
    & git -C $demo -c core.autocrlf=false add -- config.txt notes.txt
    if ($LASTEXITCODE -ne 0) { throw 'fixture git add failed' }
    & git -C $demo -c user.name=Synthetic -c user.email=fixture@example.invalid -c core.hooksPath=NUL commit -q -m baseline
    if ($LASTEXITCODE -ne 0) { throw 'fixture baseline commit failed' }
    $original=[IO.File]::ReadAllBytes((Join-Path $demo 'config.txt'))
    Start-Sleep -Milliseconds 200
    $start=[DateTime]::UtcNow
    WriteSynthetic (Join-Path $demo 'transient.txt') 'synthetic transient'
    Remove-Item -LiteralPath (InFixture (Join-Path $demo 'transient.txt'))
    WriteSynthetic (Join-Path $demo 'config.txt') 'timeout=999'
    [IO.File]::WriteAllBytes((Join-Path $demo 'config.txt'),$original)
    Rename-Item -LiteralPath (InFixture (Join-Path $demo 'notes.txt')) -NewName notes-renamed.txt
    Rename-Item -LiteralPath (InFixture (Join-Path $demo 'notes-renamed.txt')) -NewName notes.txt
    $end=[DateTime]::UtcNow
    & git -C $demo diff --exit-code --quiet
    Check ($LASTEXITCODE -eq 0) 'public-demo-final-diff-clean'
    $status=& git -C $demo status --porcelain=v1
    Check (-not $status) 'public-demo-final-status-clean'
    # First Afterimage invocation is AFTER every operation and the clean-state check.
    $o=Query $demo $start $end 'demo'
    Check ($o -match '\[TRANSIENT\].*transient.txt') 'public-demo-transient'
    Check ($o -match '\[MODIFIED\].*config.txt') 'public-demo-modify-restore'
    Check ($o -match 'notes.txt -> notes-renamed.txt -> notes.txt') 'public-demo-rename-back'
    Check ($o -notmatch 'status  : UNAVAILABLE') 'public-demo-readable'

    $scope=Join-Path $WorkRoot 'scope'; $outside=Join-Path $WorkRoot 'outside'
    New-Item -ItemType Directory $scope,$outside | Out-Null
    $start=[DateTime]::UtcNow
    WriteSynthetic (Join-Path $scope 'file-a.txt') 'a'
    Rename-Item -LiteralPath (InFixture (Join-Path $scope 'file-a.txt')) -NewName file-b.txt
    $oldParent=Join-Path $scope 'old-parent'; New-Item -ItemType Directory $oldParent | Out-Null
    WriteSynthetic (Join-Path $oldParent 'child.txt') 'before'
    Rename-Item -LiteralPath (InFixture $oldParent) -NewName new-parent
    WriteSynthetic (Join-Path $scope 'new-parent\child.txt') 'after'
    WriteSynthetic (Join-Path $outside 'outside-before.txt') 'outside'
    Move-Item -LiteralPath (InFixture (Join-Path $outside 'outside-before.txt')) -Destination (InFixture (Join-Path $scope 'entered.txt'))
    WriteSynthetic (Join-Path $scope 'leaving.txt') 'inside'
    Move-Item -LiteralPath (InFixture (Join-Path $scope 'leaving.txt')) -Destination (InFixture (Join-Path $outside 'outside-after.txt'))
    $moving=Join-Path $outside 'moving-dir'; New-Item -ItemType Directory $moving | Out-Null
    WriteSynthetic (Join-Path $moving 'outside-child-before.txt') 'outside'
    Move-Item -LiteralPath (InFixture $moving) -Destination (InFixture (Join-Path $scope 'moving-dir'))
    WriteSynthetic (Join-Path $scope 'moving-dir\inside-child.txt') 'inside'
    Move-Item -LiteralPath (InFixture (Join-Path $scope 'moving-dir')) -Destination (InFixture (Join-Path $outside 'moved-away'))
    WriteSynthetic (Join-Path $outside 'moved-away\outside-child-after.txt') 'outside'
    WriteSynthetic (Join-Path $scope 'delete-old.txt') 'delete'
    Rename-Item -LiteralPath (InFixture (Join-Path $scope 'delete-old.txt')) -NewName delete-new.txt
    Remove-Item -LiteralPath (InFixture (Join-Path $scope 'delete-new.txt'))
    $end=[DateTime]::UtcNow
    $o=Query $scope $start $end 'boundaries'
    Check ($o.Contains('file-a.txt -> file-b.txt')) 'A-file-rename'
    Check ($o.Contains('pathCandidate=<prefix>\old-parent\child.txt') -and $o.Contains('pathCandidate=<prefix>\new-parent\child.txt')) 'B-parent-rename'
    Check ($o.Contains('historicalName=entered.txt') -and -not $o.Contains('outside-before.txt')) 'C-outside-to-inside'
    Check ($o.Contains('historicalName=leaving.txt') -and -not $o.Contains('outside-after.txt')) 'D-inside-to-outside'
    Check ($o.Contains('pathCandidate=<prefix>\moving-dir\inside-child.txt') -and -not $o.Contains('outside-child-before.txt') -and -not $o.Contains('outside-child-after.txt')) 'E-parent-scope-moves'
    Check ($o.Contains('delete-old.txt -> delete-new.txt') -and $o.Contains('historicalName=delete-new.txt')) 'F-rename-delete'
    Check ($o.Contains('currentResolvedPath=<outside-scope-redacted>')) 'current-path-outside-redacted'
    Check ($o -notmatch [regex]::Escape($WorkRoot)) 'absolute-fixture-path-not-output'
    $o=Query $demo ([DateTime]::new(2000,1,1,0,0,0,[DateTimeKind]::Utc)) ([DateTime]::new(2000,1,2,0,0,0,[DateTimeKind]::Utc)) 'negative'
    Check ($summary.negativeExit -ne 0 -and $o -match 'INCONCLUSIVE' -and $o -match 'requested-start-before-oldest-observable-record') 'negative-retention-gap'
    $summary.allPass=$true
} catch { $summary.error=$_.Exception.Message }
finally {
    $summary | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 (Join-Path $WorkRoot 'summary.json')
    $summary | ConvertTo-Json -Depth 5 | Write-Output
}
if (-not $summary.allPass) { exit 1 }
