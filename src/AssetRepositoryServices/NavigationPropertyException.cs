using Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types;
using Meshmakers.Octo.ConstructionKit.Contracts;

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



    public static Exception NavigationWithoutRestrictionNotAllowed(RtCkId<CkAssociationRoleId> navigationPairCkRoleId,
        GraphDirections navigationPairDirection, RtCkId<CkTypeId> navigationPairTargetCkTypeId)
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

    public static Exception AttributeNotFound(string attributePath, RtCkId<CkTypeId> ckTypeId)
    {
        return new NavigationPropertyException($"Attribute '{attributePath}' not found in type '{ckTypeId}'");
    }

    public static Exception MatchFailed(MappingResult mappingResult)
    {
        var detailMessages = new List<DetailMessage>();
        foreach (var mappingError in mappingResult.Errors)
        {
            var detailMessage = new DetailMessage
            {
                Message = $"{mappingError.ErrorId}: {mappingError.ErrorMessage}:"
            };
            detailMessages.Add(detailMessage);
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

                detailMessage.Details.Add(
                    $"{navigationPairFieldFilter.Key}={value}");
            }
        }

        var ex = new NavigationPropertyException("Data could not be fully assigned. Please check the details.");
        ex.Details.AddRange(detailMessages);
        return ex;
    }
}