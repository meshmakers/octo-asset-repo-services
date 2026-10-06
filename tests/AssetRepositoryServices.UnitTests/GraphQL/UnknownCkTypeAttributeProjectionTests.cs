using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Utils;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL;

/// <summary>
///     AB#5535: the generic attribute projection must not fail a list query for an entity whose CK type is not in
///     the CK cache (live: an outdated <c>System.Communication/AiConfiguration</c> entity), must not look the type up
///     for an empty <c>attributeNames</c> filter, and must still never return a recognisable secret.
/// </summary>
public class UnknownCkTypeAttributeProjectionTests
{
    private const string TenantId = "unit-tenant";
    private const string FakePlaintext = "fake-unknown-type-secret";
    private static readonly RtCkId<CkTypeId> UnknownTypeId = new("Outdated.Model/RemovedType");

    [Fact]
    public void EmptyFilter_ReturnsNothing_WithoutTouchingTheCkCache()
    {
        var cache = A.Fake<ICkCacheService>(o => o.Strict());

        var result = RtEntityGenericDtoType.CreateAttributeDtos(cache, null, null, TenantId,
            CreateEntity(), UnknownTypeId, Array.Empty<string>(), false);

        result.Should().BeEmpty();
    }

    [Fact]
    public void EmptyFilter_OnAssociation_ReturnsNothing_WithoutTouchingTheCkCache()
    {
        var cache = A.Fake<ICkCacheService>(o => o.Strict());

        var result = RtAssociationDtoType.CreateAttributeDtos(cache, null, null, TenantId, new RtAssociation(),
            new RtCkId<CkAssociationRoleId>("Outdated.Model/RemovedRole"), Array.Empty<string>());

        result.Should().BeEmpty();
    }

    [Fact]
    public void UnknownType_ProjectsStoredAttributes_AndMasksRecognisableSecrets()
    {
        var cache = CreateLoadedCacheWithoutTypes();
        var setAt = new DateTime(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc);
        var entity = CreateEntity(new Dictionary<string, object?>
        {
            ["Name"] = "outdated",
            ["ApiKey"] = RtSecretValue.Protected(Envelope("k1"), setAt),
            ["LegacyEnvelope"] = Envelope("k1"),
            ["StoredDocument"] = new Dictionary<string, object?> { ["_t"] = "OctoSecret", ["e"] = "x" },
            ["Tags"] = new List<object> { "a", RtSecretValue.Pending(FakePlaintext) },
            ["Count"] = 3
        });

        var result = RtEntityGenericDtoType.CreateAttributeDtos(cache, CreateProtector(), null, TenantId, entity,
            UnknownTypeId, null, false);

        result.Select(a => a.AttributeName).Should()
            .BeEquivalentTo("name", "apiKey", "legacyEnvelope", "storedDocument", "tags", "count");
        Single(result, "name").Value.Should().Be("outdated");
        Single(result, "name").SecretIsSet.Should().BeNull();
        Single(result, "count").Value.Should().Be(3);

        var apiKey = Single(result, "apiKey");
        apiKey.Value.Should().BeNull();
        apiKey.SecretIsSet.Should().BeTrue();
        apiKey.SecretSetAt.Should().Be(setAt);

        Single(result, "legacyEnvelope").Value.Should().BeNull();
        Single(result, "legacyEnvelope").SecretIsSet.Should().NotBeNull();
        Single(result, "storedDocument").Value.Should().BeNull();
        Single(result, "storedDocument").SecretIsSet.Should().BeTrue();
        ((IEnumerable<object?>)Single(result, "tags").Value!).Should().Equal(new object?[] { "a", null });

        result.Select(a => a.Value?.ToString() ?? string.Empty).Should().NotContain(v => v.Contains("enc:v"));
    }

    [Fact]
    public void UnknownType_HonoursTheAttributeNamesFilter()
    {
        var cache = CreateLoadedCacheWithoutTypes();
        var entity = CreateEntity(new Dictionary<string, object?> { ["Name"] = "n", ["Other"] = "o" });

        var result = RtEntityGenericDtoType.CreateAttributeDtos(cache, null, null, TenantId, entity, UnknownTypeId,
            ["name"], false);

        result.Should().ContainSingle().Which.AttributeName.Should().Be("name");
    }

    [Fact]
    public void UnknownType_RecordOfUnknownCkRecord_IsProjectedAndMasked()
    {
        var cache = CreateLoadedCacheWithoutTypes();
        var record = new RtRecord(new RtCkId<CkRecordId>("Outdated.Model/RemovedRecord"),
            new Dictionary<string, object?> { ["Label"] = "l", ["Token"] = RtSecretValue.Pending(FakePlaintext) });
        var entity = CreateEntity(new Dictionary<string, object?> { ["Endpoint"] = record });

        var result = RtEntityGenericDtoType.CreateAttributeDtos(cache, null, null, TenantId, entity, UnknownTypeId,
            null, false);

        var recordDto = Single(result, "endpoint").Value.Should()
            .BeOfType<RtRecordDto>().Subject;
        recordDto.Attributes!.Single(a => a.AttributeName == "label").Value.Should().Be("l");
        var token = recordDto.Attributes!.Single(a => a.AttributeName == "token");
        token.Value.Should().BeNull();
        token.SecretIsSet.Should().BeTrue();
    }

    [Fact]
    public void UnloadedCkCache_StillFailsLoudly()
    {
        var cache = A.Fake<ICkCacheService>();
        A.CallTo(() => cache.IsTenantLoaded(TenantId)).Returns(false);
        A.CallTo(() => cache.GetRtCkType(TenantId, UnknownTypeId)).Throws(new InvalidOperationException("not loaded"));

        var act = () => RtEntityGenericDtoType.CreateAttributeDtos(cache, null, null, TenantId, CreateEntity(),
            UnknownTypeId, null, false);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void UnknownType_WarnsOncePerType_WithoutValues()
    {
        var cache = CreateLoadedCacheWithoutTypes();
        var logger = new CapturingLogger();
        var typeId = new RtCkId<CkTypeId>($"Outdated.Model/Warned{Guid.NewGuid():N}");
        var entity = CreateEntity(new Dictionary<string, object?> { ["Name"] = FakePlaintext });

        RtEntityGenericDtoType.CreateAttributeDtos(cache, null, logger, TenantId, entity, typeId, null, false);
        RtEntityGenericDtoType.CreateAttributeDtos(cache, null, logger, TenantId, entity, typeId, null, false);

        logger.Messages.Should().ContainSingle();
        logger.Messages[0].Level.Should().Be(LogLevel.Warning);
        logger.Messages[0].Text.Should().Contain(typeId.ToString()).And.NotContain(FakePlaintext);
    }

    [Theory]
    [InlineData("plain text", false)]
    [InlineData("enc:not-an-envelope", false)]
    public void PlainStrings_AreNotSecrets(string value, bool expected)
    {
        UnknownCkTypeAttributeProjection.IsRecognisableSecret(value).Should().Be(expected);
    }

    private static RtEntityAttributeDto Single(IEnumerable<RtEntityAttributeDto> attributes, string name)
    {
        return attributes.Single(a => a.AttributeName == name);
    }

    private static RtEntity CreateEntity(Dictionary<string, object?>? attributes = null)
    {
        return new RtEntity(UnknownTypeId, OctoObjectId.GenerateNewId(), attributes ?? new Dictionary<string, object?>
        {
            ["Name"] = "x"
        });
    }

    private static ICkCacheService CreateLoadedCacheWithoutTypes()
    {
        var cache = A.Fake<ICkCacheService>();
        A.CallTo(() => cache.IsTenantLoaded(TenantId)).Returns(true);
        CkTypeGraph? noType;
        A.CallTo(() => cache.TryGetRtCkType(TenantId, A<RtCkId<CkTypeId>>._, out noType)).Returns(false);
        CkRecordGraph? noRecord;
        A.CallTo(() => cache.TryGetRtCkRecord(TenantId, A<RtCkId<CkRecordId>>._, out noRecord)).Returns(false);
        A.CallTo(() => cache.GetRtCkType(A<string>._, A<RtCkId<CkTypeId>>._))
            .Throws(new InvalidOperationException("must not be called for a loaded cache"));
        return cache;
    }

    // Structurally valid envelope (nonce + tag + payload of zero bytes); never decrypted by these tests.
    private static string Envelope(string keyId)
    {
        var encoded = Convert.ToBase64String(new byte[33]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return SecretEnvelope.BuildHeaderV2(keyId) + encoded;
    }

    private static ISecretAttributeProtector CreateProtector()
    {
        var protector = A.Fake<ISecretAttributeProtector>();
        A.CallTo(() => protector.DescribeSecret(A<RtSecretValue?>._, A<SecretAccessContext?>._))
            .ReturnsLazily((RtSecretValue? value, SecretAccessContext? _) =>
                SecretValueStates.Describe(value, keyId => keyId == "k1"));
        return protector;
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add((logLevel, formatter(state, exception)));
        }
    }
}
