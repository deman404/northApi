using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using northApi.models;
using northApi.services;

static class FeatureChecks
{
    public static async Task<int> Run(HttpRequestEngine engine, string serverUrl)
    {
        var count = 0;
        void Check(bool condition,string label) { if (!condition) throw new Exception("FAILED: " + label); Console.WriteLine("PASS: " + label); count++; }
        var root = Path.Combine(Path.GetTempPath(),"NorthApiFeatureChecks",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root,"workspace.json");
        var store = new WorkspaceStore(path);
        var environment = new EnvironmentProfile { Name = "Local", Variables = [new() { Key = "baseUrl", Value = serverUrl }, new() { Key = "token", Value = "local-token" }, new() { Key = "message", Value = "hello" }, new() { Key = "nested", Value = "{{baseUrl}}/echo" }] };
        store.SaveEnvironment(environment);
        var definition = new RequestDefinition
        {
            Name = "Create item", Method = "POST", Url = "{{baseUrl}}/echo", EnvironmentId = environment.Id,
            Body = new() { Mode = "raw", ContentType = "application/json", Raw = "{\"message\":\"{{message}}\"}" },
            Auth = new() { Type = "bearer", Token = "{{token}}" },
            Headers = [new() { Key = "X-Custom", Value = "{{message}}" }, new() { Key = "Content-Type", Value = "application/json" }],
            Parameters = [new() { Key = "q", Value = "hello & world" },new() { Key = "disabled", Value = "{{notDefined}}", Enabled = false }]
        };
        store.SaveRequest(definition,"Examples");
        var reloaded = new WorkspaceStore(path).Snapshot();
        var saved = reloaded.Collections.Single().Requests.Single();
        Check(saved.Url == "{{baseUrl}}/echo" && saved.Body.Raw.Contains("{{message}}") && saved.Auth.Token == "{{token}}", "collection restart preserves templates, JSON and auth");
        Check(saved.CollectionId == reloaded.Collections[0].Id && saved.EnvironmentId == environment.Id,"collection and environment identities persist");
        Check(new VariableResolver(environment).Resolve("{{nested}}") == serverUrl + "/echo","nested environment variables");
        using (var request = RequestBuilder.Build(saved,reloaded.Environments.Single()))
        {
            var result = await engine.SendAsync(request,TimeSpan.FromSeconds(5));
            using var json = JsonDocument.Parse(result.Body);
            var fields = json.RootElement.GetProperty("headers");
            Check(fields.GetProperty("Authorization").GetString() == "Bearer local-token" && fields.GetProperty("X-Custom").GetString() == "hello","saved headers and Bearer auth reach HTTP server");
            Check(fields.GetProperty("Content-Type").GetString() == "application/json","custom content header overrides generated header");
            Check(json.RootElement.GetProperty("body").GetString() == "{\"message\":\"hello\"}","environment interpolation in raw JSON reaches server");
            Check(json.RootElement.GetProperty("path").GetString()!.Contains("q=hello%20%26%20world") && !json.RootElement.GetProperty("path").GetString()!.Contains("disabled"),"parameters encoded and disabled parameters excluded");
        }
        foreach (var method in new[] {"POST","PUT","PATCH"})
        {
            var copy = WorkspaceStore.Copy(saved); copy.Method = method;
            using var request = RequestBuilder.Build(copy,environment);
            using var json = JsonDocument.Parse((await engine.SendAsync(request,TimeSpan.FromSeconds(5))).Body);
            Check(json.RootElement.GetProperty("method").GetString() == method && json.RootElement.GetProperty("body").GetString()!.Contains("hello"),method + " raw JSON through shared request builder");
        }
        var basic = WorkspaceStore.Copy(saved); basic.Auth = new() {Type="basic",Username="alice",Password="s:ecret"};
        using (var request = RequestBuilder.Build(basic,environment))
        {
            using var json = JsonDocument.Parse((await engine.SendAsync(request,TimeSpan.FromSeconds(5))).Body);
            Check(json.RootElement.GetProperty("headers").GetProperty("Authorization").GetString()=="Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:s:ecret")),"Basic authorization reaches server");
        }
        var api = WorkspaceStore.Copy(saved); api.Auth = new() {Type="apiKey",ApiKeyName="key",ApiKeyValue="{{token}}",ApiKeyInQuery=true};
        using(var request=RequestBuilder.Build(api,environment)) Check(request.RequestUri!.Query.Contains("key=local-token"),"API key query variables");
        var headDefinition = WorkspaceStore.Copy(saved); headDefinition.Method = "HEAD"; headDefinition.Body.Mode = "none";
        Check(CodeGenerator.Generate(headDefinition,environment).Curl.Contains("--head"),"cURL HEAD handles header-only responses");
        var emptyDefinition = WorkspaceStore.Copy(saved); emptyDefinition.Body.Mode = "none";
        Check(CodeGenerator.Generate(emptyDefinition,environment).CSharp.Contains("request.Content ??= new ByteArrayContent"),"generated content headers initialize an empty body when needed");
        var snippets = CodeGenerator.Generate(saved,environment);
        Check(snippets.CSharp.Contains("HttpClient") && !snippets.CSharp.Contains("{{") && snippets.CSharp.Contains("local-token") && snippets.CSharp.Contains("application/json"),"C# generator resolves the saved request");
        Check(snippets.Curl.StartsWith("curl ") && snippets.Curl.Contains("--data-raw") && snippets.Curl.Contains("Bearer local-token") && !snippets.Curl.Contains("{{"),"cURL generator includes resolved body and auth");
        Check(saved.Url.Contains("{{baseUrl}}") && saved.Auth.Token=="{{token}}","send and code generation never mutate saved templates");
        var history = new RequestHistory {Request = saved}; store.RecordHistory(history); history.StatusCode=201; history.ElapsedMilliseconds=25; store.RecordHistory(history);
        var previous = new WorkspaceStore(path).Snapshot().History.Single();
        Check(previous.StatusCode==201 && previous.Request.Body.Raw == saved.Body.Raw && previous.Request.Auth.Token == "{{token}}","history round trip and completion updates");
        var edit = WorkspaceStore.Copy(saved); edit.Name="Updated"; store.SaveRequest(edit,"Examples");
        Check(store.Snapshot().Collections.Single().Requests.Count==1 && store.Snapshot().Collections.Single().Requests.Single().Name=="Updated","save updates existing request without duplication");
        store.SaveRequest(edit,"Moved");
        Check(store.Snapshot().Collections.Sum(collection => collection.Requests.Count)==1 && store.Snapshot().Collections.Single(collection => collection.Name=="Moved").Requests.Count==1,"move between collections");
        var imported = new WorkspaceStore(Path.Combine(root,"imported.json")); imported.ImportCollections(store.ExportCollections());
        Check(imported.Snapshot().Collections.SelectMany(collection => collection.Requests).Single().Body.Raw==saved.Body.Raw,"collection export and import preserve requests");
        var snapshot=store.Snapshot();snapshot.Collections.Clear();
        Check(store.Snapshot().Collections.Count>0,"workspace snapshots are isolated");
        try {new VariableResolver(null).Resolve("{{missing}}");throw new Exception("Missing variable accepted");}catch(ArgumentException){Check(true,"undefined variables produce a useful error");}
        try {new VariableResolver(new() {Variables=[new() {Key="a",Value="{{b}}"},new() {Key="b",Value="{{a}}"}]}).Resolve("{{a}}");throw new Exception("Cycle accepted");}catch(ArgumentException){Check(true,"variable cycles rejected");}
        var broken=Path.Combine(root,"broken.json");File.WriteAllText(broken,"not json");var recovered=new WorkspaceStore(broken);
        Check(recovered.LoadWarning is not null && Directory.GetFiles(root,"broken.json.backup-*").Length==1,"corrupted storage is preserved before recovery");
        var badHeader=WorkspaceStore.Copy(saved);badHeader.Headers=[new(){Key="X-Test",Value="hello\r\nInjected: yes"}];
        try{using var request=RequestBuilder.Build(badHeader,environment);throw new Exception("Invalid header accepted");}catch(ArgumentException){Check(true,"invalid multiline headers rejected");}
        // Produce a compilation fixture containing snippets for every supported body mode.
        var cases=new List<string>();
        foreach(var mode in new[]{"none","raw","form","urlencoded","graphql","binary"})
        {
            var item=WorkspaceStore.Copy(saved);item.Headers=mode=="none" ? [new(){Key="Content-Type",Value="application/json"}] : [];item.Body.Mode=mode;
            item.Body.FormData=[new(){Key="field",Value="a'b\"c"}];item.Body.Urlencoded=item.Body.FormData;
            item.Body.Query="query { item { id } }";item.Body.Variables="{}";
            item.Body.BinaryPath=Path.Combine(root,"payload.bin");File.WriteAllBytes(item.Body.BinaryPath,[0,1,2]);
            var generated=CodeGenerator.Generate(item,environment).CSharp;
            cases.Add("static async System.Threading.Tasks.Task Case"+cases.Count+"()\n{\n"+generated[generated.IndexOf("using var client",StringComparison.Ordinal)..]+"\n}");
        }
        var compileRoot=Path.Combine(root,"snippets");Directory.CreateDirectory(compileRoot);
        File.WriteAllText(Path.Combine(compileRoot,"Snippets.csproj"),"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(compileRoot,"Generated.cs"),"using System;\nusing System.IO;\nusing System.Collections.Generic;\nusing System.Net.Http;\nusing System.Text;\nusing System.Threading;\nclass Generated {\n"+string.Join("\n",cases)+"\n}");
        Console.WriteLine("Generated snippet compilation fixture: " + Path.Combine(compileRoot,"Snippets.csproj"));
        return count;
    }
}
