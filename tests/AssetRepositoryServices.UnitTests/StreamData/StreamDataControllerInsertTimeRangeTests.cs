using System.Text.Json;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.StreamData.Controllers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AssetRepositoryServices.UnitTests.StreamData;

/// <summary>
///     AB#5185: <c>StreamDataController.InsertTimeRange</c> must hand CLR-typed attribute values to the
///     repository. ASP.NET Core binds the body with the System.Text.Json web defaults, which leave the
///     values of an object-typed dictionary as <see cref="JsonElement" />; Npgsql cannot bind those, so
///     every point carrying attribute values answered 500. The DTO now materializes the values through
///     the shared <c>RtAttributesConverter</c>. The tests therefore bind a real JSON body with the MVC
///     defaults and assert what reaches <see cref="IStreamDataRepository.InsertTimeRangeAsync" />.
/// </summary>
public class StreamDataControllerInsertTimeRangeTests
{
    private const string TenantId = "maco";
    private const string CkTypeId = "Basic.Energy/EnergyMeasurement";

    /// <summary>The options MVC binds <c>[FromBody]</c> with when the service registers none of its own.</summary>
    private static readonly JsonSerializerOptions MvcBodyOptions = new JsonOptions().JsonSerializerOptions;

    private readonly OctoObjectId _archiveRtId = OctoObjectId.GenerateNewId();
    private readonly OctoObjectId _rtId = OctoObjectId.GenerateNewId();
    private readonly IStreamDataRepository _repository = A.Fake<IStreamDataRepository>();
    private readonly StreamDataController _controller;

    public StreamDataControllerInsertTimeRangeTests()
    {
        var tenantContext = A.Fake<ITenantContext>();
        A.CallTo(() => tenantContext.GetStreamDataRepository()).Returns(_repository);
        var systemContext = A.Fake<ISystemContext>();
        A.CallTo(() => systemContext.FindTenantContextAsync(TenantId)).Returns(tenantContext);

        _controller = new StreamDataController(
            A.Fake<ILogger<StreamDataController>>(),
            systemContext,
            A.Fake<IHostApplicationLifetime>());
    }

    [Fact]
    public async Task InsertTimeRange_HandsClrTypedAttributeValuesToTheRepository()
    {
        var body = $$"""
            [{
              "rtId": "{{_rtId}}",
              "ckTypeId": "{{CkTypeId}}",
              "from": "2026-08-31T22:00:00Z",
              "to": "2026-09-01T22:00:00Z",
              "rtWellKnownName": "meter-1",
              "attributes": {
                "Amount.Value": 10.5,
                "Amount.Count": 10,
                "Amount.Total": 2147483648,
                "Amount.Unit": "kWh",
                "Amount.Estimated": true,
                "Amount.Comment": null
              }
            }]
            """;

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(body));

        result.Should().BeOfType<NoContentResult>();
        var (archiveRtId, points) = CapturedInsert();
        archiveRtId.Should().Be(_archiveRtId);
        var point = points.Should().ContainSingle().Subject;
        point.RtId.Should().Be(_rtId);
        point.CkTypeId.Should().Be(new RtCkId<CkTypeId>(CkTypeId));
        point.From.Should().Be(new DateTime(2026, 8, 31, 22, 0, 0, DateTimeKind.Utc));
        point.To.Should().Be(new DateTime(2026, 9, 1, 22, 0, 0, DateTimeKind.Utc));
        point.RtWellKnownName.Should().Be("meter-1");

        point.Attributes.Values.Should().NotContain(value => value is JsonElement,
            "Npgsql cannot bind a JsonElement without an explicit type");
        point.Attributes["Amount.Value"].Should().BeOfType<double>().Which.Should().Be(10.5);
        point.Attributes["Amount.Count"].Should().BeOfType<int>().Which.Should().Be(10);
        point.Attributes["Amount.Total"].Should().BeOfType<long>().Which.Should().Be(2147483648L);
        point.Attributes["Amount.Unit"].Should().Be("kWh");
        point.Attributes["Amount.Estimated"].Should().Be(true);
        point.Attributes["Amount.Comment"].Should().BeNull();
    }

    [Fact]
    public async Task InsertTimeRange_KeepsIntegersIntegral_AndOnlyRealsBecomeDouble()
    {
        // Acceptance criterion: integer attribute values are stored as integers, not silently widened
        // to floating point. A JSON real that happens to be integral stays a double, as in the pipeline path.
        var body = $$"""
            [{ "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}",
               "from": "2026-08-31T22:00:00Z", "to": "2026-09-01T22:00:00Z",
               "attributes": { "Count": 0, "Negative": -42, "IntegralReal": 2.0 } }]
            """;

        await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(body));

        var attributes = CapturedInsert().Points.Single().Attributes;
        attributes["Count"].Should().BeOfType<int>().Which.Should().Be(0);
        attributes["Negative"].Should().BeOfType<int>().Which.Should().Be(-42);
        attributes["IntegralReal"].Should().BeOfType<double>().Which.Should().Be(2.0);
    }

    [Fact]
    public async Task InsertTimeRange_TreatsMissingOrNullAttributesAsEmpty()
    {
        var body = $$"""
            [{ "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}",
               "from": "2026-08-31T22:00:00Z", "to": "2026-09-01T22:00:00Z" },
             { "rtId": "{{_rtId}}", "ckTypeId": "{{CkTypeId}}",
               "from": "2026-09-01T22:00:00Z", "to": "2026-09-02T22:00:00Z", "attributes": null }]
            """;

        var result = await _controller.InsertTimeRange(TenantId, _archiveRtId.ToString(), Bind(body));

        result.Should().BeOfType<NoContentResult>();
        var points = CapturedInsert().Points;
        points.Should().HaveCount(2);
        points.Should().OnlyContain(p => p.Attributes != null && p.Attributes.Count == 0);
    }

    private static IReadOnlyList<InsertTimeRangePointRestDto> Bind(string body) =>
        JsonSerializer.Deserialize<IReadOnlyList<InsertTimeRangePointRestDto>>(body, MvcBodyOptions)
        ?? throw new InvalidOperationException("The body deserialized to null.");

    private (OctoObjectId ArchiveRtId, IReadOnlyList<TimeRangeStreamDataPoint> Points) CapturedInsert()
    {
        var call = Fake.GetCalls(_repository)
            .Should().ContainSingle(c => c.Method.Name == nameof(IStreamDataRepository.InsertTimeRangeAsync))
            .Subject;
        var points = call.GetArgument<IEnumerable<TimeRangeStreamDataPoint>>(1)
            ?? throw new InvalidOperationException("No points were passed to the repository.");
        return (call.GetArgument<OctoObjectId>(0), points.ToList());
    }
}
