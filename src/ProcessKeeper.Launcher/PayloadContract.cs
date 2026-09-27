using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProcessKeeper.Launcher;

internal sealed class PayloadManifest
{
    public int FormatVersion { get; set; }
    public string EntryPoint { get; set; } = "";
    public string ZipSha256 { get; set; } = "";
    public List<PayloadFile> Files { get; set; } = [];
}

internal sealed class PayloadFile
{
    public string Path { get; set; } = "";
    public long Length { get; set; }
    public string Sha256 { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PayloadManifest))]
internal sealed partial class PayloadJsonContext : JsonSerializerContext { }

internal sealed class PayloadContract
{
    public string Hash { get; }
    public string EntryPoint { get; }
    public IReadOnlyDictionary<string, PayloadFile> Files { get; }

    private PayloadContract(PayloadManifest manifest)
    {
        if (manifest.FormatVersion != 1 || manifest.Files is null || manifest.Files.Count is < 1 or > 50000)
            throw new InvalidDataException("The embedded file manifest has an invalid format.");
        ValidateHash(manifest.ZipSha256);
        Hash = manifest.ZipSha256.ToLowerInvariant();
        ValidateRelativePath(manifest.EntryPoint);
        if (!manifest.EntryPoint.Equals("ProcessKeeper.exe", StringComparison.Ordinal))
            throw new InvalidDataException("The embedded application entry point is invalid.");
        EntryPoint = manifest.EntryPoint;

        var files = new Dictionary<string, PayloadFile>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var item in manifest.Files)
        {
            if (item is null) throw new InvalidDataException("The embedded manifest contains an empty entry.");
            ValidateRelativePath(item.Path);
            ValidateHash(item.Sha256);
            if (item.Length < 0 || item.Length > 4L * 1024 * 1024 * 1024)
                throw new InvalidDataException("An embedded file has an invalid size.");
            total = checked(total + item.Length);
            if (total > 8L * 1024 * 1024 * 1024 || !files.TryAdd(item.Path, item))
                throw new InvalidDataException("The embedded manifest is too large or contains duplicate paths.");
        }
        if (!files.ContainsKey(EntryPoint)) throw new InvalidDataException("The embedded manifest is missing the main application.");
        foreach (var relative in files.Keys)
        {
            var offset = relative.LastIndexOf('/');
            while (offset >= 0)
            {
                if (files.ContainsKey(relative[..offset]))
                    throw new InvalidDataException("The embedded manifest contains a file/directory conflict.");
                offset = relative.LastIndexOf('/', offset - 1);
            }
        }
        Files = files;
    }

    public static PayloadContract Read(Stream stream)
    {
        if (!stream.CanSeek || stream.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("The embedded file manifest has an invalid size.");
        var manifest = JsonSerializer.Deserialize(stream, PayloadJsonContext.Default.PayloadManifest)
            ?? throw new InvalidDataException("The embedded file manifest could not be read.");
        return new PayloadContract(manifest);
    }

    public void VerifyArchiveHash(Stream payload)
    {
        if (!payload.CanSeek) throw new InvalidDataException("The embedded payload could not be read.");
        payload.Position = 0;
        var actual = Convert.ToHexString(SHA256.HashData(payload));
        payload.Position = 0;
        if (!actual.Equals(Hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The embedded payload failed SHA-256 verification. Download a complete ProcessKeeper.exe again.");
    }

    public string ResolvePath(string root, string relative)
    {
        ValidateRelativePath(relative);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("An embedded file path escapes the application cache.");
        return fullPath;
    }

    public static void ValidateRelativePath(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 1024 || relative.Contains('\\') ||
            relative.StartsWith('/') || Path.IsPathRooted(relative) || relative.IndexOfAny([':', '<', '>', '"', '|', '?', '*', '\0']) >= 0)
            throw new InvalidDataException("The embedded manifest contains an unsafe path.");
        foreach (var part in relative.Split('/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(char.IsControl)) throw new InvalidDataException("The embedded manifest contains an unsafe path segment.");
            var name = part.Split('.')[0].ToUpperInvariant();
            if (name is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" ||
                (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) && name[3] is >= '1' and <= '9'))
                throw new InvalidDataException("The embedded manifest contains a Windows reserved name.");
        }
    }

    private static void ValidateHash(string? value)
    {
        if (value is null || value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new InvalidDataException("The embedded manifest contains an invalid SHA-256 value.");
    }
}
