using System.Text.Json;

namespace Tern.Remote.Auth;

/// <summary>Reads claims from a JWT's payload. Nothing is verified; the token is only inspected.</summary>
public static class Jwt
{
    public static string? Claim(string? token, string name)
    {
        var parts = token?.Split('.');
        if (parts is not { Length: >= 2 })
        {
            return null;
        }
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(name, out var value))
            {
                return null;
            }
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }
}
