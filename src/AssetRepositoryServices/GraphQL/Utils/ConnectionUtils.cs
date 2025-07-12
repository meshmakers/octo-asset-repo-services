using GraphQL.Builders;
using GraphQL.Types.Relay.DataObjects;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;

internal static class ConnectionUtils
{
    private const string Prefix = "arrayconnection";

    public static OctoConnection<TSource> ToConnection<TSource, TParent>(
        IEnumerable<TSource> items,
        IResolveConnectionContext<TParent> context,
        AggregationResult? aggregationResult = null,
        IEnumerable<FieldAggregationResult>? fieldAggregationResults = null,
        bool strictCheck = true
    )
    {
        var list = items.ToList();
        return ToConnection(list, context, 0, list.Count, aggregationResult, fieldAggregationResults, strictCheck);
    }

    public static OctoConnection<TSource> ToConnection<TSource, TParent>(
        IEnumerable<TSource> slice,
        IResolveConnectionContext<TParent> context,
        int sliceStartIndex,
        int totalCount,
        AggregationResult? aggregationResult = null,
        IEnumerable<FieldAggregationResult>? fieldAggregationResults = null,
        bool strictCheck = true
    )
    {
        var sliceList = slice as IList<TSource> ?? slice.ToList();

        var metrics = ArraySliceMetrics.Create(
            sliceList,
            context,
            sliceStartIndex,
            totalCount,
            strictCheck
        );

        var edges = metrics.Slice.Select((item, i) => new Edge<TSource>
            {
                Node = item,
                Cursor = OffsetToCursor(metrics.StartOffset + i)
            })
            .ToList();

        var firstEdge = edges.FirstOrDefault();
        var lastEdge = edges.LastOrDefault();

        return new OctoConnection<TSource>
        {
            Aggregation = aggregationResult,
            FieldAggregations = fieldAggregationResults,
            Edges = edges,
            TotalCount = totalCount,
            PageInfo = new PageInfo
            {
                StartCursor = firstEdge?.Cursor,
                EndCursor = lastEdge?.Cursor,
                HasPreviousPage = metrics.HasPrevious,
                HasNextPage = metrics.HasNext
            }
        };
    }

    public static string? CursorForObjectInConnection<T>(
        IEnumerable<T> slice,
        T item
    )
    {
        var idx = slice.ToList().IndexOf(item);

        return idx == -1 ? null : OffsetToCursor(idx);
    }

    public static int CursorToOffset(string cursor)
    {
        return int.Parse(cursor.DecodeBase64().Substring(Prefix.Length + 1)
        );
    }

    public static string OffsetToCursor(int offset)
    {
        return $"{Prefix}:{offset}".EncodeBase64();
    }

    public static int OffsetOrDefault(string? cursor, int defaultOffset)
    {
        if (cursor == null)
        {
            return defaultOffset;
        }

        try
        {
            return CursorToOffset(cursor);
        }
        catch
        {
            return defaultOffset;
        }
    }
}