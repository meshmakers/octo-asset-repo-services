using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.RequestHandling;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     Implements an Octo query, based on a given data source
/// </summary>
[DoNotRegister]
internal sealed class OctoQuery : ObjectGraphType
{
    public OctoQuery(ILoggerFactory loggerFactory, IGraphTypesCache graphTypesCache)
    {
        Name = "OctoQuery";

        Field<CkQuery>("ConstructionKit")
            .Resolve(_ => new object());

        Field("Runtime", new RuntimeModelQuery(loggerFactory.CreateLogger<RuntimeModelQuery>(), graphTypesCache))
            .Resolve(_ => new RtEntityDto());


        Field("StreamData", new StreamDataQuery(loggerFactory.CreateLogger<StreamDataQuery>()))
            .Resolve(_ => new StreamDataEntityDto());

        Field("Blueprints", new BlueprintsQuery(loggerFactory.CreateLogger<BlueprintsQuery>()))
            .Resolve(_ => new object());

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<ArchivePathInfoDtoType>>>>("availableArchivePaths")
            .Description("Returns the attribute paths reachable from the given CK type that may be used as columns in a CkArchive (concept §16). Bounded by maxDepth so deep records terminate predictably.")
            .Argument<NonNullGraphType<StringGraphType>>("ckTypeId", "The CK type id to introspect, e.g. \"Energy/Sensor\".")
            .Argument<IntGraphType>("maxDepth", "Maximum recursion depth into nested records. Defaults to 5.")
            .Resolve(ctx =>
            {
                var ckTypeIdRaw = ctx.GetArgument<string>("ckTypeId");
                var maxDepth = ctx.GetArgument<int?>("maxDepth") ?? 5;
                var rtCkTypeId = new RtCkId<CkTypeId>(ckTypeIdRaw);
                var gql = (GraphQlUserContext)ctx.UserContext;
                return AvailableArchivePathsResolver.Resolve(
                    ctx.GetCkCacheService(), gql.TenantId, rtCkTypeId, maxDepth);
            });
    }
}