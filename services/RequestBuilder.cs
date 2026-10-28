using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using northApi.models;

namespace northApi.services;

public static class RequestBuilder
{
    public static RequestDefinition Resolve(RequestDefinition source, EnvironmentProfile? environment)
    {
        var result = WorkspaceStore.Copy(source);
        var resolver = new VariableResolver(environment);
        result.Url = resolver.Resolve(result.Url);
        foreach (var field in result.Parameters.Concat(result.Headers).Where(field => field.Enabled))
        { field.Key = resolver.Resolve(field.Key); field.Value = resolver.Resolve(field.Value); }
        switch (result.Body.Mode)
        {
            case "raw": result.Body.Raw = resolver.Resolve(result.Body.Raw); break;
            case "form": case "urlencoded":
                foreach (var field in (result.Body.Mode == "form" ? result.Body.FormData : result.Body.Urlencoded).Where(field => field.Enabled))
                { field.Key = resolver.Resolve(field.Key); field.Value = resolver.Resolve(field.Value); }
                break;
            case "binary": result.Body.BinaryPath = resolver.Resolve(result.Body.BinaryPath); break;
            case "graphql": result.Body.Query = resolver.Resolve(result.Body.Query); result.Body.Variables = resolver.Resolve(result.Body.Variables); break;
        }
        switch (result.Auth.Type)
        {
            case "basic": result.Auth.Username = resolver.Resolve(result.Auth.Username); result.Auth.Password = resolver.Resolve(result.Auth.Password); break;
            case "bearer": case "oauth2": result.Auth.Token = resolver.Resolve(result.Auth.Token); break;
            case "apiKey": result.Auth.ApiKeyName = resolver.Resolve(result.Auth.ApiKeyName); result.Auth.ApiKeyValue = resolver.Resolve(result.Auth.ApiKeyValue); break;
        }
        return result;
    }

    public static HttpRequestMessage Build(RequestDefinition definition, EnvironmentProfile? environment = null) => BuildResolved(Resolve(definition, environment));
    public static HttpRequestMessage BuildResolved(RequestDefinition definition)
    {
        if (!Uri.TryCreate(definition.Url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new ArgumentException("Enter a valid http:// or https:// URL.");
        var builder = new UriBuilder(uri);
        var query = builder.Query.TrimStart('?');
        void AddQuery(string key, string value) => query += (query.Length == 0 ? "" : "&") + Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value);
        foreach (var field in definition.Parameters.Where(field => field.Enabled && !string.IsNullOrWhiteSpace(field.Key))) AddQuery(field.Key, field.Value);
        if (definition.Auth.Type == "apiKey" && definition.Auth.ApiKeyInQuery)
        {
            if (string.IsNullOrWhiteSpace(definition.Auth.ApiKeyName)) throw new ArgumentException("Enter an API key name.");
            AddQuery(definition.Auth.ApiKeyName, definition.Auth.ApiKeyValue);
        }
        builder.Query = query;
        var request = new HttpRequestMessage(new HttpMethod(definition.Method), builder.Uri);
        try
        {
            request.Content = CreateBody(definition.Body);
            foreach (var field in definition.Headers.Where(field => field.Enabled && !string.IsNullOrWhiteSpace(field.Key)))
            {
                if (field.Key.Contains('\r') || field.Key.Contains('\n') || field.Value.Contains('\r') || field.Value.Contains('\n')) throw new ArgumentException("Headers cannot contain line breaks.");
                var added = false;
                try
                {
                    request.Headers.Remove(field.Key);
                    added = request.Headers.TryAddWithoutValidation(field.Key, field.Value);
                }
                catch (InvalidOperationException) { /* Known content header: add to the body below. */ }
                if (added) continue;
                request.Content ??= new ByteArrayContent([]);
                request.Content.Headers.Remove(field.Key);
                if (!request.Content.Headers.TryAddWithoutValidation(field.Key, field.Value)) throw new ArgumentException($"Invalid header: {field.Key}");
            }
            var auth = definition.Auth;
            switch (auth.Type)
            {
                case "basic":
                    if (auth.Username.Contains(':')) throw new ArgumentException("Basic Auth usernames cannot contain a colon.");
                    request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(auth.Username + ":" + auth.Password))); break;
                case "bearer": case "oauth2":
                    if (string.IsNullOrWhiteSpace(auth.Token)) throw new ArgumentException("Enter an authorization token.");
                    request.Headers.Authorization = new("Bearer", auth.Token.Trim()); break;
                case "apiKey" when !auth.ApiKeyInQuery:
                    if (string.IsNullOrWhiteSpace(auth.ApiKeyName)) throw new ArgumentException("Enter an API key name.");
                    if (auth.ApiKeyName.Contains('\r') || auth.ApiKeyName.Contains('\n') || auth.ApiKeyValue.Contains('\r') || auth.ApiKeyValue.Contains('\n'))
                        throw new ArgumentException("API key headers cannot contain line breaks.");
                    request.Headers.Remove(auth.ApiKeyName);
                    if (!request.Headers.TryAddWithoutValidation(auth.ApiKeyName, auth.ApiKeyValue)) throw new ArgumentException("Invalid API key header.");
                    break;
                case "apiKey": case "inherit": case "none": break;
                default: throw new ArgumentException("Unsupported authorization type.");
            }
            return request;
        }
        catch (FormatException exception) { request.Dispose(); throw new ArgumentException("A request header or authorization value is invalid.", exception); }
        catch { request.Dispose(); throw; }
    }
    public static string GraphJson(BodyDefinition body)
    {
        try
        {
            using var variables = JsonDocument.Parse(string.IsNullOrWhiteSpace(body.Variables) ? "{}" : body.Variables);
            if (variables.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("GraphQL variables must be a JSON object.");
            return JsonSerializer.Serialize(new { query = body.Query, variables = variables.RootElement });
        }
        catch (JsonException exception) { throw new ArgumentException("GraphQL variables must contain valid JSON.", exception); }
    }
    private static HttpContent? CreateBody(BodyDefinition body)
    {
        switch (body.Mode)
        {
            case "none": return null;
            case "raw": return body.Raw.Length == 0 ? null : new StringContent(body.Raw, Encoding.UTF8, body.ContentType);
            case "form":
                var multipart = new MultipartFormDataContent();
                try { foreach (var field in body.FormData.Where(field => field.Enabled && field.Key.Length > 0)) multipart.Add(new StringContent(field.Value), field.Key); return multipart; }
                catch { multipart.Dispose(); throw; }
            case "urlencoded": return new FormUrlEncodedContent(body.Urlencoded.Where(field => field.Enabled && field.Key.Length > 0).Select(field => new KeyValuePair<string, string>(field.Key, field.Value)));
            case "binary":
                if (string.IsNullOrWhiteSpace(body.BinaryPath)) throw new ArgumentException("Select a file for the binary request body.");
                var binary = new StreamContent(File.OpenRead(body.BinaryPath)); binary.Headers.ContentType = new("application/octet-stream"); return binary;
            case "graphql": return new StringContent(GraphJson(body), Encoding.UTF8, "application/json");
            default: throw new ArgumentException("Unsupported request body mode.");
        }
    }
}
