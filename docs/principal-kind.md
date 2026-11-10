# Principal kind of the caller (AB#8705)

The asset repository tells the CK engine whether a write comes from a **person** or a **service client**. CK types
with a `writeAccess` block (engine, AB#8574, `docs/ck-type-write-access.md` in `octo-construction-kit-engine`) admit
or refuse writes by this kind; types without the block ignore it. Source: `Security/CallerPrincipalKind.cs`.

## Where it is set

| Surface | How the kind reaches the engine |
|---------|---------------------------------|
| GraphQL queries and mutations (incl. CK methods) | `GraphQL.Helpers.GetSecurityContext` → `RtSecurityContext.PrincipalKind` of the request session (`OctoSessionListener`, `RtEntityMutation*`, method resolver) |
| REST (Files, LargeBinaries and every other route that opens a caller session) | the same `GetSecurityContext(HttpContext.User)` |
| `POST Models/ImportRt` (user path) | `ImportRtCommandRequest.InitiatedByPrincipalKind` → bot services `ImportModelJob` → `RtImportCallerContext.UserInitiated(subject, kind)` |

System paths (blueprint install/update, migrations, pipelines, in-process sessions without a principal) stay system
sessions and are never restricted.

## Rule

The rule uses only claims the identity service already issues; no new claim is needed and existing tokens keep
working. Claim names are read in the JWT short form and in the form the default inbound claim mapping produces.

| # | Token | Claims that decide | Kind |
|---|-------|--------------------|------|
| 1 | not authenticated | — | `Unknown` |
| 2 | **delegated / on-behalf-of** (e.g. a pipeline with `mcpDelegateToCaller`), **impersonation** | `act` present, or `amr` = `delegation` / `impersonation` | **`Client`** |
| 3 | user token: authorization code (Studio), device code (octo-cli, MCP desktop), their refresh; cross-tenant token exchange of a person's own token (`amr` = `token_exchange`, no actor) | `sub` present | `Interactive` |
| 4 | client credentials | no `sub` (stripped by the identity service by platform contract), `client_id` present | `Client` |
| 5 | anything else (authenticated, neither `sub` nor `client_id`) | — | `Unknown` |

The rules are applied in this order, so a delegated token is `Client` although it carries the user's `sub`.

**Delegated tokens count as `Client`** (decision Odin, 2026-11-10, on AB#8705; the story text first said
Interactive). A restriction to interactive writers exists to keep human-only operations human, for example setting a
knowledge page to Verified; an agent or pipeline acting on a person's behalf is an automated actor. The subject of the
session stays the user (`RtCreatedBy`, data permissions), only the kind says that an automated actor writes.

`Unknown` is refused by every restricted type (fail closed) and ignored by all others, so writes of a token without
the distinguishing claims to unrestricted types keep working.

## Compatibility

- Older bot services ignore `InitiatedByPrincipalKind`; an older asset repository sends none. Both read as
  `Unknown`: restricted types refuse the import, all other types are unaffected.
- Hangfire jobs enqueued before AB#8705 run through the overloads without the kind (`Unknown`).

## Limits

- A client that forwards a person's own access token (pass-through, no token exchange with an actor) is seen as that
  person (`Interactive`): the asset repository cannot tell it apart from the person. Service-side protections (e.g.
  the MCP knowledge tools, AB#8568) and the write channels of AB#8574 cover those paths.

## Tests

- Unit: `tests/AssetRepositoryServices.UnitTests/Security/CallerPrincipalKindTests.cs` (every token shape, mapped
  claim names), `TenantApi/ModelsControllerImportRtCallerTests.cs` (kind sent with the import command).
- Integration: `tests/AssetRepositoryServices.IntegrationTests/Security/PrincipalKindWriteAccessTests.cs` against
  the test type `AssetRepositoryIntegrationTest/InteractiveOnlyNote` (`writeAccess.principals: [Interactive]`):
  GraphQL, REST session and ImportRt user path; user tokens write, client-credentials, delegated, impersonated and
  claim-less tokens are refused with message 144 and nothing is persisted; unrestricted types accept every token.
