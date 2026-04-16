using GraphQL;
using GraphQL.Builders;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// Cells-based stream-data row returned by the generic <c>StreamDataEntities(ckId:...)</c> connection,
/// where the CkType is supplied at query time and the output cannot be statically typed.
/// Mirrors the role that <see cref="RtEntityGenericDtoType"/> plays for runtime data.
/// </summary>
internal sealed class StreamDataEntityGenericDtoType : ObjectGraphType<StreamDataQueryRowDto>
{
    public StreamDataEntityGenericDtoType()
    {
        Name = "StreamDataEntityGeneric";
        Description = "A stream-data row returned by the generic StreamDataEntities endpoint.";

        Field(d => d.RtId, typeof(OctoObjectIdType));
        Field(d => d.CkTypeId, typeof(RtCkIdGraph<CkTypeId>));
        Field(d => d.Timestamp, typeof(DateTimeGraphType));
        Field(d => d.RtWellKnownName, true);
        Field(d => d.RtCreationDateTime, typeof(DateTimeGraphType));
        Field(d => d.RtChangedDateTime, typeof(DateTimeGraphType));

        Connection<NonNullGraphType<RtQueryCellDtoType>>("Cells")
            .Description("Selected attribute cells for this row.")
            .Resolve(ResolveCells);
    }

    private static object ResolveCells(IResolveConnectionContext<StreamDataQueryRowDto> ctx)
    {
        var row = ctx.Source;
        var cells = row.ColumnNames.Select(mapping =>
        {
            row.Values.TryGetValue(mapping.Canonical, out var v);
            return new RtQueryCellDto
            {
                AttributePath = mapping.Wire,
                Value = v
            };
        });
        return ConnectionUtils.ToOctoConnection(cells, ctx);
    }
}
