using System.Text.RegularExpressions;

namespace Interactive.Utilities;

internal static partial class VariableHelper
{
    [GeneratedRegex(@"\$\{(?<var>[a-zA-Z_][a-zA-Z0-9_]*)\}")]
    public static partial Regex VariableMatcher();

    /// <summary>
    /// Extracts the names of all variables found in the specified input string.
    /// </summary>
    /// <param name="input">The input string to search for variable names. Cannot be null.</param>
    /// <returns>An array of unique variable names found in the input string. The array is empty if no variable names are found.</returns>
    public static string[] GetVariableNames(this string input)
    {
        var matches = VariableMatcher().Matches(input);
        var varNames = new HashSet<string>();
        foreach (Match match in matches)
        {
            var varName = match.Groups["var"].Value;
            varNames.Add(varName);
        }
        return [.. varNames];
    }

    public static bool TryGetVariableNames(this string input, out HashSet<string> varNames)
    {
        var matches = VariableMatcher().Matches(input);
        varNames = [];
        foreach (Match match in matches)
        {
            var varName = match.Groups["var"].Value;
            varNames.Add(varName);
        }
        return varNames.Count > 0;
    }

    /// <summary>
    /// Determines whether the specified string contains one or more variable placeholders.
    /// </summary>
    /// <param name="input">The string to examine for variable placeholders. Cannot be null.</param>
    /// <returns>true if the input string contains at least one variable placeholder; otherwise, false.</returns>
    public static bool ContainsVariables(this string input)
    {
        return VariableMatcher().IsMatch(input);
    }


    public static string MapVariables(this IDictionary<string, string> variables, string input, ReplaceOptions options = ReplaceOptions.RemoveIfNotFound)
    {
        return VariableMatcher().Replace(input, match =>
        {
            var varName = match.Groups["var"].Value;
            if (variables.TryGetValue(varName, out var value))
            {
                return value;
            }
            if (options.HasFlag(ReplaceOptions.SearchSystemEnvironment))
            {
                var envValue = Environment.GetEnvironmentVariable(varName);
                if (envValue is not null)
                {
                    return envValue;
                }
            }
            if (options.HasFlag(ReplaceOptions.RemoveIfNotFound))
            {
                return string.Empty; // 未找到变量，移除该部分
            }
            else
            {
                return match.Value; // 未找到变量，移除该部分
            }
        });
    }

    [Flags]
    public enum ReplaceOptions
    {
        RetainIfNotFound = 0,
        RemoveIfNotFound = 1 << 0, // 1 
        SearchSystemEnvironment = 1 << 1, // 2
    }
}
