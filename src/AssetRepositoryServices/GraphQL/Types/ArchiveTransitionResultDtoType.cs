using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;

/// <summary>
/// GraphQL projection of <see cref="ArchiveTransitionResultDto"/>. Returned by the four archive
/// lifecycle mutations.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class ArchiveTransitionResultDtoType : ObjectGraphType<ArchiveTransitionResultDto>
{
    public ArchiveTransitionResultDtoType()
    {
        Name = "ArchiveTransitionResult";
        Description = "Result of an archive lifecycle mutation: the archive's runtime id, its new status, and the transition name.";

        Field(x => x.ArchiveRtId, type: typeof(NonNullGraphType<OctoObjectIdType>))
            .Description("Runtime id of the archive that the transition applied to.");

        Field<NonNullGraphType<StringGraphType>>("status")
            .Description("New status of the archive after the transition.")
            .Resolve(ctx => ctx.Source!.Status.ToString());

        Field(x => x.Transition, type: typeof(NonNullGraphType<StringGraphType>))
            .Description("Name of the transition that was performed (Activate / Disable / Enable / RetryActivation).");
    }
}
