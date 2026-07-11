using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Streamyfin.Configuration;

public class Config
{
  public Notifications.Notifications? notifications { get; set; }

  public Settings.Settings? settings { get; set; }
  
  [JsonPropertyName(name: "other")]
  public Other? Other { get; set; }
}
