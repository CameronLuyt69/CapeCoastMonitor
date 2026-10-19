using System.Collections.Concurrent;
using System.Threading.Channels;
using Npgsql;

namespace CapeCoast.Api;

/// <summary>
/// Holds one connection that LISTENs for the worker's pg_notify calls and fans each message out to
/// every open SSE stream. The worker and API are separate processes, so the database is the bridge.
/// </summary>
public class IngestionNotifier : BackgroundService
{
    private readonly NpgsqlDataSource _db;
    private readonly ILogger<IngestionNotifier> _log;
    private readonly ConcurrentDictionary<Guid, Channel<string>> _subscribers = new();

    public IngestionNotifier(NpgsqlDataSource db, ILogger<IngestionNotifier> log)
    {
        _db = db;
        _log = log;
    }

    public int SubscriberCount => _subscribers.Count;

    public Subscription Subscribe()
    {
        var id = Guid.NewGuid();
        // a slow browser only ever needs the newest few messages
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _subscribers[id] = channel;
        return new Subscription(channel.Reader, () => _subscribers.TryRemove(id, out _));
    }

    private void Broadcast(string payload)
    {
        foreach (var channel in _subscribers.Values)
            channel.Writer.TryWrite(payload);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var conn = await _db.OpenConnectionAsync(stoppingToken);
                conn.Notification += (_, e) => Broadcast(e.Payload);
                await using (var cmd = new NpgsqlCommand("LISTEN ingestion", conn))
                    await cmd.ExecuteNonQueryAsync(stoppingToken);

                _log.LogInformation("Listening for ingestion notifications");
                while (!stoppingToken.IsCancellationRequested)
                    await conn.WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Lost the notification connection, retrying in 5 s");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    public sealed class Subscription : IDisposable
    {
        private readonly Action _dispose;
        public ChannelReader<string> Reader { get; }

        public Subscription(ChannelReader<string> reader, Action dispose)
        {
            Reader = reader;
            _dispose = dispose;
        }

        public void Dispose() => _dispose();
    }
}
