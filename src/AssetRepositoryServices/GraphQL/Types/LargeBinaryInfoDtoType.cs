using GraphQL.Types;
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

        Field(x => x.BinaryId, type: typeof(NonNullGraphType<OctoObjectIdType>))
            .Description("Returns the id of binary");
        Field(x => x.ContentType, type: typeof(NonNullGraphType<StringGraphType>))
            .Description("Returns the content type of the binary");
        Field(x => x.Filename, type: typeof(NonNullGraphType<StringGraphType>))
            .Description("Returns the filename of the binary");
        Field(x => x.UploadDateTime, type: typeof(NonNullGraphType<DateTimeGraphType>))
            .Description("Returns the uploaded date time of the binary");
        Field(x => x.Length, type: typeof(NonNullGraphType<BigIntGraphType>))
            .Description("Returns the lengths of the binary");
        Field(x => x.DownloadUri, type: typeof(NonNullGraphType<UriGraphType>))
            .Description("Returns the download link of the binary");
    }
}
