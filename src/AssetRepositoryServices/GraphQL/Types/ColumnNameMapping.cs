namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// Column-name pair used by cells-based stream-data resolvers to bridge the
/// PascalCase-canonical internal form and the camelCase wire form.
/// </summary>
/// <param name="Canonical">PascalCase dotted name — used to look up values in StreamDataRow.Values / StreamDataQueryRowDto.Values.</param>
/// <param name="Wire">camelCase dotted name — emitted as attributePath on the GraphQL wire.</param>
internal record ColumnNameMapping(string Canonical, string Wire);
