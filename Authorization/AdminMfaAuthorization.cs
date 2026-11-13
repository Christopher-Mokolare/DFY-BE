using System.Security.Claims;
using DoForYou.API.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DoForYou.API.Authorization;

public sealed class AdminMfaRequirement : IAuthorizationRequirement;

public sealed class AdminMfaAuthorizationHandler(AppDbContext context)
    : AuthorizationHandler<AdminMfaRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext authorizationContext,
        AdminMfaRequirement requirement)
    {
        var rawUserId = authorizationContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? authorizationContext.User.FindFirstValue("sub");
        if (!int.TryParse(rawUserId, out var userId))
            return;

        var account = await context.Users.AsNoTracking()
            .Where(user => user.Id == userId && !user.IsArchived)
            .Select(user => new
            {
                user.Roles,
                user.EmailVerified,
                user.AuthenticatorTwoFactorEnabled
            })
            .FirstOrDefaultAsync();

        var hasAdminRole = account?.Roles?.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(role => string.Equals(role.Trim(), "Admin", StringComparison.OrdinalIgnoreCase)) == true;
        if (hasAdminRole && account?.EmailVerified == true && account.AuthenticatorTwoFactorEnabled)
            authorizationContext.Succeed(requirement);
    }
}
