using ProcessKeeper.Core;
using System.Text.Json;

namespace ProcessKeeper.App;

/// <summary>First-run state belongs to this user and is independent of shared settings.</summary>
internal sealed class OnboardingStore
{
    private const int MaximumFileBytes = 4096;
    internal string FilePath { get; }

    internal OnboardingStore(string? directory = null)
    {
        directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProcessKeeper");
        FilePath = Path.Combine(Path.GetFullPath(directory), "onboarding.json");
    }

    internal bool IsCompleted(out string? warning)
    {
        warning = null;
        try
        {
            using var stream = File.OpenRead(FilePath);
            if (stream.Length > MaximumFileBytes) throw new InvalidDataException();
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (!names.Add(property.Name) || property.Name is not ("Version" or "Completed"))
                    throw new InvalidDataException();
            if (!root.TryGetProperty("Version", out var version) || !version.TryGetInt32(out var value) || value != 1 ||
                !root.TryGetProperty("Completed", out var completed) ||
                completed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException();
            return completed.GetBoolean();
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            warning = L.T("无法读取引导记录，将重新显示介绍。");
            return false;
        }
    }

    internal void Complete()
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".onboarding-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new { Version = 1, Completed = true });
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null, ignoreMetadataErrors: false);
            else File.Move(temporary, FilePath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

internal sealed class OnboardingProgress
{
    internal const int PageCount = 5;
    internal int PageIndex { get; private set; }
    internal bool CanGoBack => PageIndex > 0 && PageIndex < PageCount - 1;
    internal bool IsLastPage => PageIndex == PageCount - 1;
    internal void Back() { if (CanGoBack) PageIndex--; }
    internal void Next() { if (!IsLastPage) PageIndex++; }
}
