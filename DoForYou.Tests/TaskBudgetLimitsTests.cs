using System.ComponentModel.DataAnnotations;

namespace DoForYou.Tests;

public class TaskBudgetLimitsTests
{
    [Theory]
    [InlineData(50)]
    [InlineData(100000)]
    public void TaskEntityAcceptsConfiguredBudgetBoundaries(decimal budget)
    {
        var task = new DoForYou.API.Models.Task { Budget = budget };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            task, new ValidationContext(task), results, validateAllProperties: true);

        Assert.True(isValid);
    }

    [Theory]
    [InlineData(49.99)]
    [InlineData(100000.01)]
    public void TaskEntityRejectsBudgetOutsideConfiguredLimits(decimal budget)
    {
        var task = new DoForYou.API.Models.Task { Budget = budget };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            task, new ValidationContext(task), results, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(DoForYou.API.Models.Task.Budget)));
    }
}
