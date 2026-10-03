using System.Globalization;
using System.Text;

namespace ProcessKeeper.Core;

/// <summary>A bounded view of an append-only log. Missing legacy time precision is never invented.</summary>
public sealed class ActivityLogBuffer
{
    public const int DefaultMaximumEntries = 400;
    public const int DefaultMaximumCharacters = 100_000;
    public const int MaximumReadBytes = 1_048_576;
    public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff zzz";
    private const int MaximumEntryCharacters = 12_000;
    private readonly int _maximumEntries;
    private readonly int _maximumCharacters;
    private readonly List<Entry> _entries = [];
    private long _sequence;

    public ActivityLogBuffer(int maximumEntries = DefaultMaximumEntries, int maximumCharacters = DefaultMaximumCharacters)
    {
        if (maximumEntries < 1) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        if (maximumCharacters < 256) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        _maximumEntries = maximumEntries;
        _maximumCharacters = maximumCharacters;
    }

    public int Count => _entries.Count;

    public void LoadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var start = Math.Max(0, length - MaximumReadBytes);
        stream.Position = start;
        var bytes = new byte[(int)(length - start)];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = stream.Read(bytes, read, bytes.Length - read);
            if (count == 0) break;
            read += count;
        }
        var text = Encoding.UTF8.GetString(bytes, 0, read);
        if (start > 0)
        {
            // Drop the partially read line and any continuation of the event before the tail.
            var end = text.IndexOf('\n');
            text = end < 0 ? "" : text[(end + 1)..];
        }
        Load(text, skipLeadingContinuation: start > 0);
    }

    public void Load(string text, bool skipLeadingContinuation = false)
    {
        _entries.Clear();
        _sequence = 0;
        StringBuilder? current = null;
        DateTimeOffset? timestamp = null;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var header = TryReadHeader(line, out var parsed);
            if (header)
            {
                Commit();
                current = new StringBuilder();
                timestamp = parsed;
            }
            else if (current is null)
            {
                if (skipLeadingContinuation) continue;
                current = new StringBuilder();
            }
            if (current.Length > 0) current.Append(Environment.NewLine);
            current.Append(line);
        }
        Commit();
        Trim();

        void Commit()
        {
            if (current is not null && current.Length > 0)
                _entries.Add(new Entry(timestamp, _sequence++, BoundEntry(current.ToString())));
        }
    }

    /// <returns>The complete serialized event to append to disk, without truncating its message.</returns>
    public string Append(string message, DateTimeOffset timestamp)
    {
        timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timestamp.ToUnixTimeMilliseconds()).ToOffset(timestamp.Offset);
        // Indentation makes timestamp-like lines inside a message unambiguous on the next load.
        var normalized = message.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var serialized = timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture) + "  " +
            normalized.Replace("\n", Environment.NewLine + "    ", StringComparison.Ordinal);
        _entries.Add(new Entry(timestamp, _sequence++, BoundEntry(serialized)));
        Trim();
        return serialized;
    }

    public string Render()
    {
        if (_entries.Count == 0) return "";
        var result = new StringBuilder();
        var previousLegacy = _entries[0].Timestamp is null;
        foreach (var entry in _entries)
        {
            if (previousLegacy && entry.Timestamp is not null) result.AppendLine();
            if (result.Length > 0 && result[^1] != '\n') result.AppendLine();
            result.Append(DisplayText(entry));
            previousLegacy = entry.Timestamp is null;
        }
        return result.ToString();
    }

    private string BoundEntry(string text)
    {
        var limit = Math.Min(MaximumEntryCharacters, _maximumCharacters - 128);
        if (text.Length <= limit) return text;
        return text[..(limit - 64)] + Environment.NewLine + L.T("[界面已截短 | 完整内容保留在日志文件中]");
    }

    private void Trim()
    {
        _entries.Sort((left, right) =>
        {
            var byTime = Nullable.Compare(left.Timestamp, right.Timestamp);
            return byTime != 0 ? byTime : left.Sequence.CompareTo(right.Sequence);
        });
        // Reserve enough space for line separators and the legacy/current format separation.
        var characters = _entries.Sum(entry => DisplayText(entry).Length + Environment.NewLine.Length) + 128;
        while (_entries.Count > _maximumEntries || characters > _maximumCharacters)
        {
            characters -= DisplayText(_entries[0]).Length + Environment.NewLine.Length;
            _entries.RemoveAt(0);
        }
    }

    private static string DisplayText(Entry entry) => entry.Timestamp is { } timestamp
        ? TimeDisplay.Format(timestamp, includeMilliseconds: true) + entry.Text.Substring(30)
        : entry.Text;

    private static bool TryReadHeader(string line, out DateTimeOffset? timestamp)
    {
        timestamp = null;
        if (line.Length >= 32 && line.AsSpan(30, 2).SequenceEqual("  ") &&
            DateTimeOffset.TryParseExact(line.AsSpan(0, 30), TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var precise))
        {
            timestamp = precise;
            return true;
        }
        // The previous app format has neither year nor milliseconds. Do not infer them from today's date.
        return line.Length >= 16 && line.AsSpan(14, 2).SequenceEqual("  ") &&
            DateTime.TryParseExact("2000-" + line[..14], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _);
    }

    private sealed record Entry(DateTimeOffset? Timestamp, long Sequence, string Text);
}
