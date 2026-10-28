namespace northApi.models;

public class FieldValue
{
    public bool Enabled { get; set; } = true;
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public class BodyDefinition
{
    public string Mode { get; set; } = "raw";
    public string Raw { get; set; } = "";
    public string ContentType { get; set; } = "application/json";
    public List<FieldValue> FormData { get; set; } = [];
    public List<FieldValue> Urlencoded { get; set; } = [];
    public string? BinaryPath { get; set; }
    public string Query { get; set; } = "";
    public string Variables { get; set; } = "{}";
}

public class AuthDefinition
{
    public string Type { get; set; } = "inherit";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Token { get; set; } = "";
    public string ApiKeyName { get; set; } = "";
    public string ApiKeyValue { get; set; } = "";
    public bool ApiKeyInQuery { get; set; }
}

public class RequestDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? CollectionId { get; set; }
    public string? EnvironmentId { get; set; }
    public string Name { get; set; } = "Untitled Request";
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 30;
    public List<FieldValue> Parameters { get; set; } = [];
    public List<FieldValue> Headers { get; set; } = [];
    public BodyDefinition Body { get; set; } = new();
    public AuthDefinition Auth { get; set; } = new();
}

public class RequestCollection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<RequestDefinition> Requests { get; set; } = [];
}

public class RequestHistory
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
    public RequestDefinition Request { get; set; } = new();
    public int? StatusCode { get; set; }
    public double? ElapsedMilliseconds { get; set; }
    public string? Error { get; set; }
}

public class EnvironmentProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<FieldValue> Variables { get; set; } = [];
    public override string ToString() => Name;
}

public class WorkspaceData
{
    public int Version { get; set; } = 1;
    public List<RequestCollection> Collections { get; set; } = [];
    public List<RequestHistory> History { get; set; } = [];
    public List<EnvironmentProfile> Environments { get; set; } = [];
}
