using System.Security.Claims;
using DoForYou.API.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.API.Authorization;

public sealed class SuperAdminMfaRequirement : IAuthorizationRequirement { }

public sealed class SuperAdminMfaAuthorizationHandler(AppDbContext context)
    : AuthorizationHandler<SuperAdminMfaRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext authorizationContext,
        SuperAdminMfaRequirement requirement)
    {
        var rawUserId = authorizationContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? authorizationContext.User.FindFirstValue("sub");
        if (!int.TryParse(rawUserId, out var userId))
            return;

        var account = await context.Users.AsNoTracking()
            .Where(user => user.Id == userId && !user.IsArchived && !user.IsSuspended)
            .Select(user => new
            {
                user.Roles,
                user.EmailVerified,
                user.AuthenticatorTwoFactorEnabled
            })
            .FirstOrDefaultAsync();

        var hasSuperAdminRole = account?.Roles?.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(role => string.Equals(role.Trim(), "SuperAdmin", StringComparison.OrdinalIgnoreCase)) == true;
        if (hasSuperAdminRole && account?.EmailVerified == true && account.AuthenticatorTwoFactorEnabled)
            authorizationContext.Succeed(requirement);
    }
}
