using System.Threading.Channels;
using Advideo.Orchestrator.Core.Abstractions;
using Advideo.Orchestrator.Core.Model;

namespace Advideo.Orchestrator.Infrastructure.Queue;

/// <summary>
/// In-process, bounded job queue backed by System.Threading.Channels. Great for
/// local dev and single-node deployments; provides natural backpressure. Swap
/// for RabbitMQ/Service Bus in production by implementing <see cref="IJobQueue"/>.
/// </summary>
public sealed class ChannelJobQueue : IJobQueue
{
    private readonly Channel<VideoJob> _channel;

    public ChannelJobQueue(int capacity = 1000)
    {
        _channel = Channel.CreateBounded<VideoJob>(new BoundedChannelOptions(capacity)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public ValueTask EnqueueAsync(VideoJob job, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(job, ct);

    public IAsyncEnumerable<VideoJob> DequeueAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}
