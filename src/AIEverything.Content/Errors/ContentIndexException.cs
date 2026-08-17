namespace AIEverything.Content.Errors;

public sealed class ContentIndexException : Exception
{
    public ContentIndexException(
        string code,
        string message,
        string correctiveAction,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        CorrectiveAction = correctiveAction;
    }

    public string Code { get; }

    public string CorrectiveAction { get; }
}

public static class ContentErrorCodes
{
    public const string ServiceUnavailable = "CONTENT_SERVICE_UNAVAILABLE";
    public const string IndexNotConfigured = "CONTENT_INDEX_NOT_CONFIGURED";
    public const string RootNotFound = "ROOT_NOT_FOUND";
    public const string RootNotAllowed = "ROOT_NOT_ALLOWED";
    public const string UnsupportedFileType = "UNSUPPORTED_FILE_TYPE";
    public const string UnsupportedEncoding = "UNSUPPORTED_ENCODING";
    public const string FileTooLarge = "FILE_TOO_LARGE";
    public const string OcrRequired = "OCR_REQUIRED";
    public const string ExtractionTimeout = "EXTRACTION_TIMEOUT";
    public const string ExtractionFailed = "EXTRACTION_FAILED";
    public const string IndexBusy = "CONTENT_INDEX_BUSY";
    public const string IndexCorrupt = "CONTENT_INDEX_CORRUPT";
    public const string QueryTooBroad = "QUERY_TOO_BROAD";
    public const string InvalidArguments = "INVALID_ARGUMENTS";
}
