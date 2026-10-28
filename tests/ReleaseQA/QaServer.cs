using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace ReleaseQA;

internal sealed record WireRequest(string Method,string Target,Dictionary<string,string> Headers,string Body);
internal sealed class QaServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback,0);
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private readonly X509Certificate2? certificate;
    public string Url { get; }
    public ConcurrentQueue<WireRequest> Requests { get; } = new();
    public QaServer(bool tls=false)
    {
        if(tls)
        {
            using var rsa=RSA.Create(2048);
            var request=new CertificateRequest("CN=localhost",rsa,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
            var san=new SubjectAlternativeNameBuilder();san.AddDnsName("localhost");san.AddIpAddress(IPAddress.Loopback);request.CertificateExtensions.Add(san.Build());
            certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),DateTimeOffset.UtcNow.AddDays(1));
        }
        listener.Start();Url=$"{(tls?"https":"http")}://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        loop=Accept();
    }
    private async Task Accept()
    {
        try{while(!stop.IsCancellationRequested){var client=await listener.AcceptTcpClientAsync(stop.Token);_ = Serve(client);}}
        catch(OperationCanceledException){}
    }
    private async Task Serve(TcpClient client)
    {
        using(client)
        try
        {
            Stream stream=client.GetStream();
            if(certificate is not null){var ssl=new SslStream(stream,false);await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=certificate},stop.Token);stream=ssl;}
            await using(stream)
            {
                var first=(await Line(stream)).Split(' ');if(first.Length<2)return;
                var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                while(true){var line=await Line(stream);if(line.Length==0)break;var colon=line.IndexOf(':');if(colon>0)headers[line[..colon]]=line[(colon+1)..].Trim();}
                using var buffer=new MemoryStream();
                if(headers.TryGetValue("Content-Length",out var length))
                {var bytes=new byte[int.Parse(length)];await stream.ReadExactlyAsync(bytes,stop.Token);buffer.Write(bytes);}
                else if(headers.TryGetValue("Transfer-Encoding",out var transfer) && transfer.Contains("chunked"))
                {
                    while(true){var size=Convert.ToInt32((await Line(stream)).Split(';')[0],16);if(size==0){while((await Line(stream)).Length>0){}break;}var bytes=new byte[size];await stream.ReadExactlyAsync(bytes,stop.Token);buffer.Write(bytes);await Line(stream);}
                }
                var observed=new WireRequest(first[0],first[1],headers,Encoding.UTF8.GetString(buffer.ToArray()));Requests.Enqueue(observed);
                var path=first[1].Split('?')[0];var status=200;var type="application/json; charset=utf-8";
                var body=JsonSerializer.SerializeToUtf8Bytes(observed);
                if(path.StartsWith("/status/")){status=int.Parse(path[8..]);body=JsonSerializer.SerializeToUtf8Bytes(new{error=status,message="deterministic error"});}
                if(path=="/malformed")body=Encoding.UTF8.GetBytes("{not-json");
                if(path=="/empty"){status=204;body=[];}
                if(path=="/slow")await Task.Delay(2000,stop.Token);
                var reason=status switch{200=>"OK",204=>"No Content",400=>"Bad Request",401=>"Unauthorized",404=>"Not Found",429=>"Too Many Requests",500=>"Internal Server Error",503=>"Service Unavailable",_=>"Test"};
                var response=Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nX-QA: deterministic\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response,stop.Token);await stream.FlushAsync(stop.Token);
                if(path=="/slow-body")await Task.Delay(2000,stop.Token);
                if(first[0]!="HEAD")await stream.WriteAsync(body,stop.Token);
            }
        }
        catch(Exception exception)when(exception is IOException or SocketException or OperationCanceledException or AuthenticationException){}
    }
    private async Task<string> Line(Stream stream)
    {
        using var bytes=new MemoryStream();var one=new byte[1];
        while(await stream.ReadAsync(one,stop.Token)>0){if(one[0]==10)break;bytes.WriteByte(one[0]);if(bytes.Length>65536)throw new IOException("Header limit.");}
        return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
    }
    public async ValueTask DisposeAsync(){stop.Cancel();listener.Stop();await loop;certificate?.Dispose();stop.Dispose();}
}
