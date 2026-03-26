using ZimraFdms.Models;

namespace ZimraFdms;

public class FdmsApiException : Exception
{
    public int HttpStatusCode { get; }
    public string? ErrorCode { get; }
    public ApiProblemDetails? ProblemDetails { get; }

    public FdmsApiException(int httpStatusCode, ApiProblemDetails? details)
        : base(details?.Title ?? $"FDMS API error {httpStatusCode}")
    {
        HttpStatusCode = httpStatusCode;
        ErrorCode = details?.ErrorCode;
        ProblemDetails = details;
    }

    public FdmsApiException(string message, Exception inner)
        : base(message, inner) { HttpStatusCode = 0; }
}
