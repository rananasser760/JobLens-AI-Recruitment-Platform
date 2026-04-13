namespace JobLens.Application.Contracts;

public sealed record InternalApiError(string Code, string Message, string? Details = null);

public sealed record InternalApiEnvelope<T>(
    string RequestId,
    bool Success,
    T? Data,
    InternalApiError? Error = null);
