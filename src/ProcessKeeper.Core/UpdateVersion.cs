using System.Text.RegularExpressions;

namespace ProcessKeeper.Core;

/// <summary>Strict SemVer precedence, optionally prefixed by v. Release titles never participate.</summary>
public sealed class UpdateVersion : IComparable<UpdateVersion>
{
    private static readonly Regex Pattern = new(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private readonly string[] _numbers, _pre;
    public string Value { get; }
    private UpdateVersion(string value, string[] numbers, string[] pre) { Value = value; _numbers = numbers; _pre = pre; }
    public static bool TryParse(string? text, out UpdateVersion? version)
    {
        version = null;
        if (text is null || text.Length == 0 || text.Length > 128 || text != text.Trim()) return false;
        var value = text[0] is 'v' or 'V' ? text.Substring(1) : text;
        var match = Pattern.Match(value); if (!match.Success) return false;
        var pre = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : Array.Empty<string>();
        if (pre.Any(p => p.Length > 1 && p[0] == '0' && p.All(c => c >= '0' && c <= '9'))) return false;
        version = new UpdateVersion(value, new[] { match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value }, pre); return true;
    }
    public int CompareTo(UpdateVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < 3; i++) { var result = Numeric(_numbers[i], other._numbers[i]); if (result != 0) return result; }
        if (_pre.Length == 0 || other._pre.Length == 0) return _pre.Length == other._pre.Length ? 0 : _pre.Length == 0 ? 1 : -1;
        for (var i = 0; i < Math.Min(_pre.Length, other._pre.Length); i++)
        {
            var left = _pre[i]; var right = other._pre[i];
            var ln = left.All(c => c >= '0' && c <= '9'); var rn = right.All(c => c >= '0' && c <= '9');
            var result = ln && rn ? Numeric(left, right) : ln != rn ? ln ? -1 : 1 : string.CompareOrdinal(left, right);
            if (result != 0) return result;
        }
        return _pre.Length.CompareTo(other._pre.Length);
    }
    private static int Numeric(string left, string right) => left.Length == right.Length ? string.CompareOrdinal(left, right) : left.Length.CompareTo(right.Length);
    public override string ToString() => Value;
}
