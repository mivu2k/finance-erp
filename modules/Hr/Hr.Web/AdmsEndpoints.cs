using Hr.Infrastructure.Attendance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Hr.Web;

/// <summary>
/// The endpoints a ZKTeco terminal in ADMS ("cloud server") mode calls. The device
/// dials out to us; we never initiate.
/// </summary>
/// <remarks>
/// <para>
/// These are necessarily <b>anonymous</b> — a door terminal cannot hold a login,
/// and the protocol has no authentication of its own. The only identity on offer is
/// the serial number in the query string, which is trivially forgeable. Treat this
/// as a LAN-only surface: do not expose <c>/iclock/*</c> to the internet, and if the
/// app is published, block that path at the reverse proxy.
/// </para>
/// <para>
/// The firmware is fussy. It wants <c>text/plain</c>, it reads the body literally,
/// and an unexpected status or shape makes it discard the batch and retry the same
/// records forever. Every reply here is one the device is known to accept.
/// </para>
/// </remarks>
public static class AdmsEndpoints
{
    public static IEndpointRouteBuilder MapHrAdmsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/iclock").AllowAnonymous();

        // Opening call of each cycle: the device asks for its configuration.
        group.MapGet("/cdata", async (
            HttpRequest request, string? SN, IAdmsService adms, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(SN)) return Results.BadRequest("SN is required.");

            var body = await adms.HandshakeAsync(SN, Caller(request), ct);
            return Text(body);
        });

        // Records. `table` says what kind; only ATTLOG carries attendance.
        group.MapPost("/cdata", async (
            HttpRequest request, string? SN, string? table, IAdmsService adms,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(SN)) return Results.BadRequest("SN is required.");

            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(ct);

            var result = await adms.UploadAsync(SN, table, body, Caller(request), ct);
            return Text(result.Reply);
        });

        // The device asking for work. We have none, but silence reads as an outage.
        group.MapGet("/getrequest", async (
            HttpRequest request, string? SN, IAdmsService adms, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(SN)) return Results.BadRequest("SN is required.");

            return Text(await adms.PollAsync(SN, Caller(request), ct));
        });

        // Result of a command we issued. We issue none; acknowledge and move on.
        group.MapPost("/devicecmd", () => Text("OK"));

        // Some firmware probes this before anything else.
        group.MapGet("/ping", () => Text("OK"));

        return app;
    }

    /// <summary>
    /// Plain text with no charset suffix and no trailing ceremony — the device
    /// parses the body bytes directly.
    /// </summary>
    private static IResult Text(string body) => Results.Text(body, "text/plain");

    /// <summary>
    /// Where the terminal called from. Behind a reverse proxy this is the proxy
    /// unless forwarded headers are honoured, so it is recorded for diagnostics
    /// only and never used to decide anything.
    /// </summary>
    private static string? Caller(HttpRequest request) =>
        request.HttpContext.Connection.RemoteIpAddress?.ToString();
}
