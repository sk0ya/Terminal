using System.Collections;

namespace Terminal.Sessions;

internal static class ConPtyProcessEnvironment
{
    public static string[] Build(IReadOnlyDictionary<string, string?>? overrides)
    {
        if (overrides is null || overrides.Count == 0)
        {
            return [];
        }

        var inheritedVariables = new Dictionary<string, string>();
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && !string.IsNullOrEmpty(key))
            {
                inheritedVariables[key] = entry.Value?.ToString() ?? string.Empty;
            }
        }

        return Build(inheritedVariables, overrides);
    }

    internal static string[] Build(
        IEnumerable<KeyValuePair<string, string>> inheritedVariables,
        IReadOnlyDictionary<string, string?> overrides)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in inheritedVariables)
        {
            if (!string.IsNullOrEmpty(pair.Key))
            {
                variables[pair.Key] = pair.Value;
            }
        }

        foreach (KeyValuePair<string, string?> pair in overrides)
        {
            if (string.IsNullOrEmpty(pair.Key) || pair.Key.Contains('='))
            {
                continue;
            }

            if (pair.Value is null)
            {
                variables.Remove(pair.Key);
            }
            else
            {
                variables[pair.Key] = pair.Value;
            }
        }

        return variables.Select(pair => $"{pair.Key}={pair.Value}").ToArray();
    }
}
