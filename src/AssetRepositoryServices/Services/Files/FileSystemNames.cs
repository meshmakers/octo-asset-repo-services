namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;

/// <summary>
///     Name and path rules of the platform file system (AB#6171).
/// </summary>
public static class FileSystemNames
{
    /// <summary>
    ///     Longest name of a file, folder or root.
    /// </summary>
    public const int MaxNameLength = 255;

    /// <summary>
    ///     Throws <see cref="FileSystemException" /> (<c>INVALID_NAME</c>) when <paramref name="name" /> cannot be the
    ///     name of a file system entry: empty or whitespace only, longer than <see cref="MaxNameLength" />,
    ///     <c>.</c> / <c>..</c>, or containing <c>/</c>, <c>\</c> or control characters.
    /// </summary>
    public static void Validate(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw FileSystemException.InvalidName(name, "the name must not be empty.");
        }

        if (name.Length > MaxNameLength)
        {
            throw FileSystemException.InvalidName(name, $"the name must not be longer than {MaxNameLength} characters.");
        }

        if (name is "." or "..")
        {
            throw FileSystemException.InvalidName(name, "'.' and '..' are not allowed.");
        }

        if (name.Trim() != name)
        {
            throw FileSystemException.InvalidName(name, "the name must not start or end with whitespace.");
        }

        foreach (var c in name)
        {
            if (c == '/' || c == '\\' || char.IsControl(c))
            {
                throw FileSystemException.InvalidName(name, "'/', '\\' and control characters are not allowed.");
            }
        }
    }

    /// <summary>
    ///     Splits a slash-separated path into its segments; empty segments (leading, trailing or double slashes)
    ///     are dropped. The segments arrive URL-decoded from routing.
    /// </summary>
    public static IReadOnlyList<string> SplitPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return [];
        }

        return path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    ///     Joins path segments with '/'.
    /// </summary>
    public static string JoinPath(IEnumerable<string> segments) => string.Join('/', segments);

    /// <summary>
    ///     Returns the next free name for "keep both": <c>a.pdf</c> → <c>a (1).pdf</c>, <c>a (2).pdf</c>, …
    /// </summary>
    public static string NextFreeName(string name, Func<string, bool> isTaken)
    {
        var extension = Path.GetExtension(name);
        var stem = extension.Length > 0 && extension.Length < name.Length ? name[..^extension.Length] : name;
        if (stem.Length == name.Length)
        {
            extension = string.Empty;
        }

        for (var i = 1; i < 10_000; i++)
        {
            var candidate = $"{stem} ({i}){extension}";
            if (!isTaken(candidate))
            {
                return candidate;
            }
        }

        throw FileSystemException.NameConflict(name);
    }
}
