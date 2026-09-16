using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Birooni.Licensing.Api.Common;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class AdminApiKeyAttribute : Attribute, IAsyncActionFilter
{
    private const string ApiKeyHeaderName = "X-Admin-Key";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var expectedKey = configuration["Admin:ApiKey"]
            ?? configuration["ADMIN_API_KEY"]
            ?? Environment.GetEnvironmentVariable("ADMIN_API_KEY")
            ?? "birooni-admin-secret-2026";

        if (!context.HttpContext.Request.Headers.TryGetValue(ApiKeyHeaderName, out var extractedKey))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Admin authentication required. Missing X-Admin-Key header." });
            return;
        }

        if (!string.Equals(expectedKey, extractedKey.ToString().Trim()))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid admin authentication key." });
            return;
        }

        await next();
    }
}
