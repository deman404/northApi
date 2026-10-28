using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using northApi.services;

await using var server = new LocalServer();
var engine = new HttpRequestEngine();
var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}
async Task<HttpResponseResult> Send(string method, string path, HttpContent? content = null)
{
    using var request = new HttpRequestMessage(new HttpMethod(method), server.Url + path) { Content = content };
    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "local-test-token");
    request.Headers.Add("X-Test", "NorthApi");
    return await engine.SendAsync(request, TimeSpan.FromSeconds(5));
}

foreach (var method in new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS" })
{
    var result = await Send(method, "/echo?key=hello%20world", method == "GET" ? null : new StringContent("{\"message\":\"hello\"}", Encoding.UTF8, "application/json"));
    using var json = JsonDocument.Parse(result.Body);
    Check(result.StatusCode == 200 && json.RootElement.GetProperty("method").GetString() == method, method + " real HTTP request");
    Check(json.RootElement.GetProperty("path").GetString() == "/echo?key=hello%20world", method + " query preserved");
    var headers = json.RootElement.GetProperty("headers");
    Check(headers.GetProperty("Authorization").GetString() == "Bearer local-test-token" && headers.GetProperty("X-Test").GetString() == "NorthApi", method + " headers and authorization");
    if (method != "GET") Check(json.RootElement.GetProperty("body").GetString() == "{\"message\":\"hello\"}", method + " JSON body");
}
Check((await Send("HEAD", "/echo")).BodyBytes == 0, "HEAD response has no body");
var form = await Send("POST", "/echo", new FormUrlEncodedContent(new Dictionary<string, string> { ["name"] = "hello world", ["symbol"] = "&" }));
using (var json = JsonDocument.Parse(form.Body))
    Check(json.RootElement.GetProperty("body").GetString() == "name=hello+world&symbol=%26", "form URL encoding");
var multipart = new MultipartFormDataContent();
multipart.Add(new StringContent("hello"), "name");
using (var json = JsonDocument.Parse((await Send("POST", "/echo", multipart)).Body))
    Check(json.RootElement.GetProperty("body").GetString()!.Contains("hello") && json.RootElement.GetProperty("headers").GetProperty("Content-Type").GetString()!.StartsWith("multipart/form-data; boundary="), "multipart body and boundary");
using (var json = JsonDocument.Parse((await Send("POST", "/echo", new ByteArrayContent([65, 66, 67]))).Body))
    Check(json.RootElement.GetProperty("body").GetString() == "ABC", "binary request payload");
var notFound = await Send("GET", "/missing");
Check(notFound.StatusCode == 404 && notFound.Body.Contains("missing"), "HTTP errors retain response body");
Check((await Send("GET", "/redirect")).StatusCode == 200, "redirects followed");
Check((await Send("GET", "/gzip")).Body == "compressed response", "gzip response decoded");
var latin = await Send("GET", "/latin");
Check(latin.Body == "café" && latin.BodyBytes == 4, "response charset and actual byte size");
var cookie = await Send("GET", "/cookie");
Check(cookie.Cookies.Single() == "session=local; HttpOnly" && cookie.Headers.Any(h => h.Name == "X-Response"), "response headers and cookies");
var large = await Send("GET", "/large");
Check(large.IsTruncated && large.BodyBytes == HttpRequestEngine.MaxPreviewBytes, "large response preview bounded");
using (var request = new HttpRequestMessage(HttpMethod.Get, server.Url + "/slow"))
{
    try { await engine.SendAsync(request, TimeSpan.FromMilliseconds(100)); throw new Exception("Timeout did not fire"); }
    catch (TimeoutException) { Check(true, "timeout reported separately"); }
}
using (var cancellation = new CancellationTokenSource(100))
using (var request = new HttpRequestMessage(HttpMethod.Get, server.Url + "/slow"))
{
    try { await engine.SendAsync(request, TimeSpan.FromSeconds(5), cancellation.Token); throw new Exception("Cancellation did not fire"); }
    catch (OperationCanceledException) { Check(true, "user cancellation"); }
}
using (var request = new HttpRequestMessage(HttpMethod.Get, server.Url + "/slow-body"))
{
    try { await engine.SendAsync(request, TimeSpan.FromMilliseconds(100)); throw new Exception("Body timeout did not fire"); }
    catch (TimeoutException) { Check(true, "timeout includes reading response body"); }
}
passed += await FeatureChecks.Run(engine,server.Url);
Console.WriteLine($"{passed} HTTP and workspace checks passed.");

sealed class LocalServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stopping = new();
    private readonly Task acceptLoop;
    public string Url { get; }
    public LocalServer()
    {
        listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        acceptLoop = AcceptLoop();
    }
    private async Task AcceptLoop()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stopping.Token);
                _ = Handle(client);
            }
        }
        catch (OperationCanceledException) { }
    }
    private static async Task Handle(TcpClient client)
    {
        using (client)
        try
        {
            var stream = client.GetStream();
            using var headerBuffer = new MemoryStream();
            var one = new byte[1];
            var ending = 0;
            while (await stream.ReadAsync(one) > 0)
            {
                headerBuffer.WriteByte(one[0]);
                ending = (ending << 8) | one[0];
                if (ending == 0x0D0A0D0A) break;
                if (headerBuffer.Length > 65536) throw new IOException("Headers too large");
            }
            var lines = Encoding.ASCII.GetString(headerBuffer.ToArray()).Split("\r\n");
            var first = lines[0].Split(' ');
            var method = first[0]; var path = first[1];
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1).Where(line => line.Contains(':')))
            {
                var index = line.IndexOf(':');
                headers[line[..index]] = line[(index + 1)..].Trim();
            }
            var payload = new byte[headers.TryGetValue("Content-Length", out var length) ? int.Parse(length) : 0];
            if (payload.Length > 0) await stream.ReadExactlyAsync(payload);
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { method, path, headers, body = Encoding.UTF8.GetString(payload) }));
            var status = "200 OK"; var extra = "X-Response: local-check\r\n"; var contentType = "application/json";
            switch (path)
            {
                case "/missing": status = "404 Not Found"; body = Encoding.UTF8.GetBytes("missing"); break;
                case "/redirect": status = "302 Found"; extra += "Location: /echo\r\n"; body = []; break;
                case "/gzip":
                    using (var compressed = new MemoryStream())
                    {
                        using (var gzip = new GZipStream(compressed, CompressionMode.Compress, true)) gzip.Write(Encoding.UTF8.GetBytes("compressed response"));
                        body = compressed.ToArray();
                    }
                    extra += "Content-Encoding: gzip\r\n"; contentType = "text/plain"; break;
                case "/latin": body = Encoding.Latin1.GetBytes("café"); contentType = "text/plain; charset=iso-8859-1"; break;
                case "/cookie": extra += "Set-Cookie: session=local; HttpOnly\r\n"; break;
                case "/large": body = Enumerable.Repeat((byte)'x', HttpRequestEngine.MaxPreviewBytes + 1024).ToArray(); contentType = "text/plain"; break;
                case "/slow": await Task.Delay(800); break;
            }
            var responseHeaders = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\n{extra}Connection: close\r\n\r\n");
            await stream.WriteAsync(responseHeaders);
            if (path == "/slow-body") await Task.Delay(800);
            if (method != "HEAD") await stream.WriteAsync(body);
        }
        catch (Exception exception) when (exception is IOException or SocketException) { /* Client canceled or stopped reading a bounded preview. */ }
    }
    public async ValueTask DisposeAsync()
    {
        stopping.Cancel(); listener.Stop();
        await acceptLoop;
        stopping.Dispose();
    }
}
