using GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Meta;

/// <summary>
///     The SDK <see cref="CkModelDto" /> plus the CK v2 model members (F1.5-S3, AB#5922), read from the persisted
///     <see cref="CkModel" />.
/// </summary>
internal sealed class CkV2CkModelDto : CkModelDto
{
    /// <summary>Declared CK language; <c>null</c> means 1.</summary>
    public int? CkLanguage { get; init; }

    /// <summary>Lowest engine version that can read the model (v2 / range-retaining models only).</summary>
    public string? MinEngineVersion { get; init; }

    /// <summary>Declared range + floor per direct dependency of a range-retaining model; <c>null</c> otherwise.</summary>
    public IReadOnlyList<CkModelDependency>? DependencyRanges { get; init; }
}

/// <summary>
///     <c>type CkModelDependencyRange</c>: a range-retaining dependency (declared range and floor version).
/// </summary>
internal sealed class CkModelDependencyRangeDtoType : ObjectGraphType<CkModelDependency>
{
    public CkModelDependencyRangeDtoType()
    {
        Name = "CkModelDependencyRange";
        Description = "Declared dependency range of a range-retaining construction kit model (CK v2).";

        Field<NonNullGraphType<StringGraphType>>("range")
            .Description("The declared range, e.g. 'System-[2.4,3.0)'.")
            .Resolve(ctx => ctx.Source.Range);
        Field<NonNullGraphType<StringGraphType>>("floor")
            .Description("The lowest version the model was compiled and validated against, e.g. '2.4.0'.")
            .Resolve(ctx => ctx.Source.Floor);
    }
}
