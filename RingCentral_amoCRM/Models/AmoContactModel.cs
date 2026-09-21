using System.Text.Json.Serialization;

// =======================================================
// 1. Корневой объект ответа API (AmoCrmContactsResponse)
// =======================================================
public class AmoCrmContactsResponse
{
    // Поле, содержащее все основные данные
    [JsonPropertyName("_embedded")]
    public EmbeddedData Embedded { get; set; }

    [JsonPropertyName("_page")]
    public int Page { get; set; }
    
    // _links можно пропустить, если не нужен
}

// =======================================================
// 2. Секция _embedded (EmbeddedData)
// =======================================================
public class EmbeddedData
{
    // Список найденных контактов
    [JsonPropertyName("contacts")]
    public List<AmoCrmContact> Contacts { get; set; }
    [JsonPropertyName("leads")]
    public List<AmoCrmContact> Leads { get; set; }
}

// =======================================================
// 3. Объект Контакта (AmoCrmContact)
// =======================================================
public class AmoCrmContact
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("responsible_user_id")]
    public long ResponsibleUserId { get; set; }

    [JsonPropertyName("custom_fields_values")]
    public List<CustomFieldsValue> CustomFieldsValues { get; set; }

    // Вложенная секция _embedded внутри контакта (содержит Leads, Tags)
    [JsonPropertyName("_embedded")]
    public ContactEmbeddedData Embedded { get; set; }
}

// =======================================================
// 4. Секция _embedded внутри Контакта (ContactEmbeddedData)
// =======================================================
public class ContactEmbeddedData
{
    // Список связанных сделок (самое важное для нас поле)
    [JsonPropertyName("leads")]
    public List<AmoCrmLead> Leads { get; set; }
    
    [JsonPropertyName("tags")]
    public List<AmoCrmTag> Tags { get; set; }

    // companies можно пропустить, если не нужен
}

// =======================================================
// 5. Объект Сделки (AmoCrmLead)
// =======================================================
public class AmoCrmLead
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    // _links можно пропустить, если не используется
}

// =======================================================
// 6. Объекты дополнительных полей (для полноты)
// =======================================================
public class CustomFieldsValue
{
    [JsonPropertyName("field_id")]
    public int FieldId { get; set; }
    
    [JsonPropertyName("field_code")]
    public string FieldCode { get; set; }

    [JsonPropertyName("values")]
    public List<CustomFieldValueItem> Values { get; set; }
}

public class CustomFieldValueItem
{
    [JsonPropertyName("value")]
    public string Value { get; set; }

    [JsonPropertyName("enum_id")]
    public int EnumId { get; set; }
}

public class AmoCrmTag
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }
}