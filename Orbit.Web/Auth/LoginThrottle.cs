using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Orbit.Application;

namespace Orbit.Auth;

/// <summary>
/// Limits FAILED sign-ins per client address (spec §8.3). Per-account lockout stops someone guessing at one account;
/// it can't see one common password being tried across many accounts ("spraying"), because no single account fails
/// often enough to lock. This counts by where the attempts come from instead.
///
/// It counts failures rather than requests - which is why it isn't the rate-limiting middleware, which has to decide
/// before the outcome is known. A whole office shares one public address; fifty people signing in successfully at
/// nine o'clock must cost nothing.
///
/// The counting itself is the framework's sliding-window limiter, one per address, created and evicted on demand.
/// </summary>
public sealed class LoginThrottle : IDisposable
{
    private readonly PartitionedRateLimiter<string>? _limiter;
    private readonly ILogger<LoginThrottle> _logger;
    private readonly bool _warnAboutLoopback;
    private int _warned;

    public LoginThrottle(IOptions<SecurityOptions> options, IHostEnvironment environment, ILogger<LoginThrottle> logger)
    {
        _logger = logger;
        _warnAboutLoopback = !environment.IsDevelopment();

        var settings = options.Value.LoginThrottle;
        if (!settings.Enabled) return;

        var maxFailures = Math.Max(1, settings.MaxFailures);
        var windowMinutes = Math.Max(1, settings.WindowMinutes);
        _limiter = PartitionedRateLimiter.Create<string, string>(key =>
            RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = maxFailures,
                Window = TimeSpan.FromMinutes(windowMinutes),
                SegmentsPerWindow = Math.Min(windowMinutes, 60), // the budget comes back a minute at a time
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    }

    /// <summary>True when this address has used up its failures for the current window. Checking costs nothing.</summary>
    public bool IsBlocked(IPAddress? address) =>
        Key(address) is { } key && _limiter!.GetStatistics(key) is { CurrentAvailablePermits: <= 0 };

    public void RecordFailure(IPAddress? address)
    {
        if (Key(address) is { } key)
            _limiter!.AttemptAcquire(key).Dispose();
    }

    /// <summary>The bucket an address belongs to, or null when it isn't throttled at all.</summary>
    private string? Key(IPAddress? address)
    {
        if (_limiter is null || address is null) return null;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        // Loopback in production means a reverse proxy that isn't sending X-Forwarded-For: every user would appear to be
        // 127.0.0.1 and share one bucket, so twenty bad guesses from anyone would stop the whole company signing in.
        // Failing open - back to how things were without a throttle - beats handing out a denial-of-service lever.
        if (IPAddress.IsLoopback(address))
        {
            if (_warnAboutLoopback && Interlocked.Exchange(ref _warned, 1) == 0)
                _logger.LogWarning("Sign-in attempts are arriving from a loopback address, so the per-address sign-in throttle is NOT in effect. " +
                    "The reverse proxy must send X-Forwarded-For (see deploy/README.md).");
            return null;
        }

        // One subscriber routinely holds an entire IPv6 /64 and can use a fresh address for every request.
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            Array.Clear(bytes, 8, 8);
            return new IPAddress(bytes) + "/64";
        }
        return address.ToString();
    }

    public void Dispose() => _limiter?.Dispose();
}
