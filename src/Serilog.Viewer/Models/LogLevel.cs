using System.Text.Json.Serialization;

namespace Serilog.Viewer.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LogLevel
{
    Verbose,
    Debug,
    Information,
    Warning,
    Error,
    Fatal,
}
