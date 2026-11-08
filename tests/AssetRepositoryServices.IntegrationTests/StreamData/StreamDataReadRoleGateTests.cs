using System.Security.Claims;
using FluentAssertions;
using GraphQL;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Communication.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.StreamData;

/// <summary>
///     AB#8559: stream-data reads through the GraphQL layer require one of the tenant roles StreamDataReader,
///     StreamDataWriter or StreamDataAdmin, staged by <c>StreamDataReadAuthorization:RoleEnforcement</c>
///     (Off / Warn / Enforce). A role-less token reads in Off and Warn and is refused with
///     <c>StreamDataReadRoleRequired</c> in Enforce; a token with a stream-data role and an in-process (system)
///     execution read in every mode.
/// </summary>
[Collection(StreamDataCollection.Name)]
public class StreamDataReadRoleGateTests(StreamDataFixture fixture, ITestOutputHelper output)
{
    public enum Caller
    {
        Reader,
        Writer,
        Admin,
        NoStreamDataRole,
        System
    }

    public enum QueryKind
    {
        Simple,
        Aggregation,
        Downsampling
    }

    public static TheoryData<StreamDataReadRoleEnforcementMode, Caller, QueryKind> Matrix()
    {
        var data = new TheoryData<StreamDataReadRoleEnforcementMode, Caller, QueryKind>();
        foreach (var mode in Enum.GetValues<StreamDataReadRoleEnforcementMode>())
        foreach (var caller in Enum.GetValues<Caller>())
        foreach (var kind in Enum.GetValues<QueryKind>())
        {
            data.Add(mode, caller, kind);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task StreamDataRead_FollowsTheRoleRequirement(StreamDataReadRoleEnforcementMode mode, Caller caller,
        QueryKind kind)
    {
        fixture.OutputHelper = output;
        var options = fixture.Provider!.GetRequiredService<IOptionsMonitor<StreamDataReadAuthorizationOptions>>()
            .CurrentValue;
        var previous = options.RoleEnforcement;
        options.RoleEnforcement = mode;
        try
        {
            var result = await fixture.ExecuteGraphQlAsync(Query(kind), user: Principal(caller));
            output.WriteLine(fixture.SerializeGraphQl(result));

            var denied = mode == StreamDataReadRoleEnforcementMode.Enforce && caller == Caller.NoStreamDataRole;
            if (denied)
            {
                result.Errors.Should().NotBeNullOrEmpty();
                result.Errors!.Should().Contain(e => e.Code == "StreamDataReadRoleRequired");
                result.Errors!.Select(e => e.Message).Should()
                    .Contain(m => m.Contains(CommonConstants.StreamDataReaderRole));
            }
            else
            {
                result.Errors.Should().BeNullOrEmpty($"{caller} reads stream data in mode {mode}");
                fixture.SerializeGraphQl(result).Should().MatchRegex("\"totalCount\":[1-9]",
                    "the read returns the archive's rows");
            }
        }
        finally
        {
            options.RoleEnforcement = previous;
        }
    }

    [Fact]
    public void Gate_IsRegisteredWithTheDefaultWarnMode()
    {
        var options = fixture.Provider!.GetRequiredService<IOptions<StreamDataReadAuthorizationOptions>>().Value;

        options.RoleEnforcement.Should().Be(StreamDataReadRoleEnforcementMode.Warn);
        fixture.Provider!.GetService<Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils.StreamDataReadRoleGate>()
            .Should().NotBeNull();
    }

    private static ClaimsPrincipal? Principal(Caller caller)
    {
        var role = caller switch
        {
            Caller.Reader => CommonConstants.StreamDataReaderRole,
            Caller.Writer => CommonConstants.StreamDataWriterRole,
            Caller.Admin => CommonConstants.StreamDataAdminRole,
            Caller.NoStreamDataRole => "MeterReporter",
            _ => null
        };
        if (role == null)
        {
            // In-process execution without a principal: the internal (system) path.
            return null;
        }

        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("client_id", $"ab8559-{caller}"), new Claim("role", role)], "Bearer"));
    }

    private string Query(QueryKind kind) => kind switch
    {
        QueryKind.Simple => $$"""
            {
                streamData {
                    transientStreamDataQuery {
                        simple(archiveRtId: "{{fixture.ArchiveRtIdString}}", columnPaths: ["Voltage"], first: 10) {
                            items { rows(first: 10) { totalCount } }
                        }
                    }
                }
            }
            """,
        QueryKind.Aggregation => $$"""
            {
                streamData {
                    transientStreamDataQuery {
                        aggregation(
                            archiveRtId: "{{fixture.ArchiveRtIdString}}"
                            columnPaths: [{ attributePath: "Voltage", aggregationType: AVG }]
                            first: 10
                        ) {
                            items { rows(first: 10) { totalCount } }
                        }
                    }
                }
            }
            """,
        _ => $$"""
            {
                streamData {
                    transientStreamDataQuery {
                        downsampling(
                            archiveRtId: "{{fixture.ArchiveRtIdString}}"
                            columnPaths: [{ attributePath: "Voltage", aggregationType: AVG }]
                            limit: 4
                            from: "{{fixture.TestDataStartTime:O}}"
                            to: "{{fixture.TestDataStartTime.AddHours(1):O}}"
                            first: 10
                        ) {
                            items { rows(first: 10) { totalCount } }
                        }
                    }
                }
            }
            """
    };
}
