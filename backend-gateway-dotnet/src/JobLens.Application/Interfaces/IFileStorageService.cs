namespace JobLens.Application.Interfaces;

public interface IFileStorageService
{
    Task<string> SaveAsync(string fileName, byte[] content, CancellationToken cancellationToken);
    Task<byte[]?> ReadAsync(string storageKey, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string storageKey, CancellationToken cancellationToken);
}
