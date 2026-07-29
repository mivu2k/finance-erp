using Hr.Infrastructure.Attendance;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Hr.Web;

/// <summary>
/// Takes webcam frames from a kiosk and answers with whatever QR code is in them.
/// </summary>
/// <remarks>
/// A plain HTTP endpoint rather than a Blazor call: frames are tens of kilobytes
/// arriving several times a second, and pushing that through the SignalR circuit
/// would mean raising the hub's message limit for the whole application to suit
/// one page.
///
/// Anonymous for the same reason the kiosk page is — nobody logs into a machine by
/// a door — and gated the same way, on the station token.
/// </remarks>
public static class KioskEndpoints
{
    /// <summary>
    /// Enough for a downscaled JPEG frame and nothing like enough to be worth
    /// posting anything else to.
    /// </summary>
    private const int MaxFrameBytes = 512 * 1024;

    public static IEndpointRouteBuilder MapHrKioskEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/hr/kiosk/{token}/frame", async (
            string token, HttpRequest request,
            IKioskService kiosk, IQrFrameDecoder decoder, CancellationToken ct) =>
        {
            // 204 rather than 404 for an unknown station: this is polled several
            // times a second, and a status the error-page middleware wants to
            // re-execute turns every frame into wasted work. The page itself already
            // tells a human when the station token is wrong.
            if (await kiosk.ResolveStationAsync(token, ct) is null) return Results.NoContent();

            if (request.ContentLength is > MaxFrameBytes) return Results.NoContent();

            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            if (buffer.Length == 0) return Results.NoContent();

            var text = decoder.Decode(buffer.ToArray());

            // No code in this frame is the ordinary case, not a failure — the camera
            // is pointed at an empty doorway most of the time.
            return text is null ? Results.NoContent() : Results.Ok(new { text });
        }).AllowAnonymous();

        return app;
    }
}
