using System.IO;
using Newtonsoft.Json;
using NewtonsoftJsonSerializer = Newtonsoft.Json.JsonSerializer;

namespace Jellyfin.Plugin.Streamyfin;

/// <summary>
/// JSON serialization helper for outbound push notification payloads.
/// </summary>
public class SerializationHelper
{
    private readonly NewtonsoftJsonSerializer _jsonSerializer;

    public SerializationHelper()
    {
        _jsonSerializer = NewtonsoftJsonSerializer.CreateDefault();
    }

    /// <summary>
    /// Serialize an object to JSON using Newtonsoft (used for push payloads).
    /// </summary>
    public string ToJson<T>(T item)
    {
        var output = new StringWriter();
        _jsonSerializer.Serialize(output, item);
        var outputAsString = output.ToString();
        output.Dispose();
        return outputAsString;
    }
}
