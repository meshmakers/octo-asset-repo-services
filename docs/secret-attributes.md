# Secret attributes in the asset repository (AB#5528 / AB#5535)

CK attributes of value type `Secret` (concept: `octo-construction-kit-engine/docs/concept-secret-attribute-type.md`)
are never returned by this service - neither the plaintext, legacy clear text nor the stored envelope.

## Read

- Typed fields are `OctoSecretState { isSet: Boolean!, keyMissing: Boolean!, setAt: DateTime }`
  (`OctoSecretStateDtoType`; non-null when the attribute is required, the resolver always returns an object). A
  client that still selects such a field as a scalar fails validation instead of reading a credential.
- The generic projection returns `value: null` plus `RtEntityAttribute.secretIsSet`, `secretKeyMissing` and
  `secretSetAt` (all null for non-secret attributes); records follow the same rules per member.
- The state comes from `ISecretAttributeProtector.DescribeSecret` (never decrypts): a protected value whose key id
  is not in the key ring reads `isSet: false, keyMissing: true` (the ciphertext stays stored, decision 2026-10-06);
  a corrupt stored value (an `enc:v2` envelope found as a plain string) and a legacy placeholder string still
  waiting for the migration read as not set. `setAt` is the stored "set at" of a protected value (null for legacy
  values and while the MongoDB repository does not store it).
- All output paths go through `SecretAttributeProjection` and decide by the **CK attribute type**, never by
  `value is RtSecretValue` - change-stream documents carry legacy strings that are not normalised.
- `SimpleScalarType.Serialize` redacts any `RtSecretValue` to null, and the GraphQL STJ serializer has the SDK's
  `AddOctoSecretConverters()` as a last line of defence.

## Write

- Placeholders (`<…>`, `TODO_SET_*`) have no meaning on input: they are ordinary values and are encrypted like any
  other (decision 2026-10-06).
- `RtMutationBase.TryHandleAttributeAsync`: only a non-empty string sets a secret (passed to the engine as
  `RtSecretValue.Pending`; the engine encrypts). `null`, `""` and the read marker (`{ isSet }` / echoed
  `secretIsSet`) are left out of the write = unchanged; inside records the engine carries omitted members over by
  record key. A non-string input is refused; the error names the type only.
- Clearing is explicit: `clearSecretAttributes: [String!]` on the typed (`UpdateMutationDtoType`) and generic
  (`RtEntityDtoGenericUpdateType`) update inputs, mapped camelCase -> CK attribute name and passed to
  `EntityUpdateInfo.CreateUpdate(..., clearSecretAttributes)`. The engine validates it (messages 21-24, required on
  create = message 2); the errors surface as `ASSET1004` with the message numbers in `OctoDetails`.
- Persistent-query row mutations (`runtime.runtimeQuery(rtId).create/update`) refuse cells that address a secret
  with `SecretAttributeNotQueryable` (operation `query column`): secrets are not query columns, so a query row can
  neither read nor write them.

## Query

- Only `IS_NULL` / `IS_NOT_NULL` filters. `SecretQueryGuard` refuses other operators, sort, attribute search,
  aggregations, group-by, secret query columns, subscription filters and navigation lookups before the repository
  runs (the MongoDB repository enforces the same rules, including navigation paths to a target's secret).
- `SecretAttributeNotQueryableException` maps to the GraphQL error code `SecretAttributeNotQueryable` with
  `attributePath` / `operation` extensions (`HandleException` and the global `UnhandledExceptionDelegate`).
  `SecretEncryptionNotConfiguredException` maps to `SecretEncryptionNotConfigured`.
- `availableArchivePaths` omits secrets; archives cannot contain secret columns.

## No decryption

`SecretDecryptionArchitectureTests` (unit tests) fails on any call to `ISecretAttributeProtector.Unprotect`,
`GetSecretPlaintext`, `InstanceSecretCrypto.Decrypt` or `RtSecretValue.Envelope` in the service assembly; the
allowlist is empty.

## Key ring

Bound by `AddRuntimeEngine()` from the `SecretEncryption` section (`OCTO_SECRETENCRYPTION__KEYS__k1`,
`OCTO_SECRETENCRYPTION__ACTIVEKEYID`, `OCTO_SECRETENCRYPTION__LEGACYV1KEY` via the `OCTO_` environment prefix).
Never put key values into appsettings. Without keys the service starts and reads `isSet`; writing a secret fails
with `SecretEncryptionNotConfiguredException`. Integration tests configure a generated key in
`ServiceCollectionFixture`.
