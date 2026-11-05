using System.Text;
using System.Text.Json;
using PlainCommander.Core.Operations;

namespace PlainCommander.Core.Elevation;

/// <summary>What crosses the pipe between the app and its elevated helper. One JSON object per line.</summary>
internal enum MessageType
{
    // app → helper
    Run,
    Answer,
    Pause,
    Resume,
    Cancel,

    // helper → app
    Progress,
    Conflict,
    Error,
    Done,
}

/// <param name="Id">The job; for <see cref="MessageType.Answer"/> the question.</param>
/// <param name="Question">Conflict/Error: the number the answer must carry.</param>
/// <param name="Choice">Answer: a <see cref="ConflictChoice"/> or <see cref="ErrorChoice"/> as a number.</param>
/// <param name="Value">Done of a command: the path it created or renamed to.</param>
/// <param name="Paused">Run: the app's dialog is paused already, so the job must not do any work before a Resume.</param>
/// <param name="Failure">Error: what went wrong at <see cref="FailureInfo.Path"/>; Done of a command: why it failed.</param>
internal sealed record ElevationMessage(
    MessageType Type,
    int Id,
    ElevatedJob? Job = null,
    OperationProgress? Progress = null,
    ConflictInfo? Conflict = null,
    FailureInfo? Failure = null,
    int Question = 0,
    int Choice = 0,
    OperationResult? Result = null,
    string? Value = null,
    bool Paused = false);

/// <summary>An exception flattened for the pipe and rebuilt on the other side, close enough for the user's message.</summary>
internal sealed record FailureInfo(string Path, string Kind, string Message, int HResult, RuleViolation Violation = default, string? RulePath = null)
{
    public static FailureInfo From(string path, Exception exception) => exception switch
    {
        OperationRuleException rule => new(path, "rule", rule.Message, rule.HResult, rule.Violation, rule.Path),
        UnauthorizedAccessException => new(path, "access", exception.Message, exception.HResult),
        FileNotFoundException => new(path, "file", exception.Message, exception.HResult),
        DirectoryNotFoundException => new(path, "dir", exception.Message, exception.HResult),
        ArgumentException => new(path, "argument", exception.Message, exception.HResult),
        _ => new(path, "io", exception.Message, exception.HResult),
    };

    public Exception ToException() => Kind switch
    {
        "rule" => new OperationRuleException(Violation, RulePath ?? Path),
        "access" => new UnauthorizedAccessException(Message),
        "file" => new FileNotFoundException(Message),
        "dir" => new DirectoryNotFoundException(Message),
        "argument" => new ArgumentException(Message),
        _ => new IOException(Message, HResult),
    };
}

/// <summary>Line-delimited JSON over a duplex stream. Sends are serialized; reads come from one loop.</summary>
internal sealed class ElevationChannel : IAsyncDisposable
{
    /// <summary>A message longer than this is refused: nothing legitimate comes near it.</summary>
    private const int MaxLine = 4 << 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly SemaphoreSlim _send = new(1, 1);

    public ElevationChannel(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 14, leaveOpen: true);
    }

    public async Task SendAsync(ElevationMessage message, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _send.Release();
        }
    }

    /// <summary>The next message; null when the other side closed the pipe.</summary>
    public async Task<ElevationMessage?> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            if (line.Length > MaxLine)
            {
                throw new InvalidDataException("Message too long.");
            }

            if (line.Length == 0)
            {
                continue;
            }

            return JsonSerializer.Deserialize<ElevationMessage>(line, Json) ?? throw new InvalidDataException("Empty message.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _reader.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
        _send.Dispose();
    }
}
