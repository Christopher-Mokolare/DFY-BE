namespace DoForYou.API.Models;

/// <summary>
/// Canonical task budget limits. The API, persistence model and client-facing
/// configuration endpoint all use these values.
/// </summary>
public static class TaskBudgetLimits
{
    public const decimal Minimum = 50m;
    public const decimal Maximum = 100000m;
    public const string Currency = "ZAR";

    public static bool IsValid(decimal budget) =>
        budget >= Minimum && budget <= Maximum;
}
