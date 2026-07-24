using System.Threading.Channels;

namespace SupportForge.Ingestion;

public sealed class IngestionQueue
{
    private readonly Channel<IIngestionJob> _channel = Channel.CreateUnbounded<IIngestionJob>();

    public void Enqueue(IIngestionJob job) => _channel.Writer.TryWrite(job);

    public ValueTask<IIngestionJob> DequeueAsync(CancellationToken ct) => _channel.Reader.ReadAsync(ct);
}
