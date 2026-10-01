using Microsoft.AspNetCore.Http;
using S1Atlas.Application.Envelope;

namespace S1Atlas.Web.Endpoints;

internal static class ServeHttp
{
    internal static IResult Html(string body, int statusCode = StatusCodes.Status200OK) =>
        Results.Text(body, "text/html", statusCode: statusCode);

    internal static IResult Envelope<T>(ToolEnvelope<T> envelope) where T : class =>
        Results.Json(envelope, ServeApiJson.Options, statusCode: HttpStatus(envelope.Status));

    private static int HttpStatus(ToolStatus status) => status switch
    {
        ToolStatus.Resolved => StatusCodes.Status200OK,
        ToolStatus.Ambiguous => StatusCodes.Status200OK,
        ToolStatus.NotFound => StatusCodes.Status404NotFound,
        ToolStatus.Invalid => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status503ServiceUnavailable
    };
}
