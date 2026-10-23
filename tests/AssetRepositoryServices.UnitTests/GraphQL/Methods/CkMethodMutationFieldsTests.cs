using FakeItEasy;
using FluentAssertions;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Caches;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Inputs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL.Methods;

/// <summary>
///     AB#5670: the schema generation of the CK method mutation fields (names, arguments, input and result types,
///     marker metadata, name clashes) on a plain mutation type, without a tenant.
/// </summary>
public class CkMethodMutationFieldsTests
{
    private static readonly RtCkId<CkTypeId> TypeId = new("Sample/Account");
    private readonly IGraphTypesCache _cache = A.Fake<IGraphTypesCache>();

    public CkMethodMutationFieldsTests()
    {
        A.CallTo(() => _cache.GetEnum(A<RtCkId<CkEnumId>>._)).ReturnsLazily((RtCkId<CkEnumId> id) =>
            new RtEnumScalarType(id));
        A.CallTo(() => _cache.GetRecordInput(A<RtCkId<CkRecordId>>._)).ReturnsLazily((RtCkId<CkRecordId> id) =>
            new RtRecordDtoInputType(id));
    }

    [Fact]
    public void InstanceMethod_GetsRtIdAndInput_StaticMethodGetsNeither()
    {
        var type = Build(
            Method("Reset-1", CkMethodKindDto.Instance, [Param("newPassword", AttributeValueTypesDto.String)]),
            Method("Lookup-1", CkMethodKindDto.Static));

        var reset = FieldOf(type, "reset");
        reset.Arguments!.Select(a => a.Name).Should().Equal("rtId", "input");
        reset.Arguments!.Find("rtId")!.ResolvedType.Should().BeOfType<NonNullGraphType>();
        var lookup = FieldOf(type, "lookup");
        lookup.Arguments!.Count.Should().Be(0);
    }

    [Fact]
    public void InputArgument_IsRequiredOnlyWhenAParameterIsRequired()
    {
        var type = Build(
            Method("Needs-1", CkMethodKindDto.Static, [Param("a", AttributeValueTypesDto.Int)]),
            Method("Optional-1", CkMethodKindDto.Static, [Param("a", AttributeValueTypesDto.Int, optional: true)]));

        FieldOf(type, "needs").Arguments!.Find("input")!.ResolvedType.Should().BeOfType<NonNullGraphType>();
        FieldOf(type, "optional").Arguments!.Find("input")!.ResolvedType.Should().BeAssignableTo<InputObjectGraphType>();
    }

    [Fact]
    public void Input_ParametersBecomeFields_RequiredNonNull_EnumAndRecordTyped()
    {
        var method = Method("Do-1", CkMethodKindDto.Static,
        [
            Param("text", AttributeValueTypesDto.String),
            Param("count", AttributeValueTypesDto.Int, optional: true),
            Param("mode", AttributeValueTypesDto.Enum, optional: true, enumId: "Sample/Mode"),
            Param("spec", AttributeValueTypesDto.Record, optional: true, recordId: "Sample/Spec"),
            Param("tags", AttributeValueTypesDto.StringArray, optional: true)
        ]);

        var types = CkMethodGraphTypes.Create(_cache, method);

        types.Input!.Name.Should().Be("SampleAccountDoMethodInput");
        types.InputRequired.Should().BeTrue();
        var fields = types.Input.Fields.ToDictionary(f => f.Name);
        fields.Keys.Should().BeEquivalentTo("text", "count", "mode", "spec", "tags");
        fields["text"].ResolvedType.Should().BeNull();
        fields["text"].Type.Should().Be(typeof(NonNullGraphType<StringGraphType>));
        fields["count"].Type.Should().Be(typeof(IntGraphType));
        fields["mode"].ResolvedType.Should().BeOfType<RtEnumScalarType>().Which.Name.Should().Be("SampleMode");
        fields["spec"].ResolvedType.Should().BeOfType<RtRecordDtoInputType>();
    }

    [Fact]
    public void Result_CarriesInvocationIdAndTheDeclaredResultOnly()
    {
        var withResult = CkMethodGraphTypes.Create(_cache,
            Method("Calc-1", CkMethodKindDto.Static, result: new CkMethodResultDto { ValueType = AttributeValueTypesDto.Int }));
        var withoutResult = CkMethodGraphTypes.Create(_cache, Method("Touch-1", CkMethodKindDto.Instance));

        withResult.Result.Name.Should().Be("SampleAccountCalcMethodResult");
        withResult.Result.Fields.Select(f => f.Name).Should().Equal("invocationId", "result");
        withoutResult.Result.Fields.Select(f => f.Name).Should().Equal("invocationId");
        withoutResult.Input.Should().BeNull();
        withResult.Result.Fields.Select(f => f.Name).Should().NotContain(["error", "success"]);
    }

    [Fact]
    public void MethodVersions_GetDistinctFieldAndTypeNames()
    {
        var type = Build(Method("Reset-1", CkMethodKindDto.Instance), Method("Reset-2", CkMethodKindDto.Instance));
        var names = type.Fields.Select(f => f.Name).ToList();

        var second = CkMethodGraphTypes.Create(_cache, Method("Reset-2", CkMethodKindDto.Instance));
        var resultName = second.Result.Name;

        names.Should().BeEquivalentTo("create", "update", "reset", "reset2");
        resultName.Should().Be("SampleAccountReset2MethodResult");
    }

    [Fact]
    public void Field_CarriesTheMethodCallMarker()
    {
        var type = Build(Method("Reset-1", CkMethodKindDto.Instance));

        var field = FieldOf(type, "reset");
        field.Metadata.Should().ContainKey(Statics.CkMethodCall);
        var info = field.Metadata[Statics.CkMethodCall].Should().BeOfType<CkMethodCallInfo>().Subject;
        info.QualifiedMethodId.Should().Be("Sample/Account.Reset-1");
        info.Kind.Should().Be(CkMethodKindDto.Instance);
        info.CkTypeId.Should().Be(TypeId);
        Statics.CkMethodCall.Should().Be("CkMethodCall");
    }

    [Theory]
    [InlineData("Create-1")]
    [InlineData("Update-1")]
    public void NameClash_WithAnExistingField_FailsTheBuildNamingTypeAndMethod(string methodId)
    {
        var act = () => Build(Method(methodId, CkMethodKindDto.Static));

        act.Should().Throw<OctoGraphQLException>()
            .Which.Message.Should().Contain("Sample/Account").And.Contain($"Sample/Account.{methodId}");
    }

    [Fact]
    public void TypeWithoutMethods_AddsNothing()
    {
        var type = Build();

        type.Fields.Select(f => f.Name).Should().Equal("create", "update");
    }

    private static FieldType FieldOf(ObjectGraphType type, string name) => type.Fields.First(f => f.Name == name);

    private ObjectGraphType Build(params CkMethodGraph[] methods)
    {
        var type = new ObjectGraphType { Name = "SampleAccountMutations" };
        type.AddField(new FieldType { Name = "create", ResolvedType = new StringGraphType() });
        type.AddField(new FieldType { Name = "update", ResolvedType = new StringGraphType() });
        CkMethodMutationFields.Add(type, TypeId, methods, m => CkMethodGraphTypes.Create(_cache, m));
        return type;
    }

    private static CkMethodGraph Method(string id, CkMethodKindDto kind, List<CkMethodParameterDto>? parameters = null,
        CkMethodResultDto? result = null) =>
        new(new CkId<CkTypeId>("Sample-1.0.0/Account-1"),
            new CkMethodDto { MethodId = id, Kind = kind, Parameters = parameters, Result = result });

    private static CkMethodParameterDto Param(string name, AttributeValueTypesDto type, bool optional = false,
        string? enumId = null, string? recordId = null) =>
        new()
        {
            Name = name,
            ValueType = type,
            IsOptional = optional,
            ValueCkEnumId = enumId == null ? null : new CkId<CkEnumId>(enumId.Replace("Sample/", "Sample-1.0.0/") + "-1"),
            ValueCkRecordId = recordId == null ? null : new CkId<CkRecordId>(recordId.Replace("Sample/", "Sample-1.0.0/") + "-1")
        };
}
