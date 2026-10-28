using System.Text;
using System.Text.Json;
using northApi.models;

namespace northApi.services;

public sealed record GeneratedCode(string CSharp, string Curl);

public static class CodeGenerator
{
    public static GeneratedCode Generate(RequestDefinition definition, EnvironmentProfile? environment = null)
    {
        var resolved = RequestBuilder.Resolve(definition, environment);
        using var request = RequestBuilder.BuildResolved(resolved);
        static string Q(string value) => JsonSerializer.Serialize(value);
        static string Shell(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
        var cs = new StringBuilder("using System;\nusing System.Collections.Generic;\nusing System.IO;\nusing System.Net.Http;\nusing System.Text;\nusing System.Threading;\n\nusing var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };\n");
        cs.AppendLine($"using var request = new HttpRequestMessage(new HttpMethod({Q(resolved.Method)}), {Q(request.RequestUri!.AbsoluteUri)});");
        var curl = new StringBuilder($"curl --globoff --request {Shell(resolved.Method)} --location --max-time {resolved.TimeoutSeconds} {Shell(request.RequestUri.AbsoluteUri)}");
        if (resolved.Method == "HEAD") curl.Append(" --head");
        var body = resolved.Body;
        switch (body.Mode)
        {
            case "raw" when request.Content is null: break;
            case "raw": case "graphql":
                var text = body.Mode == "raw" ? body.Raw : RequestBuilder.GraphJson(body);
                var contentType = body.Mode == "raw" ? body.ContentType : "application/json";
                cs.AppendLine($"request.Content = new StringContent({Q(text)}, Encoding.UTF8, {Q(contentType)});");
                curl.Append($" --data-raw {Shell(text)}"); break;
            case "urlencoded":
                cs.AppendLine("request.Content = new FormUrlEncodedContent(new KeyValuePair<string, string>[]\n{");
                foreach (var field in body.Urlencoded.Where(field => field.Enabled && field.Key.Length > 0))
                { cs.AppendLine($"    new({Q(field.Key)}, {Q(field.Value)}),"); curl.Append($" --data-urlencode {Shell(field.Key + "=" + field.Value)}"); }
                cs.AppendLine("});"); break;
            case "form":
                cs.AppendLine("var form = new MultipartFormDataContent();");
                foreach (var field in body.FormData.Where(field => field.Enabled && field.Key.Length > 0))
                { cs.AppendLine($"form.Add(new StringContent({Q(field.Value)}), {Q(field.Key)});"); curl.Append($" --form-string {Shell(field.Key + "=" + field.Value)}"); }
                cs.AppendLine("request.Content = form;"); break;
            case "binary": cs.AppendLine($"request.Content = new StreamContent(File.OpenRead({Q(body.BinaryPath!)}));"); curl.Append($" --data-binary {Shell("@" + body.BinaryPath)}"); break;
        }
        foreach (var header in request.Headers)
            foreach (var value in header.Value)
            { cs.AppendLine($"request.Headers.TryAddWithoutValidation({Q(header.Key)}, {Q(value)});"); curl.Append($" --header {Shell(header.Key + ": " + value)}"); }
        if (request.Content is not null)
        {
            cs.AppendLine("request.Content ??= new ByteArrayContent(Array.Empty<byte>());");
            if (body.Mode == "none" || body.Mode == "raw" && body.Raw.Length == 0) curl.Append(" --data-raw ''");
            foreach (var header in request.Content.Headers)
            {
                // Each runtime must generate its own multipart boundary.
                if (body.Mode == "form" && header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && header.Value.Any(value => value.Contains("boundary=", StringComparison.OrdinalIgnoreCase))) continue;
                cs.AppendLine($"request.Content.Headers.Remove({Q(header.Key)});");
                foreach (var value in header.Value)
                { cs.AppendLine($"request.Content.Headers.TryAddWithoutValidation({Q(header.Key)}, {Q(value)});"); curl.Append($" --header {Shell(header.Key + ": " + value)}"); }
            }
        }
        cs.AppendLine($"using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds({resolved.TimeoutSeconds}));");
        cs.AppendLine("using var response = await client.SendAsync(request, cancellation.Token);\nConsole.WriteLine($\"{(int)response.StatusCode} {response.ReasonPhrase}\");\nConsole.WriteLine(await response.Content.ReadAsStringAsync(cancellation.Token));");
        return new GeneratedCode(cs.ToString(), curl.ToString());
    }
}
