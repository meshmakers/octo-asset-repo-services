using System.Globalization;
using GraphQL.Types;
using GraphQLParser.AST;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.GraphQL.Types.Scalars;

internal class SimpleScalarType : ScalarGraphType
{
    public override object? Serialize(object? value)
    {
        return value;
    }

    public override object? ParseValue(object? value)
    {
        return value;
    }

    public override object? ParseLiteral(GraphQLValue value)
    {
        if (value is GraphQLStringValue str)
        {
            return str.Value.ToString();
        }

        if (value is GraphQLListValue list)
        {
            var items = new List<object?>();
            if (list.Values == null)
            {
                return items; // Return an empty list if the list is empty or null
            }

            foreach (var item in list.Values)
            {
                if (item is GraphQLStringValue stringItem)
                {
                    items.Add(stringItem.Value.ToString());
                }
                else if (item is GraphQLIntValue intItem)
                {
                    items.Add(int.Parse(intItem.Value));
                }
                else if (item is GraphQLFloatValue floatItem)
                {
                    items.Add(double.Parse(floatItem.Value, NumberStyles.Float, CultureInfo.InvariantCulture));
                }
                else if (item is GraphQLBooleanValue boolItem)
                {
                    items.Add(boolItem.Value);
                }
                else
                    // Handle other types as needed
                {
                    items.Add(item.ToString());
                }
            }

            return items;
        }

        return null;
    }
}