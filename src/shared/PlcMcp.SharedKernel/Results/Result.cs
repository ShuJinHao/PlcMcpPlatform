namespace PlcMcp.SharedKernel.Results;

public sealed record OperationError(string Code, string Message, bool Retryable = false);

public sealed class Result<T>
{
    private Result(T? value, OperationError? error) { Value = value; Error = error; }
    public bool IsSuccess => Error is null;
    public T? Value { get; }
    public OperationError? Error { get; }
    public static Result<T> Success(T value) => new(value, null);
    public static Result<T> Failure(string code, string message, bool retryable = false)
        => new(default, new(code, message, retryable));
}
