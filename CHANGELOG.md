# Changelog

## Unreleased

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
