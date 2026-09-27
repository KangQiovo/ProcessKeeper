namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
namespace ProcessKeeper.App
{
    internal static class LegacyExtensions
    {
        internal static void Write(this Stream stream, byte[] bytes) => stream.Write(bytes, 0, bytes.Length);
        internal static bool Contains(this string text, string query, StringComparison comparison) => text.IndexOf(query, comparison) >= 0;
        internal static HashSet<T> ToHashSet<T>(this IEnumerable<T> values) => new(values);
        internal static HashSet<T> ToHashSet<T>(this IEnumerable<T> values, IEqualityComparer<T> comparer) => new(values, comparer);
    }
}
