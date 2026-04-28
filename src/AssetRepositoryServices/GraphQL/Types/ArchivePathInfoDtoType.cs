using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// GraphQL projection of <see cref="ArchivePathInfoDto"/>. Surfaces one entry from the
/// <c>availableArchivePaths</c> query so the Refinery Studio can render a path picker.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class ArchivePathInfoDtoType : ObjectGraphType<ArchivePathInfoDto>
{
    public ArchivePathInfoDtoType()
    {
        Name = "ArchivePathInfo";
        Description = "Attribute path reachable from a CK type, suitable for use as a CkArchive column.";

        Field(x => x.Path, type: typeof(NonNullGraphType<StringGraphType>))
            .Description("Dot-separated attribute path, e.g. \"voltage\" or \"sensor.reading.value\".");

        Field<AttributeValueTypesDtoType, AttributeValueTypesDto?>("primitiveType")
            .Description("Leaf primitive type when the path terminates on a scalar; null for records.")
            .Resolve(ctx => ctx.Source!.PrimitiveType);

        Field(x => x.IsRecord, type: typeof(NonNullGraphType<BooleanGraphType>))
            .Description("True when the path terminates on a record attribute.");

        Field(x => x.IsArray, type: typeof(NonNullGraphType<BooleanGraphType>))
            .Description("True when the path traverses or terminates on an array attribute.");

        Field<StringGraphType>("recordTypeId")
            .Description("CK record id of the record terminating the path; null for scalars.")
            .Resolve(ctx => ctx.Source?.RecordTypeId?.ToString());

        Field<StringGraphType>("inheritedFromCkTypeId")
            .Description("CK type id from which the leaf attribute is inherited, when not the queried type itself.")
            .Resolve(ctx => ctx.Source?.InheritedFromCkTypeId?.ToString());
    }
}
