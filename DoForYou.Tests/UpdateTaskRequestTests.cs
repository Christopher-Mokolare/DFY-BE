using System.ComponentModel.DataAnnotations;
using DoForYou.API.DTOs;
using DoForYou.API.Models;

namespace DoForYou.Tests;

public class UpdateTaskRequestTests
{
    [Fact]
    public void TaskNameIsOptionalWhenOnlyOtherFieldsAreUpdated()
    {
        var request = new UpdateTaskRequest { TaskName = null };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            request,
            new ValidationContext(request),
            results,
            validateAllProperties: true);

        Assert.True(isValid);
    }

    [Fact]
    public void TaskNameRejectsValuesLongerThanSixtyCharacters()
    {
        var request = new UpdateTaskRequest { TaskName = new string('x', 61) };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            request,
            new ValidationContext(request),
            results,
            validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(UpdateTaskRequest.TaskName)));
    }

    [Fact]
    public void TaskNameAcceptsValidValues()
    {
        var request = new UpdateTaskRequest { TaskName = "Assemble a desk" };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            request,
            new ValidationContext(request),
            results,
            validateAllProperties: true);

        Assert.True(isValid);
    }
    [Theory]
    [InlineData(50)]
    [InlineData(100000)]
    public void BudgetAcceptsBackendLimits(decimal budget)
    {
        var request = new UpdateTaskRequest { Budget = budget };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            request, new ValidationContext(request), results, validateAllProperties: true);

        Assert.True(isValid);
    }

    [Theory]
    [InlineData(49.99)]
    [InlineData(100000.01)]
    public void BudgetRejectsValuesOutsideBackendLimits(decimal budget)
    {
        var request = new UpdateTaskRequest { Budget = budget };
        var results = new List<ValidationResult>();

        var isValid = Validator.TryValidateObject(
            request, new ValidationContext(request), results, validateAllProperties: true);

        Assert.False(isValid);
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(UpdateTaskRequest.Budget)));
    }
}
