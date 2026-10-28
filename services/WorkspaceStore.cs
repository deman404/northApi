using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using northApi.models;

namespace northApi.services;

public sealed class WorkspaceStore
{
    private static readonly Lazy<WorkspaceStore> SharedStore = new(() => new WorkspaceStore(Path.Combine(
        Environment.GetEnvironmentVariable("NORTHAPI_DATA_DIRECTORY") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NorthApi"), "workspace.json")));
    public static WorkspaceStore Shared => SharedStore.Value;
    private readonly string path;
    private readonly object gate = new();
    private WorkspaceData data;
    private bool preserveUnreadableFile;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public event EventHandler? Changed;
    public string? LoadWarning { get; private set; }

    public WorkspaceStore(string path)
    {
        this.path = path;
        data = new();
        if (!File.Exists(path)) return;
        try
        {
            var stored = File.ReadAllBytes(path);
            var legacy = !ProtectedWorkspace.IsProtected(stored);
            var plaintext = legacy ? stored : ProtectedWorkspace.Unprotect(stored);
            try { data = JsonSerializer.Deserialize<WorkspaceData>(plaintext, Options) ?? throw new JsonException(); Validate(data); }
            finally { if (!legacy) CryptographicOperations.ZeroMemory(plaintext); }
            if (legacy) WriteProtected(stored, path);
            // Prior releases may have made plaintext recovery backups. Protect those too.
            foreach (var backup in Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileName(path) + ".backup-*"))
            {
                var original = File.ReadAllBytes(backup);
                if (!ProtectedWorkspace.IsProtected(original)) WriteProtected(original, backup);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or CryptographicException or FormatException)
        {
            LoadWarning = "The local workspace could not be loaded. Its data has been preserved; saving is disabled until recovery.";
            var backup = path + ".backup-" + Guid.NewGuid().ToString("N");
            preserveUnreadableFile = true;
            try
            {
                var original = File.ReadAllBytes(path);
                var protectedOriginal = ProtectedWorkspace.IsProtected(original) ? original : ProtectedWorkspace.Protect(original);
                File.WriteAllBytes(backup, protectedOriginal);
                if (!ProtectedWorkspace.IsProtected(original)) AtomicWrite(protectedOriginal,path);
            }
            catch (Exception backupException) when (backupException is IOException or UnauthorizedAccessException or CryptographicException) { }
            data = new();
        }
    }

    public static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
    public WorkspaceData Snapshot() { lock (gate) return Copy(data); }

    private void Update(Action<WorkspaceData> change)
    {
        lock (gate)
        {
            if (preserveUnreadableFile) throw new IOException("The original workspace could not be backed up. It has been preserved; local saving is unavailable.");
            var next = Copy(data);
            change(next);
            Validate(next);
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(next,Options);
            try { WriteProtected(plaintext,path); data = next; }
            catch (CryptographicException exception) { throw new IOException("Local storage protection failed.",exception); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void WriteProtected(byte[] plaintext,string destination) => AtomicWrite(ProtectedWorkspace.Protect(plaintext),destination);
    private static void AtomicWrite(byte[] protectedData,string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            { file.Write(protectedData); file.Flush(flushToDisk:true); }
            File.Move(temporary,destination,overwrite:true);
        }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
    }

    public string AddCollection(string name)
    {
        var id = "";
        Update(state =>
        {
            var collection = state.Collections.FirstOrDefault(item => item.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (collection is null) { collection = new() { Name = name.Trim() }; state.Collections.Add(collection); }
            id = collection.Id;
        });
        return id;
    }

    public void SaveRequest(RequestDefinition request, string collectionName)
    {
        Update(state =>
        {
            var collection = state.Collections.FirstOrDefault(item => item.Name.Equals(collectionName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (collection is null) { collection = new() { Name = collectionName.Trim() }; state.Collections.Add(collection); }
            foreach (var group in state.Collections) group.Requests.RemoveAll(item => item.Id == request.Id);
            var saved = Copy(request);
            saved.CollectionId = collection.Id;
            collection.Requests.Add(saved);
        });
    }

    public void SaveEnvironment(EnvironmentProfile environment) => Update(state =>
    {
        state.Environments.RemoveAll(item => item.Id == environment.Id);
        state.Environments.Add(Copy(environment));
    });

    public void RecordHistory(RequestHistory entry) => Update(state =>
    {
        state.History.RemoveAll(item => item.Id == entry.Id);
        state.History.Insert(0, Copy(entry));
        if (state.History.Count > 500) state.History.RemoveRange(500, state.History.Count - 500);
    });

    public void ImportCollections(string json)
    {
        var imported = JsonSerializer.Deserialize<List<RequestCollection>>(json, Options) ?? throw new JsonException("Choose a NorthApi collection export.");
        Validate(new WorkspaceData { Collections = imported });
        Update(state =>
        {
            foreach (var collection in imported)
            {
                var copy = Copy(collection);
                copy.Id = Guid.NewGuid().ToString("N");
                foreach (var request in copy.Requests) { request.Id = Guid.NewGuid().ToString("N"); request.CollectionId = copy.Id; }
                state.Collections.Add(copy);
            }
        });
    }
    public string ExportCollections() => JsonSerializer.Serialize(Snapshot().Collections, Options);

    private static void Validate(WorkspaceData state)
    {
        if (state.Version != 1 || state.Collections is null || state.History is null || state.Environments is null)
            throw new JsonException("Unsupported workspace format.");
        foreach (var collection in state.Collections)
        {
            if (collection is null || string.IsNullOrWhiteSpace(collection.Name) || collection.Requests is null) throw new JsonException("Invalid collection.");
            foreach (var request in collection.Requests) ValidateRequest(request);
        }
        foreach (var entry in state.History) { if (entry is null) throw new JsonException("Invalid history entry."); ValidateRequest(entry.Request); }
        foreach (var environment in state.Environments)
            if (environment is null || string.IsNullOrWhiteSpace(environment.Name) || environment.Variables is null || environment.Variables.Any(field => field is null || field.Key is null || field.Value is null)) throw new JsonException("Invalid environment.");
    }
    private static void ValidateRequest(RequestDefinition request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Id) || request.Name is null || request.Url is null || request.Method is null || request.Body is null || request.Auth is null || request.Parameters is null || request.Headers is null || request.Body.FormData is null || request.Body.Urlencoded is null) throw new JsonException("Invalid saved request.");
        if (request.Parameters.Concat(request.Headers).Concat(request.Body.FormData).Concat(request.Body.Urlencoded).Any(field => field is null || field.Key is null || field.Value is null)) throw new JsonException("Invalid request field.");
        if (request.Body.Raw is null || request.Body.Mode is null || request.Body.ContentType is null || request.Body.Query is null || request.Body.Variables is null || request.Auth.Type is null || request.Auth.Username is null || request.Auth.Password is null || request.Auth.Token is null || request.Auth.ApiKeyName is null || request.Auth.ApiKeyValue is null) throw new JsonException("Invalid body or authorization.");
        if (request.Method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "OPTIONS") || request.Auth.Type is not ("inherit" or "none" or "apiKey" or "basic" or "bearer" or "oauth2") || request.Body.Mode is not ("none" or "raw" or "form" or "urlencoded" or "binary" or "graphql")) throw new JsonException("Unsupported request method, body mode, or authorization type.");
    }
}
