using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using northApi.models;
using northApi.services;
using Xunit;

namespace ReleaseQA;

public class SecurityStorageTests
{
    internal static string NewRoot(){var path=Path.Combine(Path.GetTempPath(),"NorthAPI-release-QA",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
    [Fact]
    public void DPAPI_protects_collections_history_environment_and_preserves_explicit_exports()
    {
        var root=NewRoot();var path=Path.Combine(root,"workspace.json");var secret=Guid.NewGuid().ToString("N");
        var store=new WorkspaceStore(path);var source=HttpTests.Definition("POST","http://localhost");
        source.Auth=new(){Type="basic",Username="qa-user",Password=secret};source.Headers.Add(new(){Key="X-Api-Key",Value=secret});
        store.SaveRequest(source,"Protected");store.RecordHistory(new(){Request=source});store.SaveEnvironment(new(){Name="QA",Variables=[new(){Key="token",Value=secret}]});
        Assert.True(!File.ReadAllText(path).Contains(secret),"A canary is present in local storage.");
        foreach(var file in Directory.GetFiles(root))Assert.True(!File.ReadAllText(file).Contains(secret),"A canary is present in a storage artifact.");
        var json=JsonDocument.Parse(File.ReadAllBytes(path));var payload=Convert.FromBase64String(json.RootElement.GetProperty("Payload").GetString()!);
        var clear=ProtectedData.Unprotect(payload,null,DataProtectionScope.CurrentUser);
        Assert.True(Encoding.UTF8.GetString(clear).Contains(secret),"DPAPI decryption did not restore protected content.");CryptographicOperations.ZeroMemory(clear);
        var reopened=new WorkspaceStore(path).Snapshot();
        Assert.True(reopened.Collections[0].Requests[0].Auth.Password==secret && reopened.History[0].Request.Auth.Password==secret && reopened.Environments[0].Variables[0].Value==secret,"Protected round trip failed.");
        Assert.True(store.ExportCollections().Contains(secret),"Explicit export was silently changed.");
        var generated=CodeGenerator.Generate(source);Assert.True(generated.CSharp.Contains("Authorization") && generated.Curl.Contains("Authorization"));
    }
    [Fact]
    public void Legacy_plaintext_and_old_backups_migrate_without_leaving_plaintext()
    {
        var root=NewRoot();var path=Path.Combine(root,"workspace.json");var secret=Guid.NewGuid().ToString("N");
        var source=HttpTests.Definition("GET","http://localhost");source.Auth=new(){Type="bearer",Token=secret};
        var json=JsonSerializer.Serialize(new WorkspaceData{Collections=[new(){Name="Legacy",Requests=[source]}]});File.WriteAllText(path,json);File.WriteAllText(path+".backup-legacy",json);
        var store=new WorkspaceStore(path);Assert.True(store.LoadWarning is null);Assert.True(store.Snapshot().Collections[0].Requests[0].Auth.Token==secret);
        foreach(var file in Directory.GetFiles(root))Assert.True(!File.ReadAllText(file).Contains(secret),"Legacy canary remains on disk.");
        Assert.True(new WorkspaceStore(path).Snapshot().Collections.Count==1);
    }
    [Fact]
    public void Corrupt_file_is_preserved_protected_and_readonly_not_overwritten()
    {
        var root=NewRoot();var path=Path.Combine(root,"workspace.json");var secret=Guid.NewGuid().ToString("N");File.WriteAllText(path,"{ broken: "+secret);
        var store=new WorkspaceStore(path);Assert.True(store.LoadWarning is not null);Assert.True(Directory.GetFiles(root,"*.backup-*").Length==1);
        foreach(var file in Directory.GetFiles(root))Assert.True(!File.ReadAllText(file).Contains(secret),"Corrupt recovery leaked a canary.");
        Assert.Throws<IOException>(()=>store.AddCollection("Do not overwrite"));
    }
    [Fact]
    public void Missing_store_initializes_and_readonly_write_fails_without_data_loss()
    {
        var path=Path.Combine(NewRoot(),"nested","workspace.json");var store=new WorkspaceStore(path);Assert.Empty(store.Snapshot().Collections);
        store.AddCollection("Original");File.SetAttributes(path,FileAttributes.ReadOnly);
        try{Assert.ThrowsAny<IOException>(()=>{try{store.AddCollection("Rejected");}catch(UnauthorizedAccessException ex){throw new IOException("Denied",ex);}});Assert.Single(store.Snapshot().Collections);}
        finally{File.SetAttributes(path,FileAttributes.Normal);}
        Assert.Single(new WorkspaceStore(path).Snapshot().Collections);
    }
    [Fact]
    public void Diagnostics_never_echo_request_values_or_exception_text()
    {
        var secret=Guid.NewGuid().ToString("N");
        Exception[] exceptions=[new ArgumentException(secret),new HttpRequestException(secret),new IOException(secret),new TimeoutException(secret),new UnauthorizedAccessException(secret)];
        foreach(var exception in exceptions)Assert.True(!SafeDiagnostics.RequestError(exception).Contains(secret),"Diagnostic canary leaked.");
        var path=Path.Combine(NewRoot(),"workspace.json");File.WriteAllText(path,"{\""+secret+"\":");var store=new WorkspaceStore(path);
        Assert.True(!store.LoadWarning!.Contains(secret));
    }
    [Theory][InlineData("basic")][InlineData("bearer")]
    public async Task Auth_reaches_server_but_is_not_logged(string type)
    {
        await using var server=new QaServer();var secret=Guid.NewGuid().ToString("N");var source=HttpTests.Definition("GET",server.Url);
        source.Auth=new(){Type=type,Username="qa",Password=secret,Token=secret};using var request=RequestBuilder.Build(source);
        var response=await new HttpRequestEngine().SendAsync(request,TimeSpan.FromSeconds(5));var wire=JsonSerializer.Deserialize<WireRequest>(response.Body)!;
        var expected=type=="basic"?"Basic "+Convert.ToBase64String(Encoding.UTF8.GetBytes("qa:"+secret)):"Bearer "+secret;
        Assert.True(wire.Headers.TryGetValue("Authorization",out var actual)&&actual==expected,"Authorization differed; values intentionally omitted.");
    }
    [Fact]
    public void Environment_templates_resolve_and_reject_cycles_and_missing_keys()
    {
        var environment=new EnvironmentProfile{Variables=[new(){Key="baseUrl",Value="https://example.invalid"},new(){Key="path",Value="{{baseUrl}}/items"}]};
        Assert.True(new VariableResolver(environment).Resolve("{{path}}")=="https://example.invalid/items");
        Assert.Throws<ArgumentException>(()=>new VariableResolver(null).Resolve("{{missing}}"));
        environment.Variables=[new(){Key="cycle",Value="{{cycle}}"}];Assert.Throws<ArgumentException>(()=>new VariableResolver(environment).Resolve("{{cycle}}"));
    }
    [Fact]
    public void API_key_header_linebreaks_and_unsupported_auth_are_rejected()
    {
        var source=HttpTests.Definition("GET","http://localhost");source.Auth=new(){Type="apiKey",ApiKeyName="X-Key",ApiKeyValue="synthetic\r\nInjected: value"};
        Assert.Throws<ArgumentException>(()=>RequestBuilder.Build(source));
        source.Auth=new(){Type="unsupported"};Assert.Throws<ArgumentException>(()=>RequestBuilder.Build(source));
        var store=new WorkspaceStore(Path.Combine(NewRoot(),"workspace.json"));Assert.Throws<JsonException>(()=>store.SaveRequest(source,"Invalid"));Assert.Empty(store.Snapshot().Collections);
    }    [Fact]
    public void Persistence_and_code_generation_emit_no_secret_to_console_or_trace()
    {
        var secret=Guid.NewGuid().ToString("N");using var capture=new StringWriter();var previous=Console.Out;
        using var listener=new System.Diagnostics.TextWriterTraceListener(capture);
        Console.SetOut(capture);System.Diagnostics.Trace.Listeners.Add(listener);
        try
        {
            var store=new WorkspaceStore(Path.Combine(NewRoot(),"workspace.json"));var source=HttpTests.Definition("POST","http://localhost");source.Auth=new(){Type="bearer",Token=secret};
            store.SaveRequest(source,"QA");store.RecordHistory(new(){Request=source});store.SaveEnvironment(new(){Name="QA",Variables=[new(){Key="token",Value=secret}]});
            _=CodeGenerator.Generate(source);_=store.ExportCollections();_=SafeDiagnostics.RequestError(new HttpRequestException(secret));
            System.Diagnostics.Trace.Flush();Assert.True(!capture.ToString().Contains(secret),"A diagnostic canary leaked.");Assert.True(capture.ToString().Length==0,"Unexpected diagnostic output.");
        }
        finally{System.Diagnostics.Trace.Listeners.Remove(listener);Console.SetOut(previous);}
    }
    [Fact]
    public void Tampered_DPAPI_ciphertext_is_rejected_without_discarding_original()
    {
        var path=Path.Combine(NewRoot(),"workspace.json");var store=new WorkspaceStore(path);store.AddCollection("QA");
        using var original=JsonDocument.Parse(File.ReadAllBytes(path));var payload=Convert.FromBase64String(original.RootElement.GetProperty("Payload").GetString()!);payload[^1]^=1;
        File.WriteAllText(path,JsonSerializer.Serialize(new{Protection=original.RootElement.GetProperty("Protection").GetString(),Payload=Convert.ToBase64String(payload)}));
        var broken=new WorkspaceStore(path);Assert.True(broken.LoadWarning is not null);Assert.Throws<IOException>(()=>broken.AddCollection("No overwrite"));Assert.True(Directory.GetFiles(Path.GetDirectoryName(path)!,"*.backup-*").Length==1);
    }}
