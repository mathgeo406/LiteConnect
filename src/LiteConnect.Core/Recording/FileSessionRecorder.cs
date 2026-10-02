using System.Text;
using System.Threading.Channels;
using LiteConnect.Core.Abstractions;
using LiteConnect.Core.Models;

namespace LiteConnect.Core.Recording;

/// <summary>
/// Asynchronous, buffered SSH session recorder (<see cref="ISessionRecorder"/>).
/// Enqueues raw SSH stream chunks into an unbounded <see cref="Channel{T}"/> on the hot path
/// and formats/flushes them to disk on a dedicated background consumer task.
/// </summary>
public sealed class FileSessionRecorder : ISessionRecorder
{
    private readonly record struct RecordingChunk(byte[] Data, DateTimeOffset Timestamp);

    private readonly object _stateLock = new();
    private Channel<RecordingChunk>? _channel;
    private Task? _writerTask;
    private StreamWriter? _fileWriter;
    private ISessionLogFormatter? _formatter;

    private volatile bool _isRecording;
    private volatile bool _isPaused;
    private string? _currentLogPath;
    private RecordingFormat _currentFormat = RecordingFormat.PlainText;
    private long _bytesWritten;
    private bool _disposed;

    public bool IsRecording => _isRecording;
    public bool IsPaused => _isPaused;
    public string? CurrentLogPath => _currentLogPath;
    public RecordingFormat CurrentFormat => _currentFormat;
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    public event EventHandler<RecordingStateChangedEventArgs>? StateChanged;

    public async Task StartAsync(string filePath, RecordingFormat format, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("Log file path cannot be empty.", nameof(filePath));

        if (_isRecording)
        {
            await StopAsync().ConfigureAwait(false);
        }

        string fullPath = Path.GetFullPath(filePath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var formatter = CreateFormatter(format);
        var fileStream = new FileStream(
            fullPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 16384,
            useAsync: true);

        var streamWriter = new StreamWriter(fileStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false
        };

        long initialBytes = fileStream.Length;
        string header = formatter.GetHeader();
        if (!string.IsNullOrEmpty(header))
        {
            await streamWriter.WriteAsync(header.AsMemory(), ct).ConfigureAwait(false);
            await streamWriter.FlushAsync(ct).ConfigureAwait(false);
            initialBytes = fileStream.Length;
        }

        var channel = Channel.CreateUnbounded<RecordingChunk>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

        lock (_stateLock)
        {
            _formatter      = formatter;
            _fileWriter     = streamWriter;
            _channel        = channel;
            _currentLogPath = fullPath;
            _currentFormat  = format;
            _isPaused       = false;
            _isRecording    = true;
            Interlocked.Exchange(ref _bytesWritten, initialBytes);
            _writerTask = Task.Run(() => ConsumeChannelAsync(channel.Reader, streamWriter, formatter));
        }

        RaiseStateChanged();
    }

    public async Task StopAsync()
    {
        Channel<RecordingChunk>? channel;
        Task? writerTask;
        StreamWriter? fileWriter;
        ISessionLogFormatter? formatter;

        lock (_stateLock)
        {
            if (!_isRecording)
                return;

            _isRecording = false;
            _isPaused    = false;
            channel      = _channel;
            writerTask   = _writerTask;
            fileWriter   = _fileWriter;
            formatter    = _formatter;

            _channel    = null;
            _writerTask = null;
            _fileWriter = null;
            _formatter  = null;
        }

        channel?.Writer.TryComplete();

        if (writerTask is not null)
        {
            try
            {
                await writerTask.ConfigureAwait(false);
            }
            catch
            {
                // Ignore writer exceptions on shutdown
            }
        }

        if (fileWriter is not null)
        {
            try
            {
                if (formatter is not null)
                {
                    string footer = formatter.GetFooter();
                    if (!string.IsNullOrEmpty(footer))
                    {
                        await fileWriter.WriteAsync(footer).ConfigureAwait(false);
                    }
                }
                await fileWriter.FlushAsync().ConfigureAwait(false);
                if (fileWriter.BaseStream.CanSeek)
                {
                    Interlocked.Exchange(ref _bytesWritten, fileWriter.BaseStream.Length);
                }
            }
            catch
            {
                // Best effort flush
            }
            finally
            {
                await fileWriter.DisposeAsync().ConfigureAwait(false);
            }
        }

        lock (_stateLock)
        {
            _currentLogPath = null;
        }

        RaiseStateChanged();
    }

    public void Pause()
    {
        lock (_stateLock)
        {
            if (!_isRecording || _isPaused)
                return;
            _isPaused = true;
        }
        RaiseStateChanged();
    }

    public void Resume()
    {
        lock (_stateLock)
        {
            if (!_isRecording || !_isPaused)
                return;
            _isPaused = false;
        }
        RaiseStateChanged();
    }

    public void Write(ReadOnlySpan<byte> data, DateTimeOffset timestamp)
    {
        if (!_isRecording || _isPaused || data.IsEmpty)
            return;

        var channel = _channel;
        if (channel is null)
            return;

        byte[] copy = data.ToArray();
        channel.Writer.TryWrite(new RecordingChunk(copy, timestamp));
    }

    private async Task ConsumeChannelAsync(
        ChannelReader<RecordingChunk> reader,
        StreamWriter writer,
        ISessionLogFormatter formatter)
    {
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                bool wroteAny = false;
                while (reader.TryRead(out var chunk))
                {
                    string formatted = formatter.FormatChunk(chunk.Data, chunk.Timestamp);
                    if (!string.IsNullOrEmpty(formatted))
                    {
                        await writer.WriteAsync(formatted).ConfigureAwait(false);
                        wroteAny = true;
                    }
                }

                if (wroteAny)
                {
                    await writer.FlushAsync().ConfigureAwait(false);
                    if (writer.BaseStream.CanSeek)
                    {
                        Interlocked.Exchange(ref _bytesWritten, writer.BaseStream.Length);
                    }
                    RaiseStateChanged();
                }
            }
        }
        catch
        {
            // Background write failure should never crash the terminal session
        }
    }

    private static ISessionLogFormatter CreateFormatter(RecordingFormat format) => format switch
    {
        RecordingFormat.Timestamped => new TimestampedFormatter(),
        RecordingFormat.Html        => new HtmlFormatter(),
        _                           => new PlainTextFormatter()
    };

    private void RaiseStateChanged()
    {
        StateChanged?.Invoke(
            this,
            new RecordingStateChangedEventArgs(
                _isRecording,
                _currentLogPath,
                _isPaused,
                Interlocked.Read(ref _bytesWritten)));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}

/// <summary>
/// Default <see cref="ISessionRecorderFactory"/> creating <see cref="FileSessionRecorder"/> instances.
/// </summary>
public sealed class SessionRecorderFactory : ISessionRecorderFactory
{
    public ISessionRecorder CreateRecorder() => new FileSessionRecorder();
}
