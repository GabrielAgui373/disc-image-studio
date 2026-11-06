namespace DiscImageStudio.Application.Results;

/// <summary>
/// Stable failure categories. Presentation layers localize these codes and may log
/// <see cref="ApplicationError.Detail"/> for diagnostics.
/// </summary>
public enum ApplicationErrorCode
{
    InvalidRequest,
    NoProjectOpen,
    ProjectReadFailed,
    ProjectWriteFailed,
    UserCanceled,
    NavigationRequiresProject,
    JobConflict,
    JobNotFound,
    JobFailed,
    JobCanceled,
    ApplicationShuttingDown,
}

public sealed record ApplicationError(
    ApplicationErrorCode Code,
    string? Detail = null,
    Exception? Exception = null);

public readonly record struct ApplicationResult
{
    private ApplicationResult(bool succeeded, ApplicationError? error)
    {
        Succeeded = succeeded;
        Error = error;
    }

    public bool Succeeded { get; }

    public ApplicationError? Error { get; }

    public static ApplicationResult Success() => new(true, null);

    public static ApplicationResult Failure(ApplicationErrorCode code, string? detail = null, Exception? exception = null)
        => new(false, new ApplicationError(code, detail, exception));
}

public readonly record struct ApplicationResult<T> where T : notnull
{
    private readonly T? _value;

    private ApplicationResult(bool succeeded, T? value, ApplicationError? error)
    {
        Succeeded = succeeded;
        _value = value;
        Error = error;
    }

    public bool Succeeded { get; }

    public T Value => Succeeded
        ? _value!
        : throw new InvalidOperationException("A failed application result has no value.");

    public ApplicationError? Error { get; }

    public static ApplicationResult<T> Success(T value)
        => new(true, value ?? throw new ArgumentNullException(nameof(value)), null);

    public static ApplicationResult<T> Failure(
        ApplicationErrorCode code,
        string? detail = null,
        Exception? exception = null)
        => new(false, default, new ApplicationError(code, detail, exception));
}
