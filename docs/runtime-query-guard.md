---
description: Unbounded runtime-entity query guard: scope, classification, configuration, log and metric, error ASSET1007 and how to page.
background: true
---

# Unbounded runtime-entity query guard

One query over a CK type with large attributes (for example `Basic.Energy/EdaMessage` with ~72 KB of raw XML per entity) that
loads every matching entity can exhaust the memory of the asset repository and cause 503s for every tenant.
The guard finds such queries at the API boundary and, once switched to `Enforce`, refuses them before the entities are read.

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
  install, CK migrations) are never checked, refused, logged or counted (decision 2026-11-12).
- **REST**: the asset repository has no REST endpoint that queries runtime entities. `Models/ExportRtByQuery` and
  `Models/ExportRtByDeepGraph` enqueue background export jobs (bot services), which are internal callers.
- `runtime.byInterface` is bounded by its required `take` (at most 1000).
- To-one navigations inside an entity (see below).
- Association columns of persisted and transient queries (`QueryColumnPathResolver`, `RtQueryRowDtoType`): the rows are
  paged by the guarded `rows` connection, the association values of one row are not.

## Navigations inside an entity

Navigation connections inside an entity load the associated entities of each parent and are checked with the same rules
as the entry points above:

| Navigation | Example | To-one when |
|---|---|---|
| typed navigation field | `children(ckTypeIds: […]) { items { … } }`, `parent(…)` | every association behind the field name has the multiplicity `One` or `ZeroOrOne` (from the CK model, when the schema is built) |
| generic `associations` of a typed entity | `associations(roleId: "System/ParentChild", direction: INBOUND, ckId: "…") { items { … } }` | the role's multiplicity in that direction is `One` / `ZeroOrOne` (tenant CK cache) |
| `associations { targets(…) }` of a generic entity | `runtimeEntities(…) { items { associations { targets(…) { items { … } } } } }` | as above |
| `associations { definitions(…) }` of a generic entity (association records) | `definitions(roleId: "…", direction: INBOUND) { items { targetRtId } }` | as above; without `roleId` never |

- **To-one navigations are exempt** in every mode: they return at most one entity per parent. `parent` (System/ParentChild
  outbound) is to-one, `children` is to-many.
- **To-many navigations** are bounded only by `first` ≤ `MaxPageSize`, a key lookup (`rtId` / `rtIds` with at least one and
  at most `MaxPageSize` keys) or a selection without `items` / `edges` / `nodes` (`totalCount` only). `rtId: null`,
  `rtIds: null` and `rtIds: []` are **no** key lookup on a navigation: unlike the entry points, the navigation resolver
  then loads every target. `definitions(relatedRtId: …)` counts as a key lookup of one key.
- **Fail closed**: whatever is not provably to-one counts as to-many - `includeIndirect: true` (all ancestors or
  descendants), an unknown or malformed role, `direction: ANY`, a field without multiplicity metadata.
- The field is checked where it is declared (the resolver of the entity type), so aliases, fragments, inline fragments on
  interfaces or unions, `first` from a variable, mutations and subscriptions that return entities, and navigations nested
  inside navigations are all covered.
- **Once per operation**: a navigation is resolved once per parent entity. Its decision is taken once per operation,
  mode, response path (list indices left out) and facts (CK type, reason, `first`, keys): one warning and one metric
  increment, whatever the number of parents. Type-specific variants of one response path (fragments on the members of a
  union) are decided each with their own facts. The mode is read on every call, so a switch to `Enforce` applies at once,
  also on a user context that outlives one operation (a subscription connection, a batched request). In `Enforce` every
  parent's navigation is refused with `ASSET1007` and the same error id, before its load is queued; the parents themselves
  (paged by their own connection) are returned.
- Code: `RuntimeQueryGuard.CheckNested` / `IsToOneNavigation`; the fields carry the metadata `Statics.NestedNavigation`
  (and `Statics.NavigationToOne` for typed navigations), which the schema visitor wraps.

Log line of a navigation (the field path uses field names, not aliases):

```
Unbounded nested navigation (NoFirst) at runtime.basicEnergyCommunity.items.children on CK type System/Entity-2 in tenant
<tenant>: requested first (null), keys (null), limit 1000, entry point NestedTypedNavigation, operation Communities, client
octo-studio, subject …, mode warn, error id 3f2c…
```

The metric is the same counter with the same labels; `ck_type` is the navigation's target type (for typed navigations the
base type of the association, for generic ones the `ckId` argument when it is a type of the tenant, otherwise `unknown`;
for `definitions` the type of the parent entity).

Counting the targets of a navigation: a direct navigation connection that selects `totalCount` alone (typed navigation
fields, `associations(…)` of a typed entity, `associations { targets }` of a generic entity) asks the engine for its
count-only form (`take: 0`, `ResolveConnectionContextExtensions.GetAssociationTake`), which returns no target document
to the asset repository - with or without `first`, so `children { totalCount }` and `children(first: 0) { totalCount }`
cost the same. On the server the count differs by filter: without a security, id or field filter on the targets, a
dense origin (more edges than the engine's threshold) is counted on the edge index alone; with such a filter (a
restricted data permission or a `fieldFilter`) every edge runs an `_id` probe of its target in the count pipeline, which
reads the target on the server and keeps only its `_id` (no dense shortcut), so a large filtered association costs one
target read per edge. With a geospatial filter the engine counts on its item path for one item. Such a selection is
bounded and never refused. Indirect navigations (`includeIndirect: true`) pass `first` on as before.

## Classification

A query is **bounded** when it selects no `items`, `edges` or `nodes` (aliases and fragments included; for example
`totalCount` only), when it is an aggregation or grouping aggregation query (persisted or transient), when `first` is at most
`MaxPageSize`, or when it is a key lookup (`rtId` / `rtIds`) with at most `MaxPageSize` keys - a key lookup within the limit
is bounded whatever `first` says, also with `first` above `MaxPageSize` (no `FirstAboveLimit`, no metric).

Every other query is **unbounded** (`reason`):

- `NoFirst`: items selected, no `first`, no key lookup;
- `FirstAboveLimit`: `first` above `MaxPageSize`, no key lookup;
- `TooManyKeys`: a key lookup with more keys than `MaxPageSize` and no `first` within the limit.

Code: `RuntimeQueryGuard.Classify` (checks in this order: no items, aggregation, `first` within the limit, key lookup).

`first` counts with its absolute value (`RuntimeQueryGuard.EffectivePageSize`): GraphQL.NET's connection context hands the
resolvers the absolute value, so `first: -5000` loads 5000 items and is `FirstAboveLimit` (`requestedFirst` 5000);
`int.MinValue` counts as `int.MaxValue`. This holds for the entry points and the navigations.

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
| `Enforce` | refused with `ASSET1007` before the entities or rows are read (for `runtime.runtimeQuery` the stored query definition is loaded first); warning and metric as in `Warn`; never capped |

`Warn` is the default so that a deployment without configuration shows who sends unbounded queries without breaking them.
`Enforce` is switched on per cluster only after the caller inventory is closed.

## Log and metric

Warning (one per unbounded query, ids only, never a token or a user name):

```
Unbounded runtime-entity query (NoFirst) on CK type Basic.Energy/EdaMessage-1 in tenant sbeg: requested first (null),
keys (null), limit 1000, entry point TypedEntities, operation ListMessages, client octo-mcp, subject …, mode warn,
error id 3f2c…
```

Counter `octo.asset.runtime_query.unbounded` (Prometheus / Dash0 PromQL: `octo_asset_runtime_query_unbounded_total`) on the
meter `Meshmakers.Octo.AssetRepository`, labels `tenant`, `ck_type`, `client_id` (`unknown` without a client id) and `mode`
(`warn` / `enforce`). For `runtime.runtimeEntities` the `ck_type` label is the `ckId` argument only when it is a CK type of
the tenant, otherwise `unknown`, so a caller cannot create a series per input; the log line keeps the raw value.

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

`reason` is `NoFirst`, `FirstAboveLimit` or `TooManyKeys`. The error id is also in the warning log line. For a navigation
inside an entity the message starts with `The to-many navigation '<field path>' (CK type '…')` and `parameters` carries
`fieldPath` (field names joined by `.`, list indices left out); the error's own `path` points at the refused field of each
parent. ASSET1007 belongs to
the ASSET error family until a coded error contract exists; it moves there then.

## How to page and count

- Page: `first: <n ≤ MaxPageSize>` and `after: <endCursor of the previous page>` until `pageInfo.hasNextPage` is false.
- Count: select only `totalCount` (no `items`/`edges`).

## Tests

- Unit: `tests/AssetRepositoryServices.UnitTests/GraphQL/RuntimeQueryGuardTests.cs` (classification, shape, modes, metric,
  error, which fields are wrapped).
- Integration: `tests/AssetRepositoryServices.IntegrationTests/GraphQL/Queries/RuntimeQueryGuardTests.cs` (each mode on each
  entry point, `first` at and above the limit, key lookups, totalCount-only and aggregation in `Enforce`, an in-process engine
  call without `first` in `Enforce`).
- Navigations: unit `tests/AssetRepositoryServices.UnitTests/GraphQL/RuntimeQueryNestedNavigationGuardTests.cs`
  (one test per abuse case), integration
  `tests/AssetRepositoryServices.IntegrationTests/GraphQL/Queries/RuntimeQueryNestedNavigationGuardTests.cs`
  (`Car.children` to-many, `VehicleReading.parent` to-one, generic targets/definitions, and a test that every
  connection of entities inside an entity in the real schema is wrapped).
