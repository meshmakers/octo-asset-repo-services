using GraphQL.Builders;
using GraphQL.DataLoader;
using GraphQL.Types;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration.DependencyInjection.Options;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     Implements an Octo query, based on a given data source
/// </summary>
internal sealed class OctoQuery : ObjectGraphType
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IDataLoaderContextAccessor _dataLoaderContextAccessor;
    private readonly IOptions<OctoAssetRepositoryServicesOptions> _options;


    internal OctoQuery(IOptions<OctoAssetRepositoryServicesOptions> options, IGraphTypesCache graphTypesCache,
        IDataLoaderContextAccessor dataLoaderContextAccessor)
    {
        _options = options;
        _dataLoaderContextAccessor = dataLoaderContextAccessor;
        Name = "OctoQuery";

        Field<CkQuery>("ConstructionKit")
            .Resolve(_ => new object());

        Field("Runtime", new RtQuery(graphTypesCache))
            .Resolve(_ => new RtEntityDto());

        
        if (graphTypesCache.GetStreamTypes().Length != 0)
        {
            // make sure to only add the stream data field if there are stream types.
            Field("StreamData", new TsQuery(graphTypesCache))
                .Resolve(_ => new RtEntityDto());
        }

        Connection<LargeBinaryInfoDtoType>("sysLargeBinaries")
            .Argument<OctoObjectIdType>(Statics.LargeBinaryIdArg, "ID of large binary that is requested.")
            .ResolveAsync(ResolveLargeBinariesQuery);
    }

    private async Task<object?> ResolveLargeBinariesQuery(IResolveConnectionContext<object?> context)
    {
        Logger.Debug("GraphQL query handling of large binaries started");

        context.TryGetArgument(Statics.LargeBinaryIdArg, out OctoObjectId key);


        var tenantContext = Helpers.GetTenantContext(context.UserContext);

        var tenantRepository = tenantContext.GetTenantRepository();
        var downloadInfo = await tenantRepository.GetLargeBinaryAsync(key);
        if (downloadInfo == null)
        {
            Logger.Warn("GraphQL query handling of large binaries failed: Large binary not found");
            return ConnectionUtils.ToConnection(Array.Empty<LargeBinaryInfoDto>(), context, 0, 0, null);
        }

        return ConnectionUtils.ToConnection(
            new[]
            {
                new LargeBinaryInfoDto
                {
                    ContentType = downloadInfo.ContentType,
                    BinaryId = downloadInfo.BinaryId,
                    Filename = downloadInfo.Filename,
                    Length = downloadInfo.Length,
                    UploadDateTime = downloadInfo.UploadDateTime,
                    DownloadUri = new Uri(_options.Value.PublicUrl.EnsureEndsWith(
                        $"/system/v1/largeBinaries?tenantId={tenantContext.TenantId}&largeBinaryId={downloadInfo.BinaryId}"))
                }
            }, context,
            0, 1, null);
    }
}