using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace northApi.services;

public sealed class HttpRequestEngine
{
    public const int MaxPreviewBytes = 8 * 1024 * 1024;

    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        AllowAutoRedirect = true,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient client;
    public HttpRequestEngine() : this(SharedClient) { }
    public HttpRequestEngine(HttpClient client) => this.client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<HttpResponseResult> SendAsync(HttpRequestMessage request, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri is not { IsAbsoluteUri: true } uri ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Enter a valid http:// or https:// URL.");
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be greater than zero and at most 600 seconds.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var watch = Stopwatch.StartNew();
        try
        {
            // Keep the deadline active while reading the body, not just the headers.
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (buffer.Length <= MaxPreviewBytes)
            {
                var available = (int)Math.Min(chunk.Length, MaxPreviewBytes + 1L - buffer.Length);
                var count = await stream.ReadAsync(chunk.AsMemory(0, available), deadline.Token).ConfigureAwait(false);
                if (count == 0) break;
                buffer.Write(chunk, 0, count);
            }
            watch.Stop();
            var truncated = buffer.Length > MaxPreviewBytes;
            var bytes = buffer.ToArray();
            if (truncated) Array.Resize(ref bytes, MaxPreviewBytes);
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var charset = response.Content.Headers.ContentType?.CharSet;
            var encoding = Encoding.UTF8;
            if (!string.IsNullOrWhiteSpace(charset))
            {
                try { encoding = Encoding.GetEncoding(charset.Trim('"')); }
                catch (ArgumentException) { /* Unknown charset: display as UTF-8. */ }
            }
            using var reader = new StreamReader(new MemoryStream(bytes), encoding, detectEncodingFromByteOrderMarks: true);
            var body = await reader.ReadToEndAsync(deadline.Token).ConfigureAwait(false);
            var headers = response.Headers.Concat(response.Content.Headers)
                .Select(header => new HttpResponseHeader(header.Key, header.Value.ToArray())).ToArray();
            var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToArray() : [];
            return new HttpResponseResult((int)response.StatusCode, response.ReasonPhrase ?? "", body,
                mediaType, bytes.LongLength, truncated, watch.Elapsed, headers, cookies);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"Request timed out after {timeout.TotalSeconds:0.##} seconds.", exception);
        }
    }
}

public sealed record HttpResponseHeader(string Name, string[] Values);

public sealed record HttpResponseResult(int StatusCode, string ReasonPhrase, string Body,
    string? MediaType, long BodyBytes, bool IsTruncated, TimeSpan Elapsed,
    HttpResponseHeader[] Headers, string[] Cookies);
