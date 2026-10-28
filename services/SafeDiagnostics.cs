using System.IO;
using System.Net.Http;

namespace northApi.services;

/// <summary>Safe for UI error messages and persisted diagnostics. Never includes request values or raw exceptions.</summary>
public static class SafeDiagnostics
{
    public static string RequestError(Exception exception) => exception switch
    {
        OperationCanceledException => "Request canceled.",
        TimeoutException => "The request timed out. Increase the timeout or check the server.",
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => "The server name could not be resolved. Check the URL and DNS settings.",
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => "The TLS certificate could not be validated or the secure connection failed.",
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } => "Could not connect to the server. Check the address, port, and network.",
        HttpRequestException => "The HTTP connection failed. Check the network and server.",
        ArgumentException => "Invalid request settings. Check the HTTP URL, enabled headers, authorization, body, and environment variables.",
        UnauthorizedAccessException => "Access to the request file or local storage was denied.",
        IOException => "The request file or response stream could not be read.",
        _ => "The request could not be completed. Check the request settings and try again."
    };
    public static string LocalError => "Local data could not be read or saved. Check file permissions and format.";
}
