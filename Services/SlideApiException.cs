namespace SlideStudio.Services;

public sealed class SlideApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

