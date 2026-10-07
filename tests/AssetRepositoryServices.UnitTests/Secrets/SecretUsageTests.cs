using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Secrets;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AssetRepositoryServices.UnitTests.Secrets;

/// <summary>
///     AB#5544 (handover §7, Q5): finding RevealSecret@1 nodes in pipeline definitions and matching them against
///     secret slots (EXACT / BY_TYPE).
/// </summary>
public class SecretUsageTests
{
    private const string CkType = "System.Communication/EMailSenderConfiguration";
    private const string RtId = "65a1b2c3d4e5f60718293a4b";
    private const string OtherRtId = "65a1b2c3d4e5f60718293a4c";

    private static readonly OctoObjectId PipelineRtId = OctoObjectId.Parse("65a1b2c3d4e5f60718293a40");
    private static readonly OctoObjectId DataFlowRtId = OctoObjectId.Parse("65a1b2c3d4e5f60718293a41");

    private const string Definition = $$"""
        triggers:
          - type: FromExecutePipelineCommand@1
        transformations:
          - type: GetRtEntitiesByType@1
            ckTypeId: {{CkType}}
            targetPath: $.configs
          - type: RevealSecret@1
            ckTypeId: {{CkType}}
            rtId: {{RtId}}
            attributeName: password
            targetPath: $.smtp.password
          - type: ForEach@1
            iterationPath: $.configs.Items
            transformations:
              - type: SetPrimitiveValue@1
                targetPath: $.x
              - type: RevealSecret@1
                ckTypeId: {{CkType}}
                rtIdPath: $.key.RtId
                attributeName: Password
          - type: RevealSecret@1
            ckTypeIdPath: $.dynamicType
            rtIdPath: $.dynamicId
            attributeName: password
          - type: RevealSecret@1
            ckTypeId: {{CkType}}
            rtId: {{RtId}}
            attributeName: credentials.token
        """;

    private static PipelineDefinitionInfo Pipeline(string? definition)
    {
        return new PipelineDefinitionInfo(PipelineRtId, "mailer", DataFlowRtId, "notifications", definition);
    }

    private static SecretUsageIndex Index(params string?[] definitions)
    {
        return new SecretUsageIndex(definitions
            .SelectMany(d => RevealSecretNodeParser.Parse(Pipeline(d), NullLogger.Instance)).ToList());
    }

    [Fact]
    public void Parser_FindsNodesAnywhereInTheTree_WithTheirPosition()
    {
        var references = RevealSecretNodeParser.Parse(Pipeline(Definition), NullLogger.Instance);

        references.Select(r => r.NodePath).Should().Equal(
            "transformations[1]",
            "transformations[2].transformations[1]",
            "transformations[3]",
            "transformations[4]");

        var exact = references[0];
        exact.CkTypeId.Should().Be(CkType);
        exact.RtId.Should().Be(RtId);
        exact.HasRtIdPath.Should().BeFalse();
        exact.AttributeName.Should().Be("password");

        references[1].RtId.Should().BeNull();
        references[1].HasRtIdPath.Should().BeTrue();
        references[2].CkTypeId.Should().BeNull("the node resolves its CK type at run time");
    }

    [Fact]
    public void Parser_AcceptsJsonDefinitions()
    {
        var json = $$"""
            { "transformations": [ { "type": "RevealSecret@1", "ckTypeId": "{{CkType}}", "rtId": "{{RtId}}", "attributeName": "password" } ] }
            """;

        RevealSecretNodeParser.Parse(Pipeline(json), NullLogger.Instance)
            .Should().ContainSingle().Which.NodePath.Should().Be("transformations[0]");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("transformations: [ { type: RevealSecret@1, ckTypeId: \"unterminated")]
    [InlineData("transformations:\n  - type: RevealSecret@1\n   bad-indent: x\n  - [")]
    public void Parser_SkipsMissingOrInvalidDefinitions(string? definition)
    {
        RevealSecretNodeParser.Parse(Pipeline(definition), NullLogger.Instance).Should().BeEmpty();
    }

    [Fact]
    public void Parser_IgnoresTheRootAndOtherNodeTypes()
    {
        RevealSecretNodeParser.Parse(Pipeline("type: RevealSecret@1\nnote: RevealSecret"), NullLogger.Instance)
            .Should().BeEmpty();
    }

    [Fact]
    public void Index_FixedRtId_IsExact_ForThatEntityOnly()
    {
        var index = Index(Definition);

        var usages = index.Find(CkType, RtId, "password");
        usages.Should().HaveCount(2);
        var exact = usages.Single(u => u.Match == SecretUsageMatch.Exact);
        exact.NodePath.Should().Be("transformations[1]");
        exact.PipelineRtId.Should().Be(PipelineRtId);
        exact.PipelineName.Should().Be("mailer");
        exact.DataFlowRtId.Should().Be(DataFlowRtId);
        exact.DataFlowName.Should().Be("notifications");

        index.Find(CkType, OtherRtId, "password").Should().ContainSingle()
            .Which.Match.Should().Be(SecretUsageMatch.ByType);
    }

    [Fact]
    public void Index_RtIdPath_IsByType_AttributeCaseInsensitive()
    {
        var usage = Index(Definition).Find(CkType, OtherRtId, "password").Single();

        usage.Match.Should().Be(SecretUsageMatch.ByType);
        usage.NodePath.Should().Be("transformations[2].transformations[1]");
    }

    [Fact]
    public void Index_OtherTypeOrAttribute_DoesNotMatch()
    {
        var index = Index(Definition);

        index.Find("System.Communication/SftpConfiguration", RtId, "password").Should().BeEmpty();
        index.Find(CkType, RtId, "apiKey").Should().BeEmpty();
    }

    [Fact]
    public void Index_RecordMemberPath_MatchesTheDottedNodePath()
    {
        Index(Definition).Find(CkType, RtId, "credentials.token").Should().ContainSingle()
            .Which.NodePath.Should().Be("transformations[4]");
    }

    [Fact]
    public void Index_CkTypeIdWithVersionSuffix_Matches()
    {
        var reference = new RevealSecretReference(Pipeline(null), "transformations[0]", CkType + "-1", RtId, false,
            "password");

        SecretUsageIndex.Match(reference, CkType, RtId, "password").Should().Be(SecretUsageMatch.Exact);
    }

    [Fact]
    public void Index_NodeWithoutEntityReference_DoesNotMatch()
    {
        var reference = new RevealSecretReference(Pipeline(null), "transformations[0]", CkType, null, false,
            "password");

        SecretUsageIndex.Match(reference, CkType, RtId, "password").Should().BeNull();
    }

    [Fact]
    public void Index_Empty_FindsNothing()
    {
        SecretUsageIndex.Empty.Find(CkType, RtId, "password").Should().BeEmpty();
        SecretUsageIndex.Empty.Count.Should().Be(0);
    }
}
