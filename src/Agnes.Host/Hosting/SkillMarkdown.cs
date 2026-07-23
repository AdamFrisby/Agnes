namespace Agnes.Host.Hosting;

/// <summary>
/// The typed result of parsing a <c>SKILL.md</c> frontmatter block. Both fields are optional — a bundle
/// authored without frontmatter (or with only some keys) still loads.
/// </summary>
public sealed record SkillFrontmatter(string? Name, string? Description);

/// <summary>
/// Boundary parser for the emerging <c>SKILL.md</c> convention: a markdown file that may open with a
/// <c>---</c>-delimited YAML-ish frontmatter block carrying <c>name</c> / <c>description</c>. This is an
/// external, not-Agnes-owned format, so the loose text is read here and turned into a typed
/// <see cref="SkillFrontmatter"/> immediately rather than flowing inward. Tolerant by contract — a file with
/// no frontmatter (or a malformed block) yields empty fields rather than throwing.
/// </summary>
public static class SkillMarkdown
{
    /// <summary>Parses the leading frontmatter of a <c>SKILL.md</c> body. Only the simple <c>key: value</c>
    /// lines Agnes cares about (<c>name</c>, <c>description</c>) are read; everything else is ignored.</summary>
    public static SkillFrontmatter ParseFrontmatter(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return new SkillFrontmatter(null, null);
        }

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return new SkillFrontmatter(null, null);
        }

        string? name = null;
        string? description = null;
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim() == "---")
            {
                break; // end of frontmatter.
            }

            var sep = line.IndexOf(':', StringComparison.Ordinal);
            if (sep <= 0)
            {
                continue;
            }

            var key = line[..sep].Trim();
            var value = Unquote(line[(sep + 1)..].Trim());
            if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                name = value.Length == 0 ? null : value;
            }
            else if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
            {
                description = value.Length == 0 ? null : value;
            }
        }

        return new SkillFrontmatter(name, description);
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }
}
