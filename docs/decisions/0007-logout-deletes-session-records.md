# 0007: Logout deletes session records instead of writing tombstones

Status: Accepted

Date: 2026-10-30

Supersedes the durable-tombstone item of [ADR 0005](0005-plaintext-persistence-first.md)
and the corresponding tombstone requirements in the parent SPECS/ARCHITECTURE.

## Context

ADR 0005 listed "durable credential-free logout tombstones" among the first
persistence priorities, and Core's `ITokenStore.DeleteAsync` documented retained
epoch/generation metadata even for a missing key. That requirement was accepted
as part of the overall persistence direction without a detailed review of what
it protects against or what it costs.

After reflection and comparison with established CLIs, we revisited it before
implementing file logout. To our understanding of their documented behavior, GitHub CLI, Claude Code, Codex, the AWS CLI (SSO
cache), gcloud, and the Azure CLI all implement logout by removing the stored
credential (keyring item, file, or cache entry); none retains a marker to fence
work that started before logout.

The design already has the fence that matters. Every committed session receives a
fresh random epoch, and the coordinator commits login only if the epoch it
captured is still current. `SessionLifecycleState.None` and a missing record both
mean "no session". With deletion:

- A login started while signed in captures epoch E; logout removes the record;
  the login sees "missing", which differs from E, and returns OperationSuperseded.
- Future refresh work starts from an existing record and must re-check it under
  the lock before committing; a removed record fails that check.
- After logout and a new login, stale work sees the new login's different epoch.

The only race that is no longer fenced: a login started while **already signed
out**, with a logout of the already-empty profile while it runs. That login now
commits. It stores fresh credentials from a login the user completed; it cannot
resurrect removed credentials. This matches the behavior of the CLIs above.

Tombstones also had costs: a retained file per logged-out profile forever, a
generation baseline that was lost anyway when the old record was unreadable,
logout creating directories for never-used profiles, and an awkward fit for
future protected backends (a secret-store item without a secret).

## Decision

`ITokenStore.DeleteAsync` removes the session record. Afterwards `ReadAsync`
returns null. Deleting a missing record succeeds without creating anything.
Repeated logout is a successful no-op.

- `MemoryTokenStore.DeleteAsync` removes its entry.
- `FileTokenStore.DeleteAsync` deletes `<sessionKey>.json` without reading or
  parsing it, so corrupt and newer-schema records are removed too. Explicit
  logout remains the only operation that removes an unreadable record; reads,
  status, and login still fail with StorageCorrupt/UnsupportedSchema and never
  repair or replace it.
- The caller holds the matching session lease, as for writes. The `.lock` file is
  retained (ADR 0006); only the session record is removed.
- After the record is removed, FileTokenStore deletes this key's orphaned staging
  files (`.<sessionKey>.<32 hex>.tmp`), which may contain credentials from an
  interrupted write. This cleanup is best effort: logout is committed once the
  record is gone, and a failure to remove an orphan does not turn a committed
  logout into an error. Other keys' files are never touched. General orphan
  handling remains storage-plan step 5.
- A failure to remove the record itself is an error (StorageUnavailable), and the
  coordinator must not report a successful logout (SESSION-01 is unchanged).
- Removal is not secure erasure, does not synchronize the parent directory, and
  does not affect copies or remotely issued tokens.

`SessionLifecycleState.None` stays in the contract and file format. Records with
that state written by any earlier or future version read as "no session", which
keeps this decision compatible in both directions: tombstones can be reintroduced
later without a format change if a feature needs them.

## Rules for later work

- Stale-operation checks compare epoch and generation together. Generations are
  monotonic only while a record exists; they restart at 0 after logout.
- A future migration between backends must not import a session from an old
  backend that the user has logged out of in the new one (for example, remove or
  mark the source when migrating, or migrate only once).
- Raw `WriteAsync` callers can still recreate a record after logout; fencing is the
  coordinator's job. Lease- or epoch-checked mutation APIs remain the step-4
  contract question recorded in ADR 0006.

## Consequences

The Core contract documentation, `MemoryTokenStore`, and the coordinator tests
change: logout leaves no record, and the test that required a login started from
a missing session to be superseded now asserts that it commits. The internal
`StoredSession.Removed` helper is removed. The parent SPECS (STORE-02, STORE-04,
SESSION-01), ARCHITECTURE, and IMPLEMENTATION_PLAN replace tombstone wording with
record removal.

## Alternatives considered

- Durable tombstones (ADR 0005's original direction): also fence a login started
  while signed out, and keep generations monotonic across logout, at the costs
  listed above. Reversible later because `None` records remain readable.
- Tombstone only when a record existed: retains most costs and still does not
  cover the signed-out case.
- Refuse logout for unreadable records: keeps the file intact but leaves
  credentials on disk with no supported removal path.
