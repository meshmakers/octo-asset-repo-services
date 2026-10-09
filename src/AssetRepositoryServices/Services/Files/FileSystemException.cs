namespace Meshmakers.Octo.Backend.AssetRepositoryServices.Services.Files;

/// <summary>
///     A rule of the platform file system was violated (AB#6171). Carries a stable error code that REST
///     answers as problem details (<c>code</c> extension) and GraphQL as the error code, plus the HTTP status
///     the REST API answers with.
/// </summary>
public class FileSystemException : Exception
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public FileSystemException(string code, int statusCode, string message) : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }

    /// <summary>
    ///     Stable error code (e.g. <c>NAME_CONFLICT</c>).
    /// </summary>
    public string Code { get; }

    /// <summary>
    ///     HTTP status of the REST answer.
    /// </summary>
    public int StatusCode { get; }

    internal static FileSystemException RootNotFound(string root) =>
        new(FileSystemErrorCodes.RootNotFound, StatusCodes.Status404NotFound, $"Folder root '{root}' not found.");

    internal static FileSystemException PathNotFound(string path) =>
        new(FileSystemErrorCodes.PathNotFound, StatusCodes.Status404NotFound, $"'{path}' not found.");

    internal static FileSystemException ItemNotFound(string rtId) =>
        new(FileSystemErrorCodes.PathNotFound, StatusCodes.Status404NotFound, $"File system entry '{rtId}' not found.");

    internal static FileSystemException AmbiguousPath(string path) =>
        new(FileSystemErrorCodes.AmbiguousPath, StatusCodes.Status409Conflict,
            $"'{path}' matches more than one entry with the same name. Address the entry by its rtId.");

    internal static FileSystemException NameConflict(string name) =>
        new(FileSystemErrorCodes.NameConflict, StatusCodes.Status409Conflict,
            $"An entry named '{name}' already exists in the target folder.");

    internal static FileSystemException InvalidName(string? name, string reason) =>
        new(FileSystemErrorCodes.InvalidName, StatusCodes.Status400BadRequest, $"Invalid name '{name}': {reason}");

    internal static FileSystemException ReservedRootName(string name) =>
        new(FileSystemErrorCodes.ReservedName, StatusCodes.Status409Conflict,
            $"'{name}' is reserved by the file API and cannot be used as well-known name of a folder root.");

    internal static FileSystemException RootExists(string name) =>
        new(FileSystemErrorCodes.NameConflict, StatusCodes.Status409Conflict,
            $"A folder root with the well-known name '{name}' already exists.");

    internal static FileSystemException ProtectedRoot(string name) =>
        new(FileSystemErrorCodes.ProtectedRoot, StatusCodes.Status409Conflict,
            $"The folder root '{name}' is owned by a service or a blueprint and cannot be renamed or deleted.");

    internal static FileSystemException RootCreationForbidden() =>
        new(FileSystemErrorCodes.Forbidden, StatusCodes.Status403Forbidden,
            "Creating a folder root requires the role 'FileManagement'.");

    internal static FileSystemException NotAFolder(string path) =>
        new(FileSystemErrorCodes.NotAFolder, StatusCodes.Status409Conflict, $"'{path}' is not a folder.");

    internal static FileSystemException NotAFile(string path) =>
        new(FileSystemErrorCodes.NotAFile, StatusCodes.Status409Conflict, $"'{path}' is not a file.");

    internal static FileSystemException MoveIntoItself(string name) =>
        new(FileSystemErrorCodes.MoveIntoItself, StatusCodes.Status409Conflict,
            $"The folder '{name}' cannot be moved into itself or one of its subfolders.");

    internal static FileSystemException UploadTooLarge(long maxBytes) =>
        new(FileSystemErrorCodes.LimitExceeded, StatusCodes.Status413PayloadTooLarge,
            $"The file exceeds the upload limit of {maxBytes} bytes.");

    internal static FileSystemException ZipTooManyFiles(int count, int max) =>
        new(FileSystemErrorCodes.LimitExceeded, StatusCodes.Status413PayloadTooLarge,
            $"The selection contains {count} files; a zip download may contain at most {max}.");

    internal static FileSystemException ZipTooLarge(long bytes, long max) =>
        new(FileSystemErrorCodes.LimitExceeded, StatusCodes.Status413PayloadTooLarge,
            $"The selection has {bytes} bytes; a zip download may contain at most {max} bytes.");

    internal static FileSystemException SingleParent(string name) =>
        new(FileSystemErrorCodes.InvalidRequest, StatusCodes.Status400BadRequest,
            $"'{name}' can have only one parent folder; remove the current parent in the same update to move it.");

    internal static FileSystemException DeleteTooLarge(string name, int max) =>
        new(FileSystemErrorCodes.LimitExceeded, StatusCodes.Status409Conflict,
            $"Deleting '{name}' would remove more than {max} entries in one step; delete its subfolders first.");

    internal static FileSystemException Forbidden(string message) =>
        new(FileSystemErrorCodes.Forbidden, StatusCodes.Status403Forbidden, message);

    internal static FileSystemException InvalidRequest(string message) =>
        new(FileSystemErrorCodes.InvalidRequest, StatusCodes.Status400BadRequest, message);
}

/// <summary>
///     Stable error codes of the platform file system (REST problem details <c>code</c>, GraphQL error code).
/// </summary>
public static class FileSystemErrorCodes
{
    /// <summary>The folder root does not exist or is not visible.</summary>
    public const string RootNotFound = "ROOT_NOT_FOUND";

    /// <summary>The path or rtId does not exist or is not visible.</summary>
    public const string PathNotFound = "PATH_NOT_FOUND";

    /// <summary>A path segment matches more than one entry (legacy duplicates).</summary>
    public const string AmbiguousPath = "AMBIGUOUS_PATH";

    /// <summary>The name is already used in the target folder.</summary>
    public const string NameConflict = "NAME_CONFLICT";

    /// <summary>The name is empty, too long or contains '/', control characters or is '.' / '..'.</summary>
    public const string InvalidName = "INVALID_NAME";

    /// <summary>The well-known name is reserved by the file API.</summary>
    public const string ReservedName = "RESERVED_NAME";

    /// <summary>The root is owned by a service or blueprint.</summary>
    public const string ProtectedRoot = "PROTECTED_ROOT";

    /// <summary>The caller lacks the required role.</summary>
    public const string Forbidden = "FORBIDDEN";

    /// <summary>The entry is not a folder.</summary>
    public const string NotAFolder = "NOT_A_FOLDER";

    /// <summary>The entry is not a file.</summary>
    public const string NotAFile = "NOT_A_FILE";

    /// <summary>A folder would become its own descendant.</summary>
    public const string MoveIntoItself = "MOVE_INTO_ITSELF";

    /// <summary>An upload or zip limit is exceeded.</summary>
    public const string LimitExceeded = "LIMIT_EXCEEDED";

    /// <summary>The request is malformed.</summary>
    public const string InvalidRequest = "INVALID_REQUEST";
}
