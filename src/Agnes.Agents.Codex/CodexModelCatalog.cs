using Agnes.Abstractions;
using Agnes.Agents.Codex.Wire;

namespace Agnes.Agents.Codex;

internal static class CodexModelCatalog
{
    public static IReadOnlyList<ModelInfo> ToModelInfo(IReadOnlyList<CodexModel> models, bool includeHidden)
    {
        var result = new List<ModelInfo> { new(string.Empty, "Codex default") };
        var seen = new HashSet<string>(StringComparer.Ordinal) { string.Empty };

        foreach (var model in models)
        {
            if (!includeHidden && model.Hidden)
            {
                continue;
            }

            var id = FirstNonBlank(model.Model, model.Id);
            if (id is null || !seen.Add(id))
            {
                continue;
            }

            result.Add(new ModelInfo(id, FirstNonBlank(model.DisplayName, model.Model, model.Id) ?? id));
        }

        return result;
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
