using System.Text;
using FluentAssertions;
using LiteConnect.Core.Models;
using LiteConnect.Core.Recording;
using Xunit;

namespace LiteConnect.Core.Tests;

public sealed class SessionRecorderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"lc_rec_test_{Guid.NewGuid():N}");

    public SessionRecorderTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Ignore cleanup locks
        }
    }

    [Fact]
    public async Task FileSessionRecorder_PlainText_StripsAnsiAndFlushesToDisk()
    {
        string logPath = Path.Combine(_tempDir, "plain.log");
        using var recorder = new FileSessionRecorder();

        await recorder.StartAsync(logPath, RecordingFormat.PlainText);
        recorder.IsRecording.Should().BeTrue();

        var ts = new DateTimeOffset(2026, 10, 2, 12, 30, 0, TimeSpan.Zero);
        byte[] payload = Encoding.UTF8.GetBytes(
            "\x1b]0;user@web-01: ~\x07\x1b[32m$ \x1b[0mhostname\r\n\x1b[1;36mweb-server-01\x1b[0m\r\n");

        recorder.Write(payload, ts);
        await recorder.StopAsync();

        recorder.IsRecording.Should().BeFalse();
        string content = await File.ReadAllTextAsync(logPath);
        content.Should().Be("$ hostname\nweb-server-01\n");
    }

    [Fact]
    public async Task FileSessionRecorder_Timestamped_PrefixesEachLineEvenAcrossSplitChunks()
    {
        string logPath = Path.Combine(_tempDir, "timestamped.log");
        using var recorder = new FileSessionRecorder();

        await recorder.StartAsync(logPath, RecordingFormat.Timestamped);

        var ts1 = new DateTimeOffset(2026, 9, 27, 14, 32, 1, 123, TimeSpan.Zero);
        var ts2 = new DateTimeOffset(2026, 9, 27, 14, 32, 1, 456, TimeSpan.Zero);

        // First line arrives in two separate chunks
        recorder.Write(Encoding.UTF8.GetBytes("\x1b[32m$ \x1b[0mup"), ts1);
        recorder.Write(Encoding.UTF8.GetBytes("time\r\n"), ts1);

        // Second line
        recorder.Write(Encoding.UTF8.GetBytes(" 14:32:01 up 42 days\r\n"), ts2);

        await recorder.StopAsync();

        string content = await File.ReadAllTextAsync(logPath);
        string[] lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Should().HaveCount(2);
        lines[0].Should().Be("[2026-09-27 14:32:01.123] $ uptime");
        lines[1].Should().Be("[2026-09-27 14:32:01.456]  14:32:01 up 42 days");
    }

    [Fact]
    public async Task FileSessionRecorder_Html_ConvertsAnsiColorsAndEscapesHtmlEntities()
    {
        string logPath = Path.Combine(_tempDir, "session.html");
        using var recorder = new FileSessionRecorder();

        await recorder.StartAsync(logPath, RecordingFormat.Html);

        var ts = DateTimeOffset.UtcNow;
        recorder.Write(
            Encoding.UTF8.GetBytes("\x1b[1;32m$ \x1b[0mcat <config>&file\r\n\x1b[38;2;255;128;0mOK\x1b[0m\r\n"),
            ts);

        await recorder.StopAsync();

        string html = await File.ReadAllTextAsync(logPath);
        html.Should().StartWith("<pre style=\"background:#1e1e1e;color:#d4d4d4;");
        html.Should().Contain("<span style=\"color:#13a10e;font-weight:bold;\">$ </span>cat &lt;config&gt;&amp;file");
        html.Should().Contain("<span style=\"color:#ff8000;\">OK</span>");
        html.TrimEnd().Should().EndWith("</pre>");
    }

    [Fact]
    public async Task FileSessionRecorder_PauseAndResume_DiscardsDataWhilePaused()
    {
        string logPath = Path.Combine(_tempDir, "pause_test.log");
        using var recorder = new FileSessionRecorder();

        await recorder.StartAsync(logPath, RecordingFormat.PlainText);
        var ts = DateTimeOffset.UtcNow;

        recorder.Write(Encoding.UTF8.GetBytes("BeforePause\n"), ts);

        recorder.Pause();
        recorder.IsPaused.Should().BeTrue();
        recorder.Write(Encoding.UTF8.GetBytes("SecretWhilePaused\n"), ts);

        recorder.Resume();
        recorder.IsPaused.Should().BeFalse();
        recorder.Write(Encoding.UTF8.GetBytes("AfterResume\n"), ts);

        await recorder.StopAsync();

        string content = await File.ReadAllTextAsync(logPath);
        content.Should().Be("BeforePause\nAfterResume\n");
    }

    [Fact]
    public void SessionRecordingPathResolver_ExpandsVariablesAndAdjustsHtmlExtension()
    {
        var conn = new ConnectionInfo
        {
            Name                 = "Prod Web 01",
            Hostname             = "10.20.30.40",
            Username             = "ubuntu",
            RecordingDirectory   = _tempDir,
            RecordingFilePattern = "{connection}_{host}_{user}_{date}_{time}.log",
            RecordingFormat      = RecordingFormat.Html
        };

        var ts = new DateTimeOffset(2026, 10, 2, 15, 4, 5, TimeSpan.Zero).ToLocalTime();
        string resolved = SessionRecordingPathResolver.ResolveFilePath(conn, timestamp: ts);

        Path.GetDirectoryName(resolved).Should().Be(_tempDir);
        string fileName = Path.GetFileName(resolved);
        fileName.Should().StartWith("Prod Web 01_10.20.30.40_ubuntu_");
        fileName.Should().EndWith(".html");
    }
}
