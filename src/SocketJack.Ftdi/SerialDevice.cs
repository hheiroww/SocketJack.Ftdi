using System.IO.Ports;
using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

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

internal sealed class FtdiVcp : ISerialDevice {
    private readonly SerialPort serial;

    internal static string[] Ports() {
        if (OperatingSystem.IsWindows()) return WindowsPorts();
        if (OperatingSystem.IsLinux()) return LinuxFtdiPorts.Discover("/sys", "/dev");
        throw new PlatformNotSupportedException("FTDI port discovery requires Windows or Linux.");
    }

    internal static void RequirePresentPort(string port) {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (!Ports().Contains(port, comparer))
            throw new IOException("The configured device is not an active FTDI serial port. Run ports to list candidates.");
    }

    // FTDIBUS is owned by the FTDI VCP driver. Intersect its known COM ports
    // with ports the operating system reports as currently connected.
    [SupportedOSPlatform("windows")]
    private static string[] WindowsPorts() {
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
        RequirePresentPort(settings.Port);
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

internal static partial class LinuxFtdiPorts {
    [GeneratedRegex(@"^ttyUSB[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex PortName();

    internal static string[] Discover(string sysRoot, string devRoot, Func<DirectoryInfo, DirectoryInfo?>? resolve = null) {
        resolve ??= link => link.ResolveLinkTarget(returnFinalTarget: true) as DirectoryInfo;
        var tty = new DirectoryInfo(Path.Combine(sysRoot, "class", "tty"));
        if (!tty.Exists) return [];
        var devices = Path.Combine(sysRoot, "devices");
        var usbDriver = Path.Combine(sysRoot, "bus", "usb", "drivers", "ftdi_sio");
        var serialDriver = Path.Combine(sysRoot, "bus", "usb-serial", "drivers", "ftdi_sio");
        var ports = new List<string>();
        foreach (var entry in tty.EnumerateDirectories("ttyUSB*")) {
            if (!PortName().IsMatch(entry.Name)) continue;
            var port = new FileInfo(Path.Combine(devRoot, entry.Name));
            try {
                if (!port.Exists || port.LinkTarget is not null) continue;
                var physical = resolve(entry);
                if (physical is null || !IsBelow(physical, devices)) continue;
                for (var device = physical; device is not null && IsBelow(device, devices); device = device.Parent) {
                    var driver = resolve(new DirectoryInfo(Path.Combine(device.FullName, "driver")));
                    if (driver is null) continue;
                    if (driver.FullName == serialDriver || driver.FullName == usbDriver) ports.Add(port.FullName);
                    break; // The nearest driver is decisive.
                }
            } catch (IOException) { /* Disconnect can invalidate sysfs links during enumeration. */ }
            catch (UnauthorizedAccessException) { /* Skip inaccessible adapter metadata. */ }
        }
        return ports.Order(StringComparer.Ordinal).ToArray();
    }

    private static bool IsBelow(DirectoryInfo device, string root) {
        var relative = Path.GetRelativePath(root, device.FullName);
        return relative != "." && !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
