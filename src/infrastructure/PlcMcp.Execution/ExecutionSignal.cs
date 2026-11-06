using System.Threading.Channels;

namespace PlcMcp.Execution;

public sealed class ExecutionSignal
{
    private readonly Channel<bool> channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    public void Pulse() => channel.Writer.TryWrite(true);
    public async Task WaitAsync(CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try { await channel.Reader.ReadAsync(timeout.Token); }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
    }
}
