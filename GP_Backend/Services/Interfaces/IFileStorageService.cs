namespace GP_Backend.Services.Interfaces;

public interface IFileStorageService
{
    Task<string> SaveFileAsync(Stream fileStream, string fileName, string folder);
    Task<bool> DeleteFileAsync(string filePath);
    Task<Stream?> GetFileAsync(string filePath);
    Task<bool> FileExistsAsync(string filePath);
    string GetContentType(string fileName);
}
