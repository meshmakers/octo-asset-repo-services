# Changelog

## Unreleased

- AB#7812 New: Stream-data queries return attributes of the anchor entity of a row as columns. A column `anchor.<attributePath>`
  in the column list of a simple stream-data query (persisted `SimpleSdQuery` and transient `simple`, on raw, time-range and
  rollup archives) shows an attribute of the entity the row's `rtId` points at, for example the OBIS code and the unit of an
  energy measurement. Enum values come as names (`resolveEnumValuesToNames`), the anchors of one page are read in one query with
  the caller's data permissions (a hidden or missing anchor gives null cells), and an unknown path is refused with an error that
  names the column and the CK type. Display only in v1: an anchor column in a filter, sort order, aggregation, grouping or
  downsampling query (also a downsampling mode on `rows`) is refused, and Hidden and Secret attributes of the anchor cannot be shown. The value is the current value of the anchor. No CK model, CrateDB or rollup change; queries
  without anchor columns behave as before.
