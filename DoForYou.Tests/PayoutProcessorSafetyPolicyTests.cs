using DoForYou.API.Services;

namespace DoForYou.Tests;

public sealed class PayoutProcessorSafetyPolicyTests
{
    [Theory]
    [InlineData(true, true, "Staging", "sk_test_example", true)]
    [InlineData(true, false, "Staging", "sk_test_example", false)]
    [InlineData(true, true, "Staging", "sk_live_example", false)]
    [InlineData(true, true, "Production", "sk_test_example", false)]
    [InlineData(false, true, "Staging", "sk_test_example", false)]
    [InlineData(true, true, null, "sk_test_example", false)]
    [InlineData(true, true, "Staging", null, false)]
    public void CanRunTestTransfers_RequiresEverySafetyGate(
        bool e2eTestMode,
        bool testTransfersEnabled,
        string? environment,
        string? secretKey,
        bool expected)
    {
        var result = PayoutProcessorSafetyPolicy.CanRunTestTransfers(
            e2eTestMode,
            testTransfersEnabled,
            environment,
            secretKey);

        Assert.Equal(expected, result);
    }
}
