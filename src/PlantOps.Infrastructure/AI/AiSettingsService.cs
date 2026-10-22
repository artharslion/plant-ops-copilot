using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlantOps.Application.AI;
using PlantOps.Application.Assets;
using PlantOps.Domain.AI;
using PlantOps.Domain.Identity;
using PlantOps.Infrastructure.Persistence;

namespace PlantOps.Infrastructure.AI;

public sealed class AiSettingsService(PlantOpsDbContext db, IAiRunConfiguration profile) : IAiSettingsService
{
    private async Task AdminAsync(Actor actor, CancellationToken ct)
    {
        if (!await (from user in db.Users
                    join grant in db.UserRoles on user.Id equals grant.UserId
                    join role in db.Roles on grant.RoleId equals role.Id
                    where user.Id == actor.UserId && user.IsEnabled && role.Name == "Admin"
                    select user.Id).AnyAsync(ct))
        {
            throw new DirectoryException(403, "仅管理员可查看或修改 AI 设置。");
        }
    }

    private async Task<AiSettingsDto> ReadAsync(CancellationToken ct)
    {
        var setting = await db.AiRuntimeSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        return new(setting?.Provider ?? profile.DefaultProvider, profile.RealModel, profile.RealConfigured, setting?.Version ?? 0);
    }

    public async Task<AiSettingsDto> GetAsync(Actor actor, CancellationToken ct = default)
    {
        await AdminAsync(actor, ct);
        return await ReadAsync(ct);
    }

    public async Task<AiModelSelection> SelectionAsync(CancellationToken ct = default)
    {
        var settings = await ReadAsync(ct);
        return new(settings.Provider, settings.Provider == "Fake" ? "fake" : settings.RealModel);
    }

    public async Task<AiSettingsDto> SaveAsync(Actor actor, AiSettingsInput input, CancellationToken ct = default)
    {
        await AdminAsync(actor, ct);
        if (input.Provider is not ("Fake" or "OpenAiCompatible") || input.Version < 0)
        {
            throw new DirectoryException(400, "请选择模拟 AI 或真实 LLM。");
        }
        if (input.Provider == "OpenAiCompatible" && !profile.RealConfigured)
        {
            throw new DirectoryException(409, "服务端尚未配置可用的真实模型连接信息。");
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7300110)", ct);
        await AdminAsync(actor, ct);
        var row = await db.AiRuntimeSettings.SingleOrDefaultAsync(ct);
        if ((row?.Version ?? 0) != input.Version)
        {
            throw new DirectoryException(409, "AI 设置已变化，请刷新后重新选择。");
        }
        var before = row == null ? null : JsonSerializer.Serialize(new
        {
            row.Provider,
            row.Version
        });
        if (row == null)
        {
            row = new AiRuntimeSetting();
            db.AiRuntimeSettings.Add(row);
        }
        else
        {
            row.Version++;
        }
        row.Provider = input.Provider;
        db.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actor.UserId,
            ResourceId = Guid.Empty,
            ResourceType = "AiSettings",
            Action = "Update",
            Before = before,
            After = JsonSerializer.Serialize(new { row.Provider, row.Version }),
            OccurredAt = DateTimeOffset.UtcNow,
            CorrelationId = actor.CorrelationId
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await ReadAsync(ct);
    }
}
