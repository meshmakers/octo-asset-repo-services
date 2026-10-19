# GridFS orphan sweep (AB#6541)

Linked binaries are committed **before** the old bytes are deleted (after-commit design, AB#6248; see
`docs/linked-binaries-after-commit.md` in the engine-mongodb repository). A crash or a failing delete in between,
a crash before the commit, a stale `BinaryId` or a failed rollback callback leave GridFS files that no entity
references: **orphans**. They are harmless (no entity points to them), but they take space. The engine's
`GridFsOrphanSweeper` (octo-construction-kit-engine-mongodb, release 3.5.14+) finds and removes them; this service
exposes it per tenant.

## Endpoint

```
POST system/v1/files/orphan-sweep/{tenantId}?remove=false&gracePeriodMinutes=1440
```

| Parameter | Default | Meaning |
|-----------|---------|---------|
| `remove` | `false` | **Dry run is the default**: the sweep only counts. `remove=true` deletes the orphaned files and their chunks. |
| `gracePeriodMinutes` | `1440` (24 h) | Only files older than this are examined. Minimum `10` (ten times the 60 s transaction lifetime); a smaller value answers 400 and nothing runs. |

**Authorisation (fail closed, `SystemAdministratorGuard`):** policy `SystemAssetApiReadWritePolicy` (scope
`octo_api`) **plus** an in-controller check: the token's `tenant_id` must be the system tenant, and a user token needs
the role `AdminPanelManagement`. Everything else (another tenant, no tenant claim, no admin role) answers **403** and
nothing is run. Other answers: 400 (grace period out of range), 404 (unknown tenant), 409 (a sweep of this tenant is
already running on this instance), 500 (no exception text in the body; see the service log).

**Report (200):**

```json
{
  "tenantId": "...", "mode": "dry-run", "gracePeriodMinutes": 1440,
  "examinedFiles": 1820, "referencedFiles": 1815, "youngFiles": 3,
  "orphanFiles": 5, "orphanChunks": 9, "removedFiles": 0, "orphanBytes": 1048576,
  "durationMilliseconds": 840, "finishedUtc": "..."
}
```

Counts, sizes and durations only. The log line contains the same numbers, never file names, ids of customer data,
content or credentials.

## What counts as an orphan

A FileSystem GridFS file that no entity of the tenant references by `_id` in a linked-binary attribute, whatever its
`metadata.rtEntityId` says. Referenced bytes and temporary binaries are never touched; files younger than the grace
period are not examined, so bytes staged by an in-flight transaction survive. The sweep is idempotent and safe to
repeat; a second run finds nothing.

## Runbook

1. **Dry run** on every tenant: `POST .../orphan-sweep/{tenantId}` (no parameters). Nothing is deleted.
2. **Review the report**: `orphanFiles`, `orphanChunks`, `orphanBytes` must match what you expect (a few after crashes,
   zero on a healthy tenant). A large number of orphans on a tenant that never crashed is a finding: stop and report it,
   do not remove. Note that a binary id stored somewhere other than an `attributes.*.binaryId` of an `RtEntity*`
   collection (for example a future record support) would be reported as an orphan.
3. **Remove** only after the review: `POST .../orphan-sweep/{tenantId}?remove=true` (default grace period 24 h).
4. **Verify** with a second dry run: `orphanFiles` is 0.

A `remove=true` run continues after the client or a proxy disconnects (it is stopped only by service shutdown); the
result is then in the log counts. A dry run is cancelled with the request.

Tenant by tenant, one at a time; the scan reads `fs.files` and the entity collections of the tenant once, so run
it for a very large tenant in a quiet window.

## Implementation

| Part | File |
|------|------|
| Endpoint | `SystemApi/v1/Controllers/FilesOrphanSweepController.cs` |
| Caller check shared with `reconcile-registry` | `SystemApi/v1/Controllers/SystemAdministratorGuard.cs` |
| Service, report DTO, per-tenant run gate | `Services/Files/OrphanSweep/GridFsOrphanSweepService.cs` |

The engine keeps its repository access internal, so the service creates one lazily built admin client from
`OctoSystemConfiguration` (admin credentials, like `TenantMongoDatabaseProvider`) and hands it to the engine's
`GridFsOrphanSweeper`. Tests: `FilesOrphanSweepControllerTests`, `GridFsOrphanSweepServiceTests` (unit: 403, defaults,
grace validation, report mapping, one run per tenant) and `Files/GridFsOrphanSweepTests` (integration against MongoDB:
dry run deletes nothing, grace period respected, referenced and young bytes survive, exact report numbers, second run
removes nothing, 403, 404).
