// File: src/OnlineStoreSystem.Shared/Json/JsonDefaults.cs
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnlineStoreSystem.Shared.Json;

/// <summary>JSON conventions for every HTTP API and typed client in the solution.</summary>
public static class JsonDefaults
{
    /// <summary>Options for <c>HttpClient</c> JSON helpers (ReadFromJsonAsync / PostAsJsonAsync).</summary>
    public static JsonSerializerOptions Web { get; } = Create();

    /// <summary>Applies the conventions to an existing options instance (e.g. MVC's JsonOptions).</summary>
    public static void Apply(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new UtcDateTimeJsonConverter());
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Apply(options);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
