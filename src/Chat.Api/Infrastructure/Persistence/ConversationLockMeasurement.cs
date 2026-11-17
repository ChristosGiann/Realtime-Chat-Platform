using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Chat.Api.Infrastructure.Persistence;

// Measures the existing transaction boundary without changing lock acquisition or release.
internal sealed class ConversationLockMeasurement(string operation) : IDisposable
{
    private static readonly Meter Meter = new("Chat.Api.ConversationLock", "1.0");
    private static readonly Histogram<double> Wait = Meter.CreateHistogram<double>("conversation.lock.wait", "ms");
    private static readonly Histogram<double> Hold = Meter.CreateHistogram<double>("conversation.lock.hold", "ms");
    private readonly long started = Stopwatch.GetTimestamp();
    private long acquired;
    private bool disposed;

    public void Acquired()
    {
        acquired = Stopwatch.GetTimestamp();
        Wait.Record(Stopwatch.GetElapsedTime(started, acquired).TotalMilliseconds, new KeyValuePair<string, object?>("operation", operation));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (acquired != 0)
            Hold.Record(Stopwatch.GetElapsedTime(acquired).TotalMilliseconds, new KeyValuePair<string, object?>("operation", operation));
    }
}
