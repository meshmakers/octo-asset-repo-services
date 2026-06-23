namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// Column-name pair used by cells-based stream-data resolvers to bridge the engine-side
/// storage key and the GraphQL wire form.
/// </summary>
/// <param name="Canonical">
/// Storage key — used to look up values in <c>StreamDataRow.Values</c>. For simple queries
/// this is the lower-case concatenated CrateDB column name produced by
/// <see cref="Meshmakers.Octo.Runtime.Engine.CrateDb.ColumnNameMapper.PathToColumnName"/>
/// (e.g. <c>obiscode</c>, <c>sensorreadingvalue</c>). For aggregation queries this is the
/// engine's <c>{CrateDbName}_{lowercaseFunction}</c> output key (e.g. <c>voltage_avg</c>).
/// </param>
/// <param name="Wire">
/// Wire alias — emitted verbatim as <c>cell.attributePath</c> in the GraphQL response.
/// For simple queries this is the caller's requested column string, echoed back so client
/// grids can bind their <c>field</c> directly to the saved query columns. For aggregation
/// queries this is the <c>{CrateDbName}_{lowercaseFunction}</c> form by convention — the
/// frontend's <c>QueryResultsPanelComponent.toWireAggregationFieldName</c> mirrors it.
/// </param>
internal record ColumnNameMapping(string Canonical, string Wire);
