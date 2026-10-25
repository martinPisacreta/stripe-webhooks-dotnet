using System.Collections.Concurrent;
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

    // Ids waiting in the channel or being processed, so the sweeper does not fill the queue with
    // copies of work that is already on its way. Holds at most the channel capacity plus one id.
    private readonly ConcurrentDictionary<string, byte> _queued = new(StringComparer.Ordinal);

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

    /// <summary>
    /// Never blocks. Returns true if the id is now queued (or already was), false when the queue is
    /// full; the row stays Pending and the sweeper offers it again later.
    /// </summary>
    public bool TryEnqueue(string eventId)
    {
        if (!_queued.TryAdd(eventId, 0))
        {
            return true;
        }

        if (_channel.Writer.TryWrite(eventId))
        {
            return true;
        }

        _queued.TryRemove(eventId, out _);
        return false;
    }

    public bool Contains(string eventId) => _queued.ContainsKey(eventId);

    /// <summary>Called by the worker when it is done with an id it read, whatever the outcome.</summary>
    public void MarkDone(string eventId) => _queued.TryRemove(eventId, out _);
}
