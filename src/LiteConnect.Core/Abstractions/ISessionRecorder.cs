using LiteConnect.Core.Models;

namespace LiteConnect.Core.Abstractions;

/// <summary>
/// Records raw SSH terminal output to disk in three selectable formats.
/// A single <see cref="ISessionRecorder"/> is created per SSH tab via the factory.
/// </summary>
public interface ISessionRecorder : IDisposable
{
    /// <summary>True while a recording session is open (including when paused).</summary>
    bool IsRecording { get; }

    /// <summary>True when recording is currently paused (incoming data is discarded).</summary>
    bool IsPaused { get; }

    /// <summary>Path to the active log file; null when not recording.</summary>
    string? CurrentLogPath { get; }

    /// <summary>The format of the active recording.</summary>
    RecordingFormat CurrentFormat { get; }

    /// <summary>Total formatted bytes written to the current log file.</summary>
    long BytesWritten { get; }

    /// <summary>Begin recording to <paramref name="filePath"/>.</summary>
    Task StartAsync(string filePath, RecordingFormat format, CancellationToken ct = default);

    /// <summary>Flush and close the current log file.</summary>
    Task StopAsync();

    /// <summary>Pause recording — incoming data is discarded while paused.</summary>
    void Pause();

    /// <summary>Resume recording after a pause.</summary>
    void Resume();

    /// <summary>
    /// Receives raw bytes from the SSH stream.
    /// Called on whatever thread delivers SSH data; must be thread-safe and non-blocking.
    /// </summary>
    void Write(ReadOnlySpan<byte> data, DateTimeOffset timestamp);

    /// <summary>Fires when recording state or byte count changes (start/stop/pause/resume/flush).</summary>
    event EventHandler<RecordingStateChangedEventArgs> StateChanged;
}

public sealed class RecordingStateChangedEventArgs(
    bool isRecording,
    string? logPath,
    bool isPaused = false,
    long bytesWritten = 0) : EventArgs
{
    public bool IsRecording { get; } = isRecording;
    public bool IsPaused { get; } = isPaused;
    public string? LogPath { get; } = logPath;
    public long BytesWritten { get; } = bytesWritten;
}

/// <summary>Factory that creates one <see cref="ISessionRecorder"/> per SSH session tab.</summary>
public interface ISessionRecorderFactory
{
    ISessionRecorder CreateRecorder();
}
