using System.Text.Json;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.Blueprints;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Collections;
using Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.AssetRepositoryServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.IntegrationTests.Tenant;

/// <summary>
/// AB#6315: the operator flow <b>preview, apply without confirmation, apply with confirmation</b> of a
/// blueprint update that would blank tenant values, end to end through <see cref="BlueprintsController"/>
/// (request DTO, engine, MongoDB tenant, response DTO).
/// <para>
/// Blueprint <c>BlankingFlowBp</c>: 1.0.0 seeds a customer with a company name and an e-mail address, 2.0.0
/// seeds an empty company name and omits the e-mail address, so a 1.0.0 to 2.0.0 update would blank both.
/// </para>
/// </summary>
[Collection(BlueprintUpdateFlowCollection.Name)]
public class BlueprintUpdateBlankingFlowTests(BlueprintUpdateFlowFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string V1 = "BlankingFlowBp-1.0.0";
    private const string V2 = "BlankingFlowBp-2.0.0";
    private const string CustomerRtId = "67000099aaaa1111bbbb0001";
    private const string CompanyV1 = "Seed Company";
    private const string EMailV1 = "seed@example.com";
    private const string TenantCompany = "Tenant Industries Marker 4711";
    private const string TenantEMail = "operator-entered-marker-4711@example.org";

    private static readonly RtCkId<CkTypeId> CustomerCkType = new("AssetRepositoryIntegrationTest/Customer");

    /// <summary>
    /// Preview lists both attributes (value-free), applying without confirmation keeps the tenant values
    /// and reports exactly what the preview announced, a client that sends none of the new request fields
    /// is safe by default.
    /// </summary>
    [Fact]
    public async Task PreviewThenApplyWithoutConfirmation_KeepsValuesAndReportsWhatThePreviewAnnounced()
    {
        var tenantId = await fixture.CreateTenantAsync("bf-keep");
        try
        {
            await InstallV1AndEditAsync(tenantId);
            var controller = CreateController(tenantId);

            var preview = AssertOk<BlueprintUpdatePreviewDto>(await controller.PreviewUpdate(
                new BlueprintUpdateRequestDto { TargetVersion = V2, UpdateMode = "Merge" }, Ct));

            preview.BlankedAttributes.Should().HaveCount(2);
            var company = preview.BlankedAttributes.Single(b => b.AttributeName == "CompanyName");
            company.Reason.Should().Be("SeedEmpty");
            company.RtId.Should().Be(CustomerRtId);
            company.CkTypeId.Should().Contain("Customer");
            company.AppliedOnUpdate.Should().BeFalse("a preview applies nothing");
            company.CurrentSummary.Should().Be($"string ({TenantCompany.Length} chars)");
            company.IncomingSummary.Should().Be("empty string");
            var email = preview.BlankedAttributes.Single(b => b.AttributeName == "EMailAddress");
            email.Reason.Should().Be("SeedOmitted");
            email.RtId.Should().Be(CustomerRtId);
            email.CkTypeId.Should().Be(company.CkTypeId);
            email.CurrentSummary.Should().Be($"string ({TenantEMail.Length} chars)");
            email.IncomingSummary.Should().Be("omitted");
            preview.BlankedAttributes.Should().OnlyContain(b => !b.AppliedOnUpdate, "a preview applies nothing");

            // The preview changed nothing.
            (await ReadCustomerAsync(tenantId)).Should().Be((TenantCompany, TenantEMail));

            // Apply with the request an old client sends: no allowBlanking, no confirmedBlankings.
            var result = AssertOk<BlueprintUpdateResultDto>(await controller.ApplyUpdate(
                new BlueprintUpdateRequestDto { TargetVersion = V2, UpdateMode = "Merge" }, Ct));

            result.Success.Should().BeTrue();
            result.BlankedAttributes.Should().OnlyContain(b => !b.AppliedOnUpdate);
            Identity(result.BlankedAttributes).Should().BeEquivalentTo(Identity(preview.BlankedAttributes),
                "preview and apply share one detection: the apply reports exactly what the preview announced");
            (await ReadCustomerAsync(tenantId)).Should().Be((TenantCompany, TenantEMail),
                "without confirmation the tenant values are kept");
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    /// <summary>
    /// Confirming exactly one entity/attribute pair blanks that value only; the other stays kept and is
    /// still reported as not applied.
    /// </summary>
    [Fact]
    public async Task ApplyWithConfirmationForOnePair_BlanksOnlyThatAttribute()
    {
        var tenantId = await fixture.CreateTenantAsync("bf-pair");
        try
        {
            await InstallV1AndEditAsync(tenantId);
            var controller = CreateController(tenantId);

            var preview = AssertOk<BlueprintUpdatePreviewDto>(await controller.PreviewUpdate(
                new BlueprintUpdateRequestDto { TargetVersion = V2, UpdateMode = "Merge" }, Ct));

            var result = AssertOk<BlueprintUpdateResultDto>(await controller.ApplyUpdate(
                new BlueprintUpdateRequestDto
                {
                    TargetVersion = V2,
                    UpdateMode = "Merge",
                    ConfirmedBlankings = [new BlueprintBlankingConfirmationDto { RtId = CustomerRtId, AttributeName = "CompanyName" }]
                }, Ct));

            result.Success.Should().BeTrue();
            Identity(result.BlankedAttributes).Should().BeEquivalentTo(Identity(preview.BlankedAttributes));
            result.BlankedAttributes.Single(b => b.AttributeName == "CompanyName").AppliedOnUpdate.Should().BeTrue();
            result.BlankedAttributes.Single(b => b.AttributeName == "EMailAddress").AppliedOnUpdate.Should().BeFalse();

            var (company, email) = await ReadCustomerAsync(tenantId);
            company.Should().BeNullOrEmpty("the operator confirmed this pair");
            email.Should().Be(TenantEMail, "this pair was not confirmed");
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    /// <summary>
    /// allowBlanking confirms every listed attribute: both values are blanked and reported as applied.
    /// </summary>
    [Fact]
    public async Task ApplyWithAllowBlanking_BlanksEveryListedAttribute()
    {
        var tenantId = await fixture.CreateTenantAsync("bf-all");
        try
        {
            await InstallV1AndEditAsync(tenantId);
            var controller = CreateController(tenantId);

            var result = AssertOk<BlueprintUpdateResultDto>(await controller.ApplyUpdate(
                new BlueprintUpdateRequestDto { TargetVersion = V2, UpdateMode = "Merge", AllowBlanking = true }, Ct));

            result.Success.Should().BeTrue();
            result.BlankedAttributes.Should().HaveCount(2).And.OnlyContain(b => b.AppliedOnUpdate);

            var (company, email) = await ReadCustomerAsync(tenantId);
            company.Should().BeNullOrEmpty();
            email.Should().BeNullOrEmpty();
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    /// <summary>
    /// Neither the preview nor the apply response carries the tenant's values, only kind and size.
    /// </summary>
    [Fact]
    public async Task PreviewAndApplyResponses_NeverContainTheTenantValues()
    {
        var tenantId = await fixture.CreateTenantAsync("bf-novalue");
        try
        {
            await InstallV1AndEditAsync(tenantId);
            var controller = CreateController(tenantId);

            var preview = AssertOk<BlueprintUpdatePreviewDto>(await controller.PreviewUpdate(
                new BlueprintUpdateRequestDto { TargetVersion = V2, UpdateMode = "Merge" }, Ct));
            var result = AssertOk<BlueprintUpdateResultDto>(await controller.ApplyUpdate(
                new BlueprintUpdateRequestDto { TargetVersion = V2, UpdateMode = "Merge", AllowBlanking = true }, Ct));

            foreach (var json in new[] { JsonSerializer.Serialize(preview), JsonSerializer.Serialize(result) })
            {
                json.Should().NotContain("Marker 4711").And.NotContain("operator-entered");
            }
        }
        finally
        {
            await fixture.DropTenantAsync(tenantId);
        }
    }

    private async Task InstallV1AndEditAsync(string tenantId)
    {
        var applied = await fixture.GetBlueprintService().ApplyBlueprintAsync(tenantId, new BlueprintId(V1), false, Ct);
        applied.IsSuccess.Should().BeTrue(string.Join("; ", applied.OperationResult.Messages.Select(m => m.MessageText)));
        (await ReadCustomerAsync(tenantId)).Should().Be((CompanyV1, EMailV1), "1.0.0 seeds these values");

        // The operator enters their own values on the tenant.
        var repository = await fixture.GetRuntimeRepositoryProvider().GetRepositoryAsync(tenantId, Ct);
        repository.Should().NotBeNull();
        using var session = await repository!.GetSessionAsync();
        var customers = await repository.GetRtEntitiesByTypeAsync(session, CustomerCkType, RtEntityQueryOptions.Create());
        var customer = customers.Items.Should().ContainSingle().Subject;
        customer.SetAttributeRawValue("CompanyName", TenantCompany);
        customer.SetAttributeRawValue("EMailAddress", TenantEMail);
        session.StartTransaction();
        await repository.ReplaceOneRtEntityByIdAsync(session, CustomerCkType, customer.RtId, customer);
        await session.CommitTransactionAsync();
    }

    private async Task<(string? Company, string? EMail)> ReadCustomerAsync(string tenantId)
    {
        var repository = await fixture.GetRuntimeRepositoryProvider().GetRepositoryAsync(tenantId, Ct);
        repository.Should().NotBeNull();
        using var session = await repository!.GetSessionAsync();
        var customers = await repository.GetRtEntitiesByTypeAsync(session, CustomerCkType, RtEntityQueryOptions.Create());
        var customer = customers.Items.Should().ContainSingle().Subject;
        customer.RtId.ToString().Should().Be(CustomerRtId);
        return (customer.GetAttributeStringValueOrDefault("CompanyName"),
            customer.GetAttributeStringValueOrDefault("EMailAddress"));
    }

    private BlueprintsController CreateController(string tenantId)
    {
        var controller = new BlueprintsController(
            fixture.GetBlueprintHistory(), fixture.GetBlueprintService(), fixture.GetBlueprintInstallations());
        var httpContext = new DefaultHttpContext();
        httpContext.Request.RouteValues["tenantId"] = tenantId;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static T AssertOk<T>(IActionResult result) where T : class
    {
        var body = result.Should().BeOfType<OkObjectResult>().Subject.Value;
        return body.Should().BeOfType<T>().Subject;
    }

    /// <summary>What identifies a finding independent of whether it was applied: entity, attribute, reason, summaries.</summary>
    private static IEnumerable<string> Identity(IEnumerable<BlueprintBlankedAttributeDto> attributes) =>
        attributes.Select(a => $"{a.RtId}|{a.CkTypeId}|{a.AttributeName}|{a.Reason}|{a.CurrentSummary}|{a.IncomingSummary}")
            .OrderBy(x => x, StringComparer.Ordinal);
}
