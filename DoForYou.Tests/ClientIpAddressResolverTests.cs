using System.Net;
using Microsoft.AspNetCore.Http;
using DoForYou.API.Services;

namespace DoForYou.Tests;

public class ClientIpAddressResolverTests
{
    [Fact]
    public void UsesCloudflareClientIpWhenManagedIngressIsTrusted()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.8");
        context.Request.Headers["CF-Connecting-IP"] = "203.0.113.42";
        context.Request.Headers["X-Forwarded-For"] = "198.51.100.99, 10.0.0.7";

        var result = ClientIpAddressResolver.GetRateLimitPartitionKey(context, trustManagedIngress: true);

        Assert.Equal("203.0.113.42", result);
    }

    [Fact]
    public void DoesNotTrustCloudflareHeaderWhenManagedIngressTrustIsDisabled()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.8");
        context.Request.Headers["CF-Connecting-IP"] = "203.0.113.42";

        var result = ClientIpAddressResolver.GetRateLimitPartitionKey(context, trustManagedIngress: false);

        Assert.Equal("10.0.0.8", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-ip")]
    [InlineData("203.0.113.42, 198.51.100.7")]
    public void FallsBackToConnectionAddressForMissingOrInvalidCloudflareHeader(string headerValue)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.8");
        context.Request.Headers["CF-Connecting-IP"] = headerValue;

        var result = ClientIpAddressResolver.GetRateLimitPartitionKey(context, trustManagedIngress: true);

        Assert.Equal("10.0.0.8", result);
    }

    [Fact]
    public void UsesUnknownWhenNoValidClientOrConnectionAddressExists()
    {
        var context = new DefaultHttpContext();

        var result = ClientIpAddressResolver.GetRateLimitPartitionKey(context, trustManagedIngress: true);

        Assert.Equal("unknown", result);
    }
}
