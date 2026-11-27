---
description: Unbounded runtime-entity query guard, the server-side query time limit and the runtime.byInterface query: where each is wired, limits, error codes and tests.
applies_to: **/RtEntityByInterfaceTypes.cs, **/RuntimeQueryGuard*.cs, **/RuntimeModelQuery.cs, **/RtTransientQuery.cs, **/InterfaceQuery*.cs, **/QueryArgumentException.cs, tests/**/ByInterface*.cs, **/RuntimeQueryTimeout*.cs, **/RuntimeQueryTimeLimit*.cs, **/Blueprints*.cs, **/RuntimeEngineBuilderExtensions.cs
---

# Runtime query guard and query by interface

## Unbounded runtime-entity query guard

`RuntimeQueryGuardSchemaVisitor` (registered with `AddSchemaVisitor` in `RuntimeEngineBuilderExtensions`) wraps only the
resolvers of `runtime.runtimeEntities`, `runtime.<CkType>` (metadata `CkId`), `RtQuery.rows` and `RtTransientQuery.rows`,
and of every navigation connection inside an entity (metadata `NestedNavigation`: typed navigation fields, `associations`,
`associations { targets definitions }`; to-one navigations pass); `RuntimeQueryGuard.Check` runs before the resolver. Unbounded = `items`/`edges`/`nodes` selected and no `first`, `first` above
`RuntimeQueryGuard:MaxPageSize` (default 1000), or a key lookup with more keys than that and no `first` within the limit (`RuntimeQueryGuard.Classify`);
totalCount-only, aggregation queries and key lookups within the limit are bounded (a key lookup within the limit also with
`first` above the limit). The `ck_type` label of `runtimeEntities` is the argument only for a CK type of the tenant, else `unknown`. `RuntimeQueryGuard:Mode` `Off` / `Warn`
(default: warning + counter `octo.asset.runtime_query.unbounded`) / `Enforce` (`ASSET1007`, never capped). In-process
`ITenantRepository` calls are never checked. Do not add a guard call inside a resolver: wrap the field in the visitor instead.
Details: `docs/runtime-query-guard.md`.

## Server-side time limit of runtime queries

The host opts in with `AddRuntimeQueryTimeLimit()` (in `AddOctoAssetRepositoryServices`) and binds `RuntimeQueryOptions` from
the section `RuntimeQuery` (`OCTO_RUNTIMEQUERY__MAXTIMEMS`). The engine then sends every runtime-entity query and count by
type, by id, by type with navigation and by CK interface to MongoDB with `maxTimeMS`:

| Key | Values | Default |
|---|---|---|
| `MaxTimeMs` | milliseconds; `0` switches the limit off | `120000` |

- Applies per MongoDB operation (page and count of a paged query each get the full limit), for GraphQL, REST and
  in-process `ITenantRepository` calls of this host alike. Read from the options per query; the environment variable is
  read at start-up, so a changed helm value needs a rollout.
- A query that exceeds the limit is stopped on the database and ends with `RuntimeQueryTimeoutException`.
  `RuntimeQueryTimeoutError` maps it: GraphQL `ASSET1008` (`HandleException` and the `UnhandledExceptionDelegate`) with
  `extensions.errorId` and `parameters` (`ckTypeId`, `maxTimeMs`, `elapsedMs`); REST `504` problem details with the same code
  (`RuntimeQueryTimeoutExceptionFilter`). Each time-out is logged once as a warning (tenant, CK type, duration, limit, error id).
- A query that completes after more than 80 % of its limit is logged as a warning by the engine (tenant, CK type, duration,
  limit); the client gets its result.
- Batch paths override the limit at the call site, never globally: blueprint install, update and uninstall (GraphQL
  `BlueprintsMutation`, REST `BlueprintsController`) run inside `RuntimeQueryTimeLimit.Disable()`; the engine does the same for
  CK migrations and the blueprint of a new child tenant. Exports run in the bot services, which do not opt in.
- The read-only blueprint previews keep the limit: GraphQL `blueprints.updateInfo` / `previewUpdate` and REST
  `GET blueprints/updates` / `POST blueprints/updates/preview` are open to read-only callers, so they must not start
  unlimited whole-type reads. A preview above the limit ends with `ASSET1008` / `504` like any query; the REST actions
  rethrow the time-out from their generic `catch` so that the filter answers it. The uninstall dry run (GraphQL `dryRun`,
  REST `uninstall/preview`) stays inside `Disable()`: it needs the data model management role and is part of the
  uninstall path.
- Not covered yet: association and deep-graph queries (navigation connections, `associations`, deep graph).

## Generic query: `runtime.runtimeEntities`

`ckId` is required: the engine queries the entities of one CK type. A query without `ckId` (or with `ckId: null`) is
refused before the attribute guards and any database read with `INVALID_ARGUMENT`, `extensions.argument` = `ckId`
(`QueryArgumentException`); a query the unbounded query guard refuses first (no `first`, `Enforce`) gets `ASSET1007`.
The argument stays nullable in the schema so that clients passing a nullable variable keep validating. Test:
`GenericRuntimeEntitiesArgumentTests`.

## Query by interface: `runtime.byInterface`

A thin GraphQL layer over the ONE engine method `ITenantRepository.GetRtEntitiesByInterfaceAsync` (see
`octo-construction-kit-engine-mongodb/docs/interface-query.md`): **no fan-out, merge or security logic here**. Decision of
the design spike (option A); budget, v1 scope and the refusal cases are described in that document.

- **Schema** (`RuntimeModelQuery.AddByInterfaceField`, `Types/RtEntityByInterfaceTypes.cs`): field
  `runtime.byInterface(ckInterfaceId: String!, take: Int!, skip: Int = 0, fieldFilter, sortOrder, options, searchFilter,
  aggregations, geoNearFilter)` returns `RtEntityByInterfaceConnection { totalCount: Int, items: [RtEntityByInterface!]! }`.
  The item type is the **union** `RtEntityByInterface` of all concrete object types that implement at least one CK
  interface (`IGraphTypesCache.GetCkInterfaceImplementers`): a union, so that no existing type of the schema changes (the
  schema of a tenant gets exactly the field and two types). The union resolves the type by the entity's **exact** `ckTypeId`
  (not by `IsTypeOf`, which would report a derived entity as its base type). Interface members are selected with a fragment
  on the CK interface, type specific fields with a fragment on the object type. The field is **absent** when no concrete type
  implements a CK interface (a union needs members); every tenant with the System model has implementers today.
  `take` is required (non-null), `skip` defaults to 0 (schema default `0`); `searchFilter`, `aggregations` and
  `geoNearFilter` are accepted only to be refused by the engine with the stable error (a missing argument would be a
  GraphQL validation error instead of the contract).
- **`totalCount` only when selected**: `ResolveConnectionContextExtensions.IsFieldSelected("totalCount")` decides
  `includeTotalCount` (no count query otherwise). No `pageInfo` (the engine does not report "has more"; a page shorter than
  `take` is the last).
- **Guards**: the attribute guards are bound to one type graph, so the resolver runs `SecretQueryGuard.EnsureQueryable` against
  **every** implementing type of the interface (`CkInterfaceGraph.ImplementingTypes`) before the engine call, like the
  per-type query. (`access: Hidden` attributes cannot be interface members: compiler rule 99.) Data permissions run in the
  engine per collection (a denied type never appears and is not counted).
- **Errors** (`HandleException`, before the `PersistenceException` branch): `InterfaceQueryException` ->
  code **`INTERFACE_QUERY_REFUSED`** (`Statics.GraphQlInterfaceQuery`) with `extensions.messageNumber` (always 7019),
  `extensions.reason` (a token of `InterfaceQueryReasons`) and `extensions.parameters` (dictionary of strings), passed
  through unchanged: no per-reason code, no mapping table, the message text is not a contract. A malformed argument
  (`ckInterfaceId` no runtime CK id, negative `skip`/`take`) is `INVALID_ARGUMENT` with `extensions.argument`
  (`QueryArgumentException`).
- **Transactions**: queries run in a session without transaction in the GraphQL pipeline (`OctoSessionListener` only
  starts one for mutations). Inside a transaction session (pipeline nodes, in-process callers) the engine queries the
  collections sequentially; the result is identical (test `QueryInATransactionSession_ReturnsTheSameResultAsWithout`).
- **Cost** (documented in octo-documentation `graphql`): index the sort field in the implementing type, no default
  index on `name` (decided 2026-10-19); a page costs about `(skip + take) x number of implementer collections`,
  `skip + take <= 1000`, so filter and keep pages small instead of paging deep.
- **Tests**: unit `ByInterfaceSchemaTests`; integration `GraphQL/ByInterface/ByInterfaceQueryTests` (fixture
  `ByInterfaceGraphQlTestFixture`: types `InterfaceQueryA`, `InterfaceQueryB`, `InterfaceQueryBChild` (shares the collection of
  B), `InterfaceQueryOther` (not an implementer), `InterfaceQueryGuarded` with a Secret member, interfaces `Ranked-1` and
  `Guarded-1` in the integration test CK model; a data policy protects A) and `ByInterfacePerformanceTests` (fixture
  `ByInterfaceWideGraphQlTestFixture`: generated models `Perf` with 30 roots and `Wide` with 65 roots).
- **Budget check** (`ByInterfacePerformanceTests`, trait `Category=Performance`, non-flaky style of the dispatcher
  performance test: warm-up, median / p95 of 20 runs, ratios and named-constant ceilings): the same first page through the
  bare engine call and through GraphQL (parse, validate, resolve 50 items with an interface fragment, serialize); asserts
  GraphQL median <= engine median + 25 ms, end-to-end indexed first page p95 <= 100 ms (decision budget b), and one collection
  through GraphQL <= 1.25x of the GraphQL type query (budget a, 10 ms slack). 100 000 entities by default, 1 000 000 with
  `OCTO_TEST_INTERFACE_QUERY_ENTITIES=1000000`. Measured end to end (MongoDB 8.0 Testcontainer, Mac mini, 30 collection
  roots, page 50, index `(ckTypeId, attributes.name, rtState)` on every root, run 2026-10-27):

  | Entities | Engine first page (median / p95) | GraphQL first page (median / p95) | GraphQL skip 450 | GraphQL with count | GraphQL unindexed sort (rank) | One collection: interface vs type query |
  |---|---|---|---|---|---|---|
  | 100 000 | 6.2 / 6.8 ms | 7.4 / 8.3 ms | 52.6 / 65.0 ms | 10.1 / 12.9 ms | 45 / 46 ms | 1.2 vs 3.5 ms |
  | 1 000 000 | 6.6 / 9.1 ms | 7.3 / 7.9 ms | 52.6 / 63.7 ms | 23.8 / 25.5 ms | 417 / 418 ms | 5.7 vs 29.5 ms |

  The GraphQL path adds about 1 ms to the engine call. The unindexed sort is O(matching rows) and carries no promise.
- **Schema compatibility** (verified 2026-10-27 by full introspection of the integration tenant and of a tenant with only the
  System model, with and without the field): the schema is identical except for the field `RuntimeModelQuery.byInterface` and the
  two types `RtEntityByInterface` and `RtEntityByInterfaceConnection` (877 -> 879 and 395 -> 397 types). The schema build
  time did not change measurably (877 types, median of 25 builds: 122 ms with the field, 122 ms without). The unit test
  `ByInterfaceSchemaTests` guards the "no implementer, no field" rule; `Schema_GainsExactlyTheFieldAndTwoTypes_...` the
  additions.
- **Studio codegen**: the field and the two new types change `schema.graphql`; Studio needs a refresh
  (`npm run codegen` in octo-frontend-refinery-studio) only if it wants to use `byInterface`, nothing breaks without it.
