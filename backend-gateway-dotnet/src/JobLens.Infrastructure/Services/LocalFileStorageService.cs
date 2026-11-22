using JobLens.Application.Interfaces;
using JobLens.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace JobLens.Infrastructure.Services;

public sealed class LocalFileStorageService(IOptions<StorageOptions> options) : IFileStorageService
{
    private readonly StorageOptions _options = options.Value;

    private string Root => Path.GetFullPath(_options.RootPath);

    public async Task<string> SaveAsync(string fileName, byte[] content, CancellationToken cancellationToken)
    {
        var root = Root;
        Directory.CreateDirectory(root);

        var originalName = Path.GetFileName(fileName.Replace('\\', '/'));
        var safeName = $"{Guid.NewGuid():N}_{SanitizeFileName(originalName)}";
        var absolutePath = Path.Combine(root, safeName);

        await File.WriteAllBytesAsync(absolutePath, content, cancellationToken);
        return absolutePath;
    }

    public async Task<byte[]?> ReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        var path = ResolveInsideRoot(storageKey);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    public Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var path = ResolveInsideRoot(storageKey);
        if (path is null || !File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    private string? ResolveInsideRoot(string storageKey)
    {
        if (string.IsNullOrWhiteSpace(storageKey))
        {
            return null;
        }

        var root = Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        string candidate;
        try
        {
            candidate = Path.IsPathRooted(storageKey)
                ? Path.GetFullPath(storageKey)
                : Path.GetFullPath(Path.Combine(root, storageKey));
        }
        catch
        {
            return null;
        }

        if (!candidate.StartsWith(root, StringComparison.Ordinal))
        {
            var legacyName = Path.GetFileName(storageKey.Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(legacyName))
            {
                return null;
            }

            candidate = Path.GetFullPath(Path.Combine(root, legacyName));
            if (!candidate.StartsWith(root, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return candidate;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch).ToArray()).Trim();
        if (cleaned.Length == 0)
        {
            cleaned = "file";
        }

        return cleaned.Length > 120 ? cleaned[^120..] : cleaned;
    }
}
