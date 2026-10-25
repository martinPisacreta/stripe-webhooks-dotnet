using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace StripeWebhooks.Tests.Infrastructure;

/// <summary>
/// Builds a Stripe-Signature header the way Stripe does, written independently of Stripe.net so the
/// tests check the SDK call instead of trusting it: HMAC-SHA256 over "{timestamp}.{raw body}" keyed
/// with the endpoint secret, hex encoded, sent as "t={timestamp},v1={signature}".
/// See https://docs.stripe.com/webhooks#verify-manually
/// </summary>
public static class StripeSignature
{
    public static string Header(string payload, string secret, DateTimeOffset timestamp) =>
        Header(payload, timestamp, "v1", secret);

    /// <summary>
    /// One entry per secret, in order. While an endpoint secret is being rolled, Stripe sends a v1
    /// entry for the old secret and one for the new secret in the same header.
    /// </summary>
    public static string Header(string payload, DateTimeOffset timestamp, string scheme, params string[] secrets)
    {
        var t = timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var entries = secrets.Select(secret => $"{scheme}={Sign(t, payload, secret)}");
        return $"t={t}," + string.Join(',', entries);
    }

    /// <summary>A random secret in Stripe's format, generated per test run. Never a real key.</summary>
    public static string NewTestSecret() =>
        "whsec_test_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static string Sign(string timestamp, string payload, string secret) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{payload}")))
            .ToLowerInvariant();
}
