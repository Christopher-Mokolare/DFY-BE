    public async Task<ActionResult<ApiResponse<bool>>> ClaimTask(string taskId, [FromBody] ClaimTaskRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId == null) return Unauthorized();
        var user = await _context.Users.FindAsync(userId);
        if (user == null) return Unauthorized();

        if (!_userPolicyService.IsProfileComplete(user))
            return Ok(new ApiResponse<bool> { Success = false, Error = "PROFILE_INCOMPLETE", Message = "Please complete your profile before claiming tasks" });

        if (!_userPolicyService.CanAcceptTasks(user))
            return StatusCode(StatusCodes.Status403Forbidden, new ApiResponse<bool> { Success = false, Error = "RUNNER_NOT_ELIGIBLE", Message = "Only Runners and Both accounts can accept tasks." });

        if (!request.TermsAccepted)
            return BadRequest(new ApiResponse<bool> { Success = false, Error = "TERMS_NOT_ACCEPTED", Message = "You must accept the runner terms before accepting a task." });

        if (string.IsNullOrWhiteSpace(request.HelperName) || string.IsNullOrWhiteSpace(request.HelperContact))
            return BadRequest(new ApiResponse<bool> { Success = false, Error = "RUNNER_DETAILS_REQUIRED", Message = "Runner name and contact details are required." });

        var task = await _context.Tasks.FirstOrDefaultAsync(t => t.TaskId == taskId);
        if (task == null)
            return NotFound(new ApiResponse<bool> { Success = false, Error = "TASK_NOT_FOUND", Message = "Task could not be found." });

        if (task.CreatedByUserId == userId)
            return Conflict(new ApiResponse<bool> { Success = false, Error = "CANNOT_CLAIM_OWN_TASK", Message = "You cannot accept a task that you created." });

        if (task.AcceptedByUserId.HasValue || task.TaskStatus == "Claimed")
            return Conflict(new ApiResponse<bool> { Success = false, Error = "TASK_ALREADY_CLAIMED", Message = "This task has already been accepted by another runner." });

        if (task.TaskStatus != "Posted")
            return Conflict(new ApiResponse<bool> { Success = false, Error = "TASK_NOT_POSTED", Message = "This task is no longer available for acceptance." });

        if (task.PaymentStatus != "EscrowHeld")
            return Conflict(new ApiResponse<bool> { Success = false, Error = "PAYMENT_NOT_SECURED", Message = "This task cannot be accepted until the creator's payment is secured." });

        var claimedAt = DateTime.UtcNow;
        var affected = await _context.Tasks
            .Where(t => t.Id == task.Id && t.TaskStatus == "Posted" && t.PaymentStatus == "EscrowHeld" && t.AcceptedByUserId == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.AcceptedByUserId, userId.Value)
                .SetProperty(t => t.HelperName, request.HelperName.Trim())
                .SetProperty(t => t.HelperContact, request.HelperContact.Trim())
                .SetProperty(t => t.TaskStatus, "Claimed")
                .SetProperty(t => t.UpdatedAt, claimedAt));

        if (affected != 1)
            return Conflict(new ApiResponse<bool> { Success = false, Error = "TASK_ALREADY_CLAIMED", Message = "This task was just accepted by another runner." });

        await _notificationService.NotifyTaskClaimedAsync(task.CreatedByUserId, task.TaskDescription, request.HelperName.Trim());
        return Ok(new ApiResponse<bool> { Success = true, Data = true, Message = "Task accepted successfully." });
    }
    [HttpPut("{taskId}/payment-status")]