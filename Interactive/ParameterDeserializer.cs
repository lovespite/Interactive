namespace Interactive;

public static class ParameterDeserializer
{
    public static object? String(string parameterExpr) => parameterExpr;
    public static object? StringEnumerable(string parameterExpr)
    {
        var parts = parameterExpr
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Trim());
        return parts;
    }
    public static object? StringArray(string parameterExpr) => StringEnumerable(parameterExpr) is IEnumerable<string> parts ? parts.ToArray() : null;
    public static object? StringList(string parameterExpr) => StringEnumerable(parameterExpr) is IEnumerable<string> parts ? parts.ToList() : null;
    public static object? Int32(string parameterExpr) => int.Parse(parameterExpr);
    public static object? Int32Enumerable(string parameterExpr)
    {
        var parts = parameterExpr
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.Parse(x.Trim()));
        return parts;
    }
    public static object? Int32Array(string parameterExpr) => Int32Enumerable(parameterExpr) is IEnumerable<int> parts ? parts.ToArray() : null;
    public static object? Int32List(string parameterExpr) => Int32Enumerable(parameterExpr) is IEnumerable<int> parts ? parts.ToList() : null;
    public static object? Int64(string parameterExpr) => long.Parse(parameterExpr);
    public static object? Int64Enumerable(string parameterExpr)
    {
        var parts = parameterExpr
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => long.Parse(x.Trim()));
        return parts;
    }
    public static object? Int64Array(string parameterExpr) => Int64Enumerable(parameterExpr) is IEnumerable<long> parts ? parts.ToArray() : null;
    public static object? Int64List(string parameterExpr) => Int64Enumerable(parameterExpr) is IEnumerable<long> parts ? parts.ToList() : null;
    public static object? Double(string parameterExpr) => double.Parse(parameterExpr);
    public static object? DoubleEnumerable(string parameterExpr)
    {
        var parts = parameterExpr
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => double.Parse(x.Trim()));
        return parts;
    }
    public static object? DoubleArray(string parameterExpr) => DoubleEnumerable(parameterExpr) is IEnumerable<double> parts ? parts.ToArray() : null;
    public static object? DoubleList(string parameterExpr) => DoubleEnumerable(parameterExpr) is IEnumerable<double> parts ? parts.ToList() : null;
    public static object? Boolean(string parameterExpr)
    {
        return parameterExpr.ToLower() switch
        {
            "true" or "1" or "yes" or "y" or "on" => true,
            "false" or "0" or "no" or "n" or "off" => false,
            _ => throw new FormatException($"Cannot convert '{parameterExpr}' to bool.")
        };
    }
    public static object? BooleanEnumerable(string parameterExpr)
    {
        var parts = parameterExpr
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => Boolean(x.Trim()));
        return parts;
    }
    public static object? BooleanArray(string parameterExpr) => BooleanEnumerable(parameterExpr) is IEnumerable<bool> parts ? parts.ToArray() : null;
    public static object? BooleanList(string parameterExpr) => BooleanEnumerable(parameterExpr) is IEnumerable<bool> parts ? parts.ToList() : null;

    public static object? Object(string parameterExpr) => parameterExpr;
    public static object? ObjectEnumerable(string parameterExpr)
    {
        var parts = parameterExpr
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Trim());
        return parts;
    }
    public static object? ObjectArray(string parameterExpr) => ObjectEnumerable(parameterExpr) is IEnumerable<object> parts ? parts.ToArray() : null;
    public static object? ObjectList(string parameterExpr) => ObjectEnumerable(parameterExpr) is IEnumerable<object> parts ? parts.ToList() : null;

    public delegate object? ParamterDeserializer(string parameterExpr);

    private static readonly Dictionary<Type, ParamterDeserializer> _typeMap = new()
    {
        { typeof(string), String },
        { typeof(IEnumerable<string>), StringEnumerable },
        { typeof(string[]), StringArray },
        { typeof(List<string>), StringList },

        { typeof(int), Int32 },
        { typeof(IEnumerable<int>), Int32Enumerable },
        { typeof(int[]), Int32Array },
        { typeof(List<int>), Int32List },

        { typeof(long), Int64 },
        { typeof(IEnumerable<long>), Int64Enumerable },
        { typeof(long[]), Int64Array },
        { typeof(List<long>), Int64List },

        { typeof(double), Double },
        { typeof(IEnumerable<double>), DoubleEnumerable },
        { typeof(double[]), DoubleArray },
        { typeof(List<double>), DoubleList },

        { typeof(bool), Boolean },
        { typeof(IEnumerable<bool>), BooleanEnumerable },
        { typeof(bool[]), BooleanArray },
        { typeof(List<bool>), BooleanList },

        { typeof(object), Object },
        { typeof(IEnumerable<object>), ObjectEnumerable },
        { typeof(object[]), ObjectArray },
        { typeof(List<object>), ObjectList },
    };

    public static ParamterDeserializer? FromTypeOf(Type t)
    {
        if (_typeMap.TryGetValue(t, out var converter)) return converter;
        return null;
    }

    public static void SetCustomDeserializer(Type t, ParamterDeserializer converter)
    {
        _typeMap[t] = converter;
    }
}