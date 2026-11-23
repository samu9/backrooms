using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Backrooms.Core;

namespace Backrooms.Net;

/// <summary>
/// The WebSocket to the relay (server/server.ts, path /ws). Receiving and sending run on the thread
/// pool; messages are queued and handed to <see cref="NetState"/> on the main thread by Poll().
/// Everything awaits with ConfigureAwait(false), so Connect can be waited on from the main thread.
/// </summary>
public sealed class NetClient : IDisposable
{
    public const string DefaultUrl = "ws://127.0.0.1:5191/ws";

    public readonly NetState State = new();
    public string Url { get; private set; } = DefaultUrl;
    public bool Connected => ws.State == WebSocketState.Open;
    /// <summary>Set when the connection dropped after it was up.</summary>
    public event Action? Closed;

    readonly ClientWebSocket ws = new();
    readonly ConcurrentQueue<string> inbox = new();
    readonly BlockingCollection<string> outbox = new();
    readonly CancellationTokenSource cts = new();
    readonly TaskCompletionSource<int> welcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile bool closed;

    /// <summary>Join (or create) a room. Returns the room's seed; throws with a readable message.</summary>
    public async Task<int> Connect(string url, string room, string name, int? seed, int timeoutMs = 4000)
    {
        Url = url;
        State.Begin(room);
        using var timeout = new CancellationTokenSource(timeoutMs);
        try
        {
            await ws.ConnectAsync(new Uri(url), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or UriFormatException)
        {
            throw new IOException($"cannot reach the relay at {url}");
        }
        _ = Task.Run(ReceiveLoop);
        _ = Task.Run(SendLoop);
        Send(NetState.Hello(room, name, seed));
        var done = await Task.WhenAny(welcome.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        if (done != welcome.Task) throw new IOException("relay did not answer");
        return await welcome.Task.ConfigureAwait(false);
    }

    public void Send(string msg)
    {
        if (!outbox.IsAddingCompleted) outbox.Add(msg);
    }

    /// <summary>Hand the received messages to the state (main thread).</summary>
    public void Poll(double nowMs)
    {
        while (inbox.TryDequeue(out var m)) State.Handle(m, nowMs);
        if (closed)
        {
            closed = false;
            Closed?.Invoke();
        }
    }

    async Task ReceiveLoop()
    {
        var buf = new byte[16384];
        var msg = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, cts.Token).ConfigureAwait(false);
                if (r.MessageType == WebSocketMessageType.Close) break;
                msg.Write(buf, 0, r.Count);
                if (!r.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length);
                msg.SetLength(0);
                // the handshake answer is needed before the world exists: peek at it here
                if (!welcome.Task.IsCompleted && text.Contains("\"welcome\""))
                {
                    var probe = new NetState();
                    probe.Handle(text, 0);
                    if (probe.Seed is { } s) welcome.TrySetResult(s);
                }
                else if (!welcome.Task.IsCompleted && text.Contains("\"full\"")) welcome.TrySetException(new IOException("room is full"));
                inbox.Enqueue(text);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        welcome.TrySetException(new IOException("relay closed the connection"));
        closed = true;
        outbox.CompleteAdding();
    }

    async Task SendLoop()
    {
        try
        {
            foreach (var m in outbox.GetConsumingEnumerable(cts.Token))
                await ws.SendAsync(Encoding.UTF8.GetBytes(m), WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        try
        {
            if (ws.State == WebSocketState.Open) ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).Wait(500);
        }
        catch (Exception)
        {
            // closing is best effort
        }
        ws.Dispose();
    }
}
