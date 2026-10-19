using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json.Serialization;

namespace ChunsonSpace.Accounts;

// Store only fixed framework categories, never the original exception/message/stack/Data.
public sealed class AccountTransportFailure
{
    [JsonPropertyName("exceptionType")] public string ExceptionType { get; }
    [JsonPropertyName("httpRequestError")] public string? HttpRequestError { get; }
    // May describe a failed proxy tunnel; it is not a received model response status.
    [JsonPropertyName("httpStatus")] public int? HttpStatus { get; }
    [JsonPropertyName("innerExceptionTypes")] public IReadOnlyList<string> InnerExceptionTypes { get; }
    [JsonPropertyName("socketErrorCode")] public string? SocketErrorCode { get; }
    [JsonPropertyName("cancellationSource")] public string? CancellationSource { get; }

    private AccountTransportFailure(string exceptionType, string? httpRequestError, int? httpStatus,
        string[] innerExceptionTypes, string? socketErrorCode, string? cancellationSource)
    {
        ExceptionType = exceptionType; HttpRequestError = httpRequestError; HttpStatus = httpStatus;
        InnerExceptionTypes = Array.AsReadOnly(innerExceptionTypes); SocketErrorCode = socketErrorCode; CancellationSource = cancellationSource;
    }
    internal static AccountTransportFailure Capture(Exception exception, bool? callerCancellationRequested = null, bool deadlineElapsed = false)
    {
        var chain = new List<Exception>();
        for (Exception? current = exception; current is not null && chain.Count < 8; current = current.InnerException) chain.Add(current);
        var request = chain.OfType<HttpRequestException>().FirstOrDefault();
        var error = request?.HttpRequestError ?? chain.OfType<HttpIOException>().FirstOrDefault()?.HttpRequestError;
        var status = request?.StatusCode is { } code && (int)code is >= 100 and <= 599 ? (int?)code : null;
        var socket = chain.OfType<SocketException>().FirstOrDefault()?.SocketErrorCode;
        var cancellation = exception is OperationCanceledException
            ? callerCancellationRequested == true ? "CallerRequested" : deadlineElapsed ? "DeadlineElapsed" : "Unknown"
            : null;
        return new(Type(exception), error is { } value && Enum.IsDefined(value) ? value.ToString() : null, status,
            chain.Skip(1).Select(Type).ToArray(), socket is { } socketValue && Enum.IsDefined(socketValue) ? socketValue.ToString() : null, cancellation);
    }
    private static string Type(Exception exception) => exception switch
    {
        HttpRequestException => nameof(HttpRequestException),
        HttpIOException => nameof(HttpIOException),
        SocketException => nameof(SocketException),
        AuthenticationException => nameof(AuthenticationException),
        TaskCanceledException => nameof(TaskCanceledException),
        OperationCanceledException => nameof(OperationCanceledException),
        TimeoutException => nameof(TimeoutException),
        IOException => nameof(IOException),
        _ => "Other"
    };
}
