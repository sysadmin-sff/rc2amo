using System.Text.Json.Serialization;

namespace RingCentral_amoCRM.Models;

public class AmoCrmLeadsListResponse
{
    [JsonPropertyName("_embedded")]
    public LeadsEmbedded Embedded { get; set; }
}

public class LeadsEmbedded
{
    [JsonPropertyName("leads")]
    public List<AmoCrmLeadDetail> Leads { get; set; }
}

public class AmoCrmLeadDetail
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("status_id")]
    public long StatusId { get; set; }

    [JsonPropertyName("updated_at")]
    public long UpdatedAt { get; set; }
}
