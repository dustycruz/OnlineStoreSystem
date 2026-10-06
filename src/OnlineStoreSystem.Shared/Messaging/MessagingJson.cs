// File: src/OnlineStoreSystem.Shared/Messaging/MessagingJson.cs
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnlineStoreSystem.Shared.Messaging;

/// <summary>JSON settings for message bodies (camelCase, enums as strings).</summary>
public static class MessagingJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
