using System.Security.Cryptography;
using System.Text.Json;

namespace northApi.services;

internal static class ProtectedWorkspace
{
    private const string Format = "NorthAPI.DPAPI.CurrentUser.v1";
    public static bool IsProtected(byte[] bytes)
    {
        try { using var json = JsonDocument.Parse(bytes); return json.RootElement.TryGetProperty("Protection",out var property) && property.GetString() == Format; }
        catch (JsonException) { return false; }
    }
    public static byte[] Protect(byte[] plaintext)
    {
        var ciphertext = ProtectedData.Protect(plaintext,null,DataProtectionScope.CurrentUser);
        return JsonSerializer.SerializeToUtf8Bytes(new Envelope(Format,Convert.ToBase64String(ciphertext)));
    }
    public static byte[] Unprotect(byte[] bytes)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(bytes) ?? throw new CryptographicException("Invalid protected workspace.");
        if(envelope.Protection != Format) throw new CryptographicException("Unsupported protected workspace.");
        return ProtectedData.Unprotect(Convert.FromBase64String(envelope.Payload),null,DataProtectionScope.CurrentUser);
    }
    private sealed record Envelope(string Protection,string Payload);
}
