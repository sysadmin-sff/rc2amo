using System.Text.Json;
using System.Text.Json.Serialization;

namespace RingCentral_amoCRM.Models;

public class AmoCrmNotesListResponse
{
    [JsonPropertyName("_embedded")]
    public NotesEmbedded Embedded { get; set; }
}

public class NotesEmbedded
{
    [JsonPropertyName("notes")]
    public List<AmoCrmNoteDetail> Notes { get; set; }
}

public class AmoCrmNoteDetail
{
    [JsonPropertyName("note_type")]
    public string NoteType { get; set; }

    [JsonPropertyName("params")]
    public JsonElement Params { get; set; }
}
