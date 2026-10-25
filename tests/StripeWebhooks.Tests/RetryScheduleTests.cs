using StripeWebhooks.Api.Webhooks;

namespace StripeWebhooks.Tests;

public sealed class RetryScheduleTests
{
    [Theory]
    [InlineData(1, "00:00:30")]
    [InlineData(2, "00:02:00")]
    [InlineData(3, "00:08:00")]
    [InlineData(4, "00:32:00")]
    [InlineData(5, "02:08:00")]
    [InlineData(6, "06:00:00")]
    [InlineData(9, "06:00:00")]
    [InlineData(100, "06:00:00")]
    public void DefaultBackoffGrowsFourfoldAndIsCapped(int failedAttempts, string expected)
    {
        Assert.Equal(TimeSpan.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), new StripeWebhookOptions().RetryDelay(failedAttempts));
    }

    [Fact]
    public void DefaultScheduleGivesADownstreamOutageAboutADayBeforeAnEventFails()
    {
        var options = new StripeWebhookOptions();
        var total = Enumerable.Range(1, options.MaxAttempts - 1).Aggregate(TimeSpan.Zero, (sum, n) => sum + options.RetryDelay(n));

        Assert.InRange(total, TimeSpan.FromHours(20), TimeSpan.FromHours(30));
    }
}
