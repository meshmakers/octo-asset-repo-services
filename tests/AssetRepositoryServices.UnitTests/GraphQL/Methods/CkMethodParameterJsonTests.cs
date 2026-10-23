using System.Text.Json;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Methods;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Xunit;

namespace AssetRepositoryServices.UnitTests.GraphQL.Methods;

/// <summary>AB#7026: the parsed <c>input</c> argument becomes the parameters JSON object of the gateway call.</summary>
public class CkMethodParameterJsonTests
{
    [Fact]
    public void NoInput_IsAnEmptyObject() => Assert.Equal("{}", CkMethodParameterJson.ToJson(null));

    [Fact]
    public void ScalarsListsRecordsAndEnums_FollowTheWireForm()
    {
        var input = new Dictionary<string, object?>
        {
            ["text"] = "a\"b",
            ["count"] = 3,
            ["big"] = 5000000000L,
            ["ratio"] = 1.5m,
            ["on"] = true,
            ["none"] = null,
            ["mode"] = 2, // enum key
            ["when"] = new DateTime(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc),
            ["span"] = TimeSpan.FromMinutes(90),
            ["tags"] = new List<object?> { "x", "y" },
            ["bytes"] = new byte[] { 1, 2, 3 },
            ["spec"] = new RtRecordDto
            {
                Attributes = [new RtEntityAttributeDto { AttributeName = "name", Value = "n" }]
            }
        };

        using var json = JsonDocument.Parse(CkMethodParameterJson.ToJson(input));
        var root = json.RootElement;

        Assert.Equal("a\"b", root.GetProperty("text").GetString());
        Assert.Equal(3, root.GetProperty("count").GetInt32());
        Assert.Equal(5000000000L, root.GetProperty("big").GetInt64());
        Assert.Equal(1.5m, root.GetProperty("ratio").GetDecimal());
        Assert.True(root.GetProperty("on").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("none").ValueKind);
        Assert.Equal(2, root.GetProperty("mode").GetInt32());
        Assert.Equal(new DateTime(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc), root.GetProperty("when").GetDateTime().ToUniversalTime());
        Assert.Equal("01:30:00", root.GetProperty("span").GetString());
        Assert.Equal(["x", "y"], root.GetProperty("tags").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("AQID", root.GetProperty("bytes").GetString());
        Assert.Equal("n", root.GetProperty("spec").GetProperty("name").GetString());
    }
}
