using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using northApi.models;
using northApi.services;
using Xunit;

[assembly:CollectionBehavior(DisableTestParallelization=true)]
namespace ReleaseQA;

public class HttpTests
{
    [Theory]
    [InlineData("GET")][InlineData("POST")][InlineData("PUT")][InlineData("PATCH")][InlineData("DELETE")]
    public async Task Local_methods_status_headers_body_time_size(string method)
    {
        await using var server=new QaServer();var source=Definition(method,server.Url);
        using var request=RequestBuilder.Build(source);
        var response=await new HttpRequestEngine().SendAsync(request,TimeSpan.FromSeconds(5));
        Assert.Equal(200,response.StatusCode);Assert.Contains(response.Headers,h=>h.Name=="X-QA");
        Assert.Equal(Encoding.UTF8.GetByteCount(response.Body),response.BodyBytes);Assert.True(response.Elapsed>TimeSpan.Zero);
        var wire=JsonSerializer.Deserialize<WireRequest>(response.Body)!;
        Assert.Equal(method,wire.Method);Assert.Equal("/echo?q=value%20with%20spaces",wire.Target);
        Assert.True(wire.Headers.TryGetValue("X-Custom",out var value)&&value=="qa");
        Assert.True(wire.Body==(method=="GET"?"":"{\"message\":\"qa\"}"),"Request body differs.");
    }
    [Theory]
    [InlineData(400)][InlineData(401)][InlineData(404)][InlineData(429)][InlineData(500)][InlineData(503)]
    public async Task Non_success_responses_remain_inspectable(int status)
    {
        await using var server=new QaServer();using var request=new HttpRequestMessage(HttpMethod.Get,server.Url+"/status/"+status);
        var result=await new HttpRequestEngine().SendAsync(request,TimeSpan.FromSeconds(5));
        Assert.Equal(status,result.StatusCode);using var json=JsonDocument.Parse(result.Body);Assert.Equal(status,json.RootElement.GetProperty("error").GetInt32());
        Assert.True(result.BodyBytes>0&&result.Elapsed>TimeSpan.Zero&&result.Headers.Length>0);
    }
    [Theory]
    [InlineData("bad url")][InlineData("https://")][InlineData("file:///C:/Windows")][InlineData("http://[invalid")]
    public void Invalid_URL_is_understandable(string url)
    {
        var exception=Assert.Throws<ArgumentException>(()=>RequestBuilder.Build(new(){Url=url}));
        Assert.Contains("HTTP URL",SafeDiagnostics.RequestError(exception));
    }
    [Fact]
    public async Task DNS_failure_is_handled()
    {
        using var client=new HttpClient(new SocketsHttpHandler{UseProxy=false});using var request=new HttpRequestMessage(HttpMethod.Get,"https://northapi-qa-does-not-exist.invalid/");
        var exception=await Assert.ThrowsAsync<HttpRequestException>(()=>new HttpRequestEngine(client).SendAsync(request,TimeSpan.FromSeconds(8)));
        Assert.Equal(HttpRequestError.NameResolutionError,exception.HttpRequestError);Assert.Contains("DNS",SafeDiagnostics.RequestError(exception));
    }
    [Fact]
    public async Task Refused_connection_is_handled()
    {
        var listener=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);listener.Start();var port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        using var request=new HttpRequestMessage(HttpMethod.Get,$"http://127.0.0.1:{port}/");
        var exception=await Assert.ThrowsAsync<HttpRequestException>(()=>new HttpRequestEngine().SendAsync(request,TimeSpan.FromSeconds(5)));
        Assert.Contains("connect",SafeDiagnostics.RequestError(exception));
    }
    [Fact]
    public async Task Untrusted_TLS_is_rejected()
    {
        await using var server=new QaServer(tls:true);using var request=new HttpRequestMessage(HttpMethod.Get,server.Url);
        var exception=await Assert.ThrowsAsync<HttpRequestException>(()=>new HttpRequestEngine().SendAsync(request,TimeSpan.FromSeconds(5)));
        Assert.Equal(HttpRequestError.SecureConnectionError,exception.HttpRequestError);Assert.Contains("TLS",SafeDiagnostics.RequestError(exception));
    }
    [Theory][InlineData("/slow")][InlineData("/slow-body")]
    public async Task Timeout_includes_headers_and_body(string path)
    {
        await using var server=new QaServer();using var request=new HttpRequestMessage(HttpMethod.Get,server.Url+path);
        await Assert.ThrowsAsync<TimeoutException>(()=>new HttpRequestEngine().SendAsync(request,TimeSpan.FromMilliseconds(200)));
    }
    [Fact]
    public async Task Cancellation_is_distinct_from_timeout()
    {
        await using var server=new QaServer();using var request=new HttpRequestMessage(HttpMethod.Get,server.Url+"/slow");using var cancellation=new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new HttpRequestEngine().SendAsync(request,TimeSpan.FromSeconds(5),cancellation.Token));
    }
    [Theory][InlineData("/malformed",200)][InlineData("/empty",204)]
    public async Task Malformed_and_empty_are_HTTP_results(string path,int status)
    {
        await using var server=new QaServer();using var request=new HttpRequestMessage(HttpMethod.Get,server.Url+path);
        var result=await new HttpRequestEngine().SendAsync(request,TimeSpan.FromSeconds(5));Assert.Equal(status,result.StatusCode);
        Assert.True(path=="/empty" ? result.Body.Length==0&&result.BodyBytes==0 : result.Body=="{not-json");
    }
    [Theory][Trait("Category","External")]
    [InlineData("GET")][InlineData("POST")][InlineData("PUT")][InlineData("PATCH")][InlineData("DELETE")]
    public async Task External_HTTPS_httpbingo(string method)
    {
        var source=Definition(method,"https://httpbingo.org");source.Url="https://httpbingo.org/"+method.ToLowerInvariant();
        using var request=RequestBuilder.Build(source);
        request.Headers.UserAgent.ParseAdd("NorthAPI-QA/1.0"); var result=await new HttpRequestEngine().SendAsync(request,TimeSpan.FromSeconds(20));
        Assert.Equal(200,result.StatusCode);Assert.True(result.Elapsed>TimeSpan.Zero&&result.BodyBytes==Encoding.UTF8.GetByteCount(result.Body));
        Assert.Contains(result.Headers,h=>h.Name.Equals("Content-Type",StringComparison.OrdinalIgnoreCase));
        using var json=JsonDocument.Parse(result.Body);Assert.Equal(method,json.RootElement.GetProperty("method").GetString());
        Assert.True(json.RootElement.GetProperty("args").GetProperty("q")[0].GetString()=="value with spaces");
        Assert.True(json.RootElement.GetProperty("headers").GetProperty("X-Custom")[0].GetString()=="qa");
        if(method!="GET")Assert.True(json.RootElement.GetProperty("data").GetString()==source.Body.Raw);
    }
    internal static RequestDefinition Definition(string method,string url)=>new(){Method=method,Url=url+"/echo",Parameters=[new(){Key="q",Value="value with spaces"}],Headers=[new(){Key="X-Custom",Value="qa"}],Body=new(){Mode=method=="GET"?"none":"raw",ContentType="application/json",Raw="{\"message\":\"qa\"}"}};
}
