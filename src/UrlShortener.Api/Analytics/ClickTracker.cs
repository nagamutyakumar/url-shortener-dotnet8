namespace UrlShortener.Api.Analytics;

/// <summary>
/// Captures a click for a successful redirect and hands it to the queue. Best-effort: it
/// never throws and never waits on the database, so analytics cannot fail a redirect.
/// </summary>
public sealed class ClickTracker
{
    private readonly IClickEventQueue _queue;
    private readonly VisitorHasher _visitorHasher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ClickTracker> _logger;

    public ClickTracker(
        IClickEventQueue queue,
        VisitorHasher visitorHasher,
        TimeProvider timeProvider,
        ILogger<ClickTracker> logger)
    {
        _queue = queue;
        _visitorHasher = visitorHasher;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <returns>True when the click was queued; false when it was dropped.</returns>
    public bool Track(long urlMappingId, HttpContext httpContext)
    {
        try
        {
            // CountryCode is deliberately not captured: no trusted geolocation source is
            // configured, and guessing from the IP address is not done.
            var message = new ClickEventMessage(
                urlMappingId,
                _timeProvider.GetUtcNow().UtcDateTime,
                _visitorHasher.Compute(
                    urlMappingId,
                    httpContext.Connection.RemoteIpAddress,
                    httpContext.Request.Headers.UserAgent.ToString()));

            if (_queue.TryEnqueue(message))
            {
                return true;
            }

            // Not logged per event: under overload that would flood the logs. ClickEventWriter
            // reports the dropped total.
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to queue click event for UrlMapping {UrlMappingId}.", urlMappingId);
            return false;
        }
    }
}
