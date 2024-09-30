using GraphQL;
using GraphQL.Resolvers;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     Implements mutations of Octo
/// </summary>
[DoNotRegister]
internal sealed class OctoMutation : ObjectGraphType
{
    public OctoMutation(IGraphTypesCache graphTypesCache)
    {
        Field("Runtime", new RtMutation(graphTypesCache))
            .Resolve(_ => new RtEntityDto());
        
        Field<CkMutation>("ConstructionKit")
            .Resolve(_ => new object());

        AddField(new FieldType
        {
            Name = "sysCreateLargeBinary",
            Description = "Uploads a large binary and stores it. ID of file is returned.",
            Type = typeof(OctoObjectIdType),
            Arguments = new QueryArguments(
                new QueryArgument(new NonNullGraphType(new LargeBinaryDtoType()))
                    { Name = Statics.LargeBinaryDataArg }
            ),
            Resolver = new FuncFieldResolver<object, OctoObjectId>(ResolveCreateLargeBinary)
        });
    }

    private async ValueTask<OctoObjectId> ResolveCreateLargeBinary(IResolveFieldContext<object> context)
    {
        var tenantContext = Helpers.GetTenantContext(context.UserContext);
        var tenantRepository = tenantContext.GetTenantRepository();

        var file = context.GetArgument<IFormFile>(Statics.LargeBinaryDataArg);
        var fileName = file.FileName;
        var contentType = file.ContentType;

        Dictionary<string, object> metadata = new();
        return await tenantRepository.UploadLargeBinaryAsync(fileName, contentType, file.OpenReadStream(),
            metadata, CancellationToken.None);
    }
}