using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Blueprints;

/// <summary>
/// GraphQL projection of <see cref="BlueprintBackupDto"/>. Pre-operation tenant snapshot
/// produced before blueprint updates (and any other destructive blueprint flow that opts in
/// via <c>CreateBackup</c>).
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
internal sealed class BlueprintBackupDtoType : ObjectGraphType<BlueprintBackupDto>
{
    public BlueprintBackupDtoType()
    {
        Name = "BlueprintBackup";
        Description = "Tenant snapshot created before a blueprint update; usable for rollback.";

        Field<NonNullGraphType<StringGraphType>>("backupId")
            .Description("Opaque backup identifier — pass to rollback to restore this snapshot.")
            .Resolve(ctx => ctx.Source!.BackupId);

        Field<NonNullGraphType<DateTimeGraphType>>("createdAt")
            .Description("UTC timestamp when this backup was captured.")
            .Resolve(ctx => ctx.Source!.CreatedAt);

        Field<NonNullGraphType<StringGraphType>>("blueprintId")
            .Description("Fully-qualified blueprint id at the time of backup.")
            .Resolve(ctx => ctx.Source!.BlueprintId);

        Field<NonNullGraphType<StringGraphType>>("reason")
            .Description("Human-readable reason for the backup (typically the triggering operation).")
            .Resolve(ctx => ctx.Source!.Reason);

        Field<LongGraphType>("sizeBytes")
            .Description("Size of the backup payload in bytes, when reported by the storage backend.")
            .Resolve(ctx => ctx.Source!.SizeBytes);
    }
}
