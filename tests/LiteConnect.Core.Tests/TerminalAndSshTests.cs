using System.Text;
using FluentAssertions;
using LiteConnect.Core.Abstractions;
using LiteConnect.Core.Data;
using LiteConnect.Core.Models;
using LiteConnect.Core.Serialization;
using LiteConnect.Terminal;
using Xunit;

namespace LiteConnect.Core.Tests;

public sealed class TerminalAndSshTests : IAsyncLifetime
{
    private readonly TestEnvironment _env = new();
    private SqliteKnownHostsStore _knownHostsStore = null!;

    public async Task InitializeAsync()
    {
        var dbInit = new DatabaseInitializer(_env);
        await dbInit.InitializeAsync();
        _knownHostsStore = new SqliteKnownHostsStore(_env);
    }

    public Task DisposeAsync()
    {
        try
        {
            if (File.Exists(_env.DatabasePath))
                File.Delete(_env.DatabasePath);
        }
        catch
        {
            // Ignore cleanup locks
        }
        return Task.CompletedTask;
    }

    // ── TerminalBuffer Tests ────────────────────────────────────────────────────

    [Fact]
    public void TerminalBuffer_PutChar_And_DeferredAutoWrap_Works()
    {
        var buffer = new TerminalBuffer(columns: 5, rows: 3);

        foreach (char c in "HELLO")
            buffer.PutChar(c);

        // After writing 5 chars on a 5-column row, cursor stays at col 4 (deferred wrap)
        buffer.CursorRow.Should().Be(0);
        buffer.CursorColumn.Should().Be(4);
        buffer.GetLineText(0).Should().Be("HELLO");

        // Writing the 6th character triggers the wrap to row 1, col 0
        buffer.PutChar('!');
        buffer.CursorRow.Should().Be(1);
        buffer.CursorColumn.Should().Be(1);
        buffer.GetLineText(1).Should().Be("!");
    }

    [Fact]
    public void TerminalBuffer_ScrollRegion_ScrollsOnlyWithinRegion()
    {
        var buffer = new TerminalBuffer(columns: 10, rows: 5);
        var parser = new AnsiParser(buffer);

        parser.ProcessText("Row0\r\nRow1\r\nRow2\r\nRow3\r\nRow4");
        buffer.GetLineText(0).Should().Be("Row0");
        buffer.GetLineText(4).Should().Be("Row4");

        // Set scroll region to rows 2..4 (0-based rows 1..3)
        buffer.SetScrollRegion(1, 3);
        buffer.ScrollUp(1);

        // Row 0 and Row 4 must be untouched; Row1 scrolled out, Row2->1, Row3->2, Row3 blank
        buffer.GetLineText(0).Should().Be("Row0");
        buffer.GetLineText(1).Should().Be("Row2");
        buffer.GetLineText(2).Should().Be("Row3");
        buffer.GetLineText(3).Should().BeEmpty();
        buffer.GetLineText(4).Should().Be("Row4");
    }

    [Fact]
    public void TerminalBuffer_Scrollback_AccumulatesAndTrimsToCapacity()
    {
        var buffer = new TerminalBuffer(columns: 10, rows: 2, maxScrollbackLines: 3);

        for (int i = 1; i <= 6; i++)
        {
            foreach (char c in $"Line{i}")
                buffer.PutChar(c);
            if (i < 6)
            {
                buffer.CarriageReturn();
                buffer.LineFeed();
            }
        }

        // 6 lines written into a 2-row screen => 4 lines scrolled off, capped at maxScrollback=3
        buffer.ScrollbackCount.Should().Be(3);
        buffer.GetLineText(0).Should().Be("Line5");
        buffer.GetLineText(1).Should().Be("Line6");
    }

    [Fact]
    public void TerminalBuffer_AlternateScreen_PreservesPrimaryScreen()
    {
        var buffer = new TerminalBuffer(columns: 20, rows: 5);
        var parser = new AnsiParser(buffer);

        parser.ProcessText("Primary Content");
        buffer.GetLineText(0).Should().Be("Primary Content");

        // Enter alternate screen (?1049h)
        parser.ProcessText("\x1b[?1049h");
        buffer.IsAlternateScreen.Should().BeTrue();
        buffer.GetLineText(0).Should().BeEmpty();

        parser.ProcessText("Vim Alternate Buffer");
        buffer.GetLineText(0).Should().Be("Vim Alternate Buffer");

        // Exit alternate screen (?1049l)
        parser.ProcessText("\x1b[?1049l");
        buffer.IsAlternateScreen.Should().BeFalse();
        buffer.GetLineText(0).Should().Be("Primary Content");
    }

    // ── AnsiParser Tests ────────────────────────────────────────────────────────

    [Fact]
    public void AnsiParser_ParsesSgrColors_16_256_And_TrueColor()
    {
        var buffer = new TerminalBuffer(columns: 20, rows: 4);
        var parser = new AnsiParser(buffer);

        // Bold Red (ANSI 1), 256-color fg (208), TrueColor bg (10, 20, 30)
        parser.ProcessText("\x1b[1;31mA\x1b[38;5;208mB\x1b[48;2;10;20;30mC\x1b[0mD");

        var cellA = buffer.GetCell(0, 0);
        cellA.Character.Should().Be('A');
        cellA.Attributes.HasFlag(CellAttributes.Bold).Should().BeTrue();
        cellA.Foreground.Kind.Should().Be(TerminalColorKind.Indexed);
        cellA.Foreground.PaletteIndex.Should().Be(1); // Red

        var cellB = buffer.GetCell(0, 1);
        cellB.Character.Should().Be('B');
        cellB.Foreground.Kind.Should().Be(TerminalColorKind.Indexed);
        cellB.Foreground.PaletteIndex.Should().Be(208);

        var cellC = buffer.GetCell(0, 2);
        cellC.Character.Should().Be('C');
        cellC.Background.Kind.Should().Be(TerminalColorKind.Rgb);
        cellC.Background.R.Should().Be(10);
        cellC.Background.G.Should().Be(20);
        cellC.Background.B.Should().Be(30);

        var cellD = buffer.GetCell(0, 3);
        cellD.Character.Should().Be('D');
        cellD.Attributes.Should().Be(CellAttributes.None);
        cellD.Foreground.Kind.Should().Be(TerminalColorKind.Default);
    }

    [Fact]
    public void AnsiParser_HandlesOscTitle_And_DsrCursorReport_And_LineDrawing()
    {
        var buffer = new TerminalBuffer(columns: 20, rows: 5);
        var parser = new AnsiParser(buffer);

        string? receivedTitle = null;
        string? dsrResponse = null;

        parser.TitleChanged += (_, title) => receivedTitle = title;
        parser.ResponseRequired += (_, resp) => dsrResponse = resp;

        // Set OSC title, move to row 3 col 7, request DSR 6n, and draw box top-left + horizontal + top-right
        parser.ProcessText("\x1b]0;prod-web-01: ~\x07\x1b[3;7H\x1b[6n\x1b(0lqqk\x1b(B");

        receivedTitle.Should().Be("prod-web-01: ~");
        dsrResponse.Should().Be("\x1b[3;7R");
        buffer.GetLineText(2).Trim().Should().Be("┌──┐");
    }

    [Fact]
    public void AnsiParser_DecodesSplitUtf8Bytes_AcrossProcessBytesCalls()
    {
        var buffer = new TerminalBuffer(columns: 10, rows: 2);
        var parser = new AnsiParser(buffer);

        // '€' is 3 bytes in UTF-8: 0xE2, 0x82, 0xAC
        byte[] euroBytes = Encoding.UTF8.GetBytes("€");
        euroBytes.Length.Should().Be(3);

        parser.ProcessBytes(euroBytes.AsSpan(0, 1));
        parser.ProcessBytes(euroBytes.AsSpan(1, 2));

        buffer.GetLineText(0).Should().Be("€");
    }

    // ── KnownHosts & SSH Settings Serialization Tests ───────────────────────────

    [Fact]
    public async Task SqliteKnownHostsStore_Save_Get_And_Remove_Roundtrips()
    {
        await _knownHostsStore.SaveKnownHostAsync("10.0.1.50", 2222, "ssh-ed25519", "SHA256:abc123xyz", trusted: true);

        var loaded = await _knownHostsStore.GetKnownHostAsync("10.0.1.50", 2222, "ssh-ed25519");
        loaded.Should().NotBeNull();
        loaded!.Fingerprint.Should().Be("SHA256:abc123xyz");
        loaded.Trusted.Should().BeTrue();

        await _knownHostsStore.RemoveKnownHostAsync("10.0.1.50", 2222, "ssh-ed25519");
        var afterDelete = await _knownHostsStore.GetKnownHostAsync("10.0.1.50", 2222, "ssh-ed25519");
        afterDelete.Should().BeNull();
    }

    [Fact]
    public void SshConnectionSettings_WithPortForwardRules_SerializesRoundtrip()
    {
        var settings = new SshConnectionSettings
        {
            FontFamily = "JetBrains Mono",
            FontSize = 14,
            ColorScheme = "Dracula",
            ScrollbackLines = 25000,
            PortForwards =
            [
                new PortForwardRule
                {
                    Type = PortForwardType.Local,
                    BindAddress = "127.0.0.1",
                    BoundPort = 15432,
                    TargetHost = "db.internal",
                    TargetPort = 5432,
                    Enabled = true
                }
            ]
        };

        string? json = ConnectionSettingsSerializer.Serialize(settings);
        json.Should().NotBeNullOrWhiteSpace();

        var roundtripped = ConnectionSettingsSerializer.Deserialize<SshConnectionSettings>(json);
        roundtripped.Should().NotBeNull();
        roundtripped!.ColorScheme.Should().Be("Dracula");
        roundtripped.PortForwards.Should().NotBeNull();
        var forwards = roundtripped.PortForwards!;
        forwards.Should().ContainSingle();
        forwards[0].BoundPort.Should().Be(15432);
        forwards[0].TargetHost.Should().Be("db.internal");
        forwards[0].TargetPort.Should().Be(5432);
    }

    private sealed class TestEnvironment : IAppEnvironment
    {
        private readonly string _tempPath = Path.Combine(Path.GetTempPath(), $"lc_ssh_test_{Guid.NewGuid():N}.db");
        public bool IsPortable => true;
        public string DataDirectory => Path.GetTempPath();
        public string LogDirectory => Path.GetTempPath();
        public string DatabasePath => _tempPath;
        public string DpapiKeyPath => _tempPath + ".key";
    }
}
