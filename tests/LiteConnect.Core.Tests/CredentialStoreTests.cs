using System.Runtime.Versioning;
using FluentAssertions;
using LiteConnect.Core.Abstractions;
using LiteConnect.Core.Data;
using LiteConnect.Core.Models;
using LiteConnect.Core.Services;

namespace LiteConnect.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class CredentialStoreTests : IDisposable
{
    private readonly TestVaultEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task Dpapi_FirstRun_CreatesKeyFile_And_SubsequentLaunch_UnlocksSilently()
    {
        // 1st launch
        using (var store1 = new SqlCipherCredentialStore(_env))
        {
            bool unlocked1 = await store1.InitializeAsync();
            unlocked1.Should().BeTrue();
            store1.Mode.Should().Be(EncryptionMode.Dpapi);
            store1.IsUnlocked.Should().BeTrue();
            File.Exists(_env.DpapiKeyPath).Should().BeTrue();

            // Database file should be SQLCipher-encrypted (not plaintext SQLite)
            SqlCipherConnectionHelper.IsPlaintextSqliteFile(_env.DatabasePath).Should().BeFalse();
        }

        // 2nd launch
        using (var store2 = new SqlCipherCredentialStore(_env))
        {
            bool unlocked2 = await store2.InitializeAsync();
            unlocked2.Should().BeTrue();
            store2.IsUnlocked.Should().BeTrue();
        }
    }

    [Fact]
    public async Task SwitchMode_Dpapi_To_MasterPassword_And_Back_PreservesCredentials()
    {
        Guid connId;

        using (var store = new SqlCipherCredentialStore(_env))
        {
            await store.InitializeAsync();
            using var repo = new SqliteConnectionRepository(_env, store);

            var group = new ConnectionGroup { Name = "Prod" };
            var conn = new ConnectionInfo
            {
                GroupId  = group.Id,
                Name     = "rdp-01",
                Hostname = "10.0.0.50",
                Protocol = ProtocolType.RDP
            };
            connId = conn.Id;

            await repo.SaveGroupAsync(group);
            await repo.SaveConnectionAsync(conn);
            await store.SaveCredentialAsync(connId, new Credential("admin", "S3cretP@ss!", "CORP", null, null));

            // Switch DPAPI -> MasterPassword
            await store.SwitchModeAsync(EncryptionMode.MasterPassword, "MasterPass#2026");
            store.Mode.Should().Be(EncryptionMode.MasterPassword);
            File.Exists(_env.DpapiKeyPath).Should().BeFalse();
        }

        // Restart in MasterPassword mode
        using (var store2 = new SqlCipherCredentialStore(_env))
        {
            bool autoUnlocked = await store2.InitializeAsync();
            autoUnlocked.Should().BeFalse();
            store2.IsUnlocked.Should().BeFalse();

            // Wrong password rejected
            (await store2.UnlockWithPasswordAsync("WrongPassword")).Should().BeFalse();
            store2.IsUnlocked.Should().BeFalse();

            // Correct password unlocks
            (await store2.UnlockWithPasswordAsync("MasterPass#2026")).Should().BeTrue();
            store2.IsUnlocked.Should().BeTrue();

            // Credentials survived transition
            var cred = await store2.GetCredentialAsync(connId);
            cred.Should().NotBeNull();
            cred!.Username.Should().Be("admin");
            cred.Password.Should().Be("S3cretP@ss!");
            cred.Domain.Should().Be("CORP");

            // Change Master Password
            await store2.ChangeMasterPasswordAsync("MasterPass#2026", "UpdatedMaster#99");

            // Switch back MasterPassword -> DPAPI
            await store2.SwitchModeAsync(EncryptionMode.Dpapi);
            store2.Mode.Should().Be(EncryptionMode.Dpapi);
            File.Exists(_env.DpapiKeyPath).Should().BeTrue();
        }

        // Restart in DPAPI mode: unlocks silently and credentials are still intact
        using (var store3 = new SqlCipherCredentialStore(_env))
        {
            (await store3.InitializeAsync()).Should().BeTrue();
            var cred = await store3.GetCredentialAsync(connId);
            cred.Should().NotBeNull();
            cred!.Password.Should().Be("S3cretP@ss!");
        }
    }

    [Fact]
    public async Task DpapiPortabilityMismatch_Detected_And_Recovery_KeepsConnections_ClearsPasswords()
    {
        Guid connId;

        using (var store = new SqlCipherCredentialStore(_env))
        {
            await store.InitializeAsync();
            using var repo = new SqliteConnectionRepository(_env, store);

            var group = new ConnectionGroup { Name = "PortableFolder" };
            var conn = new ConnectionInfo
            {
                GroupId  = group.Id,
                Name     = "srv-portable",
                Hostname = "portable.local",
                Protocol = ProtocolType.RDP,
                Username = "mathew",
                Password = "OldMachineSecret"
            };
            connId = conn.Id;

            await repo.SaveGroupAsync(group);
            await repo.SaveConnectionAsync(conn);
        }

        // Simulate copying to another Windows user/PC where dpapi.key cannot be decrypted by CurrentUser
        await File.WriteAllBytesAsync(_env.DpapiKeyPath, [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02]);

        using (var storeOnNewPc = new SqlCipherCredentialStore(_env))
        {
            bool unlocked = await storeOnNewPc.InitializeAsync();
            unlocked.Should().BeFalse();
            storeOnNewPc.DpapiPortabilityMismatch.Should().BeTrue();

            // Recover for current user (keep connections, clear passwords)
            await storeOnNewPc.ResetDpapiForCurrentUserAsync();
            storeOnNewPc.IsUnlocked.Should().BeTrue();
            storeOnNewPc.DpapiPortabilityMismatch.Should().BeFalse();

            using var repo = new SqliteConnectionRepository(_env, storeOnNewPc);
            var tree = await repo.LoadTreeAsync();
            var loadedConn = tree.Groups.Single().Connections.Single(c => c.Id == connId);

            loadedConn.Name.Should().Be("srv-portable");
            loadedConn.Hostname.Should().Be("portable.local");
            loadedConn.Username.Should().Be("mathew");
            loadedConn.Password.Should().BeNull();
        }
    }

    [Fact]
    public async Task CredentialResolver_Inherits_Group_Username_And_Domain()
    {
        using var store = new SqlCipherCredentialStore(_env);
        await store.InitializeAsync();
        using var repo = new SqliteConnectionRepository(_env, store);

        var parentGroup = new ConnectionGroup
        {
            Name            = "CorpRoot",
            DefaultDomain   = "CORP",
            DefaultUsername = "corp-admin"
        };
        var childGroup = new ConnectionGroup
        {
            Name            = "SQLServers",
            ParentId        = parentGroup.Id,
            DefaultUsername = "sql-admin" // overrides parent username, inherits parent domain
        };
        var conn = new ConnectionInfo
        {
            GroupId  = childGroup.Id,
            Name     = "sql-01",
            Hostname = "sql01.corp.local",
            Protocol = ProtocolType.RDP
        };

        await repo.SaveGroupAsync(parentGroup);
        await repo.SaveGroupAsync(childGroup);
        await repo.SaveConnectionAsync(conn);
        await store.SaveCredentialAsync(conn.Id, new Credential(null, "SqlPass!123", null, null, null));

        var effective = await CredentialResolver.ResolveEffectiveAsync(conn, repo, store);

        effective.Username.Should().Be("sql-admin");
        effective.Domain.Should().Be("CORP");
        effective.Password.Should().Be("SqlPass!123");
    }

    private sealed class TestVaultEnvironment : IAppEnvironment, IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lc_vault_{Guid.NewGuid():N}");

        public TestVaultEnvironment() => Directory.CreateDirectory(_dir);

        public bool   IsPortable    => true;
        public string DataDirectory => _dir;
        public string LogDirectory  => _dir;
        public string DatabasePath  => Path.Combine(_dir, "connections.db");
        public string DpapiKeyPath  => Path.Combine(_dir, "dpapi.key");

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch { /* ignore */ }
        }
    }
}
