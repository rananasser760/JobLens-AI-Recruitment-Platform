using GP_Backend.Models.Entities;

namespace GP_Backend.Services.Interfaces;

public interface IAuditService
{
    Task LogAsync(long? userId, string action, string? entity = null, long? entityId = null, string? oldValues = null, string? newValues = null, string? ipAddress = null, string? userAgent = null);
    Task<List<AuditLog>> GetUserLogsAsync(long userId, int limit = 100);
    Task<List<AuditLog>> GetEntityLogsAsync(string entity, long entityId, int limit = 100);
}
