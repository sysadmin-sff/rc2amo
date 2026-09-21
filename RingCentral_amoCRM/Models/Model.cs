using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RingCentral_amoCRM.Models;
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
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("contentType")]
    public string ContentType { get; set; }
    
    [JsonPropertyName("uri")]
    public string Uri { get; set; }
    
    [JsonPropertyName("size")]
    public string Size { get; set; }
}

public class Conversation
{
    [JsonPropertyName("id")]
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
    [JsonPropertyName("id")]
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
    [JsonPropertyName("uuid")]
    public string Uuid { get; set; }

    [JsonPropertyName("event")]
    public string Event { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; set; }

    [JsonPropertyName("ownerId")]
    public string OwnerId { get; set; }

    [JsonPropertyName("body")]
    public MessageBody Body { get; set; }
}