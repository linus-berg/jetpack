using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Primitives;

namespace Jetpack.Api;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class ApiKeyAuthorizeAttribute : Attribute, IAsyncActionFilter {
  private const string C_API_KEY_HEADER_NAME_ = "X-Api-Key";

  public async Task OnActionExecutionAsync(ActionExecutingContext context,
                                           ActionExecutionDelegate next) {
    if (!context.HttpContext.Request.Headers.TryGetValue(
          C_API_KEY_HEADER_NAME_,
          out StringValues potential_api_key
        )) {
      context.Result = new UnauthorizedResult();
      return;
    }

    IConfiguration configuration = context.HttpContext.RequestServices
                                          .GetRequiredService<IConfiguration>();
    string? api_key = configuration.GetApiKey();

    if (api_key is null || !api_key.Equals(potential_api_key)) {
      context.Result = new UnauthorizedResult();
      return;
    }

    await next();
  }
}