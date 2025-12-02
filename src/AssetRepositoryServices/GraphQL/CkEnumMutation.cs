using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class CkEnumMutation : ObjectGraphType
{
    private readonly ILogger<CkEnumMutation> _logger;

    public CkEnumMutation(ILogger<CkEnumMutation> logger)
    {
        _logger = logger;

        Name = "CkEnumMutations";

        Field<ListGraphType<CkEnumDtoType>>("updateValueExtensions")
            .Description("Updates customizations of enum extensions.")
            .Argument<NonNullGraphType<ListGraphType<CkEnumUpdateDtoType>>>(Statics.ValuesArg, "The ID of the enum.")
            .ResolveAsync(ResolveUpdateValueExtensions);
    }

    private async Task<object?> ResolveUpdateValueExtensions(IResolveFieldContext<object?> arg)
    {
        try
        {
            _logger.LogDebug("ResolveUpdateValueExtensions");

            if (arg.Parent == null)
            {
                throw AssetRepositoryException.ParentUnavailable();
            }


            var keysList = new List<CkId<CkEnumId>>();
            if (arg.Parent.TryGetArgument(Statics.CkIdArg, out string? key))
            {
                keysList.Add(new CkId<CkEnumId>(key));
            }

            if (arg.Parent.TryGetArgument(Statics.CkIdsArg, null, out IEnumerable<string>? keys))
            {
                keysList.AddRange(keys.Select(k => new CkId<CkEnumId>(k)));
            }

            // If argument defined, but empty array, do not return any data. That must be a mistake by client (otherwise
            // all entities are returned)
            if (!keysList.Any() && (arg.HasArgument(Statics.CkIdArg) || arg.HasArgument(Statics.CkIdsArg)))
            {
                return new List<CkEnumDto>();
            }

            var enumUpdateDtos = arg.GetArgument<List<CkEnumUpdateDto>>(Statics.ValuesArg);
            if (enumUpdateDtos == null)
            {
                throw AssetRepositoryException.ArgumentMissing(Statics.ValuesArg);
            }

            var ckEnumUpdates = enumUpdateDtos.Select(CkEnumUpdateDtoType.CreateCkEnumValueDto).ToList();

            var graphQlUserContext = (GraphQlUserContext)arg.UserContext;
            var tenantContext = graphQlUserContext.TenantContext;

            foreach (var ckEnumId in keysList)
            {
                await tenantContext.CustomizeCkEnumAsync(ckEnumId, ckEnumUpdates);
            }

            var sessionAccessor = arg.GetSessionAccessor();

            var queryOptions = RtEntityQueryOptions.Create();
            var resultSet = await tenantContext.GetTenantRepository()
                .GetCkEnumAsync(sessionAccessor.Session, keysList, queryOptions);

            return resultSet.Items.Select(CkEnumDtoType.CreateCkEnumDto);
        }
        catch (Exception e)
        {
            return arg.HandleException(e);
        }
    }
}