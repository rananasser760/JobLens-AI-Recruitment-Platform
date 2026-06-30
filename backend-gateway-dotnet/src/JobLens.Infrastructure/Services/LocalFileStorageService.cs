using JobLens.Application.Interfaces;
using JobLens.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace JobLens.Infrastructure.Services;

public sealed class LocalFileStorageService(IOptions<StorageOptions> options) : IFileStorageService
{
    private readonly StorageOptions _options = options.Value;

    public async Task<string> SaveAsync(string fileName, byte[] content, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(_options.RootPath);
        Directory.CreateDirectory(root);

        var safeName = $"{Guid.NewGuid():N}_{Path.GetFileName(fileName)}";
        var absolutePath = Path.Combine(root, safeName);

        await File.WriteAllBytesAsync(absolutePath, content, cancellationToken);
        return absolutePath;
    }

    public async Task<byte[]?> ReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(storageKey) || !File.Exists(storageKey))
        {
            return null;
        }

        return await File.ReadAllBytesAsync(storageKey, cancellationToken);
    }

    public Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (string.IsNullOrWhiteSpace(storageKey) || !File.Exists(storageKey))
        {
            return Task.FromResult(false);
        }

        File.Delete(storageKey);
        return Task.FromResult(true);
    }
}
