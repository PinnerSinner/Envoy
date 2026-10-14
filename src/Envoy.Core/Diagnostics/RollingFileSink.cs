using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Envoy.Core.Diagnostics;

/// <summary>
/// Writes <c>{folder}\envoy.log</c> and rolls it to <c>envoy.1.log</c> … <c>envoy.{maxFiles-1}.log</c> when the next
/// line would pass <c>maxBytes</c> (REQ-REL-2). It touches only names matching <c>^envoy(\.\d+)?\.log$</c>, so chat
/// transcripts in the same folder are safe. On an I/O failure it throws to the hub (which counts the drop) and does
/// not try to reopen for <see cref="RetryDelay"/>. When only the roll fails (an old file held open by a viewer), it
/// keeps appending to envoy.log past the cap, up to <see cref="HardCapFactor"/> × <c>maxBytes</c>, and tries the roll
/// again after <see cref="RetryDelay"/>, so logging degrades instead of stopping.
/// </summary>
public sealed partial class RollingFileSink : ILogSink, IDisposable
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>While rolling fails, envoy.log may grow to this many times the cap before writes are dropped.</summary>
    public const int HardCapFactor = 2;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly string _folder;
    private readonly string _baseName;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly TimeProvider _clock;
    private StreamWriter? _writer;
    private long _size;
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;
    private DateTimeOffset _rollRetryAfter = DateTimeOffset.MinValue;
    private bool _disposed;

    public RollingFileSink(string folder, string baseName = "envoy", long maxBytes = 1_048_576, int maxFiles = 5,
                           TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFiles, 1);
        _folder = folder;
        _baseName = baseName;
        _maxBytes = maxBytes;
        _maxFiles = maxFiles;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary><c>{folder}\{baseName}.log</c>, the file being written.</summary>
    public string CurrentPath => Path.Combine(_folder, _baseName + ".log");

    public string Folder => _folder;

    /// <summary>The existing log files, newest first: envoy.log, envoy.1.log, ….</summary>
    public IReadOnlyList<string> ExistingFiles()
    {
        var files = new List<string>();
        for (var i = 0; i < _maxFiles; i++)
        {
            var path = PathFor(i);
            if (File.Exists(path)) files.Add(path);
        }
        return files;
    }

    /// <summary>True for <c>envoy.log</c> and <c>envoy.N.log</c> (the only names this sink ever touches).</summary>
    public static bool IsLogFileName(string fileName) => LogFileName().IsMatch(fileName);

    public void Write(LogEntry entry)
    {
        var text = LogFormatter.Format(entry) + Environment.NewLine;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _clock.GetUtcNow();
            if (now < _retryAfter) throw new IOException("The log file is unavailable; retrying later.");
            try
            {
                var bytes = Utf8NoBom.GetByteCount(text);
                EnsureOpen();
                if (_size > 0 && _size + bytes > _maxBytes && now >= _rollRetryAfter)
                {
                    TryRoll(now);
                    EnsureOpen();
                }
                if (_size > 0 && _size + bytes > _maxBytes * HardCapFactor)
                    throw new IOException("The log file is over its size cap and could not be rolled.");
                _writer!.Write(text);
                _size += bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Close();
                _retryAfter = _clock.GetUtcNow() + RetryDelay;
                throw;
            }
        }
    }

    public void Flush()
    {
        lock (_gate) _writer?.Flush();
    }

    /// <summary>Closes the file (the next write reopens it). Used before the log folder is deleted.</summary>
    public void Close()
    {
        lock (_gate)
        {
            try
            {
                _writer?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The final flush failed; the hub already counted the failing write.
            }
            _writer = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Close();
            _disposed = true;
        }
    }

    private string PathFor(int index) =>
        Path.Combine(_folder, index == 0 ? _baseName + ".log" : _baseName + "." + index.ToString(CultureInfo.InvariantCulture) + ".log");

    private void EnsureOpen()
    {
        if (_writer is not null) return;
        Directory.CreateDirectory(_folder);
        var stream = new FileStream(CurrentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _size = stream.Length;
        _writer = new StreamWriter(stream, Utf8NoBom) { AutoFlush = true };
    }

    /// <summary>Rolls; on failure leaves envoy.log in place (it is reopened for append) and waits before trying again.</summary>
    private void TryRoll(DateTimeOffset now)
    {
        try
        {
            Roll();
            _rollRetryAfter = DateTimeOffset.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _rollRetryAfter = now + RetryDelay;
        }
    }

    private void Roll()
    {
        Close();
        var oldest = PathFor(_maxFiles - 1);
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var i = _maxFiles - 2; i >= 1; i--)
        {
            var from = PathFor(i);
            if (File.Exists(from)) File.Move(from, PathFor(i + 1), overwrite: true);
        }
        if (_maxFiles > 1 && File.Exists(CurrentPath)) File.Move(CurrentPath, PathFor(1), overwrite: true);
        else if (File.Exists(CurrentPath)) File.Delete(CurrentPath);
        _size = 0;
    }

    [GeneratedRegex(@"^envoy(\.\d+)?\.log$", RegexOptions.IgnoreCase)]
    private static partial Regex LogFileName();
}
