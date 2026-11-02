using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Methods;
using Meshmakers.Octo.Services.Methods;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL.Methods;

/// <summary>
///     AB#8079: a typed handler returns the generated <c>{Type}{Method}Result { Value }</c>; what the GraphQL method field
///     serves as <c>result</c> is the bare value of the declared result type, not the wrapper's JSON text
///     (<c>"{\"value\":\"...\"}"</c>). End to end: real in-process host (handler adapter, result serialization) and the
///     gateway's mapping of the response (<see cref="CkMethodResponseMapper" />, <see cref="CkMethodResultConverter" />).
/// </summary>
public class CkMethodResultEndToEndTests
{
    private const string Tenant = MethodCkCacheFake.Tenant;
    private const string Model = "Sample.Results";
    private const string TypeName = "Thing";
    private const string RtId = "6ae7c66ccc5a608ccf779df9";

    private static readonly CkId<CkTypeId> TypeCkId = new($"{Model}-1.0.0/{TypeName}-1");
    private static readonly CkId<CkRecordId> SpecCkId = new($"{Model}-1.0.0/Spec-1");

    public sealed record NoParameters;

    // shaped like the records CkMethodCodeGenerator emits: one Value of the mapped CLR type
    public sealed record CreateResult
    {
        public string? Value { get; init; }
    }

    public sealed record CountResult
    {
        public int? Value { get; init; }
    }

    public sealed record CheckResult
    {
        public bool? Value { get; init; }
    }

    public sealed record Spec
    {
        public string? SpecName { get; init; }

        public double? SpecValue { get; init; }
    }

    public sealed record DescribeResult
    {
        public Spec? Value { get; init; }
    }

    private sealed class CreateHandler : ICkMethodHandler<NoParameters, CreateResult>
    {
        public Task<CkMethodOutcome<CreateResult>> HandleAsync(CkMethodContext<NoParameters> context,
            CancellationToken cancellationToken) =>
            Task.FromResult<CkMethodOutcome<CreateResult>>(new CreateResult { Value = RtId });
    }

    private sealed class CountHandler : ICkMethodHandler<NoParameters, CountResult>
    {
        public Task<CkMethodOutcome<CountResult>> HandleAsync(CkMethodContext<NoParameters> context,
            CancellationToken cancellationToken) =>
            Task.FromResult<CkMethodOutcome<CountResult>>(new CountResult { Value = 21 });
    }

    private sealed class CheckHandler : ICkMethodHandler<NoParameters, CheckResult>
    {
        public Task<CkMethodOutcome<CheckResult>> HandleAsync(CkMethodContext<NoParameters> context,
            CancellationToken cancellationToken) =>
            Task.FromResult<CkMethodOutcome<CheckResult>>(new CheckResult { Value = true });
    }

    private sealed class DescribeHandler : ICkMethodHandler<NoParameters, DescribeResult>
    {
        public Task<CkMethodOutcome<DescribeResult>> HandleAsync(CkMethodContext<NoParameters> context,
            CancellationToken cancellationToken) =>
            Task.FromResult<CkMethodOutcome<DescribeResult>>(
                new DescribeResult { Value = new Spec { SpecName = "Torque", SpecValue = 3.5 } });
    }

    private static readonly CkMethodDto[] Methods =
    [
        Method("Create-1", new CkMethodResultDto { ValueType = AttributeValueTypesDto.String }),
        Method("Count-1", new CkMethodResultDto { ValueType = AttributeValueTypesDto.Int }),
        Method("Check-1", new CkMethodResultDto { ValueType = AttributeValueTypesDto.Boolean }),
        Method("Describe-1", new CkMethodResultDto { ValueType = AttributeValueTypesDto.Record, ValueCkRecordId = SpecCkId })
    ];

    private readonly ICkCacheService _cache;
    private readonly ICkInProcessMethodTransport _transport;

    public CkMethodResultEndToEndTests()
    {
        _cache = FakeCache();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(_cache);
        services.Configure<CkMethodHandlerOptions>(o => o.ValidationTenantIds = [Tenant]);
        services.AddScoped<CreateHandler>();
        services.AddScoped<CountHandler>();
        services.AddScoped<CheckHandler>();
        services.AddScoped<DescribeHandler>();
        services.AddCkInProcessMethodHandlers(m => m
            .Handle<CreateHandler>(QualifiedId("Create-1"))
            .Handle<CountHandler>(QualifiedId("Count-1"))
            .Handle<CheckHandler>(QualifiedId("Check-1"))
            .Handle<DescribeHandler>(QualifiedId("Describe-1")));
        var provider = services.BuildServiceProvider();
        provider.InitializeMethodsAsync().GetAwaiter().GetResult();
        _transport = provider.GetRequiredService<ICkInProcessMethodTransport>();
    }

    [Fact]
    public async Task StringResult_ReachesTheClientAsTheBareValue()
    {
        var (response, outcome) = await CallAsync("Create-1");

        response.ResultJson.Should().Be($"\"{RtId}\"", "the wire carries the bare JSON string, not the value wrapper object");
        outcome.Error.Should().BeNull();
        outcome.Value!.Value.Should().Be(RtId);
    }

    [Fact]
    public async Task IntResult_ReachesTheClientAsANumber()
    {
        var (response, outcome) = await CallAsync("Count-1");

        response.ResultJson.Should().Be("21");
        outcome.Error.Should().BeNull();
        outcome.Value!.Value.Should().Be(21);
    }

    [Fact]
    public async Task BooleanResult_ReachesTheClientAsABoolean()
    {
        var (response, outcome) = await CallAsync("Check-1");

        response.ResultJson.Should().Be("true");
        outcome.Error.Should().BeNull();
        outcome.Value!.Value.Should().Be(true);
    }

    [Fact]
    public async Task RecordResult_ReachesTheClientWithItsAttributes()
    {
        var (response, outcome) = await CallAsync("Describe-1");

        response.ResultJson.Should().Be("{\"specName\":\"Torque\",\"specValue\":3.5}");
        outcome.Error.Should().BeNull();
        var dto = outcome.Value!.Value.Should().BeOfType<RtRecordDto>().Subject;
        var record = dto.UserContext.Should().BeOfType<RtRecord>().Subject;
        record.GetAttributeValueOrDefault("SpecName").Should().Be("Torque");
        record.GetAttributeValueOrDefault("SpecValue").Should().Be(3.5);
    }

    private async Task<(CkMethodResponse Response, CkMethodGraphQlOutcome Outcome)> CallAsync(string methodId)
    {
        var request = new CkMethodRequest(Tenant)
        {
            InvocationId = Guid.NewGuid(),
            MethodId = QualifiedId(methodId),
            CkTypeId = $"{Model}/{TypeName}",
            ParametersJson = "{}",
            Caller = new CkMethodCaller { SubjectId = "sub-1" },
            DeadlineUtc = DateTime.UtcNow.AddSeconds(30)
        };

        var response = await _transport.InvokeAsAuthorizedCallerAsync(request, TestContext.Current.CancellationToken);
        response.Success.Should().BeTrue(response.Error?.Code);

        var method = new CkMethodGraph(TypeCkId, Methods.Single(m => m.MethodId == methodId));
        return (response, CkMethodResponseMapper.Map(response, method, _cache, Tenant));
    }

    private static string QualifiedId(string methodId) => $"{Model}/{TypeName}.{methodId}";

    private static CkMethodDto Method(string methodId, CkMethodResultDto result) => new()
    {
        MethodId = methodId,
        Kind = CkMethodKindDto.Static,
        Result = result,
        Authorization = new CkMethodAuthorizationDto { Roles = [MethodCkCacheFake.DeclaredRole] }
    };

    private static ICkCacheService FakeCache()
    {
        var type = new CkTypeGraph(TypeCkId, new CkCompiledTypeDto
        {
            TypeId = new CkTypeId($"{TypeName}-1"),
            Methods = Methods.ToList()
        });
        var cache = A.Fake<ICkCacheService>();
        A.CallTo(() => cache.IsTenantLoaded(Tenant)).Returns(true);
        CkTypeGraph? found = type;
        A.CallTo(() => cache.TryGetRtCkType(Tenant, A<RtCkId<CkTypeId>>._, out found)).Returns(true)
            .AssignsOutAndRefParameters(type);

        var attributes = new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>
        {
            [new CkId<CkAttributeId>($"{Model}-1.0.0/SpecName-1")] = Attribute("SpecName", AttributeValueTypesDto.String),
            [new CkId<CkAttributeId>($"{Model}-1.0.0/SpecValue-1")] = Attribute("SpecValue", AttributeValueTypesDto.Double)
        };
        CkRecordGraph? recordGraph = new CkRecordGraph(SpecCkId, false, false, [], null, [], [], attributes, "spec");
        A.CallTo(() => cache.TryGetRtCkRecord(Tenant, A<RtCkId<CkRecordId>>._, out recordGraph)).Returns(true);
        return cache;
    }

    private static CkTypeAttributeGraph Attribute(string name, AttributeValueTypesDto type) =>
        new(new CkId<CkAttributeId>($"{Model}-1.0.0/{name}-1"), name, null, type, null, null, null, null, null, true, null);
}
