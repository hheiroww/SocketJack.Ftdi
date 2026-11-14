using System.IO.Ports;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace SocketJack.Ftdi;

internal sealed record SerialSettings(string Port, int Baud, string Profile) {
    public static SerialSettings Parse(string port, string baud, string profile) {
        if (!int.TryParse(baud, out var rate) || rate is < 300 or > 3_000_000)
            throw new ArgumentException("Baud must be an integer from 300 through 3000000.");
        if (profile is not ("8n1" or "7e1" or "8e1" or "8o1" or "8n2"))
            throw new ArgumentException("Use serial profile 8n1, 7e1, 8e1, 8o1, or 8n2.");
        return new SerialSettings(port, rate, profile);
    }
}

internal interface ISerialDevice : IDisposable {
    int Read(byte[] buffer);
    void Write(byte[] buffer);
}

[SupportedOSPlatform("windows")]
internal sealed class FtdiVcp : ISerialDevice {
    private readonly SerialPort serial;

    // FTDIBUS is owned by the FTDI VCP driver. Intersect its known COM ports
    // with ports the operating system reports as currently connected.
    internal static string[] Ports() {
        var active = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var bus = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\FTDIBUS");
        if (bus is null) return [];
        foreach (var deviceName in bus.GetSubKeyNames()) {
            using var device = bus.OpenSubKey(deviceName);
            if (device is null) continue;
            foreach (var instance in device.GetSubKeyNames()) {
                using var parameters = device.OpenSubKey(instance + @"\Device Parameters");
                if (parameters?.GetValue("PortName") is string port && active.Contains(port)) result.Add(port);
            }
        }
        return result.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public FtdiVcp(SerialSettings settings) {
        if (!Ports().Contains(settings.Port, StringComparer.OrdinalIgnoreCase))
            throw new IOException("The configured port is not a present FTDI Virtual COM Port.");
        var parity = settings.Profile[1] switch { 'e' => Parity.Even, 'o' => Parity.Odd, _ => Parity.None };
        serial = new SerialPort(settings.Port, settings.Baud, parity, settings.Profile[0] - '0',
            settings.Profile[2] == '2' ? StopBits.Two : StopBits.One) {
            Handshake = Handshake.None, DtrEnable = false, RtsEnable = false,
            ReadTimeout = 100, WriteTimeout = 1000
        };
        try { serial.Open(); }
        catch { serial.Dispose(); throw; }
    }

    public int Read(byte[] buffer) {
        try { return serial.Read(buffer, 0, buffer.Length); }
        catch (TimeoutException) { return 0; }
    }

    public void Write(byte[] buffer) => serial.Write(buffer, 0, buffer.Length);
    public void Dispose() => serial.Dispose();
}
