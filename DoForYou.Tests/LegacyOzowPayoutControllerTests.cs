using DoForYou.API.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace DoForYou.Tests;

public sealed class LegacyOzowPayoutControllerTests
{
    [Fact]
    public void Verify_ReturnsGone()
    {
        var result = new OzowPayoutController().Verify();

        var response = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status410Gone, response.StatusCode);
    }

    [Fact]
    public void Notification_ReturnsGone()
    {
        var result = new OzowPayoutController().Notification();

        var response = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status410Gone, response.StatusCode);
    }
}
