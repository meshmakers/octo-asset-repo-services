using System.Reflection;
using System.Security.Claims;
using FakeItEasy;
using FluentAssertions;
using Meshmakers.Octo.Backend.AssetRepositoryServices;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Configuration;
using Meshmakers.Octo.Backend.AssetRepositoryServices.DataTransferObjects.CkModelCatalog;
using Meshmakers.Octo.Backend.AssetRepositoryServices.Services;
using Meshmakers.Octo.Backend.AssetRepositoryServices.SystemApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AssetRepositoryServices.UnitTests.SystemApi;

/// <summary>
///     GET system/v1/ckmodelcatalog/{catalogName}/{modelId}/types reads a catalog model's content without
///     importing it - bounded page, stable error codes, and exactly the authorization of GET {catalogName}/{modelId}.
/// </summary>
public class CkModelCatalogModelTypesTests
{
    private const string Catalog = "PublicGitHubCatalog";
    private readonly ICatalogService _catalogService = A.Fake<ICatalogService>();
    private readonly CkModelCatalogController _controller;

    public CkModelCatalogModelTypesTests()
    {
        _controller = new CkModelCatalogController(_catalogService);
        A.CallTo(() => _catalogService.GetCatalogList(null))
            .Returns([Tuple.Create(Catalog, "public catalog")]);
    }

    private static CkCompiledModelRoot BasicModel(int extraTypes = 0)
    {
        var modelId = new CkModelId("Basic-2.7.0");
        var types = new List<CkCompiledTypeDto>
        {
            new()
            {
                TypeId = new CkTypeId("Equipment"),
                Description = "A physical asset",
                Attributes =
                [
                    new CkTypeAttributeDto
                    {
                        AttributeName = "Name",
                        CkAttributeId = new CkId<CkAttributeId>(new CkModelId("System-2.0.0"), new CkAttributeId("Name"))
                    },
                    new CkTypeAttributeDto
                    {
                        AttributeName = "State",
                        CkAttributeId = new CkId<CkAttributeId>(modelId, new CkAttributeId("EquipmentState")),
                        IsOptional = true
                    }
                ],
                Associations =
                [
                    new CkTypeAssociationDto
                    {
                        CkRoleId = new CkId<CkAssociationRoleId>(modelId, new CkAssociationRoleId("LocatedAt")),
                        TargetCkTypeId = new CkId<CkTypeId>(modelId, new CkTypeId("Location"))
                    }
                ]
            },
            new()
            {
                TypeId = new CkTypeId("Location"),
                Description = "A place",
                Attributes =
                [
                    new CkTypeAttributeDto
                    {
                        AttributeName = "GeoPosition",
                        CkAttributeId = new CkId<CkAttributeId>(modelId, new CkAttributeId("GeoPosition"))
                    }
                ]
            }
        };
        for (var i = 0; i < extraTypes; i++)
        {
            types.Add(new CkCompiledTypeDto { TypeId = new CkTypeId($"Extra{i:D3}") });
        }

        return new CkCompiledModelRoot
        {
            ModelId = modelId,
            Description = "Basic kit",
            Types = types,
            Attributes =
            [
                new CkAttributeDto
                {
                    AttributeId = new CkAttributeId("EquipmentState"),
                    ValueType = AttributeValueTypesDto.Enum,
                    ValueCkEnumId = new CkId<CkEnumId>(modelId, new CkEnumId("EquipmentState"))
                },
                new CkAttributeDto { AttributeId = new CkAttributeId("GeoPosition"), ValueType = AttributeValueTypesDto.GeospatialPoint }
            ],
            AssociationRoles =
            [
                new CkAssociationRoleDto
                {
                    AssociationRoleId = new CkAssociationRoleId("LocatedAt"),
                    InboundName = "Equipment",
                    OutboundName = "Location",
                    InboundMultiplicity = MultiplicitiesDto.N,
                    OutboundMultiplicity = MultiplicitiesDto.ZeroOrOne
                }
            ],
            Enums =
            [
                new CkEnumDto
                {
                    EnumId = new CkEnumId("EquipmentState"),
                    Values = [new CkEnumValueDto { Key = 0, Name = "Running" }, new CkEnumValueDto { Key = 1, Name = "Stopped" }]
                }
            ]
        };
    }

    private void GivenModel(CkCompiledModelRoot model) =>
        A.CallTo(() => _catalogService.GetAsync(Catalog, A<CkModelId>.That.Matches(m => m.FullName == "Basic-2.7.0"),
                A<OperationResult>.Ignored, A<CancellationToken?>.Ignored))
            .Returns(model);

    [Fact]
    public async Task GetModelTypes_ReturnsTypesAttributesAssociationsRolesAndEnums()
    {
        GivenModel(BasicModel());

        var result = await _controller.GetModelTypes(Catalog, "Basic-2.7.0", cancellationToken: TestContext.Current.CancellationToken);

        var dto = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should()
            .BeOfType<CkCatalogModelTypesResponseDto>().Subject;
        dto.ModelId.Should().Be("Basic-2.7.0");
        dto.CatalogName.Should().Be(Catalog);
        dto.TotalCount.Should().Be(2);
        dto.Types.Select(t => t.TypeId).Should().Equal("Basic/Equipment", "Basic/Location");

        var equipment = dto.Types[0];
        equipment.Attributes.Should().HaveCount(2);
        var name = equipment.Attributes.Single(a => a.Name == "Name");
        name.ValueType.Should().BeNull("the attribute is defined in another model (System)");
        var state = equipment.Attributes.Single(a => a.Name == "State");
        state.ValueType.Should().Be("Enum");
        state.EnumId.Should().Be("Basic/EquipmentState");
        state.IsOptional.Should().BeTrue();
        equipment.Associations.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { RoleId = "Basic/LocatedAt", Target = "Basic/Location" });

        dto.AssociationRoles.Should().ContainSingle().Which.OutboundMultiplicity.Should().Be("ZeroOrOne");
        dto.Enums.Should().ContainSingle().Which.Values.Select(v => v.Name).Should().Equal("Running", "Stopped");
    }

    [Fact]
    public async Task GetModelTypes_FilterMatchesTypeIdDescriptionOrAttributeName()
    {
        GivenModel(BasicModel());

        var byAttribute = await _controller.GetModelTypes(Catalog, "Basic-2.7.0", "geoposition",
            cancellationToken: TestContext.Current.CancellationToken);
        var dto = (CkCatalogModelTypesResponseDto)((OkObjectResult)byAttribute).Value!;
        dto.Types.Select(t => t.TypeId).Should().Equal("Basic/Location");
        dto.TotalCount.Should().Be(1);
        dto.ModelTypeCount.Should().Be(2);
        dto.AssociationRoles.Should().BeEmpty("only roles used by the returned page are listed");
        dto.Enums.Should().BeEmpty();

        var byDescription = await _controller.GetModelTypes(Catalog, "Basic-2.7.0", "PHYSICAL",
            cancellationToken: TestContext.Current.CancellationToken);
        ((CkCatalogModelTypesResponseDto)((OkObjectResult)byDescription).Value!).Types.Single().TypeId
            .Should().Be("Basic/Equipment");
    }

    [Fact]
    public async Task GetModelTypes_TakeIsCappedAndPaged()
    {
        GivenModel(BasicModel(extraTypes: 120));

        var result = await _controller.GetModelTypes(Catalog, "Basic-2.7.0", take: 1000, skip: 10,
            cancellationToken: TestContext.Current.CancellationToken);

        var dto = (CkCatalogModelTypesResponseDto)((OkObjectResult)result).Value!;
        dto.Take.Should().Be(CkCatalogModelTypesMapper.MaxTake);
        dto.Skip.Should().Be(10);
        dto.Types.Should().HaveCount(CkCatalogModelTypesMapper.MaxTake);
        dto.TotalCount.Should().Be(122);
    }

    [Fact]
    public void Mapper_CutsLongDescriptions()
    {
        var model = BasicModel();
        model.Types![0].Description = new string('x', 5000);

        var dto = CkCatalogModelTypesMapper.Map(Catalog, model, null, 0, 0);

        dto.Take.Should().Be(CkCatalogModelTypesMapper.DefaultTake);
        dto.Types[0].Description!.Length.Should().Be(CkCatalogModelTypesMapper.MaxDescriptionLength);
    }

    [Fact]
    public async Task GetModelTypes_UnknownCatalog_Returns404WithCode()
    {
        var result = await _controller.GetModelTypes("NoSuchCatalog", "Basic-2.7.0",
            cancellationToken: TestContext.Current.CancellationToken);

        CodeOf(result, 404)
            .Should().Be(CkCatalogModelTypesErrorCodes.CkCatalogNotFound);
        A.CallTo(() => _catalogService.GetAsync(A<string>.Ignored, A<CkModelId>.Ignored, A<OperationResult>.Ignored,
            A<CancellationToken?>.Ignored)).MustNotHaveHappened();
    }

    [Fact]
    public async Task GetModelTypes_UnknownModel_Returns404WithCode()
    {
        A.CallTo(() => _catalogService.GetAsync(Catalog, A<CkModelId>.Ignored, A<OperationResult>.Ignored,
            A<CancellationToken?>.Ignored)).Returns((CkCompiledModelRoot?)null);

        var result = await _controller.GetModelTypes(Catalog, "Basic-9.9.9",
            cancellationToken: TestContext.Current.CancellationToken);

        CodeOf(result, 404)
            .Should().Be(CkCatalogModelTypesErrorCodes.CkCatalogModelNotFound);
    }

    [Theory]
    [InlineData("Basic")]
    [InlineData("Basic-")]
    [InlineData("-2.7.0")]
    public async Task GetModelTypes_InvalidModelId_Returns400WithCode(string modelId)
    {
        var result = await _controller.GetModelTypes(Catalog, modelId, cancellationToken: TestContext.Current.CancellationToken);

        CodeOf(result, 400)
            .Should().Be(CkCatalogModelTypesErrorCodes.InvalidCkModelId);
    }

    private static object? CodeOf(IActionResult result, int status)
    {
        var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(status);
        objectResult.ContentTypes.Should().Contain("application/problem+json");
        return objectResult.Value.Should().BeOfType<ProblemDetails>().Subject.Extensions["code"];
    }

    // ---- authorization: exactly the policy of GET {catalogName}/{modelId} ----

    private static string PolicyOf(string action) =>
        typeof(CkModelCatalogController).GetMethod(action)!.GetCustomAttributes<AuthorizeAttribute>().Single().Policy!;

    private static async Task<bool> Allows(string policy, string? scope)
    {
        var claims = new List<Claim> { new("tenant_id", "t"), new("sub", "s") };
        if (scope != null)
        {
            claims.Add(new Claim(InfrastructureCommon.ClaimScope, scope));
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(o => o.AddAssetRepositoryPolicies());
        services.AddOctoTenantRoleAuthorization();
        await using var provider = services.BuildServiceProvider();
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer", "name", "role"));
        return (await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(user, policy)).Succeeded;
    }

    [Fact]
    public void GetModelTypes_CarriesThePolicyOfGetModel()
    {
        PolicyOf(nameof(CkModelCatalogController.GetModelTypes))
            .Should().Be(PolicyOf(nameof(CkModelCatalogController.GetModel)))
            .And.Be(AssetRepositoryServiceConstants.SystemAssetApiReadOnlyPolicy);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("openid", false)]
    [InlineData(CommonConstants.OctoApiReadOnly, true)]
    [InlineData(CommonConstants.OctoApiFullAccess, true)]
    public async Task GetModelTypes_CallerWithoutApiScope_IsRefused(string? scope, bool allowed)
    {
        (await Allows(PolicyOf(nameof(CkModelCatalogController.GetModelTypes)), scope)).Should().Be(allowed);
    }
}
