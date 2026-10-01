using LiteConnect.Core.Models;

namespace LiteConnect.Core.Abstractions;

/// <summary>
/// Manages encrypted credential storage.
/// Credentials never leave this service in plaintext form beyond in-memory use.
/// </summary>
public interface ICredentialStore : IDisposable
{
    /// <summary>Current encryption mode (DPAPI or master-password).</summary>
    EncryptionMode Mode { get; }

    /// <summary>True when the store has been unlocked and is ready for reads/writes.</summary>
    bool IsUnlocked { get; }

    /// <summary>
    /// Initialize the store.
    /// DPAPI mode: returns <c>true</c> — no user interaction needed.
    /// Master-password mode: returns <c>false</c> — call <see cref="UnlockWithPasswordAsync"/> next.
    /// </summary>
    Task<bool> InitializeAsync(CancellationToken ct = default);

    /// <summary>Unlock with the master password (master-password mode only).</summary>
    Task<bool> UnlockWithPasswordAsync(string masterPassword, CancellationToken ct = default);

    /// <summary>Lock the store — clears in-memory keys without closing the app.</summary>
    void Lock();

    /// <summary>Store (or overwrite) credentials for a connection.</summary>
    Task SaveCredentialAsync(Guid connectionId, Credential credential, CancellationToken ct = default);

    /// <summary>Retrieve and decrypt credentials for a connection; returns null if not found.</summary>
    Task<Credential?> GetCredentialAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>Delete credentials for a connection.</summary>
    Task DeleteCredentialAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>Switch encryption mode. Re-encrypts the database with the new key material.</summary>
    Task SwitchModeAsync(EncryptionMode newMode, string? newPassword = null, CancellationToken ct = default);

    /// <summary>Change the master password (master-password mode only).</summary>
    Task ChangeMasterPasswordAsync(string oldPassword, string newPassword, CancellationToken ct = default);
}
