using System.ComponentModel.DataAnnotations;
using System.Reflection;
using DoForYou.API.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoForYou.Tests;

public sealed class IdentityNotificationOperationsControllerTests
{
    [Fact]
    public void Dead_letter_operations_require_authenticated_admin_role()
    {
        var authorization = typeof(IdentityNotificationOperationsController)
            .GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorization);
        Assert.Equal("Admin", authorization!.Roles);
    }

    [Theory]
    [InlineData("")]
    [InlineData("too short")]
    [InlineData("This reason is deliberately longer than five hundred characters. ")]
    public void Replay_reason_is_required_and_bounded(string reason)
    {
        var request = new ReplayDeadLetterRequest { Reason = reason };
        var results = new List<ValidationResult>();
        var valid = Validator.TryValidateObject(
            request, new ValidationContext(request), results, validateAllProperties: true);

        Assert.False(valid);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void Replay_endpoint_is_post_and_uses_a_long_outbox_identifier()
    {
        var method = typeof(IdentityNotificationOperationsController)
            .GetMethod(nameof(IdentityNotificationOperationsController.ReplayDeadLetter));

        Assert.NotNull(method);
        var route = method!.GetCustomAttribute<HttpPostAttribute>();
        Assert.NotNull(route);
        Assert.Equal("{id:long}/replay", route!.Template);
    }
}
