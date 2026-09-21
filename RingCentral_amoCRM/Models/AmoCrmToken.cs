using System.Text.Json.Serialization;

namespace RingCentral_amoCRM.Models;

public class AmoCrmToken
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    // Optional fields
    [JsonPropertyName("token_type")]
    public string TokenType { get; set; }

    // Computed property
    [JsonIgnore]
    public DateTime ExpiresAtUtc { get; set; }

    public void SetExpiration()
    {
        ExpiresAtUtc = DateTime.UtcNow.AddSeconds(ExpiresIn - 30); // refresh 30s earlier
    }

    public bool IsExpired() => DateTime.UtcNow >= ExpiresAtUtc;
}