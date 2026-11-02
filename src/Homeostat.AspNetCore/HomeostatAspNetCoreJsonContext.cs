using System.Text.Json;
using System.Text.Json.Serialization;
using Homeostat.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Homeostat.AspNetCore;

/// <summary>
/// The types the Homeostat endpoints answer with. Written with source-generated serialization, so the endpoints work
/// in trimmed and Native AOT applications without the application registering anything.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(HomeostatStatus))]
[JsonSerializable(typeof(ConditionEventInfo[]))]
[JsonSerializable(typeof(CoverageReport))]
[JsonSerializable(typeof(AlertIngestionResult))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(ProblemDetails))]
internal sealed partial class HomeostatAspNetCoreJsonContext : JsonSerializerContext;

/// <summary>Results written with <see cref="HomeostatAspNetCoreJsonContext"/>.</summary>
internal static class HomeostatResults
{
    public static IResult BadRequest(string message)
        => Results.Json(message, HomeostatAspNetCoreJsonContext.Default.String, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>
    /// A problem details answer, like <c>Results.Problem</c>,
    /// written without reflection: through the application's <see cref="IProblemDetailsService"/> when it has one
    /// (<c>AddProblemDetails</c>), with source-generated serialization otherwise.
    /// </summary>
    public static IResult Problem(string detail, int statusCode) => new ProblemResult(detail, statusCode);

    private sealed class ProblemResult(string detail, int statusCode) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var problem = new ProblemDetails
            {
                Type = statusCode switch
                {
                    StatusCodes.Status403Forbidden => "https://tools.ietf.org/html/rfc9110#section-15.5.4",
                    StatusCodes.Status503ServiceUnavailable => "https://tools.ietf.org/html/rfc9110#section-15.6.4",
                    _ => null,
                },
                Title = ReasonPhrases.GetReasonPhrase(statusCode),
                Status = statusCode,
                Detail = detail,
            };

            httpContext.Response.StatusCode = statusCode;
            if (httpContext.RequestServices.GetService<IProblemDetailsService>() is { } service
                && await service.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem }))
            {
                return;
            }

            await httpContext.Response.WriteAsJsonAsync(
                problem, HomeostatAspNetCoreJsonContext.Default.ProblemDetails, "application/problem+json", httpContext.RequestAborted);
        }
    }
}
