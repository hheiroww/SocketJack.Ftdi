using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SocketJack.Ftdi;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
var key = RandomNumberGenerator.GetBytes(BridgeProtocol.KeyBytes);
var fixture = Enumerable.Range(0, 32_789).Select(value => (byte)(value % 256)).ToArray();
var settings = SerialSettings.Parse("simulated", "115200", "8n1");
CheckLinuxDiscovery();
var direct = new SerialSession(settings.Baud);
using var directReply = new MemoryStream();
var directStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var directTask = direct.Run(() => new EchoDevice(), directStarted.SetResult, bytes => directReply.Write(bytes), timeout.Token);
await directStarted.Task.WaitAsync(timeout.Token);
if (direct.Put([]) || direct.Put(new byte[BridgeProtocol.ChunkBytes + 1])) throw new Exception("Invalid frame accepted.");
if (!direct.Put(fixture[..4096])) throw new Exception("Valid frame rejected.");
direct.End();
if (direct.Put([1])) throw new Exception("Data accepted after EOF.");
await directTask;
Equal(directReply.ToArray(), fixture[..4096], "bounded memory serial session");
Console.WriteLine("PASS bounded frames, binary response and input EOF");

using var generatedKey = RSA.Create(2048);
var request = new CertificateRequest("CN=localhost", generatedKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
var subject = new SubjectAlternativeNameBuilder();
subject.AddDnsName("localhost");
request.CertificateExtensions.Add(subject.Build());
using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
var pfxBytes = generated.Export(X509ContentType.Pkcs12);
var keyStorage = OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet;
using var untrusted = X509CertificateLoader.LoadPkcs12(pfxBytes, null, keyStorage);
CryptographicOperations.ZeroMemory(pfxBytes);
var opened = 0;
Func<ISerialDevice> device = () => { Interlocked.Increment(ref opened); return new EchoDevice(); };

async Task Reject(int port, byte[] candidate, string tlsName, string message) {
    try {
        await BridgeClient.Run("127.0.0.1", port, tlsName, candidate, Stream.Null, Stream.Null, _ => throw new Exception("Connection was accepted."), timeout.Token);
    } catch (Exception error) when (error is IOException or TimeoutException && error.Message.Contains(message, StringComparison.OrdinalIgnoreCase)) {
        return;
    }
    throw new Exception("Unexpected successful connection.");
}

using (var bridge = new BridgeServer(IPAddress.Loopback, 0, untrusted, key, settings, device)) {
    var port = bridge.Listen().Port;
    using (var tcp = new System.Net.Sockets.TcpClient()) {
        await tcp.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
        // The fixture trust exception is only used here to verify that the PFX
        // handshake itself works before exercising production trust rejection.
        using var tls = new System.Net.Security.SslStream(tcp.GetStream(), false,
            (_, peer, _, _) => peer?.GetCertHashString() == untrusted.Thumbprint);
        await tls.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions {
            TargetHost = "localhost"
        }, timeout.Token);
        if (!tls.IsEncrypted) throw new Exception("Generated PFX did not establish TLS.");
    }
    await Reject(port, key, "localhost", "trusted TLS connection");
    if (opened != 0) throw new Exception("An untrusted connection opened the serial device.");
    Console.WriteLine("PASS real SocketJack TLS rejects an untrusted certificate");
}

var certPath = Environment.GetEnvironmentVariable("SOCKETJACK_FTDI_TEST_CERT");
var serverName = Environment.GetEnvironmentVariable("SOCKETJACK_FTDI_TEST_NAME") ?? "localhost";
using var trusted = string.IsNullOrEmpty(certPath)
    ? (OperatingSystem.IsWindows() ? FindLocalTestCertificate(serverName) : null)
    : X509CertificateLoader.LoadPkcs12FromFile(certPath, Environment.GetEnvironmentVariable("SOCKETJACK_FTDI_CERT_PASSWORD"), keyStorage);
if (trusted is null) {
    Console.WriteLine("Provide SOCKETJACK_FTDI_TEST_CERT with a PFX already trusted by the OS to run the authenticated loopback check.");
    return 1;
}

using (var bridge = new BridgeServer(IPAddress.Loopback, 0, trusted, key, settings, device)) {
    var bridgePort = bridge.Listen().Port;
    await Reject(bridgePort, key, "wrong-socketjack-ftdi.invalid", "trusted TLS connection");
    await Reject(bridgePort, RandomNumberGenerator.GetBytes(BridgeProtocol.KeyBytes), serverName, "key was not accepted");
    if (opened != 0) throw new Exception("A wrong hostname or key opened the serial device.");
    Console.WriteLine("PASS real SocketJack TLS validates hostname and the bridge requires its shared key");

    using var firstInput = new HeldInput(fixture);
    using var echo = new MemoryStream();
    var sessionOpen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var exchange = BridgeClient.Run("127.0.0.1", bridgePort, serverName, key, firstInput, echo,
        status => { if (status.Baud != 115200 || status.Profile != "8n1") throw new Exception("Wrong serial settings."); sessionOpen.SetResult(); },
        timeout.Token);
    await sessionOpen.Task.WaitAsync(timeout.Token);
    await Reject(bridgePort, key, serverName, "serial device is in use");
    firstInput.Release();
    await exchange;
    Equal(echo.ToArray(), fixture, "authentic loopback binary transport");
    if (opened != 1) throw new Exception("Expected just one physical-device lease.");
    Console.WriteLine("PASS exclusive authenticated real SocketJack TLS lease and a binary transfer over 32 KiB");

    using var small = new MemoryStream([0, 255, 10, 13]);
    using var second = new MemoryStream();
    await BridgeClient.Run("127.0.0.1", bridgePort, serverName, key, small, second, _ => {}, timeout.Token);
    Equal(second.ToArray(), [0, 255, 10, 13], "released lease");
    if (opened != 2) throw new Exception("The device was not released after disconnect.");
    Console.WriteLine("PASS the next authenticated client can open a released lease");
}
return 0;

static void Equal(byte[] actual, byte[] expected, string test) {
    if (!actual.AsSpan().SequenceEqual(expected)) throw new Exception($"Invalid {test}: expected {expected.Length} bytes, received {actual.Length}.");
}

static void CheckLinuxDiscovery() {
    var root = Path.Combine(Path.GetTempPath(), "socketjack-ftdi-sysfs-" + Guid.NewGuid().ToString("N"));
    var sys = Path.Combine(root, "sys");
    var dev = Path.Combine(root, "dev");
    Directory.CreateDirectory(dev);
    var links = new Dictionary<string, DirectoryInfo>(StringComparer.Ordinal);
    var useFilesystemLinks = OperatingSystem.IsLinux();
    var interfaceSuffix = useFilesystemLinks ? ":1.0" : "-interface";

    void AddLink(string link, string target) {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateDirectory(target);
        if (useFilesystemLinks) Directory.CreateSymbolicLink(link, Path.GetRelativePath(Path.GetDirectoryName(link)!, target));
        else {
            Directory.CreateDirectory(link);
            links.Add(link, new DirectoryInfo(target));
        }
    }

    void AddPort(string name, string driverBus, string driverName, bool interfaceDriver = false, bool hasNode = true, bool outsideSysfs = false) {
        var device = Path.Combine(outsideSysfs ? root : Path.Combine(sys, "devices"), "usb1", name + interfaceSuffix);
        var usbSerialPort = Path.Combine(device, name);
        var ttyPort = Path.Combine(usbSerialPort, "tty", name);
        AddLink(Path.Combine(sys, "class", "tty", name), ttyPort);
        if (driverName.Length > 0)
            AddLink(Path.Combine(interfaceDriver ? device : usbSerialPort, "driver"), Path.Combine(sys, "bus", driverBus, "drivers", driverName));
        if (hasNode) File.WriteAllBytes(Path.Combine(dev, name), []);
    }

    AddPort("ttyUSB0", "usb-serial", "ftdi_sio");
    AddPort("ttyUSB10", "usb", "ftdi_sio", interfaceDriver: true);
    AddPort("ttyUSB1", "usb-serial", "cp210x");
    AddPort("ttyUSB2", "usb-serial", "ftdi_sio", hasNode: false);
    AddPort("ttyUSB3", "usb-serial", "");
    AddPort("ttyUSB4", "usb-serial", "ftdi_sio", outsideSysfs: true);
    AddPort("ttyUSB5alias", "usb-serial", "ftdi_sio");
    AddPort("ttyUSB6", "usb", "ftdi_sio", interfaceDriver: true);
    AddLink(Path.Combine(sys, "devices", "usb1", "ttyUSB6" + interfaceSuffix, "ttyUSB6", "driver"),
        Path.Combine(sys, "bus", "usb-serial", "drivers", "cp210x"));
    AddPort("ttyUSB7", "other", "ftdi_sio");
    AddPort("ttyUSB9", "usb-serial", "FTDI_SIO");
    AddPort("ttyusb11", "usb-serial", "ftdi_sio");
    AddPort("ttyS0", "usb-serial", "ftdi_sio");
    if (useFilesystemLinks) {
        AddPort("ttyUSB8", "usb-serial", "ftdi_sio", hasNode: false);
        File.CreateSymbolicLink(Path.Combine(dev, "ttyUSB8"), "ttyUSB0");
    }

    var ports = LinuxFtdiPorts.Discover(sys, dev, useFilesystemLinks ? null : link => links.GetValueOrDefault(link.FullName));
    if (!ports.SequenceEqual(new[] { Path.Combine(dev, "ttyUSB0"), Path.Combine(dev, "ttyUSB10") }))
        throw new Exception("Linux discovery returned incorrect FTDI ports.");
    if (LinuxFtdiPorts.Discover(Path.Combine(root, "absent"), dev).Length != 0) throw new Exception("Missing sysfs returned ports.");
    Console.WriteLine("PASS Linux FTDI discovery requires a present canonical ttyUSB node and the real FTDI kernel driver");
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
static X509Certificate2? FindLocalTestCertificate(string name) {
    foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine }) {
        using var store = new X509Store(StoreName.My, location);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        foreach (var certificate in store.Certificates) {
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            if (certificate.HasPrivateKey && certificate.GetNameInfo(X509NameType.DnsName, false) == name &&
                chain.Build(certificate)) {
                try {
                    using var privateKey = certificate.GetRSAPrivateKey();
                    if (privateKey?.SignData([0], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1) is not null)
                        return certificate;
                } catch (CryptographicException) { }
            }
            certificate.Dispose();
        }
    }
    return null;
}

sealed class EchoDevice : ISerialDevice {
    private readonly BlockingCollection<byte[]> buffer = new();

    public int Read(byte[] target) {
        if (!buffer.TryTake(out var data, 30)) return 0;
        data.CopyTo(target, 0);
        return data.Length;
    }
    public void Write(byte[] source) => buffer.Add(source);
    public void Dispose() => buffer.Dispose();
}

sealed class HeldInput(byte[] fixture) : MemoryStream(fixture) {
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Release() => release.SetResult();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
        await release.Task.WaitAsync(cancellationToken);
        return await base.ReadAsync(buffer, cancellationToken);
    }
}
