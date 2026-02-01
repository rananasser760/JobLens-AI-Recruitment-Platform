using GP_Backend.Data;
using GP_Backend.Models.Entities;
using GP_Backend.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace GP_Backend.Services.Implementations;

public class AuditService : IAuditService
{
    private readonly AppDbContext _context;
    private readonly ILogger<AuditService> _logger;

    public AuditService(AppDbContext context, ILogger<AuditService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task LogAsync(long? userId, string action, string? entity = null, long? entityId = null, 
        string? oldValues = null, string? newValues = null, string? ipAddress = null, string? userAgent = null)
    {
        try
        {
            var auditLog = new AuditLog
            {
                UserId = userId,
                Action = action,
                Entity = entity,
                EntityId = entityId,
                OldValues = oldValues,
                NewValues = newValues,
                IpAddress = ipAddress,
                UserAgent = userAgent,
                CreatedAt = DateTime.UtcNow
            };

            _context.AuditLogs.Add(auditLog);
            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error logging audit entry");
        }
    }

    public async Task<List<AuditLog>> GetUserLogsAsync(long userId, int limit = 100)
    {
        return await _context.AuditLogs
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.CreatedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<AuditLog>> GetEntityLogsAsync(string entity, long entityId, int limit = 100)
    {
        return await _context.AuditLogs
            .Where(a => a.Entity == entity && a.EntityId == entityId)
            .OrderByDescending(a => a.CreatedAt)
            .Take(limit)
            .ToListAsync();
    }
}
