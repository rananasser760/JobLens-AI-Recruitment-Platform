namespace JobLens.Application.Common;

public sealed record ApiResponse<T>(
    bool Success,
    T? Data = default,
    string Message = "",
    IReadOnlyList<string>? Errors = null);
