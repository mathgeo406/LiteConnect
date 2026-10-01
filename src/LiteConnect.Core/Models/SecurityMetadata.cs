namespace LiteConnect.Core.Models;

/// <summary>
/// Persisted key-derivation and portability metadata stored alongside the encrypted
/// SQLCipher database so the application can determine the active <see cref="EncryptionMode"/>
/// and PBKDF2 salt prior to unlocking <c>connections.db</c>.
/// </summary>
public sealed class SecurityMetadata
{
    public EncryptionMode Mode { get; set; } = EncryptionMode.Dpapi;

    /// <summary>Base64-encoded 32-byte random PBKDF2 salt (MasterPassword mode).</summary>
    public string? MasterPasswordSalt { get; set; }

    /// <summary>Base64-encoded SHA-256 verification hash of the derived key (MasterPassword mode).</summary>
    public string? MasterPasswordHash { get; set; }

    /// <summary>
    /// Portable recovery envelope allowing non-credential connection rows to be preserved
    /// when a DPAPI-encrypted portable folder is moved to another Windows user/PC (Sprint 4.2).
    /// </summary>
    public string? DpapiRecoveryEnvelope { get; set; }

    /// <summary>Auto-lock idle timeout in minutes (MasterPassword mode only; default 30).</summary>
    public int AutoLockMinutes { get; set; } = 30;
}
