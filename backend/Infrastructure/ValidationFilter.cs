using Kairon.Backend.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Kairon.Backend.Infrastructure;

public class ValidationFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
        {
            var errors = context.ModelState
                .Where(e => e.Value?.Errors.Count > 0)
                .Select(e => new
                {
                    Field = e.Key,
                    Errors = e.Value?.Errors.Select(x => x.ErrorMessage)
                });

            var response = new ApiResponse<object>
            {
                Success = false,
                Error = "Validation failed",
                ErrorCode = "VALIDATION_ERROR",
                StatusCode = StatusCodes.Status400BadRequest,
                Data = errors
            };

            context.Result = new BadRequestObjectResult(response);
        }
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}