using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Net.Sockets;
using System.Security.AccessControl;

namespace ProcessKeeper.Core;

internal static class LegacyCompat
{
    internal static int ProcessId { get { using var process = Process.GetCurrentProcess(); return process.Id; } }
    internal static bool IsWindows() => Environment.OSVersion.Platform == PlatformID.Win32NT;
    internal static void ThrowIfNull(object? value) { if (value is null) throw new ArgumentNullException(nameof(value)); }
    internal static byte[] HashData(byte[] bytes) { using var hash = SHA256.Create(); return hash.ComputeHash(bytes); }
    internal static byte[] HashData(Stream stream) { using var hash = SHA256.Create(); return hash.ComputeHash(stream); }
    internal static string ToHexString(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");
    internal static string ToHexString(ReadOnlySpan<byte> bytes) => ToHexString(bytes.ToArray());
    internal static bool IsAsciiLetter(char value) => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    internal static bool IsAsciiLetterOrDigit(char value) => IsAsciiLetter(value) || value is >= '0' and <= '9';
    internal static Task<string> ReadAllTextAsync(string path, CancellationToken token) => Task.Run(() => File.ReadAllText(path), token);
    internal static void AddArgument(ProcessStartInfo info, string argument)
    {
        if (argument is null || argument.IndexOf('\0') >= 0) throw new ArgumentException("Invalid process argument.");
        var quoted = new StringBuilder("\""); int backslashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\') { backslashes++; continue; }
            quoted.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes).Append(character); backslashes = 0;
        }
        quoted.Append('\\', backslashes * 2).Append('"');
        info.Arguments += (info.Arguments.Length == 0 ? "" : " ") + quoted;
    }
    internal static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
    internal static bool IsPathFullyQualified(string path) => !string.IsNullOrWhiteSpace(path) &&
        ((path.Length >= 3 && IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/')) || path.StartsWith(@"\\", StringComparison.Ordinal));
    internal static string GetRelativePath(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var normalizedPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetPathRoot(normalizedRoot), Path.GetPathRoot(normalizedPath), StringComparison.OrdinalIgnoreCase)) return normalizedPath;
        var rootParts = normalizedRoot.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        var pathParts = normalizedPath.Split(new[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
        int common = 0;
        while (common < rootParts.Length && common < pathParts.Length && rootParts[common].Equals(pathParts[common], StringComparison.OrdinalIgnoreCase)) common++;
        var parts = Enumerable.Repeat("..", rootParts.Length - common).Concat(pathParts.Skip(common)).ToArray();
        return parts.Length == 0 ? "." : string.Join(Path.DirectorySeparatorChar.ToString(), parts);
    }
    internal static void MoveFile(string source, string destination) => File.Move(source, destination);
    internal static void MoveFile(string source, string destination, bool overwrite)
    {
        if (!overwrite || !File.Exists(destination)) { File.Move(source, destination); return; }
        File.Replace(source, destination, null);
    }
}

internal static class LegacyExtensions
{
    internal static bool Contains(this string source, string value, StringComparison comparison) => source.IndexOf(value, comparison) >= 0;
    internal static bool Contains(this string source, char value) => source.IndexOf(value) >= 0;
    internal static bool StartsWith(this string source, char value) => source.Length > 0 && source[0] == value;
    internal static bool EndsWith(this string source, char value) => source.Length > 0 && source[source.Length - 1] == value;
    internal static bool Contains(this ReadOnlySpan<char> source, char value) => source.IndexOf(value) >= 0;
    internal static string[] Split(this string source, char separator, StringSplitOptions options) => source.Split(new[] { separator }, options);
    internal static string Replace(this string source, string oldValue, string newValue, StringComparison comparison)
    {
        if (oldValue.Length == 0) throw new ArgumentException(nameof(oldValue));
        var result = new StringBuilder(); int offset = 0, next;
        while ((next = source.IndexOf(oldValue, offset, comparison)) >= 0) { result.Append(source, offset, next - offset).Append(newValue); offset = next + oldValue.Length; }
        return result.Append(source, offset, source.Length - offset).ToString();
    }
    internal static long ToInt64(this nint value) => (long)value;
    internal static ulong ToUInt64(this nuint value) => (ulong)value;
    internal static FileStream Create(this FileInfo file, FileMode mode, FileSystemRights rights, FileShare share, int bufferSize, FileOptions options, FileSecurity security) =>
        new(file.FullName, mode, rights, share, bufferSize, options, security);
    internal static HashSet<T> ToHashSet<T>(this IEnumerable<T> values, IEqualityComparer<T>? comparer = null) => new(values, comparer);
    internal static IEnumerable<T> DistinctBy<T, TKey>(this IEnumerable<T> values, Func<T, TKey> key, IEqualityComparer<TKey>? comparer = null)
    { var seen = new HashSet<TKey>(comparer); foreach (var value in values) if (seen.Add(key(value))) yield return value; }
    internal static IOrderedEnumerable<T> Order<T>(this IEnumerable<T> values, IComparer<T>? comparer = null) => values.OrderBy(value => value, comparer);
    internal static IEnumerable<T> Append<T>(this IEnumerable<T> values, T value) { foreach (var item in values) yield return item; yield return value; }
    internal static bool TryAdd<TKey,TValue>(this IDictionary<TKey,TValue> values, TKey key, TValue value)
    { if (values.ContainsKey(key)) return false; values.Add(key, value); return true; }
    internal static TValue? GetValueOrDefault<TKey,TValue>(this IReadOnlyDictionary<TKey,TValue> values, TKey key) => values.TryGetValue(key, out var value) ? value : default;
    internal static TValue GetValueOrDefault<TKey,TValue>(this IReadOnlyDictionary<TKey,TValue> values, TKey key, TValue fallback) => values.TryGetValue(key, out var value) ? value : fallback;
    internal static void Deconstruct<TKey,TValue>(this KeyValuePair<TKey,TValue> pair, out TKey key, out TValue value) { key = pair.Key; value = pair.Value; }
    internal static void ReadExactly(this Stream stream, byte[] bytes)
    { for (int offset = 0; offset < bytes.Length;) { var count = stream.Read(bytes, offset, bytes.Length - offset); if (count == 0) throw new EndOfStreamException(); offset += count; } }
    internal static void Write(this Stream stream, byte[] bytes) => stream.Write(bytes, 0, bytes.Length);
    internal static Task<int> ReadAsync(this Stream stream, byte[] bytes, CancellationToken token) => stream.ReadAsync(bytes, 0, bytes.Length, token);
    internal static Task WriteAsync(this Stream stream, byte[] bytes, CancellationToken token) => stream.WriteAsync(bytes, 0, bytes.Length, token);
    internal static async Task ConnectAsync(this TcpClient client, System.Net.IPAddress address, int port, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var task = client.ConnectAsync(address, port);
        var cancellation = new TaskCompletionSource<bool>();
        using (token.Register(() => cancellation.TrySetCanceled()))
        {
            if (await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false) != task) token.ThrowIfCancellationRequested();
            await task.ConfigureAwait(false);
        }
    }
    internal static async Task<T> WaitAsync<T>(this Task<T> task, TimeSpan timeout, CancellationToken token = default)
    {
        using var delay = CancellationTokenSource.CreateLinkedTokenSource(token);
        var completed = await Task.WhenAny(task, Task.Delay(timeout, delay.Token)).ConfigureAwait(false);
        if (completed != task) { token.ThrowIfCancellationRequested(); throw new TimeoutException(); }
        delay.Cancel(); return await task.ConfigureAwait(false);
    }
}
