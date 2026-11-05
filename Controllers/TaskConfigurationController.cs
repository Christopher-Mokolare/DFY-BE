using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using DoForYou.API.DTOs;
using DoForYou.API.Models;

namespace DoForYou.API.Controllers;

[ApiController]
[Route("api/v1/config")]
[AllowAnonymous]
public class TaskConfigurationController : ControllerBase
{
    [HttpGet("task-limits")]
    public ActionResult<ApiResponse<TaskBudgetLimitsDto>> GetTaskBudgetLimits()
    {
        return Ok(new ApiResponse<TaskBudgetLimitsDto>
        {
            Success = true,
            Data = new TaskBudgetLimitsDto
            {
                MinimumBudget = TaskBudgetLimits.Minimum,
                MaximumBudget = TaskBudgetLimits.Maximum,
                Currency = TaskBudgetLimits.Currency
            }
        });
    }
}
