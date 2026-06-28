using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
/// Builds the per-column reducer set used when a Simple stream-data query is executed in
/// DOWNSAMPLING mode (AB#4233). Shared by the persisted (<c>StreamDataQueryDtoType</c>) and
/// transient (<c>StreamDataTransientQuery</c>) Simple paths so both pick the same reducer per
/// value type:
/// <list type="bullet">
///   <item>numeric (Integer / Integer64 / Double) → AVG + MIN + MAX — the MIN/MAX envelope keeps
///     peaks the AVG centre line would smooth away;</item>
///   <item>string / enum / boolean / temporal → MAX, a stable representative (exact for
///     series-identifying columns that are constant within a (bin, series) group, e.g. obisCode);</item>
///   <item>record / array / binary / geospatial → skipped (not a chartable scalar).</item>
/// </list>
/// The bin timestamp is supplied separately as the "T" column.
/// </summary>
internal static class StreamDataDownsamplingReducers
{
    public static List<AggregationColumn> Synthesize(
        IReadOnlyList<string> columnPaths,
        IReadOnlyList<RtQueryColumnDto> columns)
    {
        var typeByPath = new Dictionary<string, AttributeValueTypesDto>();
        foreach (var c in columns)
        {
            typeByPath[c.AttributePath] = c.AttributeValueType;
        }

        var reducers = new List<AggregationColumn>();
        foreach (var path in columnPaths)
        {
            if (!typeByPath.TryGetValue(path, out var valueType))
            {
                continue;
            }

            switch (valueType)
            {
                case AttributeValueTypesDto.Integer:
                case AttributeValueTypesDto.Integer64:
                case AttributeValueTypesDto.Double:
                    reducers.Add(new AggregationColumn(path, AggregationFunction.Average));
                    reducers.Add(new AggregationColumn(path, AggregationFunction.Minimum));
                    reducers.Add(new AggregationColumn(path, AggregationFunction.Maximum));
                    break;

                case AttributeValueTypesDto.String:
                case AttributeValueTypesDto.Enum:
                case AttributeValueTypesDto.Boolean:
                case AttributeValueTypesDto.DateTime:
                case AttributeValueTypesDto.DateTimeOffset:
                case AttributeValueTypesDto.TimeSpan:
                    reducers.Add(new AggregationColumn(path, AggregationFunction.Maximum));
                    break;

                default:
                    // Records, arrays, binaries, geospatial — not reducible to a chartable scalar.
                    break;
            }
        }

        return reducers;
    }
}
