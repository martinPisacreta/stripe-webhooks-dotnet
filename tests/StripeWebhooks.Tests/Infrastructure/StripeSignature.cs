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
    public static string Header(string payload, string secret, DateTimeOffset timestamp)
    {
        var t = timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{payload}"));
        return $"t={t},v1={Convert.ToHexString(mac).ToLowerInvariant()}";
    }

    /// <summary>A random secret in Stripe's format, generated per test run. Never a real key.</summary>
    public static string NewTestSecret() =>
        "whsec_test_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
