using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RingCentral_amoCRM.Models;

// Принимает значение поля и как JSON-строку, и как JSON-число (RC непоследователен
// в типах id-подобных полей между разными payload; для основного instant-события
// (RingCentralNotification/MessageBody) это ни разу не было проблемой на проде, но
// новый non-instant message-store payload (MessageStoreChangeNotification и ниже)
// нигде не проверялся на реальных данных до вылезшего в проде
// "Cannot get the value of a token type 'Number' as a string" — используем этот
// конвертер на всех строковых полях новой модели, чтобы неверно угаданный тип
// одного поля не ронял разбор всего уведомления.
public class FlexibleStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l) ? l.ToString() : reader.GetDouble().ToString(),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => null
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}

// Обратный случай той же непоследовательности RC: числовое по смыслу поле
// (newCount/updatedCount) приходит строкой. System.Text.Json по умолчанию
// не приводит "3" к int? так же, как не приводит 3 к string — тот же класс
// падения, что чинит FlexibleStringConverter, только в другую сторону.
// Не подтверждено на реальном payload (в отличие от FlexibleStringConverter,
// у которого падение уже было в проде), добавлено превентивно по итогам
// ревью всех числовых/id-полей моделей вебхука.
public class FlexibleIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => reader.TryGetInt32(out var i) ? i : (int?)null,
            JsonTokenType.String => int.TryParse(reader.GetString(), out var parsed) ? parsed : (int?)null,
            JsonTokenType.Null => null,
            _ => null
        };
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
}
// ----------------------------------------------------------------------
// 1. Вспомогательные классы для данных "from" и "to"
// ----------------------------------------------------------------------

public class PhoneNumberInfo
{
    [JsonPropertyName("countryCode")]
    public string CountryCode { get; set; }

    [JsonPropertyName("nationalDestinationCode")]
    public string NationalDestinationCode { get; set; }

    [JsonPropertyName("subscriberNumber")]
    public string SubscriberNumber { get; set; }
}

public class PartyInfo
{
    [JsonPropertyName("phoneNumber")]
    public string PhoneNumber { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("location")]
    public string Location { get; set; }

    [JsonPropertyName("target")]
    public bool? Target { get; set; } // Может быть null или отсутствовать

    [JsonPropertyName("phoneNumberInfo")]
    public PhoneNumberInfo PhoneNumberInfo { get; set; }
}

public class Attachment
{
    // Прод: 500 "Cannot get the value of a token type 'Number' as a string"
    // на $.body.attachments[1].size — RC шлёт size числом, не строкой, хотя
    // это старая (instant SMS/MMS) модель, месяцами работавшая без проблем
    // до вложений с числовым size. id — тот же класс риска (см. комментарий
    // у FlexibleStringConverter про непоследовательность RC в id-подобных
    // полях) — конвертер на оба на всякий случай, а не только на упавшее.
    [JsonPropertyName("id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("contentType")]
    public string ContentType { get; set; }

    [JsonPropertyName("uri")]
    public string Uri { get; set; }

    [JsonPropertyName("size")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Size { get; set; }
}

public class Conversation
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Id { get; set; }
}

public class OwnerInfo
{
    [JsonPropertyName("extensionId")]
    public string ExtensionId { get; set; }

    [JsonPropertyName("extensionType")]
    public string ExtensionType { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; }
}


// ----------------------------------------------------------------------
// 2. Класс для поля "body" (Содержание сообщения)
// ----------------------------------------------------------------------

public class MessageBody
{
    // Тот же класс риска, что и у Attachment.Id/Size выше — RC id-подобные
    // поля присылает непоследовательно (строкой или числом) в разных payload.
    [JsonPropertyName("id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Id { get; set; }

    [JsonPropertyName("to")]
    public List<PartyInfo> To { get; set; }

    [JsonPropertyName("from")]
    public PartyInfo From { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("creationTime")]
    public DateTime CreationTime { get; set; }

    [JsonPropertyName("lastModifiedTime")]
    public DateTime LastModifiedTime { get; set; }

    [JsonPropertyName("readStatus")]
    public string ReadStatus { get; set; }

    [JsonPropertyName("priority")]
    public string Priority { get; set; }

    [JsonPropertyName("attachments")]
    public List<Attachment> Attachments { get; set; }

    [JsonPropertyName("direction")]
    public string Direction { get; set; }

    [JsonPropertyName("availability")]
    public string Availability { get; set; }

    // !!! ВАЖНО: Текст SMS находится здесь
    [JsonPropertyName("subject")] 
    public string Subject { get; set; }

    [JsonPropertyName("messageStatus")]
    public string MessageStatus { get; set; }

    [JsonPropertyName("conversation")]
    public Conversation Conversation { get; set; }

    [JsonPropertyName("eventType")]
    public string EventType { get; set; }

    [JsonPropertyName("owner")]
    public OwnerInfo Owner { get; set; }
}


// ----------------------------------------------------------------------
// 3. Основной класс уведомления RingCentral
// ----------------------------------------------------------------------

public class RingCentralNotification
{
    // Та же непоследовательность RC в id-подобных полях, что и у
    // MessageStoreChangeNotification ниже (там уже покрыто конвертером) —
    // этот, старый, instant-SMS путь не был защищён вообще, хотя риск тот же.
    [JsonPropertyName("uuid")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Uuid { get; set; }

    [JsonPropertyName("event")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Event { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("subscriptionId")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string SubscriptionId { get; set; }

    [JsonPropertyName("ownerId")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string OwnerId { get; set; }

    [JsonPropertyName("body")]
    public MessageBody Body { get; set; }
}


// ----------------------------------------------------------------------
// 4. Уведомление от НЕ-instant фильтра message-store
//    (/restapi/v1.0/account/~/extension/{id}/message-store?type=SMS&direction=Outbound).
//    В отличие от instant-события (см. MessageBody выше), это только сводка
//    изменений в хранилище сообщений расширения — id/from/to/text здесь нет,
//    сами сообщения нужно дозапрашивать через MessageStore().List().
//    См. https://developers.ringcentral.com/guide/notifications/event-filters/message
// ----------------------------------------------------------------------

public class MessageStoreChange
{
    [JsonPropertyName("type")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Type { get; set; }

    [JsonPropertyName("newCount")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? NewCount { get; set; }

    [JsonPropertyName("updatedCount")]
    [JsonConverter(typeof(FlexibleIntConverter))]
    public int? UpdatedCount { get; set; }
}

public class MessageStoreChangeBody
{
    [JsonPropertyName("extensionId")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string ExtensionId { get; set; }

    [JsonPropertyName("accountId")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string AccountId { get; set; }

    [JsonPropertyName("lastUpdated")]
    public DateTime? LastUpdated { get; set; }

    [JsonPropertyName("changes")]
    public List<MessageStoreChange> Changes { get; set; }
}

public class MessageStoreChangeNotification
{
    [JsonPropertyName("uuid")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Uuid { get; set; }

    [JsonPropertyName("event")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Event { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("subscriptionId")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string SubscriptionId { get; set; }

    [JsonPropertyName("ownerId")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string OwnerId { get; set; }

    [JsonPropertyName("body")]
    public MessageStoreChangeBody Body { get; set; }
}