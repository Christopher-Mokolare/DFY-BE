namespace DoForYou.API.Services;

public static class PayoutProcessorSafetyPolicy
{
    public static bool CanRunTestTransfers(
        bool e2eTestMode,
        bool testTransfersEnabled,
        string? environment,
        string? paystackSecretKey)
    {
        return e2eTestMode &&
               testTransfersEnabled &&
               string.Equals(environment, "Staging", StringComparison.OrdinalIgnoreCase) &&
               paystackSecretKey?.StartsWith("sk_test_", StringComparison.OrdinalIgnoreCase) == true;
    }
}
