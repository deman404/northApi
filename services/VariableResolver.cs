using System.Text.RegularExpressions;
using northApi.models;

namespace northApi.services;

public sealed class VariableResolver
{
    private static readonly Regex Pattern = new(@"\{\{\s*([^{}]+?)\s*\}\}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private readonly Dictionary<string, string> values;
    public VariableResolver(EnvironmentProfile? environment)
    {
        values = new(StringComparer.Ordinal);
        foreach (var field in environment?.Variables ?? [])
            if (field.Enabled && !string.IsNullOrWhiteSpace(field.Key)) values[field.Key.Trim()] = field.Value;
    }
    public string Resolve(string? input) => ResolveText(input ?? "", new HashSet<string>(StringComparer.Ordinal));
    private string ResolveText(string input, HashSet<string> visiting) => Pattern.Replace(input, match =>
    {
        var key = match.Groups[1].Value.Trim();
        if (!values.TryGetValue(key, out var value)) throw new ArgumentException($"Variable {{{{{key}}}}} is not defined in the selected environment.");
        if (visiting.Count >= 32 || !visiting.Add(key)) throw new ArgumentException($"Environment variables contain a cycle near '{key}'.");
        try { return ResolveText(value, visiting); }
        finally { visiting.Remove(key); }
    });
}
