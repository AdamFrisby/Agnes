namespace Agnes.Sandbox.Incus;

/// <summary>
/// Strict argv validation ported from CodeyBox — rejects anything that could inject an argument,
/// escape a path, or carry a control character. Everything is argv-only; there is never a shell.
/// </summary>
internal static class IncusInputValidation
{
    internal static void ValidateOptions(IncusOptions o)
    {
        ArgumentNullException.ThrowIfNull(o);
        ValidateOpaque(o.BinaryPath, nameof(o.BinaryPath), 4096);
        ValidateIdentifier(o.ProjectName, nameof(o.ProjectName), allowDotUnderscore: true);
        ValidateIdentifier(o.StoragePoolName, nameof(o.StoragePoolName), allowDotUnderscore: true);
    }

    internal static void ValidateInstanceName(string value)
        => ValidateIdentifier(value, "instance", allowDotUnderscore: false);

    internal static void ValidateIdentifier(string value, string parameterName, bool allowDotUnderscore)
    {
        if (value is null || value.Length is < 1 or > 63
            || string.IsNullOrWhiteSpace(value)
            || !char.IsAsciiLetterOrDigit(value[0])
            || !char.IsAsciiLetterOrDigit(value[^1])
            || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-' || (allowDotUnderscore && c is '.' or '_'))))
        {
            throw new ArgumentException("The identifier contains unsupported characters or has an invalid length.", parameterName);
        }
    }

    internal static void ValidateBridge(string value)
    {
        if (value is null || value.Length is < 1 or > 15
            || string.IsNullOrWhiteSpace(value)
            || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
        {
            throw new ArgumentException("The bridge must be a valid Linux interface name of at most 15 characters.", nameof(value));
        }
    }

    internal static void ValidateOpaque(string value, string parameterName, int maximumLength)
    {
        if (value is null || value.Length is < 1 || value.Length > maximumLength
            || string.IsNullOrWhiteSpace(value)
            || value.StartsWith('-')
            || value.Contains('\0')
            || value.Any(char.IsControl))
        {
            throw new ArgumentException($"The argument must be non-empty, at most {maximumLength} chars, must not start with '-', and contain no control characters.", parameterName);
        }
    }

    internal static void ValidateAbsoluteHostPath(string value)
    {
        if (value is null || value.Length is < 1 or > 4096
            || string.IsNullOrWhiteSpace(value)
            || value.Any(char.IsControl)
            || !Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("The host source must be a fully-qualified path without control characters.", nameof(value));
        }
    }

    internal static void ValidateAbsoluteGuestPath(string value)
    {
        if (value is null || value.Length is < 1 or > 4096
            || !value.StartsWith('/') || value == "/"
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.EndsWith('/')
            || value.Contains("//", StringComparison.Ordinal)
            || value.Any(char.IsControl)
            || value.Split('/').Any(seg => seg is ".." ))
        {
            throw new ArgumentException("The guest path must be a normalized absolute Unix path.", nameof(value));
        }
    }
}
