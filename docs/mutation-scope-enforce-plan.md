---
description: Operator plan for switching the GraphQL mutation scope gate from log-first to enforced per environment.
background: true
---

# Switching the GraphQL mutation scope gate to enforced per environment

This is the operator plan for the step after the GraphQL mutation scope gate ran log-first. The gate itself is described
in [tenant-enforcement.md](tenant-enforcement.md#authorization-graphql-mutation-scope-staged); the method gateway, which
checks the scope on its own, in [method-gateway.md](method-gateway.md).

| `GraphQl:EnforceMutationScope` | What a generic GraphQL mutation with a token without scope `octo_api` gets |
|---|---|
| `false` (code and chart default, log-first) | runs; one warning per mutation |
| `true` | GraphQL error with code `Statics.GraphQlForbidden` ("GraphQL mutations require the 'octo_api' scope."), nothing runs, no log line of the gate |

Nothing in this document changes an environment. Every switch, scope grant, test client and rollback is an environment
write that the ops agent executes per cluster. Who gives the go: on **test-2** the approval of the security work item that
owns the switch (the developer who runs the agent confirms it in the session); on **staging-1, prod-1 and prod-2** Gerald,
in his own words, for that cluster and that step, a rollback included.

## What the gate protects, and what it does not

The gate protects **every GraphQL mutation of the asset repository** (`tenants/{tenantId}/graphQl`, operation type
`mutation`): runtime entity create, update and delete, associations, blueprints, stream-data writes through GraphQL and
the other generic mutations. The endpoint itself only requires an authenticated caller (`AuthenticatedUserPolicyGraphApi`,
bearer or cookie), so log-first lets a token with only `octo_api.read_only`, or with no API scope at all, change tenant
data. The REST surface already requires `octo_api` for writes; enforcing closes the same gap on GraphQL.

It does **not** cover, by design:

- **CK method mutations.** The method gateway requires `octo_api` for every method call independent of this flag
  (`METHOD_FORBIDDEN`); they are protected in both modes.
- **Queries and subscriptions.** Only operations of type `mutation` are checked; reads follow the tenant gates, the data
  policies and the stream-data read gate.
- **Roles and tenant.** The gate checks the scope only. Which tenant a token may address and which roles a mutation needs
  are decided by the tenant gates and the role and data-policy checks, which run in both modes.
- **Pipelines in the mesh adapter** that write through the engine in-process; they never reach the GraphQL endpoint. A
  pipeline that calls the GraphQL API runs as the HTTP caller or as the adapter's pipeline service account and is checked
  like any other caller.

Consequence for the gate check: only the warnings of this gate count. A caller that never mutates through GraphQL does not
appear in any mode, whatever scopes its token has.

## Order

`test-2` → `staging-1` → `prod-1` → `prod-2`, one cluster at a time. A cluster is switched only after the previous one has
run enforced for **1 day** (the observation window) without a refusal of a legitimate caller (step 6 below). The approval
of one cluster does not carry over to the next. Clusters outside this list stay log-first until they get their own plan
entry.

## Preconditions per cluster

- **P1** the octo-mesh chart deployed on the cluster renders `services.assetRepository.enforceMutationScope`: the first
  chart release that contains it (3.5.80 or later; check the chart version of the cluster's last octo-mesh CD run, not
  only the pin on `main`). An older chart ignores the key without an error and the pod stays log-first.
- **P2** the asset repository on the cluster runs a release with the gate (any release since the gate shipped; the
  environment variable `OCTO_GRAPHQL__ENFORCEMUTATIONSCOPE` is rendered once P1 holds).
- **P3** alerting: a Dash0 check rule counts the log-first warnings of the gate on the cluster (logger
  `MutationScopeListener`, message `would be rejected once GraphQl:EnforceMutationScope is enabled`). The enforced refusal
  writes no log line of its own and the tenant-authorization counters do not see it, so a rule on refusals needs a signal
  first; until one exists, the 1-day watch (step 6) reads the GraphQL error code in the asset-repository request logs or
  relies on reports of the affected callers, and the plan records that limitation per cluster. The positive control
  (below) must show up in the rule.
- **P4** the local end-to-end check of the gate enforced is done (once, before the first cluster): a mutation with a token
  without `octo_api` gets the forbidden error, the same mutation with `octo_api` succeeds.

Record P1 to P4 per cluster in a comment on the security work item before the gate check.

## Gate (per cluster, before the switch)

**An empty result only counts when the query is proven to see the cluster's gate lines** (otherwise the gate fails open: a
dataset without asset-repository logs, or a changed logger name or text, also returns zero rows). Both of these are part of
every gate check, on every cluster:

- **Coverage**: the cluster's Dash0 dataset holds log lines of the asset-repository container on every day of the 7-day
  window (count per day > 0, filtered on the container, not on the logger). Most lines of the container come from engine
  loggers (`Meshmakers.Octo.Runtime.Engine.…`); the service's own loggers
  (`Meshmakers.Octo.Backend.AssetRepositoryServices.%`) write so little at log level `Warn` that a quiet day reads as a
  gap. A day without lines of the container is a gap: extend the window until 7 covered days are reached, or record the
  gap and do not switch.
- **Positive control**: right before the gate check, run one harmless GraphQL mutation on the cluster with a test token
  that carries `octo_api.read_only` but not `octo_api` (a client of an internal tenant, created for the test and named in
  the pre-write list; for example an update of an entity the test owns). Log-first lets it run; the gate query must
  return its line (subject = the test client id) and the rule of P3 must count it. Without that line the result is "not
  checked", not clean.

Then, over the **last 7 days** before the gate check, the warnings of the gate (apart from the positive control) show no
legitimate caller that would be refused. Every subject found is, before the switch, either

1. **given the scope**: its client gets `octo_api` in its allowed scopes, and the caller requests it (a client-credentials
   call names the scope; an interactive app lists it in its OIDC scopes). Write down the octo-cli command that was run:

   ```powershell
   octo-cli -c AddScopeToClient -id "<client id>" -n "octo_api"
   ```

   Granting a scope is a write on the cluster's identity configuration: on staging-1, prod-1 and prod-2 it needs Gerald's go
   like the switch itself; or
2. **declared not legitimate** (the caller must not mutate through GraphQL, for example a read-only reporting client;
   enforcing refuses it on purpose), with the reason.

A subject whose last line is older than its scope grant is resolved; lines keep showing it until the window has passed.

### Log line (exact text, from `MutationScopeListener`)

`Warning`, logger `Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling.MutationScopeListener`:

```
GraphQL mutation by subject '<subject>' without the 'octo_api' scope — would be rejected once GraphQl:EnforceMutationScope is enabled (… log-first)
```

The text in brackets at the end names the change that introduced the gate; search for the fixed part before it.

`<subject>` is the token's `sub` (a user) or, for a client-credentials token, its `client_id`; `<unknown>` when neither
is present. The structured fields arrive as the log attributes `Subject` and `Scope` (`Scope` is the **required** scope
`octo_api`, not the token's scopes); the record also carries `RequestPath` (`/tenants/<tenant>/graphQl`, the tenant) and
`RequestId`. The text names no tenant and, for a user, not the client the user signed in with: ask the user's tenant
administrator for that. A token value is never logged. The line is **not
rate-limited**: one line per mutation, so the line count is the mutation count.

### Dash0 queries

The Dash0 dataset is named after the cluster (`k8s.cluster.name` is empty; select the dataset). The logger is the
attribute `logger`; the whole line is the body and the log attribute `dash0.log.message`. Logs with D0QL (`sql`), time
range = the last 7 days:

```sql
-- Gate check: subjects the switch would refuse
SELECT log_attributes['Subject'] AS subject, count() AS mutations,
       min(timestamp) AS first_seen, max(timestamp) AS last_seen
FROM logs
WHERE log_attributes['logger'] = 'Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling.MutationScopeListener'
  AND body LIKE '%would be rejected once GraphQl:EnforceMutationScope is enabled%'
GROUP BY subject ORDER BY first_seen LIMIT 200

-- Gate lines per day (D0QL has no toDate; cut the date from the timestamp). For coverage, run the same per-day count
-- filtered on the asset-repository container of the cluster (resource attribute k8s.container.name).
SELECT substring(toString(timestamp), 1, 10) AS day,
       countIf(body LIKE '%would be rejected once GraphQl:EnforceMutationScope is enabled%') AS gate_lines,
       count() AS lines
FROM logs
WHERE log_attributes['logger'] LIKE 'Meshmakers.Octo.%'
GROUP BY day ORDER BY day
```

Cross-check with the body alone (`body LIKE '%GraphQl:EnforceMutationScope%'` must return the same lines) and open single
lines with `getLogRecords` (filter `otel.log.body` contains `EnforceMutationScope`) where a subject needs more context
(pod, time of day). If the `Subject` attribute is empty, group by the body instead. The attribute `RequestPath`
(`/tenants/<tenant>/graphQl`) names the tenant of the mutation; group by it as well when a subject needs its tenant.
D0QL rejects `position()`, `CASE`, `toDate` and `toStartOfDay`; use `LIKE`, `multiIf` and `substring`.

## Exact value

In `octo-mesh-deployment`, `clusters/<cluster>/values-octo-mesh.yaml`:

```yaml
services:
  assetRepository:
    # GraphQL mutation scope gate: enforced since <date>, go: <who, date>, exit: set false and run the octo-mesh CD
    enforceMutationScope: true   # true | false -> OCTO_GRAPHQL__ENFORCEMUTATIONSCOPE
```

Without the key the chart renders `false`. A non-boolean (also the string `"true"`), a misspelled key under
`services.assetRepository` or the key under another service fails the render. `kubectl set env` is not a substitute (the
next CD run undoes it). The service reads the value when the options are first built, so it takes effect with the pod
restart of the CD's rolling update.

## Switch, per cluster

1. **Gate check**, recorded as a comment on the security work item: P1 to P4, coverage per day and the positive control
   (time, test client, and that the query and the rule saw it), the queries with their time range, every subject found
   with its mutation count and last line, and for each the scope grant (with the command) or the reason it is not
   legitimate.
2. **Go** for this cluster: on test-2 the approval of the security work item, confirmed by the developer who runs the
   agent; on staging-1, prod-1 and prod-2 Gerald's go, in his own words.
3. **Change** (ops): PR in `octo-mesh-deployment` that sets the value with the comment above, plus the pre-write impact
   list: the rendered diff of the octo-mesh release changes only `OCTO_GRAPHQL__ENFORCEMUTATIONSCOPE` on the asset
   repository deployment.
4. **Roll out** with the cluster's octo-mesh CD run.
5. **Smoke test** right after the rollout, with the harmless mutation of the positive control:
   - the test token **without** `octo_api` (scope `octo_api.read_only`) gets the GraphQL error with the forbidden code and
     the message "GraphQL mutations require the 'octo_api' scope.", and nothing changed;
   - the same mutation with a token **with** `octo_api` succeeds;
   - Refinery Studio saves an entity change for a user of an internal tenant;
   - a pipeline service account still mutates: a deployed pipeline that writes through GraphQL runs green (or the account's
     client-credentials token runs the mutation).

   Remove the test client afterwards. Write the evidence (mutation, caller, result) into a comment on the security work
   item.
6. **1-day watch**: no legitimate caller is refused (P3 signal or caller reports; a solution frontend or integration that
   can no longer save is the typical symptom). A legitimate refusal means: grant the scope or roll back (on staging-1,
   prod-1 and prod-2 either one only with Gerald's go), and record it. Then the next cluster may start.

## Rollback

Set the value back to `false` (or remove the key) in the cluster values and run the same octo-mesh CD again (go as in step
2 of the switch: on staging-1, prod-1 and prod-2 Gerald's go in his own words, also for a rollback); the asset repository
pods roll, no data changes. Mutations refused while enforced work again at once and the warnings come back.

## Last step: code default

Once all four clusters have run enforced for one release train, the code default of `EnforceMutationScope` and the chart
default can flip to `true` (separate small PRs, CHANGELOG entries). The cluster values stay until every cluster runs a
release and chart built with the new defaults; removing them earlier turns the cluster back to log-first.

## Per-cluster checklist

Dates are decided by Gerald; the empty cells are filled at the gate check and the switch.

| Cluster | P1 chart deployed | P3 alert rule | Gate check comment (7 days, coverage, positive control) | Go (who, date) | `true` set (values commit) | Smoke test comment | 1-day watch recorded |
|---|---|---|---|---|---|---|---|
| test-2 | | | | security work item approval, confirmed by the developer running the agent | | | |
| staging-1 | | | | Gerald, own words | | | |
| prod-1 | | | | Gerald, own words | | | |
| prod-2 | | | | Gerald, own words | | | |
