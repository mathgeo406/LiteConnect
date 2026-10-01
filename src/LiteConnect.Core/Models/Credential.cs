namespace LiteConnect.Core.Models;

/// <summary>Decrypted credential — held in memory only, never persisted plaintext.</summary>
public record Credential(
    string? Username,
    string? Password,
    string? Domain,
    string? PrivateKeyPath,
    string? KeyPassphrase
);
