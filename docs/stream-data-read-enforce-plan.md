---
description: Operator plan for switching the stream-data read role gate from Warn to Enforce per environment.
background: true
---

# Switching the stream-data read gate to `Enforce` per environment

This is the operator plan for the step after the stream-data read role gate ran in `Warn`. The gate itself is described
in [tenant-enforcement.md](tenant-enforcement.md#authorization-stream-data-reads-staged); the stream-data endpoints in
[stream-data-management.md](stream-data-management.md) and [streamdata-graphql.md](streamdata-graphql.md).

| Mode | What a stream-data read without `StreamDataReader`, `StreamDataWriter` or `StreamDataAdmin` gets |
|---|---|
| `Off` | read, not logged (behaviour before the gate) |
| `Warn` (code and chart default) | read, one warning per caller, tenant and query kind per `WarnLogInterval` (default 1 h) |
| `Enforce` | GraphQL error `StreamDataReadRoleRequired`, nothing read, one warning per refused read |

Nothing in this document changes an environment. Every switch, role grant, test client and rollback is an environment
write that the ops agent executes per cluster. Who gives the go: on **test-2** the approval of the security work item that
owns the switch (the developer who runs the agent confirms it in the session); on **staging-1, prod-1 and prod-2** Gerald,
in his own words, for that cluster and that step — never without it, a rollback included.

## What the gate protects, and what it does not

The gate protects **stream-data reads through the asset repository GraphQL API with the caller's own token**: the
transient queries `simple`, `aggregation`, `groupingAggregation` and `downsampling`, and the persisted `streamDataQuery`
with its `rows` and `aggregations`. Callers are users in Refinery Studio, Grafana, Excel or a solution frontend, app
clients and service accounts with a client-credentials token, and pipeline steps that call the GraphQL API (they run as
the HTTP caller or as the adapter's pipeline service account).

It does **not** cover, by design:

- **Pipeline read nodes in the mesh adapter.** `GetStreamData` and the other archive read nodes resolve the tenant's
  stream-data repository through the adapter's system context and query it directly (`GetStreamDataNode.ResolveArchiveAsync`
  and `ExecuteAsync` in `octo-mesh-adapter`); they never reach this gate. This is the accepted boundary: pipelines run
  server-side and can only be authored and deployed with `CommunicationManagement`, which is already a privileged role.
  Whoever holds it can read every archive of the tenant through a pipeline, whatever the gate mode.
- **System contexts and in-process executions** without a principal (`RtSecurityContext.IsSystem`, `GraphQlUserContext.User`
  null). The HTTP endpoint always has an authenticated principal, so this is never a caller from outside.
- **Archive metadata** (`rollupsFor`, `coverageFor`, `recomputeJobsFor`, `archivesStorageStats`, `rollupQueryMetadata`,
  `resolveSeriesQuery`), archive entity queries (runtime queries, data policies only) and the REST endpoints of
  [stream-data-management.md](stream-data-management.md), which carry their own policies (reads and automated inserts
  scope-only, archive changes and manual writes `StreamDataAdmin`) and offer no time-series query.

Consequence for the gate check: it reads the **GraphQL warnings of this gate only**. Pipeline runs that read archives
produce no gate line in any mode and are not part of the check; a pipeline that fails after the switch fails on a GraphQL
call with its service account, which does produce a line.

## Order

`test-2` → `staging-1` → `prod-1` → `prod-2`, one cluster at a time. A cluster is switched only after the previous one has
run `Enforce` for **1 day** (the observation window) without a refusal of a legitimate caller (step 6 below). The approval
of one cluster does not carry over to the next. Clusters outside this list stay on `Warn` until they get their own plan
entry.

## Preconditions per cluster

- **P1** the octo-mesh chart deployed on the cluster renders `services.assetRepository.streamDataReadEnforcement`
  (chart 3.5.79 or later). Check the chart version of the cluster's last octo-mesh CD run, not only the pin on `main`:
  an older chart does not know the value, Helm ignores it without an error, and the pod keeps `Warn`.
- **P2** the asset repository on the cluster runs a release with the gate (the warning of the next section appears in
  the cluster's Dash0 dataset, or the environment variable `OCTO_STREAMDATAREADAUTHORIZATION__ROLEENFORCEMENT` is rendered).
- **P3** the Dash0 check rules for the gate are live on the cluster: `OctoMeshStreamDataReadRefused` (counts the
  `Enforce` refusal lines) and `OctoMeshStreamDataReadWouldRefuse` (counts the `Warn` lines), both on the log lines of
  logger `StreamDataReadRoleGate`. The positive control of the gate (below) must show up in
  `OctoMeshStreamDataReadWouldRefuse` on the cluster: the rules match on the Dash0 label `dash0_log_message` (the
  metric-label form of the log attribute `dash0.log.message`), and a rule that matches nothing never fires.
- **P4** the local end-to-end check of the gate in `Enforce` is done (once, before the first cluster).

Record P1 to P4 per cluster in a comment on the security work item before the gate check.

## Gate (per cluster, before the switch)

**An empty result only counts when the query is proven to see the cluster's gate lines** (otherwise the gate fails open:
a dataset without asset-repository logs, or a changed logger name or text, also returns zero rows). Both of these are part
of every gate check, on every cluster:

- **Coverage**: the cluster's Dash0 dataset holds asset-repository log lines on every day of the 7-day window
  (`log_attributes['logger'] LIKE 'Meshmakers.Octo.Backend.AssetRepositoryServices.%'`, count per day > 0). A day without
  lines is a gap: extend the window until 7 covered days are reached, or record the gap and do not switch.
- **Positive control**: right before the gate check, produce one `Warn` line on the cluster with the test token of the
  smoke test (a client or user without a stream-data role, created for the test and named in the pre-write list) and a
  stream-data query (as in step 5 of the switch); the gate query must return it (tenant, client, query kind as produced) and
  `OctoMeshStreamDataReadWouldRefuse` must count it. Without that line the result is "not checked", not clean.

Then, over the **last 7 days** before the gate check, the `Warn` lines of the gate (apart from the positive control) show no
legitimate caller that would be refused. Every caller found (tenant, subject, client) is, before the switch, either

1. **granted** a stream-data role (normally `StreamDataReader`, through a group where the tenant has one), written down
   with the octo-cli command that was run:

   ```powershell
   octo-cli -c AddClientToRole -id "<client id>" -r "StreamDataReader"   # service account / app client
   octo-cli -c AddUserToRole -un "<user name>" -r "StreamDataReader"      # user; prefer a group
   ```

   Granting a role is a write on the cluster's tenant: on staging-1, prod-1 and prod-2 it needs Gerald's go like the
   switch itself; or
2. **declared not legitimate** (the caller must not read stream data; `Enforce` refuses it on purpose), with the reason.

Pipeline service accounts (client ids `octo-pipeline-sa-*` and the declarative accounts of the communication controller)
get `StreamDataReader` automatically after a reconcile or a tenant setup. If one of them still appears in the window,
check that it holds the role now (`octo-cli -c GetClient …` / Studio) before the switch; if not, a workload deploy of its
pool or a tenant setup grants it, otherwise grant it by hand as in 1.

A caller whose last line is older than its role grant is resolved; lines keep showing it until the window has passed.

### Log lines (exact text, from `StreamDataReadRoleGate`)

Both are `Warning` and name the query kind, tenant, subject and client id; never a token, a name or an e-mail address.

| Mode | Fixed part to search for |
|---|---|
| `Warn` | `Stream-data read without a stream-data role allowed by StreamDataReadRoleEnforcement=Warn: query … in tenant … by subject … / client …, required any of [StreamDataReader,StreamDataWriter,StreamDataAdmin]. This would be denied with StreamDataReadRoleEnforcement=Enforce` |
| `Enforce` | `Stream-data read denied, stream-data role missing: query … in tenant … by subject … / client …, required any of [StreamDataReader,StreamDataWriter,StreamDataAdmin]` |

A user token carries a subject and the client it signed in with (for example Refinery Studio); a client-credentials token
carries the client id and no user subject. `Warn` lines are rate-limited per caller, tenant and query kind (default one
per hour per asset repository pod), so the line count is not the read count. `Enforce` lines are not rate-limited.

### Dash0 queries

The Dash0 dataset is named after the cluster (`k8s.cluster.name` is empty; select the dataset). The structured-logging
fields arrive as log attributes `TenantId`, `Subject`, `ClientId`, `QueryKind`, `RequiredRoles` and `RequestPath`; the
logger is the attribute `logger`; the whole line is the body and the log attribute `dash0.log.message`. Logs with D0QL
(`sql`), time range = the last 7 days:

```sql
-- Gate check: callers the switch would refuse, per tenant, caller and query kind
SELECT log_attributes['TenantId'] AS tenant, log_attributes['Subject'] AS subject,
       log_attributes['ClientId'] AS client, log_attributes['QueryKind'] AS kind,
       count() AS lines, min(timestamp) AS first_seen, max(timestamp) AS last_seen
FROM logs
WHERE log_attributes['logger'] = 'Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils.StreamDataReadRoleGate'
  AND body LIKE '%This would be denied with StreamDataReadRoleEnforcement=Enforce%'
GROUP BY tenant, subject, client, kind ORDER BY first_seen LIMIT 200
```

Cross-check with the body alone (`body LIKE '%StreamDataReadRoleEnforcement%'` must return the same lines) and open single
lines with `getLogRecords` and the filter `otel.log.body` contains `StreamDataReadRoleEnforcement` where a caller needs
more context (pod, request path, time of day). D0QL rejects `position()`; use `LIKE`.

After the switch, use the same query with `body LIKE '%Stream-data read denied, stream-data role missing%'`.

## Exact value

In `octo-mesh-deployment`, `clusters/<cluster>/values-octo-mesh.yaml`:

```yaml
services:
  assetRepository:
    # Stream-data read gate: Enforce since <date>, go: <who, date>, exit: set Warn and run the octo-mesh CD
    streamDataReadEnforcement: Enforce   # Off | Warn | Enforce -> OCTO_STREAMDATAREADAUTHORIZATION__ROLEENFORCEMENT
```

Without the key the chart renders `Warn`. Any other value, or a misspelled key under `services.assetRepository` that
contains `streamdataread`, fails the render; the key under any other service fails it too. `kubectl set env` is not a
substitute (the next CD run undoes it). The setting is read through `IOptionsMonitor`, but the CD rolls the asset
repository pods anyway, so plan for a normal rolling update.

## Switch, per cluster

1. **Gate check**, recorded as a comment on the security work item: P1 to P4, coverage per day and the positive control
   (time, tenant, client, and that the query and the rule saw it), the query of the gate with its time range, every caller
   found with tenant, subject or client and query kind, and for each the role granted (with the command) or
   the reason it is not legitimate.
2. **Go** for this cluster: on test-2 the approval of the security work item, confirmed by the developer who runs the
   agent; on staging-1, prod-1 and prod-2 Gerald's go, in his own words.
3. **Change** (ops): PR in `octo-mesh-deployment` that sets the value with the comment above, plus the pre-write impact
   list: the rendered diff of the octo-mesh release changes only
   `OCTO_STREAMDATAREADAUTHORIZATION__ROLEENFORCEMENT` on the asset repository deployment.
4. **Roll out** with the cluster's octo-mesh CD run.
5. **Smoke test** right after the rollout, with a stream-data query as in the gate's integration tests, for example

   ```graphql
   { streamData { transientStreamDataQuery {
       simple(archiveRtId: "<archive>", columnPaths: ["<column>"], first: 10) { items { rows(first: 10) { totalCount } } }
   } } }
   ```

   - a token **without** a stream-data role (a test client or user of an internal tenant, created and removed for the test
     and named in the pre-write list) gets the GraphQL error `StreamDataReadRoleRequired`, and the `Enforce` line appears;
   - a token **with** `StreamDataReader` gets rows;
   - a read by a declarative pipeline service account still succeeds: a deployed pipeline that queries stream data
     through GraphQL runs green (or the account's client-credentials token gets rows), and no `Enforce` line names it;
   - Refinery Studio shows a time-series chart for a user with the role.

   Write the evidence (query, caller, result) into a comment on the security work item.
6. **1-day watch**: the `Enforce` lines and the `OctoMeshStreamDataReadRefused` rule name no legitimate caller. A
   legitimate refusal means: grant the role or roll back (on staging-1, prod-1 and prod-2 either one only with Gerald's
   go), and record it. Then the next cluster may start.

## Rollback

Set the value back to `Warn` (or remove the key) in the cluster values and run the same octo-mesh CD again (go as in step
2 of the switch: on staging-1, prod-1 and prod-2 Gerald's go in his own words, also for a rollback); the asset
repository pods roll, no data changes. Reads refused under `Enforce` work again at once. `Off` also exists, but hides the
inventory (no lines at all): use it only as an emergency exit and go back to `Warn` afterwards.

## Last step: code default

Once all four clusters have run `Enforce` for one release train, the code default of `RoleEnforcement` and the chart default
can flip to `Enforce` (separate small PRs, CHANGELOG entries). The cluster values stay until every cluster runs a release
and chart built with the new defaults; removing them earlier turns the cluster back to `Warn`.

## Per-cluster checklist

Dates are decided by Gerald; the empty cells are filled at the gate check and the switch.

| Cluster | P1 chart ≥ 3.5.79 deployed | P3 rules live | Gate check comment (7 days, coverage, positive control) | Go (who, date) | `Enforce` set (values commit) | Smoke test comment | 1-day watch recorded |
|---|---|---|---|---|---|---|---|
| test-2 | | | | security work item approval, confirmed by the developer running the agent | | | |
| staging-1 | | | | Gerald, own words | | | |
| prod-1 | | | | Gerald, own words | | | |
| prod-2 | | | | Gerald, own words | | | |
