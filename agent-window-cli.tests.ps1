param([string]$SourceRoot = $PSScriptRoot,
      [string]$WorkRoot = (Join-Path ([IO.Path]::GetTempPath()) ('afterimage-window-cli-' + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $WorkRoot) { throw 'WorkRoot must not already exist.' }
New-Item -ItemType Directory -Path $WorkRoot | Out-Null
# Exercise the real CLI entry point, parsing, native-record decoder, query engine,
# path resolution, report and exit code. Only the Win32 adapter is replaced with
# deterministic retained records; this is not a privileged live-NTFS test.
$source = [IO.File]::ReadAllText((Join-Path $SourceRoot 'afterimage.cs'))
$adapter = [regex]::new('(?ms)^static class Native \{.*?^\}')
if ($adapter.Matches($source).Count -ne 1) { throw 'Expected one native adapter.' }
$fixture = @'
static class Native {
    static string scope;
    static bool Rotated { get { return Environment.GetEnvironmentVariable("AFTERIMAGE_TEST_ROTATED") == "1"; } }
    static readonly DateTime T = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    static readonly string[] Names = { "oldest.txt", "tie-a.txt", "tie-b.txt", "newest.txt" };
    public static SafeFileHandle CreateFileW(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t) {
        if (!n.StartsWith(@"\\.\")) scope = n;
        return new SafeFileHandle(new IntPtr(1000), false);
    }
    public static bool DeviceIoControl(SafeFileHandle h, uint code, byte[] input, int inl, byte[] output, int outl, out int ret, IntPtr ov) {
        if (code == 0x900F4) {
            BitConverter.GetBytes(7UL).CopyTo(output, 0);
            BitConverter.GetBytes(Rotated ? 256L : 64L).CopyTo(output, 8);
            BitConverter.GetBytes(1024L).CopyTo(output, 16);
            BitConverter.GetBytes(64L).CopyTo(output, 24);
            ret = 56; return true;
        }
        if (code != 0x900BB) throw new Exception("unexpected fixture read control");
        BitConverter.GetBytes(1024L).CopyTo(output, 0);
        int off = 8;
        long[] usns = { 64, 256, 384, 512 };
        int[] seconds = { 0, 10, 10, 20 };
        for (int i = Rotated ? 1 : 0; i < Names.Length; i++) {
            byte[] name = Encoding.Unicode.GetBytes(Names[i]);
            int length = (60 + name.Length + 7) & ~7;
            BitConverter.GetBytes(length).CopyTo(output, off);
            BitConverter.GetBytes((ushort)2).CopyTo(output, off + 4);
            BitConverter.GetBytes((ulong)(10 + i)).CopyTo(output, off + 8);
            BitConverter.GetBytes(2UL).CopyTo(output, off + 16);
            BitConverter.GetBytes(usns[i]).CopyTo(output, off + 24);
            BitConverter.GetBytes(T.AddSeconds(seconds[i]).ToFileTimeUtc()).CopyTo(output, off + 32);
            BitConverter.GetBytes(0x100u).CopyTo(output, off + 40);
            BitConverter.GetBytes((ushort)name.Length).CopyTo(output, off + 56);
            BitConverter.GetBytes((ushort)60).CopyTo(output, off + 58);
            name.CopyTo(output, off + 60);
            off += length;
        }
        ret = off; return true;
    }
    public static SafeFileHandle OpenFileById(SafeFileHandle hint, ref FILE_ID_DESCRIPTOR id, uint access, uint share, IntPtr sa, uint flags) {
        return new SafeFileHandle(new IntPtr(id.FileId), false);
    }
    public static uint GetFinalPathNameByHandleW(SafeFileHandle h, StringBuilder sb, uint len, uint flags) {
        long id = h.DangerousGetHandle().ToInt64();
        string value = id == 2 ? scope : Path.Combine(scope, Names[(int)id - 10]);
        sb.Append(value); return (uint)value.Length;
    }
}
'@
$sourcePath = Join-Path $WorkRoot 'afterimage.fixture.cs'
[IO.File]::WriteAllText($sourcePath, $adapter.Replace($source, $fixture))
$exe = Join-Path $WorkRoot 'afterimage.fixture.exe'
$compiler = Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe'
& $compiler /nologo /optimize+ /target:exe /main:Program "/out:$exe" $sourcePath (Join-Path $SourceRoot 'query.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fixture CLI compilation failed.' }
$scope = Join-Path $WorkRoot 'scope'
New-Item -ItemType Directory -Path $scope | Out-Null
$t = [datetime]::new(2026, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
$cases = @(
    @('normal-complete', '2026-01-01T00:00:05Z', 0, 4, 'COMPLETE', $false),
    @('offset-retention-gap', '2026-01-01T09:00:04.999+09:00', -1, 4, 'PARTIAL', $false),
    @('exact-boundary', '2026-01-01T09:00:05+09:00', 0, 4, 'COMPLETE', $false),
    @('boundary-plus-1ms', '2026-01-01T09:00:05.001+09:00', 1, 3, 'COMPLETE', $false),
    @('same-instant-z', '2026-01-01T00:00:04.999Z', -1, 4, 'PARTIAL', $false),
    @('same-instant-midnight', '2025-12-31T19:00:04.999-05:00', -1, 4, 'PARTIAL', $false),
    @('repeat-unchanged', '2026-01-01T09:00:05+09:00', 0, 4, 'COMPLETE', $false),
    @('repeat-rotated', '2026-01-01T09:00:05+09:00', 0, 3, 'PARTIAL', $true),
    @('identical-timestamp-boundary', '', 10000, 2, 'COMPLETE', $false))
$truth = @()
$priorRotated = [Environment]::GetEnvironmentVariable('AFTERIMAGE_TEST_ROTATED')
try {
    foreach ($c in $cases) {
        $env:AFTERIMAGE_TEST_ROTATED = [int]$c[5]
        $start = $t.AddMilliseconds($c[2]); $end = $t.AddSeconds(20)
        if ($c[0] -eq 'identical-timestamp-boundary') {
            $end = $start
            $cliArgs = @('between', '2026-01-01T00:00:10Z', '2026-01-01T09:00:10+09:00', '--prefix', $scope)
        } else {
            $log = Join-Path $WorkRoot ($c[0] + '.jsonl')
            [IO.File]::WriteAllText($log, ('{"timestamp":"' + $c[1] + '"}' + "`n" + '{"timestamp":"2026-01-01T00:00:15Z"}'))
            $cliArgs = @('agent', $log, '--prefix', $scope)
        }
        $output = (& $exe @cliArgs) -join "`n"
        $exitCode = $LASTEXITCODE
        [IO.File]::WriteAllText((Join-Path $WorkRoot ($c[0] + '.txt')), $output)
        $oldest = if ($c[5]) { $t.AddSeconds(10) } else { $t }
        $expectedExit = if ($c[4] -eq 'COMPLETE') { 0 } else { 3 }
        $expectedWindow = 'window  : ' + $start.ToString('o') + ' .. ' + $end.ToString('o') + ' UTC'
        $ok = $exitCode -eq $expectedExit -and $output.Contains('status  : ' + $c[4]) -and
            $output.Contains($expectedWindow) -and $output.Contains('oldest  : ' + $oldest.ToString('o')) -and
            $output.Contains('events  : observedInScope=' + $c[3] + ' files=' + $c[3] + ' unresolvedScope=0')
        if ($c[4] -eq 'PARTIAL') {
            $ok = $ok -and $output.Contains('requested-start-before-oldest-observable-record') -and
                $output.Contains('next    : ') -and $output.Contains('rerunning cannot recover it')
        } else { $ok = $ok -and -not $output.Contains('next    : ') }
        $truth += [pscustomobject]@{case=$c[0];queryStart=$start.ToString('o');queryEnd=$end.ToString('o');
            retainedOldest=$oldest.ToString('o');retainedNewest=$t.AddSeconds(20).ToString('o');
            availableEventSeconds=$(if ($c[5]) { @(10,10,20) } else { @(0,10,10,20) });
            expectedCount=$c[3];expectedVerdict=$c[4];expectedExit=$expectedExit;
            expectedNextAction=$(if ($expectedExit) { 'retention gap cannot be recovered; use a later start' } else { 'none' });pass=[bool]$ok}
        if (-not $ok) { throw "FAIL CLI $($c[0]): exit=$exitCode`n$output" }
        Write-Output ('PASS cli-window-' + $c[0])
    }
} finally {
    [Environment]::SetEnvironmentVariable('AFTERIMAGE_TEST_ROTATED', $priorRotated)
    $truth | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $WorkRoot 'truth-set.json')
}
Write-Output ('PASS cli-agent-window (' + $truth.Count + ')')
