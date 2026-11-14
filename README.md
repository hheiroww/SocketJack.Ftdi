# SocketJack.Ftdi

SocketJack.Ftdi connects an FTDI serial adapter on Windows or Linux to a remote process over an authenticated SocketJack TLS connection. The client reads bytes from standard input and writes serial responses to standard output. It runs on .NET 10 and uses the public [SocketJack NuGet package](https://www.nuget.org/packages/SocketJack/2026.11.14).

## Install the .NET tool

On a supported Windows or Linux system with the .NET 10 SDK installed:

```sh
dotnet tool install --global SocketJack.Ftdi
socketjack-ftdi --help
socketjack-ftdi ports
```

The same package works on Linux x64, ARM64 and ARMv7 when the board has a compatible .NET 10 installation. Use the self-contained build instructions below when you want to copy the relay to a board without installing .NET on it.

## Requirements

- On Windows, install the FTDI virtual COM port (VCP) driver. In Device Manager, confirm that **Ports (COM & LPT)** shows **USB Serial Port (COMx)**. [FTDI VCP drivers](https://ftdichip.com/drivers/vcp-drivers/)
- If Windows shows **USB Serial Converter** with no COM port, open that device’s **Properties**, select **Advanced**, and enable **Load VCP** if the driver offers it. Reconnect the adapter and check **Ports (COM & LPT)** again. [FTDI Advanced Driver Options, §2.1](https://ftdichip.com/wp-content/uploads/2020/08/AN_107_AdvancedDriverOptions_AN_000073.pdf)
- Use a PFX server certificate with the private key and a hostname or IP address matching the client’s TLS name. The client uses the operating system’s certificate trust and validates that name. Both sides need the same 32-byte key, stored as Base64 in `SOCKETJACK_FTDI_KEY`.

The bridge carries UART data using an existing serial port. It opens one serial connection at a time. Local programs using that port must release it first. Direct D2XX calls, USB forwarding, client-side COM emulation, and ECU-specific logging protocols are outside this bridge’s scope. [FTDI driver model](https://ftdichip.com/drivers/)

## Raspberry Pi and Linux ARM

Use a .NET 10-compatible Linux system with the FTDI `ftdi_sio` kernel driver. Plug the adapter into a USB host port and give the relay user read/write access to its device node. Debian-based systems commonly assign the serial device to `dialout`. The `ports` command lists detected FTDI devices by their `/dev/ttyUSBn` paths.

Publish for the board’s OS architecture from a machine with the .NET 10 SDK. Use `linux-arm64` for a 64-bit OS or `linux-arm` for supported 32-bit ARM Linux. The package includes the .NET runtime and uses your system’s native Linux libraries and certificate store. [Microsoft Linux ARM deployment](https://learn.microsoft.com/en-us/dotnet/iot/deployment)

```sh
dotnet publish src/SocketJack.Ftdi -c Release -r linux-arm64 --self-contained true -o artifacts/linux-arm64
dotnet publish src/SocketJack.Ftdi -c Release -r linux-arm --self-contained true -o artifacts/linux-arm
```

Copy the matching publish folder to the board and run the binary there.

```sh
chmod +x SocketJack.Ftdi
./SocketJack.Ftdi ports
./SocketJack.Ftdi serve --cert /path/to/server.pfx --serial /dev/ttyUSB0 --baud 115200 --listen 192.168.1.20
```

Use the board’s LAN or VPN IPv4 address in place of `192.168.1.20`. Supply the same environment key and trusted certificate as in the Windows example.

Raspberry Pi models with a supported ARMv7 or ARM64 Linux OS can use the matching build. The original Pi Zero and Zero W use ARMv6. Arduino UNO Q can run Linux applications on its Debian processor. Bare-metal Arduino boards would need separate firmware. [Raspberry Pi Zero specifications](https://www.raspberrypi.com/documentation/computers/zero.html) · [Arduino UNO Q](https://docs.arduino.cc/hardware/uno-q)

## Build and connect

```powershell
dotnet build src/SocketJack.Ftdi/SocketJack.Ftdi.csproj -c Release
$bridge = 'src/SocketJack.Ftdi/bin/Release/net10.0/SocketJack.Ftdi.dll'
dotnet $bridge ports
```

Generate the shared key once with `dotnet $bridge keygen` and store its output securely. Supply the same key in the host and client process environments through your secret manager. If the PFX needs a password, supply it to the host as `SOCKETJACK_FTDI_CERT_PASSWORD`.

```powershell
dotnet $bridge serve --cert server.pfx --serial COM3 --baud 115200 --profile 8n1
```

In a second terminal, use Command Prompt for the example below. Provide a binary request file for the device connected to the adapter.

```cmd
dotnet src\SocketJack.Ftdi\bin\Release\net10.0\SocketJack.Ftdi.dll connect --host localhost --tls-name localhost < request.bin > response.bin
```

The example uses loopback on TCP port 4465 and requires a certificate trusted for `localhost`. For another machine, pass the host’s private LAN or VPN IPv4 address to `serve --listen` and use that address for `connect --host`. Use the certificate’s name for `--tls-name` when it differs from the connection address. Add `--port` to both commands to select another TCP port.

The serial profiles are `8n1`, `7e1`, `8e1`, `8o1`, and `8n2`. Set the baud rate and profile for the device connected to the adapter. Default serial settings are 115200 baud and 8N1, with flow control disabled. Use binary stdin and stdout redirection with a process or shell that preserves bytes.

## Protocol

The peers exchange four typed messages over a trusted SocketJack TLS connection. The host validates the shared key in **OpenSerial** version 1 before opening the configured COM port. **SerialStatus** reports the baud rate, profile, and whether the port could be opened. **SerialData** carries up to 4,096 bytes per message. **SerialEnd** closes a sending direction. After client input closes, the host drains the response for up to 30 seconds and ends the session after a 750 ms quiet period. Leave stdin open for a continuous session.

## Checks

```powershell
dotnet run --project checks/SocketJack.Ftdi.Checks -c Release
```

The checks use a simulated serial device and a loopback network connection. They use an existing trusted development certificate for `localhost` when available. Otherwise, set `SOCKETJACK_FTDI_TEST_CERT` to a trusted PFX and `SOCKETJACK_FTDI_TEST_NAME` to its hostname. A physical adapter, its driver, and the attached serial device are required to verify hardware operation.

SocketJack.Ftdi is [MIT licensed](LICENSE), as is the upstream [SocketJack library](https://github.com/hheiroww/SocketJack/blob/master/LICENSE).
