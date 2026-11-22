---
description: Caller imports refuse service-managed System CK models: invariant, flow, refusal and tests.
background: true
---

# Service-managed CK models are never imported by a caller

Background of `docs/ck-model-import.md` (section "Service-managed models are never imported by a caller").

**Invariant (Security):** no request to `POST {tenantId}/v1/Models/ImportCk`, `ImportFromCatalog` or `ImportFromCatalogBatch` (octo-cli,
SDK and MCP use exactly these) caches, queues or imports a service-managed CK model - `System` or a name starting with `System.`,
compared case-insensitively after trimming - whatever its version relative to the installed one (upgrade, same version, downgrade, not
installed) and whoever calls (`DataModelManagement`, the system tenant, `forceBreakingUpdate=true`). There is no exception: the
services rely on their own models (for example on the write-channel restriction of System.Identity), so a caller must never replace
one with a self-built or older version.

- **One platform-wide name rule**, `ServiceManagedImportGuard.IsServiceManaged` (`UpgradeGuard/`), not a per-service list: System.Identity
  and System.Communication are owned by other services and are refused like the asset repository's own models.
  `CkModelLibraryStatusService.IsSystemManaged` delegates to it (case-insensitive).
- **Judged by content.** `ImportCk` reads the upload once; the refusal, the file import guard and the cache all use these bytes, so
  the import job imports exactly the bytes that were judged. For a zip (by content type, or when the bytes start with a zip header)
  every CK model entry (`.json`, `.yaml`, `.yml`, any casing) is checked, not only the entry this reader picks (the import job picks
  the first `.yaml` entry when there is no `.json` entry), so no entry choice can import an entry the check did not see. The scan is
  bounded like a `ValidateCk` source zip (at most `MaxZipEntries` = 2,000 entries and `MaxUnpackedBytes` = 50 MB unpacked, counted
  while reading; a model entry that cannot be unpacked or read as a compiled model is refused, `400`, never skipped). A source draft
  (zip with `ckModel.yaml`) is judged by the model it compiles to; only that compiled model is cached, so no other entry of the zip can
  be imported. The catalog imports check the requested ids **and** the loaded models (a harmless
  id whose catalog content is a System model is refused). One service-managed model refuses the whole batch.
- **Order**: after the tenant id and the force-policy check, before the system compatibility check, the upgrade guards, the cache
  and the queue.
- **Refusal**: `403`, `application/problem+json`, `type` `https://schemas.meshmakers.cloud/errors/ck-service-managed-model`,
  `code` `CK_SERVICE_MANAGED_MODEL`, `message` (the SDK shows it as a coded refusal), `models` (the refused ids); one WARN line
  (operation, models, tenant, caller).
- **Still installing service-managed models**: the services' embedded import at tenant resolve and setup (downgrade guard of
  engine-mongodb); to run it again for a tenant use `ClearCache` / `ReRunTenantSetup`. Blueprint and system import (catalog only,
  downgrade-guarded, service-managed models redirected to the descriptor version) and tenant restore are not changed by this rule.
- Tests: `ModelsControllerServiceManagedImportTests` (real CK serializer; one test per abuse case: higher minor, downgrade, same
  version with changed content, every System model incl. System.Identity and System.Communication, casing, harmless file name with
  System content, `forceBreakingUpdate`, zip entry choice, zip without a zip content type, zip limits, cached bytes equal judged bytes,
  both catalog transports by id and by content).
- Zip entries are buffered before they are deserialized: the serializer rewinds the stream after the schema validation, which a zip
  entry stream does not support.
