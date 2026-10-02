using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using LiteConnect.Core.Abstractions;
using LiteConnect.Core.Data;
using LiteConnect.Core.ImportExport;
using LiteConnect.Core.Models;
using LiteConnect.Core.Services;
using Xunit;

namespace LiteConnect.Core.Tests;

public sealed class ExportImportAndQuickConnectTests : IAsyncLifetime
{
    private readonly TestEnvironment _sourceEnv = new();
    private readonly TestEnvironment _targetEnv = new();
    private SqliteConnectionRepository _sourceRepo = null!;
    private SqliteConnectionRepository _targetRepo = null!;

    public async Task InitializeAsync()
    {
        await new DatabaseInitializer(_sourceEnv).InitializeAsync();
        await new DatabaseInitializer(_targetEnv).InitializeAsync();
        _sourceRepo = new SqliteConnectionRepository(_sourceEnv);
        _targetRepo = new SqliteConnectionRepository(_targetEnv);
    }

    public async Task DisposeAsync()
    {
        await _sourceRepo.DisposeAsync();
        await _targetRepo.DisposeAsync();
        _sourceEnv.Cleanup();
        _targetEnv.Cleanup();
    }

    // ── Sprint 7.1 & 7.2: Encrypted .lcx Export & Import Round-Trip ─────────────

    [Fact]
    public async Task Lcx_EncryptedExportAndImport_PreservesFoldersCredentialsAndEmbeddedSshKey()
    {
        // Create a temporary SSH key file to embed
        string tempKeyPath = Path.Combine(_sourceEnv.DataDirectory, "id_ed25519_test");
        const string keyContent = "-----BEGIN OPENSSH PRIVATE KEY-----\ntest-key-payload\n-----END OPENSSH PRIVATE KEY-----\n";
        await File.WriteAllTextAsync(tempKeyPath, keyContent);

        var prodGroup = new ConnectionGroup { Id = Guid.NewGuid(), Name = "Production" };
        var webGroup  = new ConnectionGroup { Id = Guid.NewGuid(), Name = "Web", ParentId = prodGroup.Id };
        await _sourceRepo.SaveGroupAsync(prodGroup);
        await _sourceRepo.SaveGroupAsync(webGroup);

        var sshConn = new ConnectionInfo
        {
            Id             = Guid.NewGuid(),
            GroupId        = webGroup.Id,
            Name           = "prod-web-01",
            Protocol       = ProtocolType.SSH,
            Hostname       = "10.10.1.15",
            Port           = 2222,
            Username       = "deploy",
            Password       = "SuperSecret!123",
            PrivateKeyPath = tempKeyPath,
            KeyPassphrase  = "KeyPass!456",
            SshSettings    = new SshConnectionSettings { ColorScheme = "Dracula", FontSize = 14 }
        };
        await _sourceRepo.SaveConnectionAsync(sshConn);

        var exporter = new ConnectionExportImportService(_sourceRepo, _sourceEnv);
        using var lcxStream = new MemoryStream();

        await exporter.ExportAsync(
            new ExportOptions(
                ConnectionIds:            null,
                Format:                   ExportFormat.EncryptedLcx,
                IncludeCredentials:       true,
                IncludeSshKeys:           true,
                IncludeRecordingSettings: true,
                PreserveFolderStructure:  true,
                Password:                 "BundlePass#2026"),
            lcxStream);

        // Delete original key file so import must restore the embedded key into target data dir
        File.Delete(tempKeyPath);

        // Verify wrong password fails with CryptographicException
        var importer = new ConnectionExportImportService(_targetRepo, _targetEnv);
        lcxStream.Position = 0;
        var wrongPwdAct = async () => await importer.PreviewAsync(lcxStream, password: "WrongPassword");
        await wrongPwdAct.Should().ThrowAsync<CryptographicException>();

        // Decrypt with correct password and import into target repository
        lcxStream.Position = 0;
        var fileInfo = await importer.InspectFileAsync(lcxStream);
        fileInfo.Format.Should().Be(ImportFormat.Lcx);
        fileInfo.IsEncrypted.Should().BeTrue();
        fileInfo.IncludesCredentials.Should().BeTrue();
        fileInfo.IncludesSshKeys.Should().BeTrue();

        lcxStream.Position = 0;
        var preview = await importer.PreviewAsync(lcxStream, password: "BundlePass#2026");
        preview.Items.Should().ContainSingle();
        preview.Items[0].FolderPath.Should().Be("Production/Web");
        preview.Items[0].HasConflict.Should().BeFalse();

        var importResult = await importer.ImportAsync(preview, new ImportOptions());
        importResult.ImportedCount.Should().Be(1);

        var targetTree = await _targetRepo.LoadTreeAsync();
        var importedProd = targetTree.Groups.Should().ContainSingle(g => g.Name == "Production").Subject;
        var importedWeb  = importedProd.Groups.Should().ContainSingle(g => g.Name == "Web").Subject;
        var importedConn = importedWeb.Connections.Should().ContainSingle(c => c.Name == "prod-web-01").Subject;

        importedConn.Hostname.Should().Be("10.10.1.15");
        importedConn.Port.Should().Be(2222);
        importedConn.Username.Should().Be("deploy");
        importedConn.Password.Should().Be("SuperSecret!123");
        importedConn.KeyPassphrase.Should().Be("KeyPass!456");
        importedConn.SshSettings?.ColorScheme.Should().Be("Dracula");
        importedConn.PrivateKeyPath.Should().NotBeNullOrWhiteSpace();
        File.Exists(importedConn.PrivateKeyPath!).Should().BeTrue();
        (await File.ReadAllTextAsync(importedConn.PrivateKeyPath!)).Should().Be(keyContent);

        // Re-importing the same preview detects conflict and renames to "prod-web-01 (2)"
        lcxStream.Position = 0;
        var secondPreview = await importer.PreviewAsync(lcxStream, password: "BundlePass#2026");
        secondPreview.Items[0].HasConflict.Should().BeTrue();
        secondPreview.Items[0].Resolution = ConflictResolution.Rename;

        var secondResult = await importer.ImportAsync(secondPreview, new ImportOptions());
        secondResult.ImportedCount.Should().Be(1);

        var treeAfterRename = await _targetRepo.LoadTreeAsync();
        var webConnections = treeAfterRename.Groups[0].Groups[0].Connections;
        webConnections.Select(c => c.Name).Should().Contain(["prod-web-01", "prod-web-01 (2)"]);
    }

    // ── Sprint 7.3: mRemoteNG XML Import ────────────────────────────────────────

    [Fact]
    public async Task MRemoteNgXml_ImportsFoldersSettingsAndDecryptsDefaultPassword()
    {
        // Build an mRemoteNG v1.76+ AES-GCM encrypted password using default key "mR3m"
        string encryptedPwd = EncryptMRemoteNgGcmPassword("RdpSecret99!", "mR3m");

        string xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <mrng:Connections xmlns:mrng="http://mremoteng.org" Name="Connections" ConfVersion="2.6">
              <Node Name="Datacenter" Type="Container" Expanded="True">
                <Node Name="dc-rdp-01" Type="Connection" Protocol="RDP" Hostname="10.50.0.10" Port="3389"
                      Username="Administrator" Domain="CORP" Password="{encryptedPwd}"
                      Resolution="Res1920x1080" Colors="Colors32Bit" RedirectDiskDrives="True"
                      RDGatewayUsageMethod="Always" RDGatewayHostname="gw.corp.local" />
                <Node Name="dc-ssh-01" Type="Connection" Protocol="SSH2" Hostname="10.50.0.20" Port="22"
                      Username="root" Descr="Primary Linux bastion" />
              </Node>
            </mrng:Connections>
            """;

        var importer = new ConnectionExportImportService(_targetRepo, _targetEnv);
        using var xmlStream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var preview = await importer.PreviewAsync(xmlStream, fileNameHint: "confCons.xml");
        preview.Format.Should().Be(ImportFormat.MRemoteNGXml);
        preview.Items.Should().HaveCount(2);

        var rdpItem = preview.Items.Single(i => i.Connection.Name == "dc-rdp-01");
        rdpItem.FolderPath.Should().Be("Datacenter");
        rdpItem.Connection.Protocol.Should().Be(ProtocolType.RDP);
        rdpItem.Connection.Password.Should().Be("RdpSecret99!");
        rdpItem.Connection.Domain.Should().Be("CORP");
        rdpItem.Connection.RdpSettings?.ResolutionMode.Should().Be("Custom");
        rdpItem.Connection.RdpSettings?.DesktopWidth.Should().Be(1920);
        rdpItem.Connection.RdpSettings?.DesktopHeight.Should().Be(1080);
        rdpItem.Connection.RdpSettings?.RedirectDrives.Should().BeTrue();
        rdpItem.Connection.RdpSettings?.UseGateway.Should().BeTrue();
        rdpItem.Connection.RdpSettings?.GatewayHostname.Should().Be("gw.corp.local");

        var sshItem = preview.Items.Single(i => i.Connection.Name == "dc-ssh-01");
        sshItem.Connection.Protocol.Should().Be(ProtocolType.SSH);
        sshItem.Connection.Notes.Should().Be("Primary Linux bastion");
    }

    // ── Sprint 7.4: CSV Export & Import Round-Trip ──────────────────────────────

    [Fact]
    public async Task Csv_ExportAndImport_RoundtripsConnectionsAndFolderPaths()
    {
        var group = new ConnectionGroup { Id = Guid.NewGuid(), Name = "Cloud" };
        await _sourceRepo.SaveGroupAsync(group);
        await _sourceRepo.SaveConnectionAsync(new ConnectionInfo
        {
            Id       = Guid.NewGuid(),
            GroupId  = group.Id,
            Name     = "aws-bastion, prod",
            Protocol = ProtocolType.SSH,
            Hostname = "54.10.20.30",
            Port     = 22,
            Username = "ec2-user",
            Notes    = "Quoted, note"
        });

        var exporter = new ConnectionExportImportService(_sourceRepo, _sourceEnv);
        using var csvStream = new MemoryStream();
        await exporter.ExportAsync(
            new ExportOptions(null, ExportFormat.Csv, false, false, false, true),
            csvStream);

        csvStream.Position = 0;
        var importer = new ConnectionExportImportService(_targetRepo, _targetEnv);
        var preview  = await importer.PreviewAsync(csvStream, fileNameHint: "export.csv");

        preview.Format.Should().Be(ImportFormat.Csv);
        preview.Items.Should().ContainSingle();
        preview.Items[0].Connection.Name.Should().Be("aws-bastion, prod");
        preview.Items[0].Connection.Hostname.Should().Be("54.10.20.30");
        preview.Items[0].Connection.Username.Should().Be("ec2-user");
        preview.Items[0].FolderPath.Should().Be("Cloud");
        preview.Items[0].Connection.Notes.Should().Be("Quoted, note");
    }

    // ── Sprint 8.1: QuickConnectParser Tests ────────────────────────────────────

    [Theory]
    [InlineData("admin@192.168.1.1:22", ProtocolType.RDP, "192.168.1.1", ProtocolType.SSH, 22, "admin", null, true)]
    [InlineData("CORP\\jdoe@10.0.0.50:3389", ProtocolType.SSH, "10.0.0.50", ProtocolType.RDP, 3389, "jdoe", "CORP", true)]
    [InlineData("vnc://10.0.0.99:5900", ProtocolType.SSH, "10.0.0.99", ProtocolType.VNC, 5900, null, null, true)]
    [InlineData("web-server-01", ProtocolType.SSH, "web-server-01", ProtocolType.SSH, null, null, null, false)]
    public void QuickConnectParser_ParsesHostUserPortAndAutoDetectsProtocol(
        string input,
        ProtocolType fallback,
        string expectedHost,
        ProtocolType expectedProto,
        int? expectedPort,
        string? expectedUser,
        string? expectedDomain,
        bool expectedAutoDetected)
    {
        bool ok = QuickConnectParser.TryParse(input, fallback, out var target);
        ok.Should().BeTrue();
        target.Should().NotBeNull();
        target!.Hostname.Should().Be(expectedHost);
        target.Protocol.Should().Be(expectedProto);
        target.Port.Should().Be(expectedPort);
        target.Username.Should().Be(expectedUser);
        target.Domain.Should().Be(expectedDomain);
        target.ProtocolAutoDetected.Should().Be(expectedAutoDetected);
    }

    private static string EncryptMRemoteNgGcmPassword(string plainText, string password)
    {
        byte[] salt  = RandomNumberGenerator.GetBytes(32);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] key   = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            iterations: 1000,
            HashAlgorithmName.SHA1,
            outputLength: 32);

        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] cipher     = new byte[plainBytes.Length];
        byte[] tag        = new byte[16];

        using var aesGcm = new AesGcm(key, 16);
        aesGcm.Encrypt(nonce, plainBytes, cipher, tag);

        byte[] combined = new byte[32 + 12 + cipher.Length + 16];
        salt.CopyTo(combined, 0);
        nonce.CopyTo(combined, 32);
        cipher.CopyTo(combined, 44);
        tag.CopyTo(combined, 44 + cipher.Length);
        return Convert.ToBase64String(combined);
    }

    private sealed class TestEnvironment : IAppEnvironment
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lc_exp_test_{Guid.NewGuid():N}");

        public TestEnvironment()
        {
            Directory.CreateDirectory(_dir);
        }

        public bool IsPortable => true;
        public string DataDirectory => _dir;
        public string LogDirectory => _dir;
        public string DatabasePath => Path.Combine(_dir, "connections.db");
        public string DpapiKeyPath => Path.Combine(_dir, "dpapi.key");

        public void Cleanup()
        {
            try
            {
                if (Directory.Exists(_dir))
                    Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // Ignore cleanup locks
            }
        }
    }
}
