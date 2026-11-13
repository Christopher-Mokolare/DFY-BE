using System.ComponentModel.DataAnnotations;
using System.Reflection;
using DoForYou.API.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DoForYou.Tests;

public sealed class SuperAdminBootstrapControllerTests
{
    [Fact]
    public void Bootstrap_requires_authentication_and_is_not_anonymous()
    {
        var controller = typeof(SuperAdminBootstrapController);
        Assert.NotNull(controller.GetCustomAttribute<AuthorizeAttribute>());
        Assert.Null(controller.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Equal("api/v1/admin/bootstrap", controller.GetCustomAttribute<RouteAttribute>()?.Template);
    }

    [Theory]
    [InlineData("short", "123456")]
    [InlineData("01234567890123456789012345678901", "12345")]
    [InlineData("01234567890123456789012345678901", "12345x")]
    public void Bootstrap_request_rejects_short_secret_or_invalid_authenticator_code(string secret, string code)
    {
        var request = new SuperAdminBootstrapRequest
        {
            BootstrapSecret = secret,
            AuthenticatorCode = code
        };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(
            request, new ValidationContext(request), results, validateAllProperties: true);

        Assert.False(valid);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void Bootstrap_request_accepts_high_entropy_secret_shape_and_six_digit_code()
    {
        var request = new SuperAdminBootstrapRequest
        {
            BootstrapSecret = new string('s', 48),
            AuthenticatorCode = "123456"
        };
        var results = new List<ValidationResult>();

        Assert.True(Validator.TryValidateObject(
            request, new ValidationContext(request), results, validateAllProperties: true));
    }
}
