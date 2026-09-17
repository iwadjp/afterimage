// Bounded query logic, shared by the live reader and deterministic failure fixtures.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

class Snapshot {
    public ulong Id;
    public long First, Next, Lowest, Taken;
}
class Batch {
    public long Next;
    public string Error;
    public List<Rec> Records = new List<Rec>();
}
interface IJournal {
    Snapshot Query();
    Batch Read(long start, ulong id);
    bool NamesAvailable { get; }
}
class QueryResult {
    public Snapshot Before;
    public long Scanned, Oldest;
    public bool ReadComplete;
    public readonly List<Rec> Events = new List<Rec>();
    public readonly List<Rec> Directories = new List<Rec>();
    public readonly List<string> Issues = new List<string>();
    public void Issue(string issue) { if (!Issues.Contains(issue)) Issues.Add(issue); }
    public string Status {
        get {
            if (Issues.Count == 0 && ReadComplete) return "COMPLETE";
            if (Events.Count > 0) return "PARTIAL";
            if (Before == null) return "UNAVAILABLE";
            return "INCONCLUSIVE";
        }
    }
    public int ExitCode { get { return Status == "COMPLETE" ? 0 : Status == "UNAVAILABLE" ? 4 : Status == "PARTIAL" ? 3 : 2; } }
}
static class QueryEngine {
    public static QueryResult Run(IJournal journal, long start, long end, long maxRecords) {
        var result = new QueryResult();
        try { result.Before = journal.Query(); }
        catch (Exception e) { result.Issue(e.Message); return result; }
        var s = result.Before;
        if (s.First < 0 || s.Next < s.First) { result.Issue("invalid-journal-bounds"); return result; }
        if (s.First < s.Lowest) result.Issue("journal-discontinuity");
        if (end > s.Taken) result.Issue("requested-end-after-snapshot");
        if (!journal.NamesAvailable) result.Issue("insufficient-privilege-for-historical-names");
        long cursor = Math.Max(s.First, s.Lowest), previousUsn = -1, previousTime = 0;
        var timer = Stopwatch.StartNew();
        // Read the retained USN interval, not a guessed timestamp->USN binary search.
        // No early timestamp stop: record timestamps need not be strictly monotonic.
        while (cursor < s.Next) {
            Batch b;
            try { b = journal.Read(cursor, s.Id); }
            catch (Exception e) { result.Issue("read-failure:" + e.Message); break; }
            if (b.Error != null) { result.Issue(b.Error); break; }
            if (b.Next <= cursor) { result.Issue("read-made-no-progress"); break; }
            bool bad = false;
            foreach (var r in b.Records) {
                if (r.Usn >= s.Next) break;
                if (r.Usn < cursor || r.Usn <= previousUsn || r.Ts <= 0) {
                    result.Issue("invalid-record-order-or-time"); bad = true; break;
                }
                previousUsn = r.Usn;
                if (result.Oldest == 0) result.Oldest = r.Ts;
                if (previousTime > r.Ts) result.Issue("nonmonotonic-record-timestamps");
                previousTime = r.Ts;
                result.Scanned++;
                if ((r.Attr & 0x10) != 0) result.Directories.Add(r);
                if (r.Ts >= start && r.Ts <= end) result.Events.Add(r);
                if (result.Scanned >= maxRecords) { result.Issue("scan-record-limit"); bad = true; break; }
            }
            if (bad) break;
            cursor = b.Next;
            if (timer.ElapsedMilliseconds > 30000) { result.Issue("scan-time-limit"); break; }
        }
        result.ReadComplete = cursor >= s.Next;
        if (result.Oldest == 0) result.Issue("retention-time-bound-unknown");
        else if (result.Oldest > start) result.Issue("requested-start-before-oldest-observable-record");
        CheckSnapshot(journal, result, false);
        return result;
    }
    public static void CheckSnapshot(IJournal journal, QueryResult r, bool usedCurrentPaths) {
        if (r.Before == null) return;
        try {
            var after = journal.Query();
            if (after.Id != r.Before.Id || after.First > r.Before.First ||
                after.Lowest != r.Before.Lowest || after.Next < r.Before.Next)
                r.Issue("journal-wrap-or-discontinuity-during-query");
            // The live MFT path resolver is not an atomic historical snapshot.
            if (usedCurrentPaths && after.Next != r.Before.Next)
                r.Issue("journal-advanced-during-current-path-resolution");
        } catch (Exception e) { r.Issue("post-query-verification-failed:" + e.Message); }
    }
}

class PathEvidence {
    public string Value, Basis;
    public PathEvidence(string value, string basis) { Value = value; Basis = basis; }
}
class HistoryPaths {
    readonly Dictionary<ulong, List<Rec>> dirs = new Dictionary<ulong, List<Rec>>();
    readonly Func<ulong, string> current;
    public bool UsedCurrent;
    public HistoryPaths(List<Rec> records, Func<ulong, string> currentPath) {
        current = currentPath;
        foreach (var r in records) {
            List<Rec> l;
            if (!dirs.TryGetValue(r.Frn, out l)) { l = new List<Rec>(); dirs[r.Frn] = l; }
            l.Add(r);
        }
        foreach (var l in dirs.Values) l.Sort(delegate(Rec a, Rec b) { return a.Usn.CompareTo(b.Usn); });
    }
    public PathEvidence EventPath(Rec r) {
        if (r.Name == null) return new PathEvidence(null, "historicalNameUnavailable");
        var p = Parent(r.Parent, r.Usn, new HashSet<ulong>());
        return new PathEvidence(p.Value == null ? null : Path.Combine(p.Value, r.Name),
            "recordName+" + p.Basis);
    }
    PathEvidence Parent(ulong id, long at, HashSet<ulong> seen) {
        if (!seen.Add(id) || seen.Count > 64) return new PathEvidence(null, "historicalParentUnknown");
        List<Rec> records; Rec chosen = null;
        if (dirs.TryGetValue(id, out records)) {
            // The last record at/before the event gives the directory's then name and parent.
            foreach (var r in records) { if (r.Usn > at) break; if (r.Name != null) chosen = r; }
            if (chosen != null && (chosen.Reason & 0x200) != 0)
                return new PathEvidence(null, "historicalParentUnknown");
            if (chosen == null) {
                // A later old-name/delete record can witness the preceding state. A lone
                // new-name/create record cannot tell us the previous location.
                foreach (var r in records) if (r.Name != null) {
                    if ((r.Reason & (0x100 | 0x2000)) == 0) chosen = r;
                    break;
                }
                if (chosen == null) return new PathEvidence(null, "historicalParentUnknown");
            }
        }
        if (chosen != null && chosen.Parent != id) {
            var p = Parent(chosen.Parent, at, seen);
            return new PathEvidence(p.Value == null ? null : Path.Combine(p.Value, chosen.Name),
                "journalParent/" + p.Basis);
        }
        UsedCurrent = true;
        return new PathEvidence(current(id), "currentParentAnchor");
    }
    public static bool InScope(string full, string prefix) {
        return full != null && (full.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(prefix.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }
}
