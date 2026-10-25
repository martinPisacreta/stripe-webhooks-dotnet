using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace StripeWebhooks.Api.Webhooks;

/// <summary>
/// Bounded in-memory hand-off between the HTTP endpoint and the worker. It carries event ids
/// only; the payload is already durable in ProcessedEvents, so losing the queue (restart, crash,
/// full) delays processing but never loses an event.
/// </summary>
public sealed class EventQueue
{
    private readonly Channel<string> _channel;

    public EventQueue(IOptions<StripeWebhookOptions> options)
    {
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public ChannelReader<string> Reader => _channel.Reader;

    /// <summary>Never blocks. Returns false when the queue is full; the sweeper picks the row up later.</summary>
    public bool TryEnqueue(string eventId) => _channel.Writer.TryWrite(eventId);
}
