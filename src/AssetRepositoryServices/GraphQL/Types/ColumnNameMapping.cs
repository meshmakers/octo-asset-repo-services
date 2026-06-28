using Meshmakers.Octo.ConstructionKit.Contracts;

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
/// <param name="EnumId">
/// When the column maps to an enum-typed CK attribute, the id of the referenced CK enum.
/// <c>StreamDataQueryRowDtoType.ResolveCells</c> uses it to resolve the raw integer key
/// stored in CrateDB to the enum value name — parity with the runtime query path, which
/// resolves enums via <c>AttributeValueResolveFlags.ResolveEnumsToNames</c>. Null for
/// non-enum columns and for aggregation columns whose function does not preserve the source
/// value (COUNT/SUM/AVG produce derived numbers that are not enum keys).
/// </param>
internal record ColumnNameMapping(string Canonical, string Wire, CkId<CkEnumId>? EnumId = null);
