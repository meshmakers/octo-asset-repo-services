using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;

/// <summary>
///     <c>runtime.numberRangeGapReport(numberRange, fiscalYear)</c>: the gap report of one number range
///     (<c>System/AutoIncrement</c>, by its well-known name) and one fiscal year - the counter values from the range's
///     start value to the fiscal year's current value that no released document carries, and the values more than one
///     document carries, with every one of their documents. Read-only. The engine
///     (<see cref="ITenantRepository.GetNumberRangeGapReportAsync" />) reads the range and the documents with the
///     caller's session, so the caller's data-permission read filter applies: a caller who may not read the range gets
///     null, as for a range that does not exist, and a document the caller may not read is not counted, so its number
///     shows as missing.
/// </summary>
internal static class NumberRangeGapReportQueryField
{
    public const string FieldName = "numberRangeGapReport";
    public const string NumberRangeArg = "numberRange";
    public const string FiscalYearArg = "fiscalYear";

    public static void Add(ObjectGraphType runtimeQuery)
    {
        ArgumentNullException.ThrowIfNull(runtimeQuery);

        runtimeQuery.Field<NumberRangeGapReportGraphType>(FieldName)
            .Description("Gap report of a number range for one fiscal year: the numbers from the range's start value " +
                         "to the fiscal year's current value that no released document carries (missing) and those " +
                         "more than one document carries (duplicates). Computed from the documents the caller may " +
                         "read: a document the caller may not read counts as missing. Null when the range does not " +
                         "exist or the caller may not read it.")
            .Argument<NonNullGraphType<StringGraphType>>(NumberRangeArg,
                "Well-known name of the number range (System/AutoIncrement), the autoIncrementReference of the " +
                "document type's number attribute.")
            .Argument<IntGraphType>(FiscalYearArg,
                "The fiscal year (the calendar year it starts in). Required for a range with fiscal-year periods; " +
                "omit it for a range without periods.")
            .ResolveAsync(ResolveAsync);
    }

    private static async Task<object?> ResolveAsync(IResolveFieldContext<object?> context)
    {
        try
        {
            var userContext = (GraphQlUserContext)context.UserContext;
            var repository = userContext.TenantContext.GetTenantRepository();
            // The caller's session of the request (OctoSessionListener): the engine applies its read filter.
            var session = context.GetSessionAccessor().Session;
            return await repository.GetNumberRangeGapReportAsync(session,
                context.GetArgument<string>(NumberRangeArg), context.GetArgument<int?>(FiscalYearArg),
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            return context.HandleException(e);
        }
    }
}

/// <summary>The gap report of a number range and fiscal year (<see cref="NumberRangeGapReport" />).</summary>
internal sealed class NumberRangeGapReportGraphType : ObjectGraphType<NumberRangeGapReport>
{
    public NumberRangeGapReportGraphType()
    {
        Name = "NumberRangeGapReport";
        Description = "Missing and duplicate numbers of a number range in one fiscal year.";

        Field<NonNullGraphType<StringGraphType>>("numberRange").Resolve(c => c.Source.NumberRange)
            .Description("Well-known name of the range.");
        Field<IntGraphType>("fiscalYear").Resolve(c => c.Source.FiscalYear)
            .Description("The fiscal year; null for a range without periods.");
        Field<NonNullGraphType<LongGraphType>>("startValue").Resolve(c => c.Source.StartValue)
            .Description("First number of the checked interval (the range's StartValue, or 1).");
        Field<NonNullGraphType<LongGraphType>>("currentValue").Resolve(c => c.Source.CurrentValue)
            .Description("Last number handed out in the fiscal year (of the range without periods).");
        Field<NonNullGraphType<LongGraphType>>("documentCount").Resolve(c => c.Source.DocumentCount)
            .Description("Released documents read for the range and fiscal year.");
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<NumberRangeGapGraphType>>>>("missing")
            .Resolve(c => c.Source.Missing)
            .Description("Numbers no document carries, as ascending intervals.");
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<NumberRangeDuplicateGraphType>>>>("duplicates")
            .Resolve(c => c.Source.Duplicates)
            .Description("Numbers more than one document carries, with all of them.");
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<NumberRangeIrregularNumberGraphType>>>>("irregular")
            .Resolve(c => c.Source.Irregular)
            .Description($"Documents whose number lies outside the interval or does not have the range's format " +
                         $"(at most {NumberRangeGapReportLimits.MaxIrregular}).");
        Field<NonNullGraphType<LongGraphType>>("irregularCount").Resolve(c => c.Source.IrregularCount)
            .Description("How many irregular documents there are.");
    }
}

/// <summary>An interval of missing numbers.</summary>
internal sealed class NumberRangeGapGraphType : ObjectGraphType<NumberRangeGap>
{
    public NumberRangeGapGraphType()
    {
        Name = "NumberRangeGap";
        Description = "Counter values from 'from' to 'to' (both included) that no document carries.";
        Field<NonNullGraphType<LongGraphType>>("from").Resolve(c => c.Source.From);
        Field<NonNullGraphType<LongGraphType>>("to").Resolve(c => c.Source.To);
        Field<NonNullGraphType<StringGraphType>>("fromNumber").Resolve(c => c.Source.FromNumber)
            .Description("The formatted number of 'from'.");
        Field<NonNullGraphType<StringGraphType>>("toNumber").Resolve(c => c.Source.ToNumber)
            .Description("The formatted number of 'to'.");
    }
}

/// <summary>A number more than one document carries.</summary>
internal sealed class NumberRangeDuplicateGraphType : ObjectGraphType<NumberRangeDuplicate>
{
    public NumberRangeDuplicateGraphType()
    {
        Name = "NumberRangeDuplicate";
        Description = "A number more than one document carries.";
        Field<NonNullGraphType<LongGraphType>>("sequenceValue").Resolve(c => c.Source.SequenceValue)
            .Description("The counter value.");
        Field<NonNullGraphType<StringGraphType>>("number").Resolve(c => c.Source.Number)
            .Description("The formatted number.");
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<NumberRangeDocumentGraphType>>>>("documents")
            .Resolve(c => c.Source.Documents)
            .Description("Every document that carries the number.");
    }
}

/// <summary>A document whose number is outside the interval or not in the range's format.</summary>
internal sealed class NumberRangeIrregularNumberGraphType : ObjectGraphType<NumberRangeIrregularNumber>
{
    public NumberRangeIrregularNumberGraphType()
    {
        Name = "NumberRangeIrregularNumber";
        Description = "A document whose number lies outside the checked interval or does not have the range's format.";
        Field<NonNullGraphType<NumberRangeDocumentGraphType>>("document").Resolve(c => c.Source.Document);
        Field<NonNullGraphType<StringGraphType>>("number").Resolve(c => c.Source.Number)
            .Description("The number as stored.");
        Field<LongGraphType>("sequenceValue").Resolve(c => c.Source.SequenceValue)
            .Description("The counter value read from the number; null when the number does not have the format.");
    }
}

/// <summary>A document of the report: its id and CK type.</summary>
internal sealed class NumberRangeDocumentGraphType : ObjectGraphType<RtEntityId>
{
    public NumberRangeDocumentGraphType()
    {
        Name = "NumberRangeDocument";
        Description = "A document of the gap report.";
        Field<NonNullGraphType<StringGraphType>>("rtId").Resolve(c => c.Source.RtId.ToString());
        Field<NonNullGraphType<StringGraphType>>("ckTypeId").Resolve(c => c.Source.CkTypeId.ToString());
    }
}
