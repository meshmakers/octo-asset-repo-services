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
  without the principal kind; `AddJwtBearer()` is called without an argument (`docs/tenant-enforcement.md`,
  `docs/principal-kind.md`).
- A CK method's authorization takes the requested target ID into account (`docs/method-gateway.md`; PR #181).

## Queries and the runtime query guard

- Runtime-query resolvers get no guard call of their own; the field is wrapped by `RuntimeQueryGuardSchemaVisitor`, and
  count-only and grouped queries are also limited, with paging done in the repository rather than after loading every
  entity (`docs/runtime-queries.md`; PR #176).
- GraphQL types are thread-safe because they are cached, and date fields use the UTC `DateTime` scalar, never
  `DateTimeGraphType` (`docs/architecture.md`).

## Secrets and logging

- Secret attributes are never returned, neither in plain text nor as the stored envelope, and no log line contains a
  method call's target ID, its parameters or the caller's token, not even in a refusal (`docs/secret-attributes.md`,
  `docs/method-gateway.md`).

## Dependencies and stream-data methods

- A change that needs a new engine or SDK API builds against packages that CI can resolve; it does not rely on a local
  `999.0.0` build (PR #173, PR #177, PR #185).
- Stream-data method handlers return a validation failure for undefined enum values and treat a cache unload or an
  import in progress as "not ready", not as success (PR #171, PR #178).
