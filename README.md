# Afterimage

Investigate filesystem traces that a final Git diff does not show. Afterimage reads
retained NTFS USN change records after the fact, optionally using an agent session's
time window and working directory. Afterimage itself does not need to have been
running before the incident; an existing, sufficiently retained OS journal is required.

USN parsing is established functionality, not a new forensic capability. The focus
here is a small investigation workflow for agent-assisted work. It does not prove
which actor performed an operation, recover file contents, or reconstruct every
filesystem operation. Procmon captures richer process-associated events while it
is running; this tool queries existing OS history.

## Why this exists

After a coding-agent run, `git diff` can be clean even though temporary files were
created, bytes were changed and restored, or names were moved. Afterimage can inspect
retained NTFS history after the fact. It is a workflow around an existing Windows
record source, not a replacement for Procmon, Sysmon, or forensic parsers.

## Build and query

Windows PowerShell 5.1, the .NET Framework C# compiler, a local NTFS volume using
USN V2 records, and (for historical names) administrator access are required.
Public v0.1.0 is a source-only release; build it locally as shown below.

## Modes

* **Privileged:** volume reads can include historical names, which makes deleted and
  moved entries more useful. Run from an elevated PowerShell when authorized.
* **Unprivileged:** the fallback read can omit names. A query with unresolved scope
  evidence is `PARTIAL`; it is never reported as a clean negative result.

The tool only reads the journal and current path metadata. It does not attach to a
process, modify the journal, restore files, or recover file contents.

```powershell
.\build.ps1
.\afterimage.exe between 2026-09-17T10:00:00Z 2026-09-17T10:02:00Z --prefix D:\synthetic-project
.\afterimage.exe since 2026-09-17T10:00:00Z --prefix D:\synthetic-project
.\afterimage.exe agent session.jsonl --prefix D:\synthetic-project
```

`agent` accepts the compact timestamp/cwd fields used in the tested Claude/Codex
JSONL logs. It adds five seconds around the recorded window. An unfinished/future
window is incomplete. Log schema support is narrow, not a general JSONL import API.
`--include-git` includes `.git` records; `--limit N` limits displayed file groups,
not the query. Output says when groups were omitted. `--out report.txt` writes a
report only when explicitly requested. `--help` does not access the journal.

## Result and exit status

| Status | Exit | Meaning |
| --- | ---: | --- |
| COMPLETE | 0 | Bounded retained-record query completed with observed coverage checks and resolved scope candidates. |
| PARTIAL | 3 | Some time-window records were read, but one or more completeness checks failed. Matching scope output may still be empty. |
| INCONCLUSIVE | 2 | No usable time-window evidence and the query cannot support a negative conclusion; also invalid input/unexpected query errors. |
| UNAVAILABLE | 4 | The scope/journal could not be opened or initially queried. |
| Usage | 1 | No supported command supplied. |

**Zero matching retained records is not the same as a failed or incomplete query.**
Incomplete empty output explicitly says activity is UNKNOWN. Even COMPLETE with
zero matches is not proof that no filesystem activity occurred.

Read failures, no-progress reads, malformed records, access denial, missing journal,
detected journal wrap/replacement, unsupported names, and unresolved historical scope
never silently become a successful empty result. An unresolved event might belong
to the requested scope, so even one makes the query conservative; it is not hidden
behind an arbitrary percentage threshold.

## Retention and coverage

The reader snapshots journal ID, FirstUsn, NextUsn and LowestValidUsn, and reads
forward to the captured NextUsn. It does not binary-search timestamps or stop at the
first record newer than the requested end. It checks the journal again after the
scan and path resolution. Bounds/ID changes that indicate wrap or discontinuity
invalidate completeness. Advancement while current path anchors are being used
also makes the result incomplete: live MFT resolution is not an atomic snapshot.

The oldest observed record must predate or equal the requested start. A newer
oldest record, an empty journal with no temporal bound, a future end, or observed
backward timestamps makes coverage uncertain. This is evidence about retained
records, **not a mathematically exact timestamp-to-USN mapping**. Clock changes,
coalesced operations and unrecorded history cannot be ruled out. There is a bounded
20-million-record / approximately 30-second scan; hitting a limit is incomplete.
Large retained journals can be expensive, and busy volumes can legitimately return
PARTIAL because current path anchors raced with journal advancement.

USN records can combine reasons and do not preserve every write or its contents.
The tool never creates, resets, deletes, or changes journal configuration.

## Paths, rename and attribution

Scope filtering is per record, not per file's final location. A move into the scope
can contribute its new-name record; a move out can contribute its old-name record.
The other side's path/name is not printed. A name chain contains only selected
in-scope records, so it may intentionally be incomplete.

* `historicalName`: name stored in the USN record.
* `parentFrn` / `frn` / `usn`: the record's identities.
* `pathCandidate` / `basis`: journal directory-name/parent links at that USN,
  potentially anchored in a current directory path. Not a certified historical path.
* `currentResolvedPath`: separately resolved now; outside-scope values are redacted.
  Failure to resolve is reported as unresolved-or-absent, not proof of deletion.

Parent-directory rename/move records are used at the event's USN, including records
later than the requested end. Missing historical parents are not guessed. This is
not a full MFT forensic engine; deleted parent chains outside retained evidence,
hard-link alternatives, reparse points and unusual filesystem configurations remain
limitations. Use a stable ordinary directory as scope. Output can be incomplete.

`DURING_RUN`, `LIKELY_RELATED`, and `BACKGROUND_LIKELY` are timing/name heuristics.
The last label only weakens a name match when an embedded PID resolves to a process
that predates the window. PID reuse, naming conventions, exited processes and access
denial limit it. It is neither writer-PID proof nor agent-authorship proof.

## Agent-session mode

`agent <log.jsonl>` takes the first and last supported `timestamp` fields and a
recorded `cwd`, then adds a small five-second margin to the start. The supported log
shape is intentionally narrow. A session name match only influences a heuristic label;
it does not identify the writer.

## Tests and public demonstration candidate

```powershell
.\test.ps1                        # deterministic logic; no elevation or journal access
.\build.ps1
# Elevated PowerShell; choose a new, nonexistent temporary WorkRoot on NTFS:
.\live-tests.ps1 -Exe "$PWD\afterimage.exe" -WorkRoot D:\afterimage-synthetic-example
```

The live script creates only its owned temporary fixture. It makes a synthetic Git
baseline, creates/deletes a file, changes/restores original bytes, and renames a file
back. Git diff and status are clean before the first Afterimage invocation. It then
checks retained traces, parent rename, both scope-move directions, parent moves,
rename/delete, and an unavailable historical time range. It does not use git reset
or restore, alter the system journal, attach processes, or remove existing files.
The baseline commit is inside the newly created fixture repository only.

The fixture is retained for inspection. A demo can pass its trace assertions while
the query honestly reports PARTIAL; test success must not be presented as complete
history coverage. Existing privileged attribution E2E is historical evidence; current
pure tests cover the same label rules without asserting real writer identity.

The same live script contains two public-facing negative checks: an old range returns
`INCONCLUSIVE` with an explicit retention reason, and a non-elevated read of the demo
returns `PARTIAL` when historical scope names cannot be resolved. These checks do not
change system journal settings and do not touch an existing user directory.

The sanitized result summary in `public-demo-result.json` records one clean-room
privileged run. It contains statuses, counts and pass labels only; it contains no
local path, session log or user-specific value.

## Privacy and development status

No upload/network operation is implemented. Nevertheless, names, relative paths,
timestamps and metadata may be sensitive. Secret-like name masking is a convenience,
**not a privacy guarantee**. Do not publish real session logs or raw investigation
output. Public evidence should use synthetic names only.

Historical agent session logs, old E2E folders/output, probe artifacts and local
session data are internal evidence and must not be exported. The source, build and
test files in this repository are the reviewed **Public v0.1.0** release; nothing
outside this repository is covered by that review.

References: [Microsoft change journal records](https://learn.microsoft.com/en-us/windows/win32/fileio/change-journal-records),
[journal bounds](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_journal_data_v0),
[Velociraptor USN analysis](https://docs.velociraptor.app/artifact_references/pages/windows.forensics.usn/),
[Procmon](https://learn.microsoft.com/en-us/sysinternals/downloads/procmon).

## Related tools

This project is part of a small set of tools for investigating AI-coding and
debugging problems that Git alone cannot explain.

- [Timewitness](https://github.com/iwadjp/timewitness) — check whether a regression test fails before a fix and passes after it.
- [wipwho](https://github.com/iwadjp/wipwho) — split mixed uncommitted Claude/Codex changes into request-level patches.
- [Ember](https://github.com/iwadjp/ember) — recover source retained by a still-running Node.js process.
- [Worldbisect](https://github.com/iwadjp/worldbisect) — reduce same-commit environment differences to an observed 1-minimal reproducing set.
- [Afterimage](https://github.com/iwadjp/afterimage) — inspect retained NTFS USN history after an agent run.

[Overview and articles](https://blog2020.iwadjp.com/2026/09/18/ai-coding-debugging-tools-portfolio/)

**Article:** [Git diffはclean。でもagent run中のfile操作は消えていないかもしれない。AfterimageでNTFS履歴を調べる](https://blog2020.iwadjp.com/2026/09/18/afterimage-investigate-ntfs-usn-journal-after-agent-run/)
