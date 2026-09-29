// Afterimage prototype: retroactively extract filesystem history for a time window from the NTFS USN journal.
// Does not mutate the journal or investigated files. Explicit --out writes a report.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

[StructLayout(LayoutKind.Explicit, Size = 24)]
struct FILE_ID_DESCRIPTOR { [FieldOffset(0)] public uint dwSize; [FieldOffset(4)] public int Type; [FieldOffset(8)] public long FileId; }

static class Native {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFileW(string n, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inb, int inl, byte[] outb, int outl, out int ret, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeFileHandle OpenFileById(SafeFileHandle hint, ref FILE_ID_DESCRIPTOR id, uint access, uint share, IntPtr sa, uint flags);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, StringBuilder sb, uint len, uint flags);
}
class Rec { public long Usn, Ts; public uint Reason, Attr; public ulong Frn, Parent; public string Name; }

class Journal : IJournal, IDisposable {
    const uint FSCTL_QUERY = 0x900F4, FSCTL_READ = 0x900BB, FSCTL_READ_UNPRIV = 0x903AB;
    public SafeFileHandle Dir;
    SafeFileHandle vol;
    uint code;
    public bool Privileged;
    public int Reads;
    public bool NamesAvailable { get { return Privileged; } }
    public static string Error(string operation, int error) {
        return operation + ":" + (error == 5 ? "access-denied" : error == 1179 ? "journal-not-active" :
            error == 1178 ? "journal-delete-in-progress" : error == 1181 ? "journal-entry-deleted" : "win32-" + error);
    }
    public static Journal Open(string dirPath) {
        var j = new Journal();
        j.Dir = Native.CreateFileW(dirPath, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (j.Dir.IsInvalid) throw new Exception(Error("open-scope", Marshal.GetLastWin32Error()));
        string drive = Path.GetPathRoot(Path.GetFullPath(dirPath)).TrimEnd('\\');
        var v = Native.CreateFileW(@"\\.\" + drive, 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (!v.IsInvalid) { j.vol = v; j.code = FSCTL_READ; j.Privileged = true; }
        else { v.Dispose(); j.vol = j.Dir; j.code = FSCTL_READ_UNPRIV; }
        return j;
    }
    public Snapshot Query() {
        var q = new byte[80]; int n;
        if (!Native.DeviceIoControl(vol, FSCTL_QUERY, null, 0, q, q.Length, out n, IntPtr.Zero))
            throw new Exception(Error("query", Marshal.GetLastWin32Error()));
        if (n < 56) throw new Exception("query:short-buffer");
        return new Snapshot { Id = BitConverter.ToUInt64(q, 0), First = BitConverter.ToInt64(q, 8),
            Next = BitConverter.ToInt64(q, 16), Lowest = BitConverter.ToInt64(q, 24), Taken = DateTime.UtcNow.ToFileTimeUtc() };
    }
    public Batch Read(long usn, ulong id) {
        var input = new byte[48];
        BitConverter.GetBytes(usn).CopyTo(input, 0);
        BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(input, 8);
        BitConverter.GetBytes(id).CopyTo(input, 32);
        BitConverter.GetBytes((ushort)2).CopyTo(input, 40);
        BitConverter.GetBytes((ushort)2).CopyTo(input, 42);
        var buffer = new byte[1 << 20]; int n;
        Reads++;
        if (!Native.DeviceIoControl(vol, code, input, input.Length, buffer, buffer.Length, out n, IntPtr.Zero))
            return new Batch { Error = Error("read", Marshal.GetLastWin32Error()) };
        return Decode(buffer, n);
    }
    internal static Batch Decode(byte[] buffer, int n) {
        var b = new Batch();
        if (n < 8 || n > buffer.Length) { b.Error = "read:invalid-buffer"; return b; }
        b.Next = BitConverter.ToInt64(buffer, 0);
        for (int off = 8; off < n;) {
            if (n - off < 60) { b.Error = "read:truncated-record"; break; }
            int len = BitConverter.ToInt32(buffer, off);
            if (len < 60 || len > n - off) { b.Error = "read:invalid-record-length"; break; }
            if (BitConverter.ToUInt16(buffer, off + 4) != 2) { b.Error = "read:unsupported-record-version"; break; }
            int nl = BitConverter.ToUInt16(buffer, off + 56), no = BitConverter.ToUInt16(buffer, off + 58);
            if ((nl & 1) != 0 || (nl > 0 && (no < 60 || no > len || nl > len - no))) {
                b.Error = "read:invalid-name-bounds"; break;
            }
            b.Records.Add(new Rec { Frn = BitConverter.ToUInt64(buffer, off + 8),
                Parent = BitConverter.ToUInt64(buffer, off + 16), Usn = BitConverter.ToInt64(buffer, off + 24),
                Ts = BitConverter.ToInt64(buffer, off + 32), Reason = BitConverter.ToUInt32(buffer, off + 40),
                Attr = BitConverter.ToUInt32(buffer, off + 52),
                Name = nl == 0 ? null : Encoding.Unicode.GetString(buffer, off + no, nl) });
            off += len;
        }
        return b;
    }
    public void Dispose() { if (vol != null && vol != Dir) vol.Dispose(); if (Dir != null) Dir.Dispose(); }
}

class FileHist {
    public ulong Frn; public List<Rec> Recs = new List<Rec>();
    public bool Has(uint bit) { foreach (var r in Recs) if ((r.Reason & bit) != 0) return true; return false; }
}

static class Program {
    const uint DATA = 0x77, CREATE = 0x100, DELETE = 0x200, REN_OLD = 0x1000, REN_NEW = 0x2000, CLOSE = 0x80000000;
    static Journal J;
    static Dictionary<ulong, string> currentCache = new Dictionary<ulong, string>();
    static readonly Regex Secretish = new Regex(@"(?i)(^\.env|secret|token|credential|passw|\.pem$|\.key$|\.pfx$|id_rsa|\.npmrc$)");

    static string CurrentPath(ulong frn) {
        string p;
        if (currentCache.TryGetValue(frn, out p)) return p;
        var d = new FILE_ID_DESCRIPTOR(); d.dwSize = 24; d.Type = 0; d.FileId = (long)frn;
        using (var h = Native.OpenFileById(J.Dir, ref d, 0x80, 7, IntPtr.Zero, 0x02000000)) {
            if (!h.IsInvalid) {
                var sb = new StringBuilder(1024);
                uint count = Native.GetFinalPathNameByHandleW(h, sb, 1024, 0);
                if (count > 0 && count < 1024) { p = sb.ToString(); if (p.StartsWith(@"\\?\")) p = p.Substring(4); }
            }
        }
        currentCache[frn] = p;
        return p;
    }
    static string Reasons(uint r) {
        var s = new List<string>();
        if ((r & CREATE) != 0) s.Add("CREATE"); if ((r & DATA) != 0) s.Add("MODIFY"); if ((r & REN_OLD) != 0) s.Add("RENAME_FROM");
        if ((r & REN_NEW) != 0) s.Add("RENAME_TO"); if ((r & DELETE) != 0) s.Add("DELETE"); if ((r & 0x8000) != 0) s.Add("attrs");
        if ((r & 0x800) != 0) s.Add("acl"); if ((r & 0x10000) != 0) s.Add("hardlink"); if ((r & 0x200000) != 0) s.Add("stream");
        return s.Count == 0 ? "0x" + r.ToString("x") : string.Join("+", s.ToArray());
    }
    static string Join(string a, string b) { return a.EndsWith("\\") ? a + b : a + "\\" + b; }
    static string T(long ft) { return DateTime.FromFileTimeUtc(ft).ToLocalTime().ToString("HH:mm:ss.fff"); }
    static string Mask(string name, bool show) { return (name != null && !show && Secretish.IsMatch(name)) ? "<secret-like name masked>" : name; }

    static int Main(string[] args) {
        try { return Run(args); } catch (Exception e) { Console.Error.WriteLine("status  : INCONCLUSIVE\nreason  : invalid-input-or-query-error (no completeness claim)\ndetail  : " + Detail(e)); return 2; } finally { if (J != null) J.Dispose(); }
    }

    static int Run(string[] args) {
        if (args.Length == 1 && args[0] == "--help") { Usage(); return 0; }
        if (args.Length < 1) { Usage(); return 1; }
        string prefix = null, outFile = null, logText = null; bool includeGit = false, showSecret = false; int limit = 200;
        DateTime start, end; int i;
        var culture = CultureInfo.InvariantCulture;
        if (args[0] == "between" && args.Length >= 3) {
            start = DateTime.Parse(args[1], culture, DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal);
            end = DateTime.Parse(args[2], culture, DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal); i = 3;
        } else if (args[0] == "since" && args.Length >= 2) {
            start = DateTime.Parse(args[1], culture, DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal); end = DateTime.UtcNow; i = 2;
        } else if (args[0] == "agent" && args.Length >= 2) {
            // Claude Code / Codex JSONL session log: window = first..last timestamp, scope = recorded cwd
            using (var fs = new FileStream(args[1], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) using (var sr = new StreamReader(fs)) logText = sr.ReadToEnd();
            AgentWindow(logText, out start, out end);
            var cwd = Regex.Match(logText, "\"cwd\":\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (cwd.Success) prefix = Regex.Unescape(cwd.Groups[1].Value);
            i = 2;
        } else { Usage(); return 1; }
        for (; i < args.Length; i++) {
            if (args[i] == "--prefix") prefix = Value(args, ++i);
            else if (args[i] == "--out") outFile = Value(args, ++i);
            else if (args[i] == "--include-git") includeGit = true;
            else if (args[i] == "--show-secret-names") showSecret = true;
            else if (args[i] == "--limit") { if (!int.TryParse(Value(args, ++i), out limit)) throw new Exception("--limit must be a positive integer"); }
            else throw new Exception("unknown argument: " + args[i]);
        }
        if (prefix == null) throw new Exception("--prefix is required (output never includes paths outside it)");
        prefix = NormalizePrefix(prefix);

        if (limit < 1) throw new Exception("--limit must be a positive integer");
        if (start > end) throw new Exception("start " + start.ToString("o") + " is after end " + end.ToString("o") + " (UTC)");
        try { J = Journal.Open(prefix); }
        catch (Exception e) { Console.WriteLine("status  : UNAVAILABLE\nreason  : " + e.Message); return 4; }
        var sw = Stopwatch.StartNew();
        var result = QueryEngine.Run(J, start.ToFileTimeUtc(), end.ToFileTimeUtc(), 20000000);
        if (result.Before == null) { Console.WriteLine("status  : UNAVAILABLE\nreason  : " + string.Join(", ", result.Issues.ToArray())); return 4; }
        var paths = new HistoryPaths(result.Directories, CurrentPath);
        var files = new Dictionary<ulong, FileHist>(); var order = new List<ulong>();
        var evidence = new Dictionary<long, PathEvidence>();
        int unresolved = 0, outside = 0, gitHidden = 0, matched = 0;
        foreach (var r in result.Events) {
            var p = paths.EventPath(r);
            if (p.Value == null) { unresolved++; continue; }
            if (!HistoryPaths.InScope(p.Value, prefix)) { outside++; continue; }
            string rel = Relative(p.Value, prefix);
            if (!includeGit && (rel.Equals(".git", StringComparison.OrdinalIgnoreCase) || rel.StartsWith(".git\\", StringComparison.OrdinalIgnoreCase))) { gitHidden++; continue; }
            matched++;
            FileHist fh;
            if (!files.TryGetValue(r.Frn, out fh)) { fh = new FileHist { Frn = r.Frn }; files[r.Frn] = fh; order.Add(r.Frn); }
            fh.Recs.Add(r); evidence[r.Usn] = p;
        }
        if (unresolved > 0) result.Issue("historical-scope-unresolved:" + unresolved);
        var body = new StringBuilder(); int shown = 0;
        foreach (var id in order) {
            if (shown++ >= limit) continue;
            var f = files[id]; var last = f.Recs[f.Recs.Count - 1];
            var chain = new List<string>();
            foreach (var r in f.Recs) if (chain.Count == 0 || chain[chain.Count - 1] != r.Name) chain.Add(r.Name);
            string kind = f.Has(CREATE) && f.Has(DELETE) ? "TRANSIENT" : f.Has(DELETE) ? "DELETED" : f.Has(CREATE) ? "CREATED" : f.Has(REN_OLD | REN_NEW) ? "RENAMED" : f.Has(DATA) ? "MODIFIED" : "METADATA";
            if (f.Has(REN_OLD | REN_NEW) && kind != "RENAMED") kind += "+RENAMED";
            string label = Attribution(last.Name, chain[0], logText, start, ProcessStart);
            string current = CurrentPath(id);
            body.AppendLine("[" + kind + "] <prefix>\\" + MaskPath(Relative(evidence[last.Usn].Value, prefix), showSecret) + "   " + label);
            body.AppendLine("    currentResolvedPath=" + (current == null ? "<unresolved-or-absent>" : HistoryPaths.InScope(current, prefix) ? "<prefix>\\" + MaskPath(Relative(current, prefix), showSecret) : "<outside-scope-redacted>"));
            if (chain.Count > 1) { var masked = new List<string>(); foreach (string n in chain) masked.Add(Mask(n, showSecret)); body.AppendLine("    name chain (in-scope records only): " + string.Join(" -> ", masked.ToArray())); }
            foreach (var r in f.Recs) body.AppendLine("    " + T(r.Ts) + " " + Reasons(r.Reason & ~CLOSE) +
                " historicalName=" + Mask(r.Name, showSecret) + " pathCandidate=<prefix>\\" + MaskPath(Relative(evidence[r.Usn].Value, prefix), showSecret) +
                " basis=" + evidence[r.Usn].Basis + " frn=" + r.Frn + " parentFrn=" + r.Parent + " usn=" + r.Usn);
        }
        QueryEngine.CheckSnapshot(J, result, paths.UsedCurrent || order.Count > 0);
        var o = new StringBuilder();
        o.AppendLine("Afterimage (read-only, NTFS USN journal)");
        o.AppendLine("status  : " + result.Status);
        o.AppendLine("reason  : " + (result.Issues.Count == 0 ? "retained-snapshot-read-complete" : string.Join(", ", result.Issues.ToArray())));
        o.AppendLine("mode    : " + (J.Privileged ? "privileged (names available)" : "unprivileged (historical names unavailable)"));
        o.AppendLine("window  : " + start.ToString("o") + " .. " + end.ToString("o") + " UTC");
        o.AppendLine("scope   : <prefix> (relative paths; outside-scope names redacted)");
        o.AppendLine("journal : FirstUsn=" + result.Before.First + " NextUsn=" + result.Before.Next + " LowestValidUsn=" + result.Before.Lowest + " journalId=" + result.Before.Id);
        o.AppendLine("oldest  : " + (result.Oldest == 0 ? "UNKNOWN" : DateTime.FromFileTimeUtc(result.Oldest).ToString("o")));
        o.AppendLine("scan    : records=" + result.Scanned + " reads=" + J.Reads + " elapsedMs=" + sw.ElapsedMilliseconds + " retainedIntervalRead=" + result.ReadComplete);
        o.AppendLine("events  : observedInScope=" + matched + " files=" + order.Count + " unresolvedScope=" + unresolved + " outside=" + outside + " gitFiltered=" + gitHidden);
        o.AppendLine("coverage: COMPLETE means the bounded retained-record query completed under observed bounds, not a complete operation history or proof of wall-clock coverage.");
        o.AppendLine("paths   : pathCandidate combines journal name/parent links and current anchors; it is not a certified historical full path. Current resolution is separate.");
        o.AppendLine("note    : labels are timing/name heuristics, NOT agent authorship or writer-PID proof. USN does not recover file contents.");
        if (matched == 0) o.AppendLine(result.Status == "COMPLETE" ? "result  : zero matching retained records (not proof of no activity)" : "result  : no matching records observed; query incomplete, activity UNKNOWN");
        foreach (string action in NextAction(result.Issues, result.Oldest == 0 ? "UNKNOWN" : DateTime.FromFileTimeUtc(result.Oldest).ToString("o"))) o.AppendLine("next    : " + action);
        o.Append(body);
        if (shown > limit) o.AppendLine("display : truncated " + (shown - limit) + " file groups; query status does not imply all groups were displayed (use --limit)");
        if (outFile != null) File.WriteAllText(outFile, o.ToString()); else Console.Write(o.ToString());
        return result.ExitCode;
    }
    internal static void AgentWindow(string logText, out DateTime start, out DateTime end) {
        // Collect every timestamp before parsing instants. Filtering for Z first
        // silently drops offset timestamps and can hide a retention gap.
        var ts = Regex.Matches(logText, "\"timestamp\"\\s*:\\s*\"([^\"]*)\"");
        if (ts.Count == 0) throw new Exception("no timestamps in log");
        DateTime mn = DateTime.MaxValue, mx = DateTime.MinValue;
        foreach (Match m in ts) {
            var d = DateTime.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
            if (d < mn) mn = d; if (d > mx) mx = d;
        }
        start = mn.AddSeconds(-5); end = mx.AddSeconds(5);
    }
    // Tells the user whether a non-COMPLETE status is fixable by them or a limit of the retained journal.
    internal static List<string> NextAction(List<string> issues, string oldest) {
        var next = new List<string>();
        if (issues.Contains("requested-start-before-oldest-observable-record"))
            next.Add("the window starts before the oldest retained record (" + oldest + "); activity before that is no longer in the journal, and rerunning cannot recover it. Use a start after it for a checkable window.");
        if (issues.Contains("insufficient-privilege-for-historical-names"))
            next.Add("historical names need volume access; rerun from an elevated (Administrator) PowerShell to resolve the unresolvedScope records against --prefix.");
        return next;
    }
    static string Value(string[] a, int k) { if (k >= a.Length || a[k].StartsWith("--")) throw new Exception("missing value for " + a[k - 1]); return a[k]; }
    static string Detail(Exception e) { return e is FormatException ? "not a valid timestamp (use ISO 8601, e.g. 2026-09-17T10:00:00Z)" : e.Message; }
    internal static string NormalizePrefix(string p) { p = Path.GetFullPath(p); return p.Length > Path.GetPathRoot(p).Length ? p.TrimEnd('\\') : p; }
    static string Relative(string p, string prefix) { return p.Length > prefix.Length ? p.Substring(prefix.TrimEnd('\\').Length + 1) : "."; }
    static string MaskPath(string p, bool show) { var parts = p.Split('\\'); for (int k = 0; k < parts.Length; k++) parts[k] = Mask(parts[k], show); return string.Join("\\", parts); }
    static DateTime? ProcessStart(int pid) { try { using (var p = Process.GetProcessById(pid)) return p.StartTime.ToUniversalTime(); } catch { return null; } }
    internal static string Attribution(string name, string firstName, string log, DateTime start, Func<int, DateTime?> lookup) {
        string label = "DURING_RUN";
        if (name != null && name.Length >= 4 && log != null && log.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) {
            label = "LIKELY_RELATED(name in agent log)";
            var m = Regex.Match(firstName ?? "", @"\.(\d{2,10})\.(\d{10,})\.tmp$", RegexOptions.IgnoreCase); int pid;
            if (m.Success && int.TryParse(m.Groups[1].Value, out pid)) {
                var t = lookup(pid);
                if (t.HasValue && t.Value < start) label = "BACKGROUND_LIKELY(pid " + pid + " predates run; name also in agent log)";
            }
        }
        return label;
    }
    static void Usage() {
        Console.Error.WriteLine("usage: afterimage between <start> <end> --prefix <dir> [--out f] [--include-git] [--limit n]");
        Console.Error.WriteLine("       afterimage since <start> --prefix <dir>");
        Console.Error.WriteLine("       afterimage agent <claude-or-codex-session.jsonl> [--prefix <dir>]");
    }
}
