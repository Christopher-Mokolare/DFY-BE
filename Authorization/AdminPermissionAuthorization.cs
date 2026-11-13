using System.Security.Claims;
using DoForYou.API.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.API.Authorization;

public static class AdminPermissionPolicies
{
    public const string UsersManage = "users.manage";
    public const string KycReview = "kyc.review";
    public const string KycViewDocuments = "kyc.view_documents";
    public const string KycOverride = "kyc.override";
    public const string DisputesManage = "disputes.manage";
    public const string TasksManage = "tasks.manage";
    public const string FinanceManage = "finance.manage";
    public const string ContentManage = "content.manage";
    public const string ConfigurationManage = "configuration.manage";
    public const string AuditRead = "audit.read";

    public static readonly string[] All =
    [
        UsersManage, KycReview, KycViewDocuments, KycOverride, DisputesManage,
        TasksManage, FinanceManage, ContentManage, ConfigurationManage, AuditRead
    ];

    public static bool IsKnown(string? permissionKey) =>
        !string.IsNullOrWhiteSpace(permissionKey) &&
        All.Contains(permissionKey.Trim(), StringComparer.Ordinal);
}

public sealed class AdminPermissionRequirement(string permissionKey) : IAuthorizationRequirement
{
    public string PermissionKey { get; } = permissionKey;
}

public sealed class AdminPermissionAuthorizationHandler(AppDbContext context)
    : AuthorizationHandler<AdminPermissionRequirement>
{
    private static readonly TimeSpan StepUpWindow = TimeSpan.FromMinutes(15);

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext authorizationContext,
        AdminPermissionRequirement requirement)
    {
        var rawUserId = authorizationContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? authorizationContext.User.FindFirstValue("sub");
        if (!int.TryParse(rawUserId, out var userId))
            return;

        var now = DateTime.UtcNow;
        var account = await context.Users.AsNoTracking()
            .Where(user => user.Id == userId && !user.IsArchived && !user.IsSuspended &&
                user.EmailVerified && user.AuthenticatorTwoFactorEnabled &&
                user.LastTwoFactorVerifiedAt.HasValue &&
                user.LastTwoFactorVerifiedAt.Value >= now.Subtract(StepUpWindow))
            .Select(user => user.Roles)
            .FirstOrDefaultAsync();

        if (account == null)
            return;

        var roles = account.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (roles.Contains("SuperAdmin", StringComparer.OrdinalIgnoreCase))
        {
            authorizationContext.Succeed(requirement);
            return;
        }

        if (!roles.Contains("Admin", StringComparer.OrdinalIgnoreCase))
            return;

        var hasGrant = await context.AdminPermissionGrants.AsNoTracking().AnyAsync(grant =>
            grant.UserId == userId &&
            grant.PermissionKey == requirement.PermissionKey &&
            grant.RevokedAt == null &&
            (grant.ExpiresAt == null || grant.ExpiresAt > now));

        if (hasGrant)
            authorizationContext.Succeed(requirement);
    }
}
