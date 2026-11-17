using System.Buffers.Binary;

namespace SoundProtection.Core;

/// <summary>What the tray tells the plug-in about the bass and treble caps: whether each is on, and the level it holds its band to.</summary>
public sealed record BandsConfig(uint Sequence, bool BassOn, float BassCapDb, bool TrebleOn, float TrebleCapDb);

/// <summary>
/// The 32-byte <c>bands.bin</c> that the tray writes and the plug-in reads, next to <c>config.bin</c>. All fields are little-endian:
/// magic, version, sequence, flags (bit 0 bass on, bit 1 treble on), bass cap, treble cap, reserved, checksum. The checksum is
/// FNV-1a over bytes 0 to 27. The layout and the golden files are in native/apo/SpShared.h.
/// </summary>
public static class BandsConfigFile
{
    public const int Size = 32;

    public const float MinCapDb = -60.0f;
    public const float MaxCapDb = 0.0f;

    private const uint Magic = 0x44425053; // "SPBD"
    private const uint Version = 1;
    private const uint BassFlag = 0x1;
    private const uint TrebleFlag = 0x2;
    private const int ChecksummedBytes = 28;

    /// <summary>The 32 bytes for a config. Each cap is held to [-60, 0], and one that is not a number becomes 0.</summary>
    public static byte[] ToBytes(BandsConfig config)
    {
        var bytes = new byte[Size];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), config.Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (config.BassOn ? BassFlag : 0u) | (config.TrebleOn ? TrebleFlag : 0u));
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16), ClampCap(config.BassCapDb));
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(20), ClampCap(config.TrebleCapDb));
        // Bytes 24 to 27 are reserved and stay 0.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(ChecksummedBytes), ProtectionConfigFile.Fnv1a(bytes.AsSpan(0, ChecksummedBytes)));
        return bytes;
    }

    /// <summary>
    /// Reads a config the way the plug-in does: exactly 32 bytes with the right magic, version and checksum, or nothing.
    /// A cap outside the range is held to it, and one that is not a number reads as 0. Flag bits other than the two caps' are ignored.
    /// </summary>
    public static BandsConfig? FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != Version
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[ChecksummedBytes..]) != ProtectionConfigFile.Fnv1a(bytes[..ChecksummedBytes]))
        {
            return null;
        }

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        return new BandsConfig(
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]),
            (flags & BassFlag) != 0,
            ClampCap(BinaryPrimitives.ReadSingleLittleEndian(bytes[16..])),
            (flags & TrebleFlag) != 0,
            ClampCap(BinaryPrimitives.ReadSingleLittleEndian(bytes[20..])));
    }

    /// <summary>Reads the file even while another process has it open. A missing, unreadable or invalid file gives null.</summary>
    public static BandsConfig? TryRead(string path)
    {
        var bytes = SharedFile.ReadUpTo(path, Size + 1); // one byte more than a bands file, so a longer file is seen as wrong
        return bytes is null ? null : FromBytes(bytes);
    }

    /// <summary>
    /// Writes the config to <c>path + ".tmp"</c>, then moves it over <paramref name="path"/>. The plug-in may have the file
    /// open for a moment, which makes the move fail; the write is tried again up to 10 times, 50 ms apart, and then
    /// throws. A missing folder is not retried and is never created: the folder belongs to the installer.
    /// </summary>
    public static void Write(string path, BandsConfig config) => SharedFile.WriteReplacing(path, ToBytes(config));

    /// <summary>
    /// The config for the two sliders. A band is on when its slider is below 0 dB, so the right end is Off. A slider
    /// outside the range is held to it first; one that is not a number counts as 0, which is Off.
    /// </summary>
    public static BandsConfig FromSliders(uint sequence, double bassCapDb, double trebleCapDb)
    {
        var bass = ClampCap((float)bassCapDb);
        var treble = ClampCap((float)trebleCapDb);
        return new BandsConfig(sequence, bass < 0.0f, bass, treble < 0.0f, treble);
    }

    private static float ClampCap(float capDb) => float.IsNaN(capDb) ? 0.0f : Math.Clamp(capDb, MinCapDb, MaxCapDb);
}

/// <summary>
/// Keeps <c>bands.bin</c> equal to the sliders. Call it from one thread. It never creates the data folder
/// (the installer does), and it writes only when the file differs from what the sliders ask for.
/// </summary>
public sealed class BandsConfigWriter
{
    private readonly string _path;
    private uint _lastSequence;
    private volatile bool _lastAttemptFailed;

    public BandsConfigWriter(string path) => _path = path;

    /// <summary>
    /// True when the latest <see cref="Sync"/> could not write the file. The next one that writes it, or finds nothing to
    /// write (the file is right already, or there is no data folder), clears it. Safe to read from any thread.
    /// </summary>
    public bool LastAttemptFailed => _lastAttemptFailed;

    /// <summary>
    /// Writes the config if the file is missing, invalid or different in a flag or a cap. The new sequence is the existing
    /// file's plus one. Returns true when a file was written. Throws when the write fails after its retries.
    /// </summary>
    public bool Sync(double bassCapDb, double trebleCapDb)
    {
        try
        {
            var written = WriteIfDifferent(bassCapDb, trebleCapDb);
            _lastAttemptFailed = false;
            return written;
        }
        catch (Exception)
        {
            _lastAttemptFailed = true;
            throw;
        }
    }

    private bool WriteIfDifferent(double bassCapDb, double trebleCapDb)
    {
        var folder = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return false;
        }

        var wanted = BandsConfigFile.FromSliders(0, bassCapDb, trebleCapDb);
        var existing = BandsConfigFile.TryRead(_path);
        if (existing is not null
            && existing.BassOn == wanted.BassOn
            && existing.BassCapDb == wanted.BassCapDb
            && existing.TrebleOn == wanted.TrebleOn
            && existing.TrebleCapDb == wanted.TrebleCapDb)
        {
            _lastSequence = existing.Sequence;
            return false;
        }

        var next = ProtectionConfigFile.NextSequence(existing?.Sequence ?? _lastSequence);
        BandsConfigFile.Write(_path, wanted with { Sequence = next });
        _lastSequence = next;
        return true;
    }
}
