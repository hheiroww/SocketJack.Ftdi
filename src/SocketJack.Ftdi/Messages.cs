namespace SocketJack.Ftdi;

/// <summary>Open the configured serial profile over TLS using a 32-byte key.</summary>
public sealed record OpenSerial(int Version, byte[] Key);

/// <summary>Server reply. Only an accepted reply enables serial traffic.</summary>
public sealed record SerialStatus(bool Open, string Code, int Baud, string Profile);

/// <summary>Ordered binary bytes for the one open serial session.</summary>
public sealed record SerialData(byte[] Bytes);

/// <summary>The sending side has no more serial bytes. The stream remains readable.</summary>
public sealed record SerialEnd();
