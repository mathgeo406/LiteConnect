using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using LiteConnect.Core.Abstractions;
using LiteConnect.Core.Data;
using LiteConnect.Core.Models;
using LiteConnect.Core.Serialization;
using Microsoft.Data.Sqlite;

namespace LiteConnect.Core.Services;

/// <summary>
/// Implements <see cref="ICredentialStore"/> backed by SQLCipher full-database encryption,
/// supporting Windows DPAPI (default, zero-prompt) and Master Password (PBKDF2-SHA256, 100K iterations).
/// </summary>
public sealed class SqlCipherCredentialStore : ICredentialStore
{
    private const int KeySizeBytes     = 32;
    private const int SaltSizeBytes    = 32;
    private const int Pbkdf2Iterations = 100_000;

    // Portable obfuscation mask used solely for recovering non-credential connection rows
    // when a DPAPI-encrypted portable folder is copied to a different Windows user/PC (Sprint 4.2).
    private static readonly byte[] PortableEnvelopeMask =
        SHA256.HashData("LiteConnect.PortableRecoveryEnvelope.v1"u8);

    private readonly IAppEnvironment _environment;
    private byte[]? _rawKey;
    private SecurityMetadata _metadata = new();
    private bool _disposed;

    public EncryptionMode Mode => _metadata.Mode;

    public bool IsUnlocked => _rawKey is { Length: KeySizeBytes };

    /// <summary>
    /// True when <see cref="InitializeAsync"/> detected that <c>dpapi.key</c> was encrypted by
    /// a different Windows user or machine (<see cref="CryptographicException"/>).
    /// </summary>
    public bool DpapiPortabilityMismatch { get; private set; }

    /// <summary>Auto-lock timeout in minutes (active only in <see cref="EncryptionMode.MasterPassword"/> mode).</summary>
    public int AutoLockMinutes => _metadata.AutoLockMinutes > 0 ? _metadata.AutoLockMinutes : 30;

    /// <summary>Fires when the store is locked so consumers close open SQLCipher connections.</summary>
    public event EventHandler? Locked;

    /// <summary>Fires when the store is unlocked.</summary>
    public event EventHandler? Unlocked;

    /// <summary>Fires immediately before <c>PRAGMA rekey</c> so consumers release open handles.</summary>
    public event EventHandler? BeforeRekey;

    private string SecurityMetadataPath => Path.Combine(_environment.DataDirectory, "security.json");

    public SqlCipherCredentialStore(IAppEnvironment environment)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    /// <summary>
    /// Returns the hex-encoded 32-byte SQLCipher key when unlocked, or <see langword="null"/> when locked.
    /// </summary>
    public string? GetSqlCipherKeyHex()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _rawKey is { Length: KeySizeBytes }
            ? Convert.ToHexString(_rawKey)
            : null;
    }

    // ── 4.1 & 4.2: InitializeAsync ──────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<bool> InitializeAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        DpapiPortabilityMismatch = false;

        Directory.CreateDirectory(_environment.DataDirectory);
        _metadata = LoadSecurityMetadata();

        // Master Password mode: cannot auto-unlock without user input
        if (_metadata.Mode == EncryptionMode.MasterPassword &&
            !string.IsNullOrEmpty(_metadata.MasterPasswordSalt))
        {
            ClearKeyMaterial();
            return false;
        }

        // DPAPI mode
        _metadata.Mode = EncryptionMode.Dpapi;

        if (File.Exists(_environment.DpapiKeyPath))
        {
            try
            {
                byte[] encryptedBlob = await File.ReadAllBytesAsync(_environment.DpapiKeyPath, ct);
                byte[] keyBytes = OperatingSystem.IsWindows()
                    ? ProtectedData.Unprotect(
                        encryptedBlob,
                        optionalEntropy: null,
                        scope: DataProtectionScope.CurrentUser)
                    : encryptedBlob;

                if (keyBytes.Length != KeySizeBytes)
                    throw new CryptographicException("Invalid DPAPI key length.");

                SetKeyMaterial(keyBytes);

                // Ensure recovery envelope is stored for future portability detection
                if (string.IsNullOrEmpty(_metadata.DpapiRecoveryEnvelope))
                {
                    _metadata.DpapiRecoveryEnvelope = CreateRecoveryEnvelope(keyBytes);
                    SaveSecurityMetadata(_metadata);
                }

                await EnsureDatabaseInitializedAndSyncedAsync(ct);
                Unlocked?.Invoke(this, EventArgs.Empty);
                return true;
            }
            catch (CryptographicException)
            {
                // Sprint 4.2: Encrypted by a different Windows user or on a different PC
                ClearKeyMaterial();
                DpapiPortabilityMismatch = true;
                return false;
            }
        }

        // First run in DPAPI mode: generate random 32-byte SQLCipher key
        byte[] newKey = RandomNumberGenerator.GetBytes(KeySizeBytes);
        SaveDpapiKeyBlob(newKey);
        SetKeyMaterial(newKey);

        _metadata.Mode                  = EncryptionMode.Dpapi;
        _metadata.MasterPasswordHash    = null;
        _metadata.MasterPasswordSalt    = null;
        _metadata.DpapiRecoveryEnvelope = CreateRecoveryEnvelope(newKey);
        SaveSecurityMetadata(_metadata);

        await EnsureDatabaseInitializedAndSyncedAsync(ct);
        Unlocked?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // ── 4.2: DPAPI Portability Recovery ─────────────────────────────────────────

    /// <summary>
    /// Option 1 when moving a portable install to a new PC/user:
    /// Clears old passwords, preserves all connections and folders, and re-encrypts with DPAPI for the current user.
    /// </summary>
    public async Task ResetDpapiForCurrentUserAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] newKey = RandomNumberGenerator.GetBytes(KeySizeBytes);
        await RecoverDatabaseWithNewKeyAsync(newKey, clearCredentials: true, ct);

        SaveDpapiKeyBlob(newKey);
        SetKeyMaterial(newKey);

        _metadata.Mode                  = EncryptionMode.Dpapi;
        _metadata.MasterPasswordHash    = null;
        _metadata.MasterPasswordSalt    = null;
        _metadata.DpapiRecoveryEnvelope = CreateRecoveryEnvelope(newKey);
        SaveSecurityMetadata(_metadata);

        DpapiPortabilityMismatch = false;
        await SyncAppSettingsTableAsync(ct);
        Unlocked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Option 2 when moving a portable install to a new PC/user:
    /// Preserves all connections and folders, clears old DPAPI-bound passwords, and switches to Master Password mode.
    /// </summary>
    public async Task RecoverDpapiToMasterPasswordAsync(string newMasterPassword, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(newMasterPassword);

        byte[] salt   = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        byte[] newKey = DeriveKeyFromPassword(newMasterPassword, salt);
        string hash   = ComputeVerificationHash(newKey);

        await RecoverDatabaseWithNewKeyAsync(newKey, clearCredentials: true, ct);

        DeleteDpapiKeyFile();
        SetKeyMaterial(newKey);

        _metadata.Mode                  = EncryptionMode.MasterPassword;
        _metadata.MasterPasswordSalt    = Convert.ToBase64String(salt);
        _metadata.MasterPasswordHash    = hash;
        _metadata.DpapiRecoveryEnvelope = null;
        SaveSecurityMetadata(_metadata);

        DpapiPortabilityMismatch = false;
        await SyncAppSettingsTableAsync(ct);
        Unlocked?.Invoke(this, EventArgs.Empty);
    }

    private async Task RecoverDatabaseWithNewKeyAsync(byte[] newKey, bool clearCredentials, CancellationToken ct)
    {
        BeforeRekey?.Invoke(this, EventArgs.Empty);

        string newKeyHex = Convert.ToHexString(newKey);
        byte[]? recoveredOldKey = TryUnpackRecoveryEnvelope(_metadata.DpapiRecoveryEnvelope);

        if (recoveredOldKey is not null && File.Exists(_environment.DatabasePath))
        {
            string oldKeyHex = Convert.ToHexString(recoveredOldKey);
            try
            {
                await using var conn = await SqlCipherConnectionHelper.OpenEncryptedConnectionAsync(
                    _environment.DatabasePath, oldKeyHex, ct);

                if (await SqlCipherConnectionHelper.VerifyDatabaseReadableAsync(conn, ct))
                {
                    if (clearCredentials)
                    {
                        await ClearAllStoredCredentialsInDbAsync(conn, ct);
                    }
                    await SqlCipherConnectionHelper.RekeyAsync(conn, newKeyHex, ct);
                    return;
                }
            }
            catch
            {
                // Fall through to fresh DB initialization if recovery envelope didn't match
            }
            finally
            {
                CryptographicOperations.ZeroMemory(recoveredOldKey);
            }
        }

        // Fallback if no recovery envelope existed: back up unreadable DB and initialize fresh
        if (File.Exists(_environment.DatabasePath))
        {
            string backupPath = _environment.DatabasePath + $".unreadable_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak";
            File.Move(_environment.DatabasePath, backupPath, overwrite: true);
        }

        var initializer = new DatabaseInitializer(_environment);
        await initializer.InitializeAsync(newKeyHex, ct);
    }

    private static async Task ClearAllStoredCredentialsInDbAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE Connections
            SET Password = NULL,
                KeyPassphrase = NULL,
                UpdatedAt = @now;
            """;
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── 4.3: UnlockWithPasswordAsync ────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<bool> UnlockWithPasswordAsync(string masterPassword, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrEmpty(masterPassword))
            return false;

        _metadata = LoadSecurityMetadata();
        if (_metadata.Mode != EncryptionMode.MasterPassword ||
            string.IsNullOrEmpty(_metadata.MasterPasswordSalt) ||
            string.IsNullOrEmpty(_metadata.MasterPasswordHash))
        {
            return false;
        }

        byte[] salt = Convert.FromBase64String(_metadata.MasterPasswordSalt);
        byte[] candidateKey = DeriveKeyFromPassword(masterPassword, salt);

        if (!VerifyKeyHash(candidateKey, _metadata.MasterPasswordHash))
        {
            CryptographicOperations.ZeroMemory(candidateKey);
            return false;
        }

        string candidateHex = Convert.ToHexString(candidateKey);

        // Verify SQLCipher database can be opened with this key
        if (File.Exists(_environment.DatabasePath))
        {
            await using var conn = await SqlCipherConnectionHelper.OpenEncryptedConnectionAsync(
                _environment.DatabasePath, candidateHex, ct);

            if (!await SqlCipherConnectionHelper.VerifyDatabaseReadableAsync(conn, ct))
            {
                CryptographicOperations.ZeroMemory(candidateKey);
                return false;
            }
        }

        SetKeyMaterial(candidateKey);
        await EnsureDatabaseInitializedAndSyncedAsync(ct);
        Unlocked?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // ── 4.4: Mode Switching & Password Change ───────────────────────────────────

    /// <inheritdoc />
    public async Task SwitchModeAsync(EncryptionMode newMode, string? newPassword = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureUnlocked();

        if (newMode == Mode)
            return;

        string currentKeyHex = GetSqlCipherKeyHex()!;

        if (newMode == EncryptionMode.MasterPassword)
        {
            if (string.IsNullOrWhiteSpace(newPassword))
                throw new ArgumentException("A master password is required when switching to MasterPassword mode.", nameof(newPassword));

            byte[] salt   = RandomNumberGenerator.GetBytes(SaltSizeBytes);
            byte[] newKey = DeriveKeyFromPassword(newPassword, salt);
            string hash   = ComputeVerificationHash(newKey);
            string newHex = Convert.ToHexString(newKey);

            await RekeyDatabaseFileAsync(currentKeyHex, newHex, ct);

            DeleteDpapiKeyFile();
            SetKeyMaterial(newKey);

            _metadata.Mode                  = EncryptionMode.MasterPassword;
            _metadata.MasterPasswordSalt    = Convert.ToBase64String(salt);
            _metadata.MasterPasswordHash    = hash;
            _metadata.DpapiRecoveryEnvelope = null;
            SaveSecurityMetadata(_metadata);
        }
        else
        {
            // MasterPassword -> DPAPI
            byte[] newKey = RandomNumberGenerator.GetBytes(KeySizeBytes);
            string newHex = Convert.ToHexString(newKey);

            await RekeyDatabaseFileAsync(currentKeyHex, newHex, ct);

            SaveDpapiKeyBlob(newKey);
            SetKeyMaterial(newKey);

            _metadata.Mode                  = EncryptionMode.Dpapi;
            _metadata.MasterPasswordSalt    = null;
            _metadata.MasterPasswordHash    = null;
            _metadata.DpapiRecoveryEnvelope = CreateRecoveryEnvelope(newKey);
            SaveSecurityMetadata(_metadata);
        }

        await SyncAppSettingsTableAsync(ct);
    }

    /// <inheritdoc />
    public async Task ChangeMasterPasswordAsync(string oldPassword, string newPassword, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Mode != EncryptionMode.MasterPassword)
            throw new InvalidOperationException("Cannot change master password when not in MasterPassword mode.");

        if (string.IsNullOrWhiteSpace(newPassword))
            throw new ArgumentException("New password cannot be empty.", nameof(newPassword));

        if (string.IsNullOrEmpty(_metadata.MasterPasswordSalt) ||
            string.IsNullOrEmpty(_metadata.MasterPasswordHash))
        {
            throw new InvalidOperationException("Master password metadata is missing.");
        }

        byte[] oldSalt = Convert.FromBase64String(_metadata.MasterPasswordSalt);
        byte[] oldKey  = DeriveKeyFromPassword(oldPassword, oldSalt);

        if (!VerifyKeyHash(oldKey, _metadata.MasterPasswordHash))
        {
            CryptographicOperations.ZeroMemory(oldKey);
            throw new UnauthorizedAccessException("The current master password is incorrect.");
        }

        string oldHex = Convert.ToHexString(oldKey);
        CryptographicOperations.ZeroMemory(oldKey);

        byte[] newSalt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        byte[] newKey  = DeriveKeyFromPassword(newPassword, newSalt);
        string newHash = ComputeVerificationHash(newKey);
        string newHex  = Convert.ToHexString(newKey);

        await RekeyDatabaseFileAsync(oldHex, newHex, ct);

        SetKeyMaterial(newKey);
        _metadata.MasterPasswordSalt = Convert.ToBase64String(newSalt);
        _metadata.MasterPasswordHash = newHash;
        SaveSecurityMetadata(_metadata);

        await SyncAppSettingsTableAsync(ct);
    }

    /// <summary>
    /// Updates the configured auto-lock timeout in minutes (persisted in both <c>security.json</c> and <c>AppSettings</c>).
    /// </summary>
    public async Task SetAutoLockMinutesAsync(int minutes, CancellationToken ct = default)
    {
        _metadata.AutoLockMinutes = Math.Clamp(minutes, 1, 1440);
        SaveSecurityMetadata(_metadata);
        if (IsUnlocked)
            await SyncAppSettingsTableAsync(ct);
    }

    // ── 4.5: Lock & Credential CRUD ─────────────────────────────────────────────

    /// <inheritdoc />
    public void Lock()
    {
        ClearKeyMaterial();
        Locked?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async Task SaveCredentialAsync(Guid connectionId, Credential credential, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(credential);
        EnsureUnlocked();

        await using var conn = await SqlCipherConnectionHelper.OpenEncryptedConnectionAsync(
            _environment.DatabasePath, GetSqlCipherKeyHex(), ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE Connections
            SET Username       = @username,
                Password       = @password,
                Domain         = @domain,
                PrivateKeyPath = @keyPath,
                KeyPassphrase  = @passphrase,
                UpdatedAt      = @now
            WHERE Id = @id;
            """;
        cmd.Parameters.AddWithValue("@id",         connectionId.ToString());
        cmd.Parameters.AddWithValue("@username",   (object?)credential.Username       ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@password",   (object?)credential.Password       ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@domain",     (object?)credential.Domain         ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@keyPath",    (object?)credential.PrivateKeyPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@passphrase", (object?)credential.KeyPassphrase  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now",        DateTimeOffset.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <inheritdoc />
    public async Task<Credential?> GetCredentialAsync(Guid connectionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureUnlocked();

        await using var conn = await SqlCipherConnectionHelper.OpenEncryptedConnectionAsync(
            _environment.DatabasePath, GetSqlCipherKeyHex(), ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Username, Password, Domain, PrivateKeyPath, KeyPassphrase
            FROM   Connections
            WHERE  Id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", connectionId.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new Credential(
            Username:       reader.IsDBNull(0) ? null : reader.GetString(0),
            Password:       reader.IsDBNull(1) ? null : reader.GetString(1),
            Domain:         reader.IsDBNull(2) ? null : reader.GetString(2),
            PrivateKeyPath: reader.IsDBNull(3) ? null : reader.GetString(3),
            KeyPassphrase:  reader.IsDBNull(4) ? null : reader.GetString(4)
        );
    }

    /// <inheritdoc />
    public async Task DeleteCredentialAsync(Guid connectionId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureUnlocked();

        await using var conn = await SqlCipherConnectionHelper.OpenEncryptedConnectionAsync(
            _environment.DatabasePath, GetSqlCipherKeyHex(), ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE Connections
            SET Username       = NULL,
                Password       = NULL,
                Domain         = NULL,
                PrivateKeyPath = NULL,
                KeyPassphrase  = NULL,
                UpdatedAt      = @now
            WHERE Id = @id;
            """;
        cmd.Parameters.AddWithValue("@id",  connectionId.ToString());
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Internal helpers ────────────────────────────────────────────────────────

    private async Task RekeyDatabaseFileAsync(string oldKeyHex, string newKeyHex, CancellationToken ct)
    {
        BeforeRekey?.Invoke(this, EventArgs.Empty);

        await EnsureDatabaseInitializedAndSyncedAsync(ct);

        await using var conn = await SqlCipherConnectionHelper.OpenEncryptedConnectionAsync(
            _environment.DatabasePath, oldKeyHex, ct);

        await SqlCipherConnectionHelper.RekeyAsync(conn, newKeyHex, ct);
    }

    private async Task EnsureDatabaseInitializedAndSyncedAsync(CancellationToken ct)
    {
        var initializer = new DatabaseInitializer(_environment);
        await initializer.InitializeAsync(GetSqlCipherKeyHex(), ct);
        await SyncAppSettingsTableAsync(ct);
    }

    private async Task SyncAppSettingsTableAsync(CancellationToken ct)
    {
        if (!IsUnlocked || !File.Exists(_environment.DatabasePath))
            return;

        await using var conn = await SqlCipherConnectionHelper.OpenEncryptedConnectionAsync(
            _environment.DatabasePath, GetSqlCipherKeyHex(), ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO AppSettings (Key, Value) VALUES
                ('encryption_mode',      @mode),
                ('master_password_hash', @hash),
                ('master_password_salt', @salt),
                ('auto_lock_minutes',    @lockMins);
            """;
        cmd.Parameters.AddWithValue("@mode",     _metadata.Mode == EncryptionMode.MasterPassword ? "master_password" : "dpapi");
        cmd.Parameters.AddWithValue("@hash",     _metadata.MasterPasswordHash ?? string.Empty);
        cmd.Parameters.AddWithValue("@salt",     _metadata.MasterPasswordSalt ?? string.Empty);
        cmd.Parameters.AddWithValue("@lockMins", AutoLockMinutes.ToString());

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private void SaveDpapiKeyBlob(byte[] rawKey)
    {
        var dir = Path.GetDirectoryName(_environment.DpapiKeyPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        byte[] protectedBytes = OperatingSystem.IsWindows()
            ? ProtectedData.Protect(
                rawKey,
                optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser)
            : rawKey;

        File.WriteAllBytes(_environment.DpapiKeyPath, protectedBytes);
    }

    private void DeleteDpapiKeyFile()
    {
        try
        {
            if (File.Exists(_environment.DpapiKeyPath))
                File.Delete(_environment.DpapiKeyPath);
        }
        catch { /* ignore */ }
    }

    private SecurityMetadata LoadSecurityMetadata()
    {
        try
        {
            if (File.Exists(SecurityMetadataPath))
            {
                string json = File.ReadAllText(SecurityMetadataPath, Encoding.UTF8);
                return ConnectionSettingsSerializer.Deserialize<SecurityMetadata>(json) ?? new SecurityMetadata();
            }
        }
        catch { /* fallback to default */ }

        return new SecurityMetadata();
    }

    private void SaveSecurityMetadata(SecurityMetadata metadata)
    {
        var dir = Path.GetDirectoryName(SecurityMetadataPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string json = ConnectionSettingsSerializer.Serialize(metadata) ?? "{}";
        File.WriteAllText(SecurityMetadataPath, json, Encoding.UTF8);
    }

    private static byte[] DeriveKeyFromPassword(string password, ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Pbkdf2Iterations,
            HashAlgorithmName.SHA256,
            KeySizeBytes);

    private static string ComputeVerificationHash(ReadOnlySpan<byte> derivedKey)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(derivedKey, hash);
        return Convert.ToBase64String(hash);
    }

    private static bool VerifyKeyHash(ReadOnlySpan<byte> candidateKey, string expectedBase64Hash)
    {
        try
        {
            byte[] expected = Convert.FromBase64String(expectedBase64Hash);
            Span<byte> actual = stackalloc byte[32];
            SHA256.HashData(candidateKey, actual);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }

    private static string CreateRecoveryEnvelope(ReadOnlySpan<byte> key)
    {
        Span<byte> masked = stackalloc byte[KeySizeBytes];
        for (int i = 0; i < KeySizeBytes; i++)
            masked[i] = (byte)(key[i] ^ PortableEnvelopeMask[i]);
        return Convert.ToBase64String(masked);
    }

    private static byte[]? TryUnpackRecoveryEnvelope(string? envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope))
            return null;

        try
        {
            byte[] masked = Convert.FromBase64String(envelope);
            if (masked.Length != KeySizeBytes)
                return null;

            byte[] key = new byte[KeySizeBytes];
            for (int i = 0; i < KeySizeBytes; i++)
                key[i] = (byte)(masked[i] ^ PortableEnvelopeMask[i]);
            return key;
        }
        catch
        {
            return null;
        }
    }

    private void SetKeyMaterial(byte[] newKey)
    {
        ClearKeyMaterial();
        _rawKey = (byte[])newKey.Clone();
    }

    private void ClearKeyMaterial()
    {
        if (_rawKey is not null)
        {
            CryptographicOperations.ZeroMemory(_rawKey);
            _rawKey = null;
        }
    }

    private void EnsureUnlocked()
    {
        if (!IsUnlocked)
            throw new InvalidOperationException("Credential store is currently locked.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearKeyMaterial();
    }
}
