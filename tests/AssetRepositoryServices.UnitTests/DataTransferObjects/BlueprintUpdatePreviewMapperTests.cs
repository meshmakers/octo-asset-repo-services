using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.TransportContainer.DTOs;
using Xunit;

namespace AssetRepositoryServices.UnitTests.DataTransferObjects;

/// <summary>
/// The preview mapper is the only place that turns the engine's transport-shaped attribute values
/// into text for the operator (AB#5297, AB#5308). Records are the case that broke 3.4.124 - the
/// engine DTOs must never meet a default serializer - so they are exercised explicitly here.
/// </summary>
public class BlueprintUpdatePreviewMapperTests
{
    private const string Model = "Test.Model";

    [Fact]
    public void ToDto_CarriesCountsChangesAndConflicts()
    {
        var preview = new BlueprintUpdatePreview
        {
            EntitiesToAdd = 1,
            EntitiesToUpdate = 2,
            EntitiesUnchanged = 125,
            EntitiesToDelete = 0,
            Warnings = ["merge notice"],
            Conflicts =
            [
                new BlueprintUpdateConflict
                {
                    EntityId = "c1", Description = "user modified", SuggestedResolution = ConflictResolution.KeepUser
                }
            ],
            Changes =
            [
                new BlueprintEntityChange
                {
                    EntityId = "67d4a2f0b2e4d8c3a1f00111",
                    EntityWellKnownName = "EmailImportM365",
                    EntityDisplayName = "Email Import (Microsoft 365)",
                    EntityCkTypeId = "System.Communication/Pipeline",
                    Attributes =
                    [
                        new BlueprintAttributeChange { AttributeName = "Enabled", OldValue = true, NewValue = false }
                    ]
                },
                new BlueprintEntityChange
                {
                    EntityId = "aa0000000000000000000304",
                    EntityCkTypeId = "System.Communication/PipelineTrigger",
                    Note = "CK type not in cache"
                }
            ]
        };

        var dto = BlueprintUpdatePreviewMapper.ToDto(preview, "MeshmakersAccounting-1.0.2");

        dto.TargetVersion.Should().Be("MeshmakersAccounting-1.0.2");
        dto.EntitiesToAdd.Should().Be(1);
        dto.EntitiesToUpdate.Should().Be(2);
        dto.EntitiesUnchanged.Should().Be(125);
        dto.Warnings.Should().ContainSingle().Which.Should().Be("merge notice");
        dto.Conflicts.Should().ContainSingle().Which.SuggestedResolution.Should().Be("KeepUser");

        dto.Changes.Should().HaveCount(2);
        var pipeline = dto.Changes[0];
        pipeline.EntityId.Should().Be("67d4a2f0b2e4d8c3a1f00111");
        pipeline.EntityWellKnownName.Should().Be("EmailImportM365");
        pipeline.EntityDisplayName.Should().Be("Email Import (Microsoft 365)");
        pipeline.EntityCkTypeId.Should().Be("System.Communication/Pipeline");
        pipeline.Note.Should().BeNull();
        var enabled = pipeline.Attributes.Should().ContainSingle().Subject;
        enabled.AttributeName.Should().Be("Enabled");
        enabled.OldValue.Should().Be("true");
        enabled.NewValue.Should().Be("false");

        var trigger = dto.Changes[1];
        trigger.Attributes.Should().BeEmpty();
        trigger.Note.Should().Be("CK type not in cache");
    }

    [Fact]
    public void FormatValue_RendersScalarsVerbatimAndCultureInvariant()
    {
        BlueprintUpdatePreviewMapper.FormatValue(null).Should().BeNull();
        BlueprintUpdatePreviewMapper.FormatValue("Notify ToDo").Should().Be("Notify ToDo");
        BlueprintUpdatePreviewMapper.FormatValue(true).Should().Be("true");
        BlueprintUpdatePreviewMapper.FormatValue(42L).Should().Be("42");
        BlueprintUpdatePreviewMapper.FormatValue(3.5).Should().Be("3.5");
        BlueprintUpdatePreviewMapper.FormatValue(1.25m).Should().Be("1.25");
        BlueprintUpdatePreviewMapper.FormatValue(new DateTime(2026, 9, 22, 9, 33, 25, DateTimeKind.Utc))
            .Should().Be("2026-09-22T09:33:25.0000000Z");
    }

    [Fact]
    public void FormatValue_RendersARecordAsJsonWithoutTouchingTheEngineSerializer()
    {
        var record = Record(("Host", "imap.example.org"), ("Port", 993L), ("Ssl", true), ("Comment", null));

        var text = BlueprintUpdatePreviewMapper.FormatValue(record);

        text.Should().NotBeNull();
        using var doc = JsonDocument.Parse(text!);
        var root = doc.RootElement;
        root.GetProperty("ckRecordId").GetString().Should().Be($"{Model}/TestRecord");
        root.GetProperty($"{Model}/Host").GetString().Should().Be("imap.example.org");
        root.GetProperty($"{Model}/Port").GetInt64().Should().Be(993);
        root.GetProperty($"{Model}/Ssl").GetBoolean().Should().BeTrue();
        root.GetProperty($"{Model}/Comment").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void FormatValue_RendersListsOfScalarsAndRecordsAsJsonArrays()
    {
        var scalars = BlueprintUpdatePreviewMapper.FormatValue(new List<object?> { "a", 1L, null });
        scalars.Should().Be("[\"a\",1,null]");

        var records = BlueprintUpdatePreviewMapper.FormatValue(new List<RtRecordTcDto>
        {
            Record(("Name", "first")), Record(("Name", "second"))
        });

        using var doc = JsonDocument.Parse(records!);
        doc.RootElement.GetArrayLength().Should().Be(2);
        doc.RootElement[1].GetProperty($"{Model}/Name").GetString().Should().Be("second");
    }

    [Fact]
    public void FormatValue_RendersJsonStructuresRawAndJsonStringsAsScalars()
    {
        using var doc = JsonDocument.Parse("{\"widgets\":[1,2],\"title\":\"Finance Cockpit\"}");

        BlueprintUpdatePreviewMapper.FormatValue(doc.RootElement)
            .Should().Be("{\"widgets\":[1,2],\"title\":\"Finance Cockpit\"}");
        BlueprintUpdatePreviewMapper.FormatValue(doc.RootElement.GetProperty("widgets")).Should().Be("[1,2]");
        // A string token is a scalar like any other string: unquoted, same as FormatValue("...").
        BlueprintUpdatePreviewMapper.FormatValue(doc.RootElement.GetProperty("title")).Should().Be("Finance Cockpit");
    }

    [Fact]
    public void FormatValue_RendersBinariesAsBase64_AloneAndInsideARecord()
    {
        byte[] png = [0x89, 0x50, 0x4E, 0x47];

        BlueprintUpdatePreviewMapper.FormatValue(png).Should().Be("iVBORw==");

        var text = BlueprintUpdatePreviewMapper.FormatValue(Record(("Logo", png)));
        using var doc = JsonDocument.Parse(text!);
        doc.RootElement.GetProperty($"{Model}/Logo").GetString().Should().Be("iVBORw==");
    }

    private static RtRecordTcDto Record(params (string Name, object? Value)[] attributes)
    {
        var record = new RtRecordTcDto { CkRecordId = new RtCkId<CkRecordId>($"{Model}/TestRecord") };
        foreach (var (name, value) in attributes)
        {
            record.Attributes.Add(new RtAttributeTcDto
            {
                Id = new CkId<CkAttributeId>($"{Model}/{name}").ToRtCkId(),
                Value = value
            });
        }

        return record;
    }
}
