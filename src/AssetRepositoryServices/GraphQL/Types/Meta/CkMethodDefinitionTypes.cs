using GraphQL;
using GraphQL.Types;
using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Enums;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Meta;

/// <summary>
///     A CK v2 method definition with the element that declares it (type or interface). Definitions only — method
///     invocation is CK v2 Phase 3.
/// </summary>
internal sealed record CkMethodDefinitionView(
    CkMethodDto Definition,
    CkId<CkTypeId>? DeclaringCkTypeId,
    CkId<CkInterfaceId>? DeclaringCkInterfaceId)
{
    public static CkMethodDefinitionView From(CkMethodGraph method) => new(method.Definition, method.DeclaringCkTypeId, null);

    public static CkMethodDefinitionView From(CkInterfaceMethodGraph method) =>
        new(method.Definition, null, method.DeclaringCkInterfaceId);

    /// <summary>
    ///     <c>System.Identity/User.ChangePassword-1</c> for type methods; <c>null</c> for interface methods.
    /// </summary>
    public string? QualifiedMethodId => DeclaringCkTypeId == null
        ? null
        : CkMethodIds.Qualify(DeclaringCkTypeId.ToRtCkId(), Definition.MethodId);
}

/// <summary>
///     <c>type CkMethod</c> — CK v2 meta introspection of a method definition (F1.5-S3, AB#5922). Sensitive parameters
///     are flagged (<c>sensitive</c>); no values exist in the meta model.
/// </summary>
internal sealed class CkMethodDtoType : ObjectGraphType<CkMethodDefinitionView>
{
    public CkMethodDtoType()
    {
        Name = "CkMethod";
        Description = "Definition of a construction kit method (CK v2). Definitions only; methods cannot be invoked " +
                      "through GraphQL yet.";

        Field<NonNullGraphType<StringGraphType>>("methodId")
            .Description("Element-versioned method id, e.g. 'ChangePassword-1'.")
            .Resolve(ctx => ctx.Source.Definition.MethodId);
        Field<StringGraphType>("qualifiedMethodId")
            .Description("Model-version-less id of the declaring type plus the method id, e.g. " +
                         "'System.Identity/User.ChangePassword-1'. Null for interface methods.")
            .Resolve(ctx => ctx.Source.QualifiedMethodId);
        Field<CkIdGraph<CkTypeId>>("declaringCkTypeId")
            .Description("The type that declares the method (null for interface methods); differs from the queried " +
                         "type for inherited methods.")
            .Resolve(ctx => ctx.Source.DeclaringCkTypeId);
        Field<CkIdGraph<CkInterfaceId>>("declaringCkInterfaceId")
            .Description("The interface that declares the method (null for type methods).")
            .Resolve(ctx => ctx.Source.DeclaringCkInterfaceId);
        Field<NonNullGraphType<StringGraphType>>("kind")
            .Description("Instance or Static.")
            .Resolve(ctx => ctx.Source.Definition.Kind.ToString());
        Field<StringGraphType>("description").Resolve(ctx => ctx.Source.Definition.Description);
        Field<NonNullGraphType<StringGraphType>>("visibility")
            .Description("Public (default) or Internal.")
            .Resolve(ctx => CkModifiers.ResolveVisibility(ctx.Source.Definition.Visibility).ToString());
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<CkMethodParameterDtoType>>>>("parameters")
            .Resolve(ctx => ctx.Source.Definition.Parameters ?? []);
        Field<CkMethodResultDefinitionDtoType>("result")
            .Description("The declared result; null when the method has none.")
            .Resolve(ctx => ctx.Source.Definition.Result);
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<CkMethodErrorDefinitionDtoType>>>>("errors")
            .Description("Error codes the method declares (platform METHOD_* codes are not listed).")
            .Resolve(ctx => ctx.Source.Definition.Errors ?? []);
        Field<NonNullGraphType<CkMethodAuthorizationDtoType>>("authorization")
            .Resolve(ctx => ctx.Source.Definition.Authorization ?? new CkMethodAuthorizationDto());
        Field<NonNullGraphType<CkMethodExecutionDtoType>>("execution")
            .Resolve(ctx => ctx.Source.Definition.Execution ?? new CkMethodExecutionDto());
    }
}

internal sealed class CkMethodParameterDtoType : ObjectGraphType<CkMethodParameterDto>
{
    public CkMethodParameterDtoType()
    {
        Name = "CkMethodParameter";
        Description = "Parameter of a construction kit method definition.";

        Field<NonNullGraphType<StringGraphType>>("name").Resolve(ctx => ctx.Source.Name);
        Field<NonNullGraphType<AttributeValueTypesDtoType>>("valueType").Resolve(ctx => ctx.Source.ValueType);
        Field<CkIdGraph<CkRecordId>>("valueCkRecordId").Resolve(ctx => ctx.Source.ValueCkRecordId);
        Field<CkIdGraph<CkEnumId>>("valueCkEnumId").Resolve(ctx => ctx.Source.ValueCkEnumId);
        Field<NonNullGraphType<BooleanGraphType>>("isOptional").Resolve(ctx => ctx.Source.IsOptional);
        Field<NonNullGraphType<BooleanGraphType>>("sensitive")
            .Description("Sensitive parameter values are never logged, audited or traced.")
            .Resolve(ctx => ctx.Source.Sensitive);
        Field<StringGraphType>("description").Resolve(ctx => ctx.Source.Description);
    }
}

internal sealed class CkMethodResultDefinitionDtoType : ObjectGraphType<CkMethodResultDto>
{
    public CkMethodResultDefinitionDtoType()
    {
        Name = "CkMethodResultDefinition";
        Description = "Declared result of a construction kit method definition.";

        Field<NonNullGraphType<AttributeValueTypesDtoType>>("valueType").Resolve(ctx => ctx.Source.ValueType);
        Field<CkIdGraph<CkRecordId>>("valueCkRecordId").Resolve(ctx => ctx.Source.ValueCkRecordId);
        Field<CkIdGraph<CkEnumId>>("valueCkEnumId").Resolve(ctx => ctx.Source.ValueCkEnumId);
    }
}

internal sealed class CkMethodErrorDefinitionDtoType : ObjectGraphType<CkMethodErrorDto>
{
    public CkMethodErrorDefinitionDtoType()
    {
        // Not "CkMethodError": that name is reserved for the invocation result of the method runtime (Phase 3).
        Name = "CkMethodErrorDefinition";
        Description = "An error code declared by a construction kit method definition.";

        Field<NonNullGraphType<StringGraphType>>("code").Resolve(ctx => ctx.Source.Code);
        Field<StringGraphType>("description").Resolve(ctx => ctx.Source.Description);
    }
}

internal sealed class CkMethodAuthorizationDtoType : ObjectGraphType<CkMethodAuthorizationDto>
{
    public CkMethodAuthorizationDtoType()
    {
        Name = "CkMethodAuthorization";
        Description = "Declarative authorization of a construction kit method definition.";

        Field<NonNullGraphType<ListGraphType<NonNullGraphType<StringGraphType>>>>("roles")
            .Description("Any-of role names.")
            .Resolve(ctx => ctx.Source.Roles ?? []);
        Field<NonNullGraphType<BooleanGraphType>>("allowSelf")
            .Description("The caller whose subject equals the target entity may invoke the method.")
            .Resolve(ctx => ctx.Source.AllowSelf);
        Field<NonNullGraphType<ListGraphType<NonNullGraphType<StringGraphType>>>>("scopes")
            .Description("Additional required scopes (octo_api is always required).")
            .Resolve(ctx => ctx.Source.Scopes ?? []);
    }
}

internal sealed class CkMethodExecutionDtoType : ObjectGraphType<CkMethodExecutionDto>
{
    public CkMethodExecutionDtoType()
    {
        Name = "CkMethodExecution";
        Description = "Execution settings of a construction kit method definition (synchronous only).";

        Field<NonNullGraphType<IntGraphType>>("timeoutSeconds")
            .Description("Effective timeout (declared value, otherwise 15).")
            .Resolve(ctx => ctx.Source.TimeoutSeconds ?? CkMethodExecutionDto.DefaultTimeoutSeconds);
        Field<NonNullGraphType<BooleanGraphType>>("idempotent").Resolve(ctx => ctx.Source.Idempotent);
    }
}
