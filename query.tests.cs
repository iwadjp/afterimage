using System;
using System.Collections.Generic;

class FakeJournal : IJournal {
    public Snapshot Initial = new Snapshot { Id = 7, First = 1, Next = 10, Lowest = 1, Taken = 1000 };
    public Snapshot Final;
    public string QueryError;
    public bool Names = true;
    public int Queries;
    public Queue<Batch> Batches = new Queue<Batch>();
    public bool NamesAvailable { get { return Names; } }
    public Snapshot Query() { if (QueryError != null) throw new Exception(QueryError); return Queries++ == 0 || Final == null ? Initial : Final; }
    public Batch Read(long at, ulong id) { return Batches.Count == 0 ? new Batch { Error = "fixture-exhausted" } : Batches.Dequeue(); }
}
static class QueryTests {
    static int passed;
    static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL " + name); passed++; Console.WriteLine("PASS " + name); }
    static Rec R(long usn, long time, ulong id, ulong parent, string name, uint reason, bool dir) {
        return new Rec { Usn = usn, Ts = time, Frn = id, Parent = parent, Name = name, Reason = reason, Attr = dir ? 0x10u : 0u };
    }
    static Rec Old() { return R(1, 10, 90, 2, "older.txt", 0x100, false); }
    static Rec Event() { return R(3, 30, 91, 2, "event.txt", 0x100, false); }
    static FakeJournal Good(params Rec[] rs) { var f = new FakeJournal(); var b = new Batch { Next = 10 }; b.Records.AddRange(rs); f.Batches.Enqueue(b); return f; }
    static QueryResult Run(FakeJournal f) { return QueryEngine.Run(f, 20, 100, 1000); }
    static string Current(ulong id) { return id == 1 ? @"X:\fixture" : id == 2 ? @"X:\fixture\scope" : id == 3 ? @"X:\fixture\outside" : null; }
    static bool Inside(PathEvidence p) { return HistoryPaths.InScope(p.Value, @"X:\fixture\scope"); }
    public static int Main(string[] args) {
        try {
            var r = Run(Good(Old())); Check(r.Status == "COMPLETE" && r.ExitCode == 0 && r.Events.Count == 0, "zero-events-complete");
            var f = new FakeJournal(); f.Batches.Enqueue(new Batch { Error = "read:win32-1117" }); r = Run(f);
            Check(r.Status == "INCONCLUSIVE" && r.ExitCode != 0 && r.Events.Count == 0, "failure-before-usable-batch-not-zero-success");
            f = Good(Old(), Event()); var first = f.Batches.Dequeue(); first.Next = 4; f.Batches.Enqueue(first); f.Batches.Enqueue(new Batch { Error = "read:win32-1117" }); r = Run(f);
            Check(r.Status == "PARTIAL" && r.ExitCode == 3 && r.Events.Count == 1 && !r.ReadComplete, "failure-after-data-retains-partial");
            r = Run(new FakeJournal { QueryError = "query:access-denied" }); Check(r.Status == "UNAVAILABLE" && r.ExitCode == 4, "access-denied");
            r = Run(new FakeJournal { QueryError = "query:journal-not-active" }); Check(r.Status == "UNAVAILABLE", "journal-not-active");
            r = Run(Good(Event())); Check(r.Status == "PARTIAL" && r.Issues.Contains("requested-start-before-oldest-observable-record"), "retention-gap-with-data");
            f = Good(R(1, 150, 90, 2, "newer.txt", 0x100, false)); r = Run(f); Check(r.Status == "INCONCLUSIVE" && r.Events.Count == 0, "retention-gap-with-zero-events");
            f = Good(Old()); f.Initial.Lowest = 2; r = Run(f); Check(r.ExitCode != 0 && r.Issues.Contains("journal-discontinuity"), "lowest-valid-discontinuity");
            f = Good(Old(), Event()); f.Final = new Snapshot { Id = 8, First = 5, Lowest = 5, Next = 12, Taken = 1000 }; r = Run(f);
            Check(r.Status == "PARTIAL" && r.Issues.Contains("journal-wrap-or-discontinuity-during-query"), "journal-wrap-or-replacement");
            f = Good(Old()); f.Names = false; r = Run(f); Check(r.Status == "INCONCLUSIVE", "unprivileged-zero-not-negative-proof");
            f = Good(Old()); f.Initial.Next = 1; r = Run(f); Check(r.Status == "INCONCLUSIVE", "empty-journal-time-bound-unknown");
            f = new FakeJournal(); f.Batches.Enqueue(new Batch { Next = 1 }); r = Run(f); Check(r.ExitCode != 0, "read-no-progress");
            r = Run(Good(Old(), R(2, 150, 92, 2, "later", 1, false), Event()));
            Check(r.Events.Count == 1 && r.Issues.Contains("nonmonotonic-record-timestamps"), "nonmonotonic-time-no-early-stop");
            f = Good(Old()); f.Initial.Taken = 99; r = Run(f); Check(r.ExitCode != 0, "future-end-incomplete");
            f = Good(Old(), Event()); r = QueryEngine.Run(f, 20, 100, 1); Check(r.ExitCode != 0, "bounded-scan-limit");
            f = Good(Old()); f.Final = new Snapshot { Id = 7, First = 1, Lowest = 1, Next = 11, Taken = 1000 }; r = Run(f);
            QueryEngine.CheckSnapshot(f, r, true); Check(r.Status == "INCONCLUSIVE", "current-path-race-conservative");
            Check(Journal.Decode(new byte[7], 7).Error != null, "short-native-buffer");
            var bad = new byte[68]; BitConverter.GetBytes(61).CopyTo(bad, 8); Check(Journal.Decode(bad, bad.Length).Error != null, "native-record-out-of-bounds");

            var h = new HistoryPaths(new List<Rec>(), Current);
            Check(h.EventPath(R(10, 30, 10, 2, "a", 0x1000, false)).Value == @"X:\fixture\scope\a" &&
                h.EventPath(R(11, 31, 10, 2, "b", 0x2000, false)).Value == @"X:\fixture\scope\b", "file-rename-names");
            var dirs = new List<Rec> { R(20, 40, 10, 2, "old-parent", 0x1000, true), R(21, 41, 10, 2, "new-parent", 0x2000, true) };
            h = new HistoryPaths(dirs, Current);
            Check(h.EventPath(R(10, 30, 11, 10, "f", 1, false)).Value == @"X:\fixture\scope\old-parent\f" &&
                h.EventPath(R(22, 42, 11, 10, "f", 1, false)).Value == @"X:\fixture\scope\new-parent\f", "parent-rename-before-and-after");
            Check(!Inside(h.EventPath(R(30, 50, 12, 3, "m", 0x1000, false))) && Inside(h.EventPath(R(31, 51, 12, 2, "m", 0x2000, false))), "file-outside-to-inside");
            Check(Inside(h.EventPath(R(30, 50, 12, 2, "m", 0x1000, false))) && !Inside(h.EventPath(R(31, 51, 12, 3, "m", 0x2000, false))), "file-inside-to-outside");
            dirs = new List<Rec> { R(20, 40, 10, 3, "moved-dir", 0x1000, true), R(21, 41, 10, 2, "moved-dir", 0x2000, true), R(40, 60, 10, 2, "moved-dir", 0x1000, true), R(41, 61, 10, 3, "moved-dir", 0x2000, true) };
            h = new HistoryPaths(dirs, Current);
            Check(!Inside(h.EventPath(R(10, 30, 11, 10, "f", 1, false))) && Inside(h.EventPath(R(30, 50, 11, 10, "f", 1, false))) && !Inside(h.EventPath(R(50, 70, 11, 10, "f", 1, false))), "parent-scope-moves-no-false-inclusion");
            Check(h.EventPath(R(35, 55, 11, 10, "renamed-deleted", 0x200, false)).Value == @"X:\fixture\scope\moved-dir\renamed-deleted", "rename-then-delete-without-current-file");
            Check(h.EventPath(R(10, 30, 11, 999, "f", 1, false)).Value == null, "unknown-parent-not-guessed");
            Check(!HistoryPaths.InScope(@"X:\fixture\scope-old\f", @"X:\fixture\scope"), "scope-prefix-collision");
            dirs = new List<Rec> { R(20, 40, 10, 2, "new-only", 0x2000, true) }; h = new HistoryPaths(dirs, Current);
            Check(h.EventPath(R(10, 30, 11, 10, "f", 1, false)).Value == null, "missing-old-name-not-current-path");
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            Check(Program.Attribution("data.json", ".data.json.123.1234567890123.tmp", "data.json", start, delegate(int pid) { return start.AddSeconds(-1); }).StartsWith("BACKGROUND_LIKELY"), "background-downgrade");
            Check(Program.Attribution("data.json", ".data.json.123.1234567890123.tmp", "data.json", start, delegate(int pid) { return start.AddSeconds(1); }).StartsWith("LIKELY_RELATED"), "child-no-downgrade");
            Check(Program.Attribution("data.json", ".data.json.123.1234567890123.tmp", "data.json", start, delegate(int pid) { return null; }).StartsWith("LIKELY_RELATED"), "unknown-pid-unchanged");
            Check(Program.Attribution("data.json", "data.json", "data.json", start, delegate(int pid) { throw new Exception("must not query"); }).StartsWith("LIKELY_RELATED"), "no-pid-pattern-unchanged");
            Check(Program.Attribution("data.json", "data.json", null, start, delegate(int pid) { return null; }) == "DURING_RUN", "no-authorship-inference");
            Check(Program.NormalizePrefix(@"X:\") == @"X:\", "drive-root-not-drive-relative");
            Check(Program.NormalizePrefix(@"X:\fixture\scope\") == @"X:\fixture\scope", "ordinary-prefix-normalization");
            Check(HistoryPaths.InScope(@"X:\fixture\file", @"X:\"), "drive-root-scope-boundary");
            Console.WriteLine("ALL_PASS=True tests=" + passed);
            return 0;
        } catch (Exception e) { Console.WriteLine(e.Message); return 1; }
    }
}
