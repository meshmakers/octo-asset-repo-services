using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices;

/// <summary>
/// Exception for errors during navigation property resolution or filter resolution
/// </summary>
internal class NavigationPropertyException : AssetRepositoryException
{
    public NavigationPropertyException()
    {
    }

    public NavigationPropertyException(string message) : base(message)
    {
    }

    public NavigationPropertyException(string message, Exception inner) : base(message, inner)
    {
    }

    public string? DetailMessage { get; set; }


    public static Exception NoMatchFound2(int lineNumber, NavigationPair navigationPair)
    {
        string text =
            $"No match found for navigation property with role id '{navigationPair.CkRoleId}' in direction '{navigationPair.Direction}' to '{navigationPair.TargetCkTypeId}'";
        if (navigationPair.FieldFilters != null)
        {
            foreach (var navigationPairFieldFilter in navigationPair.FieldFilters)
            {
                string? value;
                if (navigationPairFieldFilter.ComparisonValue is string comparisonValue)
                {
                    value = comparisonValue;
                }
                else if (navigationPairFieldFilter.ComparisonValue is IEnumerable<object> enumerable)
                {
                    value = "[" + string.Join(", ", enumerable.ToArray()) + "]";
                }
                else if (navigationPairFieldFilter.ComparisonValue != null)
                {
                    value = navigationPairFieldFilter.ComparisonValue.ToString();
                }
                else
                {
                    value = "null";
                }

                text +=
                    $", filter: {navigationPairFieldFilter.AttributePath} {navigationPairFieldFilter.Operator} {value}";
            }
        }

        return new NavigationPropertyException(
                $"No match of a navigation property value found for query row number {lineNumber}")
            { DetailMessage = text };
    }

    public static Exception MultipleCandidatesFound(RtQueryRowDto queryRowDto, CkId<CkAssociationRoleId> keyCkRoleId,
        GraphDirections keyDirection, CkId<CkTypeId> keyTargetCkTypeId)
    {
        var queryRowDtoJson = System.Text.Json.JsonSerializer.Serialize(queryRowDto);
        return new NavigationPropertyException(
            $"Multiple candidates NavigationPropertyAssignException for query row: {queryRowDtoJson}, role id: {keyCkRoleId}, direction: {keyDirection}, target type id: {keyTargetCkTypeId}");
    }

    public static Exception NavigationWithoutRestrictionNotAllowed(CkId<CkAssociationRoleId> navigationPairCkRoleId,
        GraphDirections navigationPairDirection, CkId<CkTypeId> navigationPairTargetCkTypeId)
    {
        return new NavigationPropertyException(
            $"Navigation without restriction is not allowed for role id '{navigationPairCkRoleId}' in direction '{navigationPairDirection}' to '{navigationPairTargetCkTypeId}'");
    }

    private static Exception CannotConvertValue(object o, Type type)
    {
        return new NavigationPropertyException($"Cannot convert value '{o}' to type '{type}'");
    }

    public static Exception CannotConvertValueToString(object o)
    {
        return CannotConvertValue(o, typeof(string));
    }

    public static Exception AttributeNotFound(string attributePath, CkId<CkTypeId> ckTypeId)
    {
        return new NavigationPropertyException($"Attribute '{attributePath}' not found in type '{ckTypeId}'");
    }

    public static Exception MatchFailed(MappingResult mappingResult)
    {
        var messages = new List<string>();
        foreach (var mappingError in mappingResult.Errors)
        {
            messages.Add($"{mappingError.ErrorId}: {mappingError.ErrorMessage}:");
            foreach (var navigationPairFieldFilter in mappingError.Comparision)
            {
                string? value;
                if (navigationPairFieldFilter.Value is string comparisonValue)
                {
                    value = comparisonValue;
                }
                else if (navigationPairFieldFilter.Value is IEnumerable<object> enumerable)
                {
                    value = "[" + string.Join(", ", enumerable.ToArray()) + "]";
                }
                else if (navigationPairFieldFilter.Value != null)
                {
                    value = navigationPairFieldFilter.Value.ToString();
                }
                else
                {
                    value = "null";
                }

                messages.Add(
                    $"{navigationPairFieldFilter.Key}={value}");
            }
        }

        return new NavigationPropertyException("Mapping error. See details.")
            { DetailMessage = string.Join(Environment.NewLine, messages) };
    }
}