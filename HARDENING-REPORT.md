# Reliability hardening — 2026-09-17

Verdict: **PUBLIC_READY_PATH_CLEAR for the audited reliability gaps**. This is not
publication approval. At the time of this hardening run, no export, remote
repository, release, or system-journal configuration change was performed. See
`hardening-validation.json` for results
and final source hashes.

## Root cause and changes

The previous loop treated failed reads like exhaustion and returned exit 0. Its
timestamp binary search and early stop could additionally hide coverage uncertainty.
Grouping a file by its final name/parent mixed historical records with current paths
and lost the in-scope side of a move.

The live adapter now validates buffers and preserves read/query error reasons. A
small shared query engine has an injectable journal reader, bounded forward scan,
pre/post journal checks, and COMPLETE/PARTIAL/INCONCLUSIVE/UNAVAILABLE status with
exit codes 0/3/2/4. Invalid input uses exit 2; usage uses exit 1. Zero matching records
is a successful retained-record query only when completeness checks pass.

Scope selection uses each record's name/parent, with a small directory-link timeline
indexed by USN. Current path output is separate. Unknown parents are counted and
invalidate complete scope coverage. Outside-scope names are not included in chains.
No actor attribution or ETW/Sysmon integration was added.

## Validation

* 35/35 deterministic logic tests: complete zero matches, read failure before data,
  partial reads, access denial, missing journal, retention gap, wrap/replacement,
  timestamp regression, no-progress, limits, native buffer validation, paths,
  attribution label regression, and drive-root normalization.
* 15/15 privileged live checks in a new synthetic directory. No prior Afterimage
  capture. A synthetic Git baseline commit was made only in the fixture repository.
* Public story: create/delete, modify/restore original bytes, rename/back; final Git
  diff and status clean; subsequent query COMPLETE/0, 16 matching records, 3 files.
* Boundaries: file rename, parent rename, both file scope-move directions, a parent
  moving into and out of scope, rename/delete. COMPLETE/0, 38 matching records in
  9 file identities, 19 outside records excluded; no unresolved records. The scope
  directory itself is one of those identities.
* Negative live query: a period older than the oldest retained record produced
  INCONCLUSIVE/2, zero observed matches, and an explicit retention-gap reason.
* Final non-elevated CLI over the same demo period: PARTIAL/3, 16 unresolved records,
  zero scope matches, activity UNKNOWN. Missing scope: UNAVAILABLE/4.

The saved live E2E ran before a final small drive-root normalization correction.
The correction is covered by the final 35-test suite; it does not change the ordinary
directory paths used by the E2E. The final executable was rebuilt and used for the
non-elevated/missing-scope checks. Historical successful E2E and actual session
evidence were preserved, not rerun or relabeled as current tests.

The live scan examined about 12.6 million retained records. Measured query time was
approximately 6–15 seconds on this host. This is a cost of avoiding a guessed
timestamp seek, not a general performance claim. The reader has a 20-million-record
and approximately 30-second bound; limits return incomplete status.

## Coverage and residual limitations

COMPLETE applies to the bounded retained-record query and observed checks, not an
exhaustive filesystem operation history. FirstUsn/NextUsn/LowestValidUsn and the
oldest timestamp do not prove an exact time mapping. USN coalesces reasons and
does not preserve contents or actor identity. Live MFT paths are not an atomic
snapshot. Busy volumes, unavailable names, or unknown parents can legitimately
yield incomplete output. Path candidates remain explicitly labeled evidence.

No unresolved Critical/High issue was found in the tested ordinary NTFS V2 scope.
This does not certify untested filesystems, record formats, hard-link alternatives,
reparse configurations, or a general forensic completeness/attribution claim.

## Privacy and remaining gates

New candidate documents and synthetic evidence were scanned for local private roots,
user names, private project identifiers and common credential formats. No such
matches were found. The email in the fixture is the synthetic reserved-domain value
`fixture@example.invalid`. This is not a guarantee that heuristic name masking
anonymizes arbitrary investigation output.

The directory still deliberately contains internal historical evidence. A future
export must use an explicit allowlist, excluding real session output, old E2E data,
probe artifacts and embedded local paths. Current `afterimage.exe` was rebuilt;
release packaging should build from reviewed source rather than copy old artifacts.

Remaining: human decision, license, public-only packaging/allowlist, independent
clean-room build and demo, and final privacy review of that exact package. None of
these is authorization to publish.
