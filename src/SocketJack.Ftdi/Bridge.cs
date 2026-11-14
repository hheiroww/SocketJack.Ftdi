using System.Diagnostics;
using System.Net;
using System.Security.Authentication;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using SocketJack.Net;
using JackClient = SocketJack.Net.TcpClient;
using JackServer = SocketJack.Net.TcpServer;

namespace SocketJack.Ftdi;

internal static class BridgeProtocol {
    internal const int Version = 1;
    internal const int ChunkBytes = 4096;
    internal const int QueueLength = 32;
    internal const int KeyBytes = 32;
    internal static readonly TimeSpan ReplyQuiet = TimeSpan.FromMilliseconds(750);
    internal static readonly TimeSpan MaximumReplyWait = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan Negotiation = TimeSpan.FromSeconds(12);
    internal static readonly TimeSpan Lease = TimeSpan.FromHours(4);

    internal static NetworkOptions Options(IPAddress? bind = null) {
        var options = new NetworkOptions {
            BindAddress = bind ?? IPAddress.Any,
            UseSsl = true, UsePeerToPeer = false, AutoReconnect = false, UseCompression = false, Chunking = false,
            MaximumBufferSize = 64 * 1024, MaximumSendQueueBytes = 512 * 1024, Backlog = 8,
            ConnectionTimeout = TimeSpan.FromSeconds(6)
        };
        foreach (var type in new[] { typeof(OpenSerial), typeof(SerialStatus), typeof(SerialData), typeof(SerialEnd) })
            options.Whitelist.Add(type);
        return options;
    }

    internal static Channel<byte[]> CreateQueue() => Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueLength) {
        SingleReader = true, FullMode = BoundedChannelFullMode.Wait
    });

    internal static bool IsTlsEncrypted(NetworkConnection? connection) => connection is { HasAuthenticatedEncryption: true } &&
        connection.SslStream?.SslProtocol is SslProtocols.Tls12 or SslProtocols.Tls13;
}

internal sealed class SerialSession {
    private readonly Channel<byte[]> input = BridgeProtocol.CreateQueue();
    private readonly object gate = new();
    private long endTimestamp;
    private readonly int bytesPerSecond;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double available = 4 * BridgeProtocol.ChunkBytes;
    private double lastUpdate;

    public SerialSession(int baud) { bytesPerSecond = Math.Max(1024, baud / 8 * 2); }

    internal bool Put(byte[]? bytes) {
        if (bytes is null || bytes.Length is 0 or > BridgeProtocol.ChunkBytes) return false;
        lock (gate) {
            if (endTimestamp != 0) return false;
            var now = clock.Elapsed.TotalSeconds;
            available = Math.Min(4 * BridgeProtocol.ChunkBytes, available + (now - lastUpdate) * bytesPerSecond);
            lastUpdate = now;
            if (bytes.Length > available || !input.Writer.TryWrite(bytes)) return false;
            available -= bytes.Length;
            return true;
        }
    }

    internal void End() {
        lock (gate) {
            if (endTimestamp != 0) return;
            endTimestamp = Stopwatch.GetTimestamp();
            input.Writer.TryComplete();
        }
    }

    internal async Task Run(Func<ISerialDevice> open, Action ready, Action<byte[]> send, CancellationToken stop) {
        using var closed = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var serial = open();
        ready();
        long lastReceived = 0, drainedAt = 0;
        var writer = Task.Run(async () => {
            try {
                await foreach (var data in input.Reader.ReadAllAsync(closed.Token)) serial.Write(data);
                Interlocked.Exchange(ref drainedAt, Stopwatch.GetTimestamp());
            } catch { await closed.CancelAsync(); throw; }
        });
        try {
            await Task.Run(() => {
                var buffer = new byte[BridgeProtocol.ChunkBytes];
                while (!closed.IsCancellationRequested) {
                    var count = serial.Read(buffer);
                    if (count > 0) {
                        lastReceived = Stopwatch.GetTimestamp();
                        send(buffer[..count]);
                    }
                    var drained = Interlocked.Read(ref drainedAt);
                    if (drained != 0 &&
                        (Stopwatch.GetElapsedTime(Math.Max(drained, lastReceived)) >= BridgeProtocol.ReplyQuiet ||
                         Stopwatch.GetElapsedTime(drained) >= BridgeProtocol.MaximumReplyWait)) break;
                }
            });
        } finally {
            await closed.CancelAsync();
            try { await writer; }
            catch (OperationCanceledException) when (closed.IsCancellationRequested) { }
        }
        stop.ThrowIfCancellationRequested();
    }
}

internal sealed class BridgeServer : IDisposable {
    private sealed record Owner(NetworkConnection Connection, SerialSession Session, CancellationTokenSource Stop);
    private readonly JackServer server;
    private readonly object gate = new();
    private readonly byte[] key;
    private readonly SerialSettings settings;
    private readonly Func<ISerialDevice> open;
    private readonly CancellationTokenSource shutdown = new();
    private Owner? owner;

    public BridgeServer(IPAddress bind, int port, X509Certificate2 certificate, byte[] key, SerialSettings settings, Func<ISerialDevice> open) {
        this.key = key;
        this.settings = settings;
        this.open = open;
        var options = BridgeProtocol.Options(bind);
        options.Authorization.AnonymousMessageTypes.Add(typeof(OpenSerial));
        options.Authorization.AuthorizeMessage = context =>
            BridgeProtocol.IsTlsEncrypted(context.Connection) &&
            (context.MessageType == typeof(OpenSerial) ||
             context.Principal?.Identity?.IsAuthenticated == true &&
             (context.MessageType == typeof(SerialData) || context.MessageType == typeof(SerialEnd)));
        server = new JackServer(options, port, "SocketJack FTDI") { SslCertificate = certificate };
        server.RegisterCallback<OpenSerial>(message => _ = Begin(message.Connection, message.Object));
        server.RegisterCallback<SerialData>(message => Receive(message.Connection, message.Object));
        server.RegisterCallback<SerialEnd>(message => Current(message.Connection)?.Session.End());
        server.ClientConnected += message => _ = CloseIfUnauthorized(message.Connection);
        server.ClientDisconnected += message => StopOwner(message.Connection);
    }

    public IPEndPoint Listen() {
        if (!server.Listen()) throw new IOException("Could not listen. Check the address, port, and server certificate.");
        return server.LocalIPEndPoint;
    }

    private async Task CloseIfUnauthorized(NetworkConnection connection) {
        try {
            await Task.Delay(BridgeProtocol.Negotiation, shutdown.Token);
            if (!connection.IsAuthenticated) connection.CloseConnection();
        } catch (OperationCanceledException) { }
    }

    private Owner? Current(NetworkConnection connection) {
        lock (gate) return owner?.Connection == connection ? owner : null;
    }

    private void StopOwner(NetworkConnection connection) {
        lock (gate) {
            if (owner?.Connection == connection) owner.Stop.Cancel();
        }
    }

    private async Task Begin(NetworkConnection connection, OpenSerial request) {
        if (request.Version != BridgeProtocol.Version || request.Key is not { Length: BridgeProtocol.KeyBytes } ||
            !CryptographicOperations.FixedTimeEquals(key, request.Key)) {
            connection.Send(new SerialStatus(false, "denied", 0, ""));
            await Task.Delay(200);
            connection.CloseConnection();
            return;
        }
        Owner? active;
        lock (gate) {
            if (shutdown.IsCancellationRequested || owner is not null) {
                if (owner?.Connection == connection) return;
                active = null;
            } else {
                var expires = DateTimeOffset.UtcNow + BridgeProtocol.Lease;
                var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "serial-operator")], "shared-key"));
                if (connection.Closed || connection.Closing) return;
                try { connection.SetAuthenticatedPrincipal(principal, expires); }
                catch (System.Security.SecurityException) { return; }
                var stop = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
                stop.CancelAfter(BridgeProtocol.Lease);
                active = owner = new Owner(connection, new SerialSession(settings.Baud), stop);
            }
        }
        if (active is null) {
            connection.Send(new SerialStatus(false, "busy", 0, ""));
            await Task.Delay(200);
            connection.CloseConnection();
            return;
        }
        try {
            await active.Session.Run(open, () => connection.Send(new SerialStatus(true, "open", settings.Baud, settings.Profile)),
                bytes => connection.Send(new SerialData(bytes)), active.Stop.Token);
            connection.Send(new SerialEnd());
            await Task.Delay(200, active.Stop.Token);
        } catch (OperationCanceledException) when (active.Stop.IsCancellationRequested) { }
        catch (Exception) {
            connection.Send(new SerialStatus(false, "device-error", 0, ""));
            await Task.Delay(200);
        } finally {
            connection.ClearAuthentication();
            lock (gate) {
                owner = null;
                active.Stop.Dispose();
            }
            connection.CloseConnection();
        }
    }

    private void Receive(NetworkConnection connection, SerialData message) {
        if (Current(connection) is not { } active || !active.Session.Put(message.Bytes)) {
            StopOwner(connection);
            connection.CloseConnection();
        }
    }

    public void Dispose() {
        shutdown.Cancel();
        server.Dispose();
        shutdown.Dispose();
    }
}

internal static class BridgeClient {
    internal static async Task Run(string host, int port, string tlsName, byte[] key, Stream input, Stream output,
        Action<SerialStatus> opened, CancellationToken cancel) {
        using var client = new JackClient(BridgeProtocol.Options()) { SslTargetHost = tlsName };
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var stop = transfer.Token;
        var accepted = new TaskCompletionSource<SerialStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var incoming = BridgeProtocol.CreateQueue();
        client.RegisterCallback<SerialStatus>(message => {
            if (!accepted.TrySetResult(message.Object) && !message.Object.Open)
                incoming.Writer.TryComplete(new IOException("The remote serial device is unavailable."));
        });
        client.RegisterCallback<SerialData>(message => {
            if (accepted.Task is { IsCompletedSuccessfully: true, Result.Open: true } &&
                message.Object.Bytes is { Length: > 0 and <= BridgeProtocol.ChunkBytes } bytes &&
                incoming.Writer.TryWrite(bytes)) return;
            incoming.Writer.TryComplete(new IOException("Serial frame or receive queue is invalid."));
            message.Connection.CloseConnection();
        });
        client.RegisterCallback<SerialEnd>(_ => incoming.Writer.TryComplete());
        client.OnDisconnected += _ => {
            accepted.TrySetException(new IOException("Connection closed before the serial session opened."));
            incoming.Writer.TryComplete(new IOException("The remote serial connection closed."));
        };
        if (!await client.Connect(host, port).WaitAsync(BridgeProtocol.Negotiation, stop) ||
            !BridgeProtocol.IsTlsEncrypted(client.Connection))
            throw new IOException("Could not establish a trusted TLS connection. Verify the host, port, and certificate.");
        client.Send(new OpenSerial(BridgeProtocol.Version, key));
        var status = await accepted.Task.WaitAsync(BridgeProtocol.Negotiation, stop);
        if (!status.Open) throw new IOException(status.Code switch {
            "busy" => "The serial device is in use.", "denied" => "The serial session key was not accepted.",
            _ => "The remote serial device could not be opened."
        });
        try { SerialSettings.Parse("", status.Baud.ToString(), status.Profile); }
        catch (ArgumentException) { throw new IOException("The server advertised invalid serial settings."); }
        opened(status);

        async Task Send() {
            var buffer = new byte[BridgeProtocol.ChunkBytes];
            var serialBits = 1 + status.Profile[0] - '0' + (status.Profile[1] == 'n' ? 0 : 1) + status.Profile[2] - '0';
            var bytesPerSecond = (double)status.Baud / serialBits;
            var chunkSize = Math.Min(buffer.Length, Math.Max(1, (int)bytesPerSecond / 20));
            int count;
            while ((count = await input.ReadAsync(buffer.AsMemory(0, chunkSize), stop)) > 0) {
                client.Send(new SerialData(buffer[..count]));
                // Bound the UART's producer even when stdin is a large regular file.
                await Task.Delay(TimeSpan.FromSeconds(count / bytesPerSecond), stop);
            }
            client.Send(new SerialEnd());
        }
        async Task Receive() {
            await foreach (var data in incoming.Reader.ReadAllAsync(stop)) {
                await output.WriteAsync(data, stop);
                await output.FlushAsync(stop);
            }
        }
        var sending = Send();
        var receiving = Receive();
        try {
            if (await Task.WhenAny(sending, receiving) == sending) await sending;
            await receiving;
        } finally { await transfer.CancelAsync(); }
    }
}
