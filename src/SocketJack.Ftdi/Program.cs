using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SocketJack.Ftdi;

const string keyVariable = "SOCKETJACK_FTDI_KEY";
const int defaultPort = 4465;
const string help = """
    SocketJack.Ftdi

      ports
      keygen
      serve --cert PATH --serial COMx [--listen IPV4] [--port 4465] [--baud 115200] [--profile 8n1]
      connect [--host localhost] [--port 4465] [--tls-name NAME]

    serve uses a Windows FTDI Virtual COM Port. The default listen address is 127.0.0.1.
    Both peers read a 32-byte base64 key from SOCKETJACK_FTDI_KEY.
    Optional PFX password: SOCKETJACK_FTDI_CERT_PASSWORD.
    connect reads and writes binary bytes through redirected stdin and stdout.
    """;

if (args.Length == 0 || args is ["-h" or "--help" or "help"]) {
    Console.WriteLine(help);
    return 0;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, cancel) => { cancel.Cancel = true; stop.Cancel(); };

try {
    if (args is ["keygen"]) {
        Console.WriteLine(Convert.ToBase64String(RandomNumberGenerator.GetBytes(BridgeProtocol.KeyBytes)));
        return 0;
    }
    if (args is ["ports"]) {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("FTDI port discovery requires Windows.");
        foreach (var port in FtdiVcp.Ports()) Console.WriteLine(port);
        return 0;
    }
    if (args[0] == "serve") {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Serving a physical FTDI COM port requires Windows.");
        var options = ParseOptions(args[1..], ["cert", "serial", "listen", "port", "baud", "profile"]);
        var settings = SerialSettings.Parse(Required(options, "serial"), options.GetValueOrDefault("baud", "115200"), options.GetValueOrDefault("profile", "8n1"));
        if (!FtdiVcp.Ports().Contains(settings.Port, StringComparer.OrdinalIgnoreCase))
            throw new IOException("The configured port is not a present FTDI Virtual COM Port. Run ports to list candidates.");
        if (!IPAddress.TryParse(options.GetValueOrDefault("listen", "127.0.0.1"), out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Listen address must be an IPv4 address.");
        var key = ReadKey();
        try {
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(Required(options, "cert"),
                Environment.GetEnvironmentVariable("SOCKETJACK_FTDI_CERT_PASSWORD"), X509KeyStorageFlags.UserKeySet);
            if (!certificate.HasPrivateKey) throw new ArgumentException("The PFX certificate requires a private key.");
            using var bridge = new BridgeServer(address, NetworkPort(options), certificate, key, settings,
                () => OperatingSystem.IsWindows() ? new FtdiVcp(settings) : throw new PlatformNotSupportedException());
            Console.Error.WriteLine($"Listening on {bridge.Listen()} for {settings.Port} at {settings.Baud} {settings.Profile}.");
            await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
        } finally { CryptographicOperations.ZeroMemory(key); }
        return 0;
    }
    if (args[0] == "connect") {
        if (!Console.IsInputRedirected || !Console.IsOutputRedirected)
            throw new ArgumentException("Redirect binary stdin and stdout when using connect.");
        var options = ParseOptions(args[1..], ["host", "port", "tls-name"]);
        var host = options.GetValueOrDefault("host", "localhost");
        var key = ReadKey();
        try {
            await BridgeClient.Run(host, NetworkPort(options), options.GetValueOrDefault("tls-name", host), key,
                Console.OpenStandardInput(), Console.OpenStandardOutput(),
                status => Console.Error.WriteLine($"Serial session open at {status.Baud} {status.Profile}."), stop.Token);
        } finally { CryptographicOperations.ZeroMemory(key); }
        return 0;
    }
    throw new ArgumentException("Unknown command. Run --help for usage.");
} catch (OperationCanceledException) when (stop.IsCancellationRequested) {
    return 0;
} catch (Exception error) when (error is ArgumentException or IOException or TimeoutException or SocketException or
    CryptographicException or UnauthorizedAccessException or PlatformNotSupportedException) {
    Console.Error.WriteLine(error.Message);
    return 1;
}

byte[] ReadKey() {
    var value = Environment.GetEnvironmentVariable(keyVariable) ?? "";
    var key = new byte[BridgeProtocol.KeyBytes];
    if (value.Length == 44 && Convert.TryFromBase64String(value, key, out var length) && length == key.Length)
        return key;
    CryptographicOperations.ZeroMemory(key);
    throw new ArgumentException($"Set {keyVariable} to a 32-byte base64 key generated with keygen.");
}

static Dictionary<string, string> ParseOptions(string[] values, string[] allowed) {
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var index = 0; index < values.Length; index += 2) {
        var flag = values[index];
        if (!flag.StartsWith("--", StringComparison.Ordinal) || !allowed.Contains(flag[2..], StringComparer.Ordinal) ||
            index + 1 >= values.Length || string.IsNullOrWhiteSpace(values[index + 1]) ||
            values[index + 1].StartsWith("--", StringComparison.Ordinal) || !options.TryAdd(flag[2..], values[index + 1]))
            throw new ArgumentException("Invalid or repeated option. Run --help for usage.");
    }
    return options;
}

static string Required(Dictionary<string, string> options, string key) =>
    options.GetValueOrDefault(key) ?? throw new ArgumentException($"--{key} is required.");

static int NetworkPort(Dictionary<string, string> options) {
    if (!options.TryGetValue("port", out var value)) return defaultPort;
    return int.TryParse(value, out var port) && port is > 0 and <= 65535 ? port :
        throw new ArgumentException("Network port must be from 1 through 65535.");
}
