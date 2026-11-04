# Changelog

## Unreleased

- AB#8275 New: the REST rollup list `GET streamdata/archives/{archiveRtId}/rollups` returns `referenceTimeZone` and
  `dayStartOffsetMs` per rollup, with the same values as GraphQL `rollupsFor` (null when the rollup has none), so the SDK,
  octo-cli `ListRollupsForArchive` and MCP `list_rollups_for_archive` can show a gas-day rollup (`Europe/Vienna`, `21600000` =
  06:00). Additive JSON fields; the other fields and the response shape are unchanged.
- AB#7361 New: gas days through GraphQL. `createRollupArchive` takes the optional `dayStartOffsetMs` (local day start of a
  `CALENDAR_DAY` rollup in ms after local midnight in `referenceTimeZone`, e.g. `21600000` = 06:00); the engine validates it at
  create time and an invalid value (outside `[0, 86400000)`, not a whole minute, another alignment, a chained rollup with another
  day start or zone) is refused as STREAMDATA_ERROR naming `DayStartOffsetMs`; an unknown `referenceTimeZone` now answers
  a message naming the field instead of the masked "An error occurred". `rollupsFor` returns `dayStartOffsetMs`.
  The transient `downsampling` query takes `timeZone`, `binInterval` (whole days) and `binOrigin` (`HH:mm`): with any of them the
  bins are local days (23/24/25 h across DST, cut like `CALENDAR_DAY` rollups) and `limit` is no longer required (it became
  optional; without the new arguments it is still required). `resolveSeriesQuery` takes `dayStartOffsetMs` or `binOrigin` and sets
  the engine's `QueryDayStartOffsetMs`, so a gas-day request is answered from the gas-day rollup and a midnight request never from
  gas days (before, every request counted as a midnight request). Invalid arguments are refused naming the argument. The schema
  change is additive (new optional fields and arguments, `limit` relaxed from `Int!` to `Int`). Persisted `RtDownsamplingSdQuery`
  keeps its shape. Raw-archive local-day bins need the engine-mongodb package of AB#7360.
- AB#8228 Fix: the asset repository recovers without a restart after a tenant's MongoDB user was dropped and re-created. A request that
  fails with a MongoDB authentication error (error 13 / 18, failed SCRAM) evicts the tenant's cached MongoDB clients, at most once per 15 s and
  tenant, so the next request authenticates anew. GraphQL errors never reach the HTTP pipeline and resolvers map their own exceptions, so the
  eviction is called from the central error mapping (`HandleException`) and from the unhandled-exception hook.
  Needs the common-services package of AB#8228 (`TenantDatabaseUserHealer.EvictOnAuthenticationFailureAsync`).
  Round 3: the CK model observability sweep (background read per tenant) evicts on an authentication failure too; REST controllers that answer
  their own 500 (e.g. `ModelsController`) are covered by the common-services result filter (`TenantPoolEvictionResultFilter`).
- AB#8168 Fix: an aggregation with several STATE_DURATION columns on the same attribute (for example state 0 and state 100) no longer
  fails with ASSET1002. A single STATE_DURATION column keeps the result key `<attribute>_stateduration`; when the same attribute has
  several, each key carries its comparison value (`position_stateduration_0`, `position_stateduration_100`; rule:
  `StreamDataAggregationOutputNaming` in the engine: the comparison value is trimmed and lower-cased, ASCII letters and digits are kept,
  `-` becomes `neg`, every other character becomes `_`, runs of `_` are collapsed and leading/trailing `_` removed, a value with nothing left
  becomes `x`; `low  power` gives `low_power`, `-1.5` gives `neg1_5`).
  Clients that read the result by key must use that key for those columns. Needs the engine package of AB#8168.
- AB#7812 New: Stream-data queries return attributes of the anchor entity of a row as columns. A column `anchor.<attributePath>`
  in the column list of a simple stream-data query (persisted `SimpleSdQuery` and transient `simple`, on raw, time-range and
  rollup archives) shows an attribute of the entity the row's `rtId` points at, for example the OBIS code and the unit of an
  energy measurement. Enum values come as names (`resolveEnumValuesToNames`), the anchors of one page are read in one query with
  the caller's data permissions (a hidden or missing anchor gives null cells), and an unknown path is refused with an error that
  names the column and the CK type. Display only in v1: an anchor column in a filter, sort order, aggregation, grouping or
  downsampling query (also a downsampling mode on `rows`) is refused, and Hidden and Secret attributes of the anchor cannot be shown. The value is the current value of the anchor. No CK model, CrateDB or rollup change; queries
  without anchor columns behave as before.
