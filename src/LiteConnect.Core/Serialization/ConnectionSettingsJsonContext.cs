using System.Text.Json;
using System.Text.Json.Serialization;
using LiteConnect.Core.Models;

namespace LiteConnect.Core.Serialization;

/// <summary>
/// Source-generated JSON serialization context for connection settings types.
/// Configured with <see cref="JsonIgnoreCondition.WhenWritingNull"/> to keep
/// stored JSON compact and compatible with future nullable-column additions.
/// </summary>
[JsonSerializable(typeof(SshConnectionSettings))]
[JsonSerializable(typeof(RdpConnectionSettings))]
[JsonSerializable(typeof(VncConnectionSettings))]
[JsonSerializable(typeof(ProxySettings))]
[JsonSerializable(typeof(PortForwardRule))]
[JsonSerializable(typeof(List<PortForwardRule>))]
[JsonSerializable(typeof(SecurityMetadata))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class ConnectionSettingsJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Static helper that serializes / deserializes connection settings objects using
/// <see cref="ConnectionSettingsJsonContext"/>. All methods are null-safe.
/// </summary>
public static class ConnectionSettingsSerializer
{
    private static readonly ConnectionSettingsJsonContext s_context = new(
        new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });

    /// <summary>
    /// Serializes <paramref name="obj"/> to a JSON string.
    /// Returns <see langword="null"/> when <paramref name="obj"/> is <see langword="null"/>.
    /// </summary>
    public static string? Serialize<T>(T? obj)
    {
        if (obj is null) return null;

        return typeof(T) switch
        {
            Type t when t == typeof(SshConnectionSettings)
                => JsonSerializer.Serialize((SshConnectionSettings)(object)obj, s_context.SshConnectionSettings),
            Type t when t == typeof(RdpConnectionSettings)
                => JsonSerializer.Serialize((RdpConnectionSettings)(object)obj, s_context.RdpConnectionSettings),
            Type t when t == typeof(VncConnectionSettings)
                => JsonSerializer.Serialize((VncConnectionSettings)(object)obj, s_context.VncConnectionSettings),
            Type t when t == typeof(ProxySettings)
                => JsonSerializer.Serialize((ProxySettings)(object)obj, s_context.ProxySettings),
            Type t when t == typeof(SecurityMetadata)
                => JsonSerializer.Serialize((SecurityMetadata)(object)obj, s_context.SecurityMetadata),
            _ => JsonSerializer.Serialize(obj, s_context.Options),
        };
    }

    /// <summary>
    /// Deserializes a JSON string to <typeparamref name="T"/>.
    /// Returns <see langword="null"/> when <paramref name="json"/> is <see langword="null"/> or empty.
    /// </summary>
    public static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrEmpty(json)) return default;

        return typeof(T) switch
        {
            Type t when t == typeof(SshConnectionSettings)
                => (T?)(object?)JsonSerializer.Deserialize(json, s_context.SshConnectionSettings),
            Type t when t == typeof(RdpConnectionSettings)
                => (T?)(object?)JsonSerializer.Deserialize(json, s_context.RdpConnectionSettings),
            Type t when t == typeof(VncConnectionSettings)
                => (T?)(object?)JsonSerializer.Deserialize(json, s_context.VncConnectionSettings),
            Type t when t == typeof(ProxySettings)
                => (T?)(object?)JsonSerializer.Deserialize(json, s_context.ProxySettings),
            Type t when t == typeof(SecurityMetadata)
                => (T?)(object?)JsonSerializer.Deserialize(json, s_context.SecurityMetadata),
            _ => JsonSerializer.Deserialize<T>(json, s_context.Options),
        };
    }
}
