---
description: Attribute access, CK interfaces, visibility, record member preservation, identity-owned write guards and the CK v2 introspection.
applies_to: **/Types/Meta/**, **/Types/Rt*.cs, **/Mapping/**, **/OctoBuilder.cs, **/RecordMember*.cs, **/HiddenAttribute*.cs, **/*WriteGuard*.cs, **/AccessQuery*.cs
---

# CK language 2 on the GraphQL surface

## CK v2

CK language 2 (`ckLanguage: 2`) adds attribute `access`, CK interfaces, `visibility`, `derivable` and methods to the
construction kit. Phase 1 brought the meta-model to the GraphQL surface. The method **runtime** is shipped (Phase 3):
typed method mutations (`docs/method-gateway.md`), the gateway gate (`docs/method-gateway.md`) and data
permissions (`docs/method-in-process.md`), the dispatcher of octo-common-services with handlers over the
bus, in-process (gateway-owned methods) and as pipelines, caller-token forwarding, idempotency keys, and asynchronous methods
with operation handles and `runtime.operations` (`docs/method-gateway.md`). The handler SDK and the dispatcher live in
octo-common-services (`src/Methods`, `octo-common-services/docs/method-handler-sdk.md`); user docs: octo-documentation
`technologyGuide/constructionKits/ckLanguage2/` (`methods`, `graphql`, `methodHandlers`, `methodConsistency`).

**Schema-build timing:** `SchemaContext` logs the build time per tenant (see `docs/architecture.md`).
Baseline for the integration test CK model (`SchemaBuildTimingTests`): ~100–250 ms cold, of which ~75–200 ms is
schema initialization.

**Attribute `access`:**
- `OctoBuilder.Attribute` is the single chokepoint: `Hidden` attributes get no field on entity, abstract-type
  interface, CK interface, input, `…InputUpdate` and record types; `MethodOnly` attributes are readable but not
  part of the input types. `ReadOnly` attributes stay in the input types (the typed create and update share one input
  type), but are writable on create only (next bullet).
- Generic projections skip `Hidden`: the `attributes` connection of entities (`RtEntityGenericDtoType`), records
  (`RtRecordDtoType`) and associations (`RtAssociationDtoType`). When the CK type / record / role is unknown to the
  CK cache, the stored attributes are projected without CK knowledge (`UnknownCkTypeAttributeProjection`) — every
  name that is Hidden anywhere in the tenant is dropped there (fail closed, review L11).
- Writes: `RtMutationBase.TryHandleAttributeAsync` applies the engine rule `AttributeAccess.IsGenericallyWritable(access,
  isCreate)` to the typed and generic create/update paths (also to record members, which follow the create/update of the
  entity): `Hidden` / `MethodOnly` are never writable, `ReadOnly` only on create - refused with `ATTRIBUTE_NOT_WRITABLE`
  ("Attribute '…' of '…' is not writable via generic mutations (access: …)."), nothing written; query-row creates/updates
  (`QueryMapper` → `AccessQueryGuard.EnsureWritablePath(…, isCreate)`) apply the same rule to their cells.
  `ImportRt` and blueprint seeds keep ReadOnly importable by design (engine `IsWritableOutsideMethod`).
- Queries: the engine's query column collector and the repository do not know `access`, so the asset-repo guards
  every entry point (`AccessQueryGuard`):
  - `WithoutHiddenColumns` filters `availableQueryColumns` and `QueryColumnPathResolver.GetColumnsForPaths` (all
    transient/persisted runtime queries); a requested hidden column fails with `ATTRIBUTE_NOT_QUERYABLE`.
  - Root queries: filters (all operators, also `IS_NULL`), sort, attribute search, aggregations and group-by are
    checked inside `SecretQueryGuard.EnsureQueryable`.
  - **Association / navigation connections (review H1)**: typed navigation fields (`RtEntityDtoType.ResolveAssociationQuery`),
    the typed generic `associations(...)` connection (`ResolveGenericRtAssociationsQuery`, direct and indirect),
    generic `associations { targets(...) }` (`RtEntityGenericAssociationType`) and the (currently unused)
    `RtEntityAssociationType` call `EnsureQueryOptionsAllowed(targetType, queryOptions)` before the options reach
    `GetRtAssociationTargetsAsync` / `GetIndirectRtAssociationTargetsAsync`. Paths are checked against the target
    type and every type derived from it (a polymorphic `System/Entity` target is rejected when a derived type hides
    the attribute); an unknown target is checked by name. **Any new resolver that passes `RtEntityQueryOptions` to
    the repository must call `EnsureQueryOptionsAllowed` (or `SecretQueryGuard.EnsureQueryable`) first.**
  - Entity selectors (`members.someType[passwordHash='X']->name`, review L10 / re-review N1) are an equality
    oracle: the selector is stripped for column matching but evaluated during cell building. Every **requested or
    stored** column path is therefore validated raw, before matching, by `AccessQueryGuard.EnsureColumnPathsAllowed`
    (called from `QueryColumnPathResolver.GetColumnsForPaths`, `QueryMapperEngine` before stored selectors become
    navigation filters, and `RtMutationBase.GetRtQueryRowResultSet`): hidden paths and hidden selector keys →
    `ATTRIBUTE_NOT_QUERYABLE`, Secret selector keys → `SecretAttributeNotQueryable`. Navigation lookups in
    query-row writes (`QueryMapperEngine.EvaluateNavigationFilters`) reject hidden keys as well.
  - **Stored queries are checked when they are saved**: `RtMutationBase.ApplyChangesAsync` (typed and generic
    create/update/replace) calls `StoredQueryWriteGuard.EnsureStoredQueriesAllowedAsync` for every entity of a type derived from
    `System/PersistentQuery`. Field filters, sort orders, columns (strings or aggregation records), group-by columns and
    attribute-search paths are checked with `AccessQueryGuard.EnsureStoredQueryPathsAllowed` against the query type and every
    derived type (unknown or missing query type: by name, fail closed); on an update the effective query (update over the stored
    entity) is judged, so retargeting `QueryCkTypeId` under an existing filter is refused too. Refusal: `ATTRIBUTE_NOT_QUERYABLE`,
    nothing stored, no value in the error. Why: running such a query here was already refused, but the export job (bot-services) and
    the pipeline nodes (`GetQueryById@1`, notifications) apply a stored filter as it is - they check stored queries themselves too,
    because a query can also arrive through `ImportRt`, a restore or an older version. Not covered at save time: `ImportRt` and the
    query-row mutations. Tests: `UnitTests/GraphQL/StoredQueryWriteGuardTests`, `IntegrationTests/GraphQL/CkV2/StoredQueryHiddenAttributeTests`.
  - Every segment of a record path counts: a Hidden (or, for writes, MethodOnly) record-valued attribute also
    protects `record.field` (re-review M7 gap).
  - Paths across a navigation (`->`), selector keys and paths against an unknown type are matched by attribute name
    against every hidden assignment of the tenant (conservative, fail closed). A tenant whose CK cache is not loaded
    makes the guard throw instead of reporting "nothing hidden".
  - The hidden-name set is computed once per loaded CK model graph (cached against the cache's type collection
    instance, which is replaced on every cache reload; review M8).
- Both codes come from `HiddenAttributeAccessException`, mapped by `HandleException` and the GraphQL
  `UnhandledExceptionDelegate` (`TryCreateAttributeAccessError`).
- CK meta: `CkTypeAttribute.access: String!` (`ReadWrite` / `ReadOnly` / `MethodOnly` / `Hidden`).
- **Export / import** (review H2/H3/H4, closed by the engine): RT export skips Hidden values, RT import and
  blueprint seed refuse Hidden / MethodOnly values (engine message 139). Pinned for the real System.Identity user by the
  credential guard tests (below).
- **Credential guard regression tests**: `tests/AssetRepositoryServices.IntegrationTests/Security/IdentityCredentialGuard*Tests`
  run every GraphQL, export, import and blueprint-seed path against the **real** System.Identity model (compiler output in
  `testData/ck-models/`, loaded by `Fixtures/SystemIdentitySnapshotCkModel`; refresh procedure in the README there) with a user whose
  `PasswordHash`, `SecurityStamp` and `UserTokens` hold fixed dummy values; no response, export file or captured log line may contain
  them. Path table: the README of the integration test project. Open gaps are skipped tests with the bug in the skip reason; none is left (the former
  `Gap_GenericCreate_OfAUser_IsReplacedByCreateUser` passes since System.Identity 3.0.0: CK meta `replacesGenericCreate` is `true`). The identity store write is checked: `TheIdentityStoreWrite_LeavesNoCredentialInTheMongoCommandLog` checks the captured Debug command
  log of the identity store write, non-vacuous (the rendered insert must be in the capture). A new user is refused on every create path
  (`GENERIC_CREATE_REPLACED_BY_METHOD` naming `User.CreateUser-1`, `docs/method-in-process.md`), so the create tests of these suites assert that refusal. A stored query whose filter or selector reaches a credential is refused when it is saved (`StoredQuery_WithACredentialFilter_IsRefusedWhenSaved`, `EntitySelector_InAStoredQuery_IsRefused`).
- **Identity-owned User fields and identity associations**: the identity-owned User attributes `NormalizedUserName`,
  `NormalizedEmail`, `EmailConfirmed`, `PhoneNumberConfirmed`, `TwoFactorEnabled`, `LockoutEnabled`, `ResetPasswordOnLogin`, `Claims`, `UserLogins`,
  `PreferredChannelBindingId`, `LockoutEnd`, `AccessFailedCount` are `access: MethodOnly` in System.Identity 3.0.0 and refused by the **generic access
  guard** (`ATTRIBUTE_NOT_WRITABLE` with the access detail; typed inputs do not offer them; query rows: `AccessQueryGuard.EnsureWritablePath`). Nothing
  of the attribute list lives here any more (the former `IdentityOwnedWriteGuard.EnsureAttributeWritable` / `EnsureWritablePath` are removed; the 2.x guard
  answered with the extra extension `reason = identity-owned`). `GraphQL/Utils/IdentityOwnedWriteGuard` still refuses, for every caller, the association
  roles listed in the engine's `IdentityOwnedWrites.AssociationRoleIds` (ConstructionKit.Contracts): `AssignedRole`, `GroupMember`, `ChildGroup`, `MayActAs`,
  `GrantsPermission` (insert and delete, from either end). **Why the associations stay in a list:** the CK access model has no association-level write
  restriction (`access` exists only on attributes). Call sites: `RtMutationBase.ApplyChangesAsync` (all associations of the typed and generic
  create/update) and `RtQueryMutation` create/update (query-row navigation associations). Refusal: `ATTRIBUTE_NOT_WRITABLE`
  (`HiddenAttributeAccessException.IdentityOwned`) with the extra extension `reason = identity-owned`; nothing is written. Only the identity service writes
  these values (its REST API and the identity methods, which carry the role-change guard and the audit); it uses its own repository, which never passes here.
  The engine refuses the same associations on the caller path of `ImportRt` (message 140) and the attributes through rule 139. Not covered here:
  `PolicyPermission`, `IdentifiesUser` (open), the mesh-adapter load nodes. Tests: `UnitTests/GraphQL/IdentityOwnedWriteGuardTests`, the tests in
  `IntegrationTests/Security/IdentityCredentialGuard{GraphQl,Exchange}Tests`. A tenant still on System.Identity 2.x (until the identity service has imported 3.0.0)
  has no MethodOnly on these attributes and no list here: roll the identity service out together with this one.
- **Stream data** (review M12 / G3 E-M2): no archive column may reach a Hidden attribute. The engine refuses to
  activate such an archive and fails an active one when a model change makes a column reach Hidden
  (`ArchiveHiddenColumnGuard`, `ArchiveLifecycleService`). The asset-repo additionally refuses every stream-data
  query path that reaches Hidden (`StreamDataHiddenGuard` over `ArchiveHiddenColumnGuard.FindHiddenAttribute`: a
  Hidden segment, or a whole-record column whose record contains a Hidden sub-attribute) with
  `ATTRIBUTE_NOT_QUERYABLE`: columns, group-by, sort (incl. runtime overrides), filters (all operators and values incl. `IS_NULL`,
  transient and query-time; every stored filter of a persisted query, in Rows and in the Aggregations
  sub-connection — the stored operator enum has no null operators) and aggregations, on the transient `simple` / `aggregation` /
  `groupingAggregation` / `downsampling` queries, persisted `streamDataQuery` rows and both `aggregations`
  sub-connections. The check runs inside `StreamDataFieldValidation.ValidateStreamDataFields` (required parameter) and
  explicitly in the aggregation and runtime-override resolvers. Tests: `StreamData/StreamDataHiddenAttributeTests`.
- **Whole-record updates keep the members the caller may not write** (closes G3 review A-M2): a typed or generic
  update that writes a record (or record array) attribute rebuilds the record from its input fields; an explicit value for a
  `Hidden` / `MethodOnly` / `ReadOnly` member is refused, so without help an omitted one would be cleared.
  `GraphQL/RecordMemberPreservation` (called from `RtMutationBase.PreserveProtectedRecordMembersAsync` in both update resolvers, after
  the input is built) loads the stored entity - only when a written record has such a member at any depth - and carries the stored
  values in: a single record by position, a record-array element by its `recordKey` (same matching as the engine's Secret carry-over,
  `SecretWriteNormalizer.FindByRecordKey`; a new element has nothing to keep, a removed element takes its members with it), nested
  records recursively. A record array whose record has no usable key (none, or the key itself is not writable on update) cannot be
  matched: the update is refused with `ATTRIBUTE_NOT_WRITABLE` when a stored element holds a value in a protected member, and allowed
  when none does. Secret members are left to the engine (record key). `null` for the whole record attribute still removes it. Same idea
  as identity-owned attributes on `ImportRt` (preserved on omission). `clearSecretAttributes` is a write too: clearing a Secret
  that is Hidden / MethodOnly / ReadOnly is refused with `ATTRIBUTE_NOT_WRITABLE` (`MapClearSecretAttributes`). Tests: `UnitTests/GraphQL/RecordMemberPreservationTests`,
  `IntegrationTests/GraphQL/CkV2/RecordMemberPreservationTests` (test type `RecordAccessThing`, records `AccessTestDetail`,
  `AccessTestInner`, `AccessTestKeyedEntry`).
- **Query-row updates of record paths change exactly their path**: `RtQueryMutation.ResolveUpdate` builds the update
  document from the cells (`QueryMapper`, path-wise `RtPathEvaluator.SetValue`), and the repository `$set`s every top-level attribute
  of that document as a whole. A cell `detail.label` alone would therefore replace the stored record with `{ label }` and drop every
  other member (protected or not). Before the cells are applied, `CopyStoredRecordsAsync` reads the stored entity (request session =
  caller's data permissions, inside the request transaction; only when a cell writes a record member) and
  `GraphQL/Mapping/QueryRowRecordMerge` copies the stored value of each record / record array attribute whose member a cell writes
  into the document, **without Secret members** (the engine carries them over). The cells then change only their path: a
  single record and nested records keep all other members; a record array element is addressed by position (`entries[0].label`,
  the form `availableQueryColumns` offers) and changed in place, so it keeps its members with or without a record key - no element
  matching is needed, so the keyless refusal (message 141) does not apply here; other elements stay as stored.
  A cell that names the record attribute itself is not merged; an explicit value for a protected member is refused before anything
  is written (`AccessQueryGuard.EnsureWritablePath`). Tests: `UnitTests/GraphQL/QueryRowRecordMergeTests`,
  `IntegrationTests/GraphQL/CkV2/QueryRowRecordMemberPreservationTests` (record `AccessTestKeyedDetail`: record key plus Hidden,
  MethodOnly and Secret members; the Secret test pins the engine carry-over for a single record and by record key).
- **GraphQL name collisions** (G3 review A-L1/A-L2, backlog): two CK elements that map to the same GraphQL name (e.g.
  a literal `Named2` next to `Named-2`, or names colliding with the generated `…Interface`, `…Input`, `…Update` names)
  make schema initialization throw, and the whole tenant schema is unavailable. `SchemaContext` builds every tenant
  under one process-wide semaphore and does not cache failures, so a broken tenant serialises all cache-miss builds.
  Compiler rule I-5 only compares exact type/interface names.
- **`ReadOnly`**: set on create, never changed by a generic update, typed update or query-row update
  (`ATTRIBUTE_NOT_WRITABLE`); e.g. the `LockoutEnd` / `AccessFailedCount` of a user change only through the identity service.
  Tests: `GraphQL/CkV2/ReadOnlyAttributeTests` (test attribute `AccessTestAccount.ExternalReference`) and
  `Security/IdentityCredentialGuardGraphQlTests.GenericUpdate_OfAReadOnlyLockoutAttribute_IsRefused`.
- Tests: `CkAttributeAccessSchemaTests`, `AccessQueryGuardTests` (unit); `GraphQL/CkV2/HiddenAttributeTests` and
  `HiddenAttributeNavigationTests` (integration, test types `AccessTestAccount` --`AccessTestMembership`-->
  `AccessTestGroup`; the test model is `ckLanguage: 2`).


**CK interfaces:**
- Each CK interface of the tenant (`ICkCacheService.GetRtCkInterfaces`) becomes a GraphQL interface
  `CkInterfaceGraphType` named without suffix (`System.Identity/Named-1` → `SystemIdentityNamed`; abstract-type
  interfaces keep `<Type>Interface`). **Naming rule** (`Statics.GetGraphQlPascalCaseName`, all CK element kinds):
  model and element name without separators, element version > 1 appended without the dash
  (`System.Identity/Named-2` → `SystemIdentityNamed2`), so successive interface versions coexist in one schema (the
  dash produced an invalid GraphQL name before). Fields: the system fields of `RtEntityInterfaceType` plus one field per member
  (optional members nullable). Built in `GraphTypesCache.PopulateAsync` after enums/records and before the types,
  registered in `GetKnownGraphTypes` (introspection and fragments).
- `GraphTypesCache.GetImplementedInterfaces` also returns the CK interfaces in `CkTypeGraph.AllImplementedInterfaces`
  (own and inherited). An **optional member the type does not assign** is added to the object type as a nullable
  field that always resolves to `null` — GraphQL requires every interface field on the implementing object, and
  rules I-1..I-4 only cover required members (e.g. `Named-1.Description` on `Role`).
- **Interface inheritance (`extends`):** a CK interface's fields are its `AllAttributes` (own plus every
  extended interface's members), and the GraphQL interface implements all its (transitive) parent interfaces
  (`LinkExtendedInterfaces`, GraphQL interface inheritance). Implementing object types list the full closure — the
  engine's `CkTypeGraph.AllImplementedInterfaces` already contains the extended interfaces.
- **Interface association members** become interface fields after all object types are populated
  (`AddInterfaceAssociationFields`, parents first): when **every** implementing object type has the outbound
  association field of that role with the same name, connection type name and arguments, the interface gets the same
  field (shared connection type and arguments); a child interface reuses the field of its parent. Otherwise the member
  is omitted with a **Warning** (members without any implementor: Debug). Inbound association members are not exposed.
- **Associations narrowed to an interface** (`targetCkInterfaceId` on a type association): the navigation union
  only contains the concrete targets that implement the interface.
- When the field alignment in `RtEntityDtoType` still fails, `ReportInterfaceNotImplemented` logs a **Warning** for CK
  interfaces (Debug for abstract-type interfaces, where it is expected for overridden associations) instead of
  skipping silently.
- `runtime.byInterface` returns entities by CK interface (see `docs/runtime-queries.md`);
  `GetRtEntitiesByType ckInterfaceId` (pipeline node) is separate work.
- Tests: `CkInterfaceSchemaTests` (unit), `GraphQL/CkV2/CkInterfaceIntrospectionTests` (integration, test interfaces
  `AssetRepositoryIntegrationTest/Labeled-1` implemented by `AccessTestAccount` and `AccessTestGroup`, and `Member-1`
  extending `Labeled-1` with the `AccessTestMembership` association member, implemented by `AccessTestAccount`;
  `AccessTestGroup.LinksToLabeled` is narrowed to `Labeled-1`).


**CK meta introspection:** the `constructionKit` API describes the CK v2 meta model. The SDK
meta DTOs carry no CK v2 members, so the new fields read the tenant's CK cache graph (`Types/Meta/CkMetaGraphLookup`)
or the persisted `CkModel`; elements missing from the cache report the v1 defaults. Enumerations are strings with the
engine's PascalCase names, like `access`.

| Type | New fields |
|---|---|
| `CkType` | `visibility` (`Public`/`Internal`), `derivable` (`Any`/`Model`; ckLanguage 2 defaults to `Model`), `declaredInterfaces: [CkInterface!]!`, `interfaces: [CkInterface!]!` (declared + inherited + extended), `methods: [CkMethod!]!` (own + inherited, see `declaringCkTypeId`) |
| `CkTypeAttribute` | `access` (`ReadWrite`/`ReadOnly`/`MethodOnly`/`Hidden`) |
| `CkRecord` | `visibility`, `derivable` |
| `CkEnum`, `CkAttribute`, `CkAssociationRole` | `visibility` |
| `CkModel` | `ckLanguage: Int!` (1 when not declared), `minEngineVersion`, `dependencyRanges: [CkModelDependencyRange!]` (`range`, `floor`; null for classic exact-pinned models) |
| `CkInterface` (new) | `ckInterfaceId`, `rtCkInterfaceId`, `description`, `visibility`, `deprecated`, `extends`, `allExtends`, `attributes: [CkInterfaceAttribute!]!` (`ckAttributeId`, `attributeName`, `attributeValueType`, `isOptional`; incl. extended members), `associations: [CkInterfaceAssociation!]!` (`ckAssociationRoleId`, `targetCkTypeId`, `targetCkInterfaceId`, `multiplicity`, `isOptional`, `declaringCkInterfaceId`), `methods`, `implementingTypes` |
| `CkMethod` (new, definitions only) | `methodId`, `qualifiedMethodId` (type methods), `declaringCkTypeId` / `declaringCkInterfaceId`, `kind` (`Instance`/`Static`), `description`, `visibility`, `parameters: [CkMethodParameter!]!` (`name`, `valueType`, `valueCkRecordId`, `valueCkEnumId`, `isOptional`, `sensitive`, `description`), `result: CkMethodResultDefinition` (`valueType`, `valueCkRecordId`, `valueCkEnumId`, `sensitive: Boolean!` - the compiled `result.sensitive` marker from the runtime CK cache, `false` when undeclared; additive on this v2-only meta type), `errors: [CkMethodErrorDefinition!]!`, `authorization: CkMethodAuthorization!` (`roles`, `allowSelf`, `scopes`), `execution: CkMethodExecution!` (`timeoutSeconds` effective, `idempotent`, `handler: String!` effective handler kind `service` (default when undeclared) or `pipeline`; `mode: String!` effective execution mode `sync` (default when undeclared) or `async` (the call returns an operation handle); both additive on this v2-only meta type, so existing queries and the Studio v1 queries are unaffected) |
| `ConstructionKitQuery` | `interfaces(ckModelIds, rtCkId, rtCkIds)` connection |

`CkMethodErrorDefinition` is deliberately not named `CkMethodError` (that name belongs to the invocation error of the
method runtime, `CkMethodError` of octo-common-services). `internal` elements are listed like public ones — they are internal
to models, not secret. Tests: `GraphQL/CkV2/CkMetaIntrospectionTests` (incl. `Schema_HasNoMethodErrorTypes`: a failed method
**invocation** is one GraphQL error with `extensions.code` / `parameters`, so the schema has no `CkMethodError` /
`CkMethodErrorDetail` type and a sync result type has no error member). An asynchronous method that was accepted can still fail
later; that failure is not a GraphQL error of the mutation but the `error` (code, parameters) of its operation, read through
`runtime.operations` (see `docs/method-gateway.md`). **Schema export for frontend codegen:** run
`OCTO_SCHEMA_EXPORT_PATH=<file> dotnet test tests/AssetRepositoryServices.IntegrationTests -c DebugL --filter
"FullyQualifiedName~SchemaExportTests"` — writes the SDL of the integration-test tenant (all CK meta types plus the
CK v2 test model's runtime types); the Studio's own `schema.graphql` stays an introspection of a real tenant.
