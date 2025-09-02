using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
///     Definition of a large binary content
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class LargeBinaryInfoDtoType : ObjectGraphType<LargeBinaryInfoDto>
{
    public LargeBinaryInfoDtoType()
    {
        Name = "LargeBinaryInfo";
        Description = "Meta information for large binaries";

        Field(x => x.BinaryId, typeof(NonNullGraphType<OctoObjectIdType>))
            .Description("Returns the id of binary");
        Field(x => x.ContentType, typeof(NonNullGraphType<StringGraphType>))
            .Description("Returns the content type of the binary");
        Field(x => x.Filename, typeof(NonNullGraphType<StringGraphType>))
            .Description("Returns the filename of the binary");
        Field(x => x.Size, typeof(NonNullGraphType<BigIntGraphType>))
            .Description("Returns the size of the binary");
        Field(x => x.DownloadUri, typeof(NonNullGraphType<UriGraphType>))
            .Description("Returns the download link of the binary");
    }
}