# Review focus: octo-asset-repo-services

Check these points in addition to the general review. Each point names its source: a rule of `AGENTS.md`, a document
under `docs/`, or the pull request whose review found the problem.

## Public repository

- Changed docs, code comments and test names carry no work item IDs, internal hostnames, customer or tenant names or
  secrets, and durable docs hold no rollout inventory, open-defect status or interim plans; those go to the tracker
  (`AGENTS.md`, "Rules"; PR #181).

## Tenant enforcement and authorization

- A new endpoint that changes a tenant's data model carries `DataModelManagementPolicy` and is listed in
  `DataModelManagementAuthorizationTests.ModelChangingEndpoints` (`AGENTS.md`, "Rules"; `docs/tenant-enforcement.md`).
- A new stream-data read resolver calls `EnsureStreamReadAllowedAsync` (`docs/tenant-enforcement.md`).
- The caller's security context is built only through `GetSecurityContext`, never with `RtSecurityContext.ForUser`
  without the principal kind (`docs/tenant-enforcement.md`, `docs/principal-kind.md`).
- A CK method's authorization takes the requested target ID into account (`docs/method-gateway.md`; PR #181).

## Queries and the runtime query guard

- The runtime query guard covers exactly the fixed entry points `runtime.runtimeEntities`, every generated
  `runtime.<CkType>` connection, `RtQuery.rows` and `RtTransientQuery.rows`, plus every navigation connection inside an
  entity (field metadata `NestedNavigation`; to-one navigations pass, anything not provably to-one counts as to-many;
  `RuntimeQueryNestedNavigationGuardTests.Schema_EveryConnectionOfEntitiesInsideAnEntityIsGuarded` checks the real
  schema): a change to one of them, or a new field that
  loads runtime entities without a bound, is wrapped by `RuntimeQueryGuardSchemaVisitor`, never by a guard call inside
  the resolver; `runtime.byInterface` (bounded by its required `take`) and the aggregation connections stay unwrapped
  (`AGENTS.md`, "Rules"; `docs/runtime-queries.md`; `docs/runtime-query-guard.md`).
- GraphQL types are thread-safe because they are cached, and date fields use the UTC `DateTime` scalar, never
  `DateTimeGraphType` (`docs/architecture.md`).

## Secrets and logging

- Secret attributes are never returned, neither in plain text nor as the stored envelope (`docs/secret-attributes.md`).
- No log line contains a method call's target ID, its parameters or the caller's token, not even in a refusal
  (`docs/method-gateway.md`).

## Dependencies

- A change that needs a new engine or SDK API builds against packages that CI can resolve; it does not rely on a local
  `999.0.0` build (PR #173, PR #177, PR #185).
