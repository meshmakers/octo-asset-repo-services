# Blueprint update: values a seed would blank (AB#6315)

An Upsert import replaces an entity as a whole, so a seed that carries an empty value (or omits an
attribute) used to clear what a tenant had entered (incident AB#6310, EDA adapter configuration).
The engine now keeps such tenant values (AB#6313). The API shows them **before** the update and
lets the operator confirm each blanking explicitly. All fields below are additive; a client that
sends and reads none of them keeps working and is safe by default.

## Preview

`POST {tenantId}/v1/blueprints/updates/preview` and GraphQL `blueprints.previewUpdate` return a new
list `blankedAttributes` (REST: `BlankedAttributes`), one entry per attribute whose non-empty tenant
value the seed would blank:

| Field | Meaning |
|-------|---------|
| `rtId`, `ckTypeId` | The entity |
| `attributeName` | Attribute as stored |
| `reason` | `SeedEmpty` (empty value, or JSON text emptying a string the tenant filled), `SeedOmitted` (attribute not declared by the seed) or `ResetToDefault` (the current value differs from the CK default and the incoming value is the default, AB#6395) |
| `currentSummary`, `incomingSummary` | Kind and size only, e.g. `string (223 chars)`, `empty string`, `omitted`. **Never the value** (it may be a credential) |
| `appliedOnUpdate` | Always `false` in a preview: without confirmation the update keeps the tenant value |

## Apply

`POST {tenantId}/v1/blueprints/updates/apply` and GraphQL `applyUpdate` take two new optional input
members (REST camelCase JSON, GraphQL `BlueprintUpdateRequestInput`):

| Member | Meaning |
|--------|---------|
| `allowBlanking` (bool, default `false`) | Confirms **every** listed blanking (engine policy `Allow`) |
| `confirmedBlankings` (`[{ rtId, attributeName }]`) | Confirms exactly these entity/attribute pairs; the rest stays kept. Ignored when `allowBlanking` is true. Pairs with an empty id or name are dropped, never widened |

Without either, the update proceeds and keeps the tenant values. The response lists the same
attributes with `appliedOnUpdate`: `false` = tenant value kept, `true` = blanked on confirmation.

- REST: the response is now **200 with a `BlueprintUpdateResultDto`** (`success`, entity counts,
  `warnings`, `blankedAttributes`) instead of 204 without body. Success is still any 2xx; clients
  that only check the status keep working. Failures stay 400 / 500.
- GraphQL: `BlueprintApplyResult.blankedAttributes` (empty for an install).
- A dry run (`dryRun: true`) returns the preview's list and changes nothing.

Typical flow: preview, read `blankedAttributes`, apply without flags (keeps) or with the confirmed
pairs (blanks exactly those).

## Tenant-owned seed entities the update did not write (AB#6454)

A seed entity with `rtBlueprintLocked: false` belongs to the tenant after the first install (engine
AB#6383): an update never rewrites it and never brings back one the tenant deleted. Two further lists
show this, on the preview (`POST {tenantId}/v1/blueprints/updates/preview`, GraphQL
`blueprints.previewUpdate`) and on the apply result (REST `updates/apply` `BlueprintUpdateResultDto`,
GraphQL `applyUpdate` `BlueprintApplyResult`; always empty for an install). REST uses PascalCase
(`TenantOwnedSkipped`), GraphQL camelCase:

| List | Meaning |
|------|---------|
| `tenantOwnedSkipped` | The tenant still holds the entity; the update left it untouched. `entityId` is its runtime id |
| `tenantOwnedStaysDeleted` | The previous seed contained it, the tenant deleted it, the update does not recreate it. `entityId` is `null` |

| Field | Meaning |
|-------|---------|
| `key` | The seed entity's identity: its `rtWellKnownName`, else its `rtId` |
| `ckTypeId` | Construction-kit type |
| `entityId` | Runtime id on the tenant, `null` for stays-deleted |
| `wellKnownName` | Well-known name, when the entity has one |

Identity only, **never attribute values** (tenant-owned entities may hold credentials). Both lists
are counted in `entitiesSkipped`. They are not blanking: `blankedAttributes`, `allowBlanking` and the
CLI's `--failOnBlanking` do not look at them. Additive, empty by default; a client that ignores them
behaves as before.

## No credential text in `changes` (follow-up of AB#6316 review)

The preview's per-entity `changes[].attributes[].oldValue` / `newValue` used to carry the raw text
of every attribute the update would change, which includes the blanked configuration of
`blankedAttributes` and any JSON text (the EDA adapter configuration holds host, user and password).
The engine now reports, for string attributes:

- every attribute listed in `blankedAttributes`, and
- every string whose old or new value is a JSON object or array text,

as a value-free summary in `oldValue` / `newValue` (`string (223 chars)`, `empty string`), exactly
like `currentSummary` / `incomingSummary`. A side without a value stays `null`. Other attributes keep
their values for the operator's diff. Secret-valued and tenant-owned attributes were never in
`changes` (the apply preserves them), and Secret members of records are redacted. The dry-run result
of `applyUpdate` carries no `changes` list, only counts and `blankedAttributes`.

## Tests

`tests/AssetRepositoryServices.IntegrationTests/Tenant/BlueprintUpdateBlankingFlowTests.cs` drives
`BlueprintsController` against a real MongoDB tenant (blueprint `BlankingFlowBp` 1.0.0 to 2.0.0): preview lists the
blanked attributes value-free; apply without flags keeps the tenant values and reports exactly the preview's
findings; apply with one confirmed pair blanks only that attribute; `allowBlanking` blanks all; no response
carries a tenant value. `BlueprintUpdateTenantOwnedFlowTests` (blueprint `TenantOwnedFlowBp`) covers the
tenant-owned lists: an edited tenant-owned entity is listed as skipped, a deleted one as stays-deleted, in
preview and apply, without values.
