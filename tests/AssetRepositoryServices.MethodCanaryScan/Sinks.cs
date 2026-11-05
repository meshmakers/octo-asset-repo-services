using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Backend.AssetRepositoryServices.MethodCanaryScan;

/// <summary>One piece of text the scan searches: where it came from and what it says.</summary>
internal sealed record ScanText(string Sink, string Source, string Text);

/// <summary>
///     The bytes appended to a set of files while the scan ran (service log files of <c>Start-Octo</c>, the file exporter of a
///     local OpenTelemetry collector). The offsets are taken before the first call; only what was written afterwards is read.
/// </summary>
internal sealed class FileWindow
{
    private readonly string _sink;
    private readonly DirectoryInfo _directory;
    private readonly string _pattern;
    private readonly Dictionary<string, long> _offsets = new(StringComparer.Ordinal);

    public FileWindow(string sink, DirectoryInfo directory, string pattern)
    {
        _sink = sink;
        _directory = directory;
        _pattern = pattern;
        foreach (var file in Files())
        {
            _offsets[file.FullName] = file.Length;
        }
    }

    /// <summary>Files that exist at the start of the scan.</summary>
    public int FileCount => _offsets.Count;

    public IReadOnlyList<ScanText> ReadAppended(out long bytes)
    {
        var result = new List<ScanText>();
        bytes = 0;
        foreach (var file in Files())
        {
            var start = _offsets.TryGetValue(file.FullName, out var offset) ? offset : 0;
            if (file.Length < start)
            {
                // the file was truncated or replaced during the scan (log rotation, service restart): read all of it
                start = 0;
            }

            if (file.Length == start)
            {
                continue;
            }

            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            bytes += text.Length;
            result.Add(new ScanText(_sink, file.Name, text));
        }

        return result;
    }

    private IEnumerable<FileInfo> Files()
    {
        _directory.Refresh();
        return _directory.Exists ? _directory.EnumerateFiles(_pattern, SearchOption.TopDirectoryOnly) : [];
    }
}

/// <summary>
///     Everything the scan's own handler host writes: every log line (message, exception with stack trace, scopes) and every
///     activity of every source, with all tags. The handler host stands in for "the handling service" of the asset-repo method.
/// </summary>
internal sealed class CaptureSink : ILoggerProvider
{
    private readonly ConcurrentQueue<ScanText> _texts = new();
    private readonly string _logFile;

    public CaptureSink(string logFile)
    {
        _logFile = logFile;
        File.WriteAllText(_logFile, string.Empty);
    }

    public IReadOnlyList<ScanText> Texts => _texts.ToList();

    public string LogFile => _logFile;

    public void Add(string sink, string source, string text)
    {
        _texts.Enqueue(new ScanText(sink, source, text));
        if (sink == "handler-host-log")
        {
            lock (_texts)
            {
                File.AppendAllText(_logFile, text + Environment.NewLine);
            }
        }
    }

    public ActivityListener CreateActivityListener() => new()
    {
        ShouldListenTo = _ => true,
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        ActivityStopped = activity => Add("handler-host-activity", activity.Source.Name, Describe(activity))
    };

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);

    public void Dispose()
    {
    }

    private static string Describe(Activity activity)
    {
        var builder = new StringBuilder();
        builder.Append(activity.OperationName).Append('|').Append(activity.DisplayName).Append('|').Append(activity.StatusDescription);
        foreach (var tag in activity.TagObjects)
        {
            builder.Append('|').Append(tag.Key).Append('=').Append(tag.Value);
        }

        foreach (var activityEvent in activity.Events)
        {
            builder.Append("|event ").Append(activityEvent.Name);
            foreach (var tag in activityEvent.Tags)
            {
                builder.Append('|').Append(tag.Key).Append('=').Append(tag.Value);
            }
        }

        foreach (var baggage in activity.Baggage)
        {
            builder.Append("|baggage ").Append(baggage.Key).Append('=').Append(baggage.Value);
        }

        return builder.ToString();
    }

    private sealed class CaptureLogger(CaptureSink sink, string category) : ILogger
    {
        private static readonly AsyncLocal<List<object?>?> Scopes = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            var list = Scopes.Value ??= [];
            list.Add(state);
            return new Pop(list);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var builder = new StringBuilder();
            builder.Append(DateTime.UtcNow.ToString("O")).Append(' ').Append(logLevel).Append(' ').Append(category).Append(": ")
                .Append(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var value in values)
                {
                    builder.Append(" |").Append(value.Key).Append('=').Append(value.Value);
                }
            }

            foreach (var scope in Scopes.Value ?? [])
            {
                builder.Append(" |scope ").Append(scope is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? string.Join(",", pairs.Select(p => $"{p.Key}={p.Value}"))
                    : scope);
            }

            if (exception != null)
            {
                builder.AppendLine().Append(exception);
            }

            sink.Add("handler-host-log", category, builder.ToString());
        }

        private sealed class Pop(List<object?> list) : IDisposable
        {
            public void Dispose()
            {
                if (list.Count > 0)
                {
                    list.RemoveAt(list.Count - 1);
                }
            }
        }
    }
}
