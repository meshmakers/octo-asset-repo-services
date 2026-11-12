# Unbounded runtime-entity query guard (AB#8898)

One query over a CK type with large attributes (for example `Basic.Energy/EdaMessage` with ~72 KB of raw XML per entity) that
loads every matching entity can exhaust the memory of the asset repository and cause 503s for every tenant (Feature AB#8793).
The guard finds such queries at the API boundary and, once switched to `Enforce`, refuses them before they load data.

## Scope: the API boundary only

The guard wraps the resolvers of the GraphQL entry points that load runtime entities:

| Entry point | Example |
|---|---|
| `runtime.<CkType>` | `runtime { basicEnergyEdaMessage { items { rtId } } }` |
| `runtime.runtimeEntities` | `runtime { runtimeEntities(ckId: "Basic.Energy/EdaMessage") { items { rtId } } }` |
| persisted query rows | `runtime { runtimeQuery(rtId: "…") { items { rows { items { … } } } } }` |
| transient query rows | `runtime { transientQuery { simple(…) { items { rows { items { … } } } } } }` |

SDK, CLI and MCP tools reach the asset repository through these entry points and are covered. The schema visitor
`GraphQL/Utils/RuntimeQueryGuardSchemaVisitor.cs` wraps only these fields when a tenant schema is built; every other field
resolves without overhead.

Not covered:

- **In-process engine calls** (`ITenantRepository` inside a service: communication controller, background jobs, blueprint
  install, CK migrations) are never checked, refused, logged or counted (decision 2026-11-12, AB#8793).
- **REST**: the asset repository has no REST endpoint that queries runtime entities. `Models/ExportRtByQuery` and
  `Models/ExportRtByDeepGraph` enqueue background export jobs (bot services), which are internal callers.
- `runtime.byInterface` is bounded by its required `take` (at most 1000, AB#6932).
- Navigation (association) connections inside an entity are not covered by AB#8898.

## Classification

A query is **unbounded** when it selects `items`, `edges` or `nodes` (aliases and fragments included) and

- has no `first`, or
- has `first` above `MaxPageSize`, or
- is a key lookup (`rtId` / `rtIds`) without a bounding `first` and carries more keys than `MaxPageSize`.

It is **bounded** when `first` is at most `MaxPageSize`, when it is a key lookup with at most `MaxPageSize` keys, when it
selects no items (for example `totalCount` only), or when it is an aggregation or grouping aggregation query (persisted or
transient). Code: `RuntimeQueryGuard.Classify`.

## Configuration

Section `RuntimeQueryGuard` (`RuntimeQueryGuardOptions`), environment variables with the `OCTO_` prefix:

| Key | Values | Default |
|---|---|---|
| `Mode` (`OCTO_RUNTIMEQUERYGUARD__MODE`) | `Off`, `Warn`, `Enforce` | `Warn` |
| `MaxPageSize` (`OCTO_RUNTIMEQUERYGUARD__MAXPAGESIZE`) | positive integer; values below 1 fall back to the default | `1000` |

The options are read per query (`IOptionsMonitor`).

| Mode | Unbounded query |
|---|---|
| `Off` | runs; no check, no log, no metric |
| `Warn` | runs as before; one warning per query and the metric |
| `Enforce` | refused before any database call with `ASSET1007`; warning and metric as in `Warn`; never capped |

`Warn` is the default so that a deployment without configuration shows who sends unbounded queries without breaking them.
`Enforce` is switched on per cluster only after the caller inventory (AB#8899) is closed (Slice 2, AB#8903).

## Log and metric

Warning (one per unbounded query, ids only, never a token or a user name):

```
Unbounded runtime-entity query (NoFirst) on CK type Basic.Energy/EdaMessage-1 in tenant sbeg: requested first (null),
keys (null), limit 1000, entry point TypedEntities, operation ListMessages, client octo-mcp, subject …, mode warn,
error id 3f2c… (AB#8898)
```

Counter `octo.asset.runtime_query.unbounded` (Prometheus / Dash0 PromQL: `octo_asset_runtime_query_unbounded_total`) on the
meter `Meshmakers.Octo.AssetRepository`, labels `tenant`, `ck_type`, `client_id` (`unknown` without a client id) and `mode`
(`warn` / `enforce`).

## Error ASSET1007

```json
{
  "message": "The query on CK type 'Basic.Energy/EdaMessage-1' is unbounded and was refused: it selects items without 'first'. Request at most 1000 items per page with 'first' (and 'after' for the next page), or select only 'totalCount' to count. Error id 3f2c….",
  "extensions": {
    "code": "ASSET1007",
    "errorId": "3f2c…",
    "parameters": {
      "ckTypeId": "Basic.Energy/EdaMessage-1",
      "maxPageSize": 1000,
      "requestedFirst": null,
      "keyCount": null,
      "reason": "NoFirst"
    }
  }
}
```

`reason` is `NoFirst`, `FirstAboveLimit` or `TooManyKeys`. The error id is also in the warning log line. ASSET1007 belongs to
the ASSET error family until the coded error contract of AB#6244 exists; it moves there then.

## How to page and count

- Page: `first: <n ≤ MaxPageSize>` and `after: <endCursor of the previous page>` until `pageInfo.hasNextPage` is false.
- Count: select only `totalCount` (no `items`/`edges`).

## Tests

- Unit: `tests/AssetRepositoryServices.UnitTests/GraphQL/RuntimeQueryGuardTests.cs` (classification, shape, modes, metric,
  error, which fields are wrapped).
- Integration: `tests/AssetRepositoryServices.IntegrationTests/GraphQL/Queries/RuntimeQueryGuardTests.cs` (each mode on each
  entry point, `first` at and above the limit, key lookups, totalCount-only and aggregation in `Enforce`, an in-process engine
  call without `first` in `Enforce`).
