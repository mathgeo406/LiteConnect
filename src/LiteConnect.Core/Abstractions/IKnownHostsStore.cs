namespace LiteConnect.Core.Abstractions;

/// <summary>
/// Represents a trusted SSH host key entry stored in the <c>KnownHosts</c> table.
/// </summary>
public sealed record KnownHostEntry(
    string Hostname,
    int Port,
    string KeyType,
    string Fingerprint,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    bool Trusted);

/// <summary>
/// Persists and verifies SSH server host-key fingerprints against the <c>KnownHosts</c> table.
/// </summary>
public interface IKnownHostsStore
{
    /// <summary>
    /// Looks up a previously seen host key for the specified host, port, and key algorithm.
    /// </summary>
    Task<KnownHostEntry?> GetKnownHostAsync(string hostname, int port, string keyType, CancellationToken ct = default);

    /// <summary>
    /// Saves or updates a trusted host key in the <c>KnownHosts</c> table.
    /// </summary>
    Task SaveKnownHostAsync(string hostname, int port, string keyType, string fingerprint, bool trusted = true, CancellationToken ct = default);

    /// <summary>
    /// Removes a host key entry from the <c>KnownHosts</c> table.
    /// </summary>
    Task RemoveKnownHostAsync(string hostname, int port, string keyType, CancellationToken ct = default);
}
