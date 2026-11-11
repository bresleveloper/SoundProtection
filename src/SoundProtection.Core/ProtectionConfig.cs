using System.Buffers.Binary;

namespace SoundProtection.Core;

/// <summary>What the tray tells the plug-in: protection on or off, and the loudness cap (with the safety cap's margin above it).</summary>
public sealed record ProtectionConfig(uint Sequence, bool Enabled, float CapDb, float MarginDb);

/// <summary>
/// The 32-byte <c>config.bin</c> that the tray writes and the plug-in reads. All fields are little-endian:
/// magic, version, sequence, enabled, capDb, marginDb, reserved, checksum. The checksum is FNV-1a over
/// bytes 0 to 27. The layout and the golden files are in native/apo/SpShared.h.
/// </summary>
public static class ProtectionConfigFile
{
    public const int Size = 32;

    public const float DefaultCapDb = -20.0f;
    public const float DefaultMarginDb = 12.0f;
    public const float MinCapDb = -60.0f;
    public const float MaxCapDb = 0.0f;
    public const float MinMarginDb = 0.0f;
    public const float MaxMarginDb = 30.0f;

    private const uint Magic = 0x46435053; // "SPCF"
    private const uint Version = 1;
    private const int ChecksummedBytes = 28;
    private const uint FnvOffsetBasis = 2166136261;
    private const uint FnvPrime = 16777619;
    private const float CeilingMaxDb = -1.0f;
    private const int MaxRetries = 10;
    private const int RetryDelayMs = 50;

    public static uint Fnv1a(ReadOnlySpan<byte> data)
    {
        var hash = FnvOffsetBasis;
        foreach (var b in data)
        {
            hash = unchecked((hash ^ b) * FnvPrime);
        }

        return hash;
    }

    /// <summary>The safety cap's ceiling: the cap plus the margin, but never above -1 dBFS.</summary>
    public static float CeilingDb(float capDb, float marginDb) => Math.Min(capDb + marginDb, CeilingMaxDb);

    /// <summary>The cap the plug-in will use for a setting: kept inside the range the file allows. Not a number gives the default.</summary>
    public static float EffectiveCapDb(double settingsCapDb) => ClampCap((float)settingsCapDb);

    /// <summary>The next sequence number. 0 means "defaults" in the plug-in's telemetry, so the count skips it.</summary>
    public static uint NextSequence(uint current) => current == uint.MaxValue ? 1 : current + 1;

    /// <summary>The 32 bytes for a config. The cap is held to [-60, 0] and the margin to [0, 30]; a value that is not a number becomes the default.</summary>
    public static byte[] ToBytes(ProtectionConfig config)
    {
        var bytes = new byte[Size];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), config.Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), config.Enabled ? 1u : 0u);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16), ClampCap(config.CapDb));
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(20), ClampMargin(config.MarginDb));
        // Bytes 24 to 27 are reserved and stay 0.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(ChecksummedBytes), Fnv1a(bytes.AsSpan(0, ChecksummedBytes)));
        return bytes;
    }

    /// <summary>
    /// Reads a config the way the plug-in does: exactly 32 bytes with the right magic, version and checksum,
    /// and finite levels, or nothing. Levels outside the allowed range are held to it.
    /// </summary>
    public static ProtectionConfig? FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != Version
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[ChecksummedBytes..]) != Fnv1a(bytes[..ChecksummedBytes]))
        {
            return null;
        }

        var cap = BinaryPrimitives.ReadSingleLittleEndian(bytes[16..]);
        var margin = BinaryPrimitives.ReadSingleLittleEndian(bytes[20..]);
        if (!float.IsFinite(cap) || !float.IsFinite(margin))
        {
            return null;
        }

        return new ProtectionConfig(
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]) != 0,
            ClampCap(cap),
            ClampMargin(margin));
    }

    /// <summary>Reads the file even while another process has it open. A missing, unreadable or invalid file gives null.</summary>
    public static ProtectionConfig? TryRead(string path)
    {
        var bytes = SharedFile.ReadUpTo(path, Size + 1); // one byte more than a config, so a longer file is seen as wrong
        return bytes is null ? null : FromBytes(bytes);
    }

    /// <summary>
    /// Writes the config to <c>path + ".tmp"</c>, then moves it over <paramref name="path"/>. The plug-in may have the file
    /// open for a moment, which makes the move fail; the write is tried again up to 10 times, 50 ms apart, and then
    /// throws. A missing folder is not retried and is never created: the folder belongs to the installer.
    /// </summary>
    public static void Write(string path, ProtectionConfig config)
    {
        var bytes = ToBytes(config);
        var temp = path + ".tmp";
        for (var retry = 0; ; retry++)
        {
            try
            {
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (IsSharingProblem(ex))
            {
                if (retry >= MaxRetries)
                {
                    TryDelete(temp);
                    throw;
                }

                Thread.Sleep(RetryDelayMs);
            }
            catch (Exception)
            {
                TryDelete(temp);
                throw;
            }
        }
    }

    private static float ClampCap(float capDb) => float.IsNaN(capDb) ? DefaultCapDb : Math.Clamp(capDb, MinCapDb, MaxCapDb);

    private static float ClampMargin(float marginDb) =>
        float.IsNaN(marginDb) ? DefaultMarginDb : Math.Clamp(marginDb, MinMarginDb, MaxMarginDb);

    // Windows reports a file that another process holds open as an IOException, or as UnauthorizedAccessException
    // when the move would replace it. Both can pass in a moment. A missing folder will not.
    private static bool IsSharingProblem(Exception ex) =>
        ex is (IOException or UnauthorizedAccessException) and not DirectoryNotFoundException;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // A leftover temp file is overwritten by the next write.
        }
    }
}

/// <summary>
/// Keeps <c>config.bin</c> equal to the settings. Call it from one thread. It never creates the data folder
/// (the installer does), and it writes only when the file differs from what the settings ask for.
/// </summary>
public sealed class ProtectionConfigWriter
{
    private readonly string _path;
    private uint _lastSequence;
    private volatile bool _lastAttemptFailed;

    public ProtectionConfigWriter(string path) => _path = path;

    /// <summary>
    /// True when the latest <see cref="Sync"/> could not write the file. The next one that writes it, or finds nothing to
    /// write (the file is right already, or there is no data folder), clears it. Safe to read from any thread.
    /// </summary>
    public bool LastAttemptFailed => _lastAttemptFailed;

    /// <summary>
    /// Writes the config if the file is missing, invalid or different. The new sequence is the existing file's plus one.
    /// Returns true when a file was written. Throws when the write fails after its retries.
    /// </summary>
    public bool Sync(bool enabled, double capDb)
    {
        try
        {
            var written = WriteIfDifferent(enabled, capDb);
            _lastAttemptFailed = false;
            return written;
        }
        catch (Exception)
        {
            _lastAttemptFailed = true;
            throw;
        }
    }

    private bool WriteIfDifferent(bool enabled, double capDb)
    {
        var folder = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return false;
        }

        var cap = ProtectionConfigFile.EffectiveCapDb(capDb);
        var existing = ProtectionConfigFile.TryRead(_path);
        if (existing is not null
            && existing.Enabled == enabled
            && existing.CapDb == cap
            && existing.MarginDb == ProtectionConfigFile.DefaultMarginDb)
        {
            _lastSequence = existing.Sequence;
            return false;
        }

        var next = ProtectionConfigFile.NextSequence(existing?.Sequence ?? _lastSequence);
        ProtectionConfigFile.Write(_path, new ProtectionConfig(next, enabled, cap, ProtectionConfigFile.DefaultMarginDb));
        _lastSequence = next;
        return true;
    }
}

/// <summary>Reads small files that another process may have open for writing.</summary>
internal static class SharedFile
{
    /// <summary>Up to <paramref name="maxBytes"/> from the start of the file, or null if it is missing or cannot be opened.</summary>
    public static byte[]? ReadUpTo(string path, int maxBytes)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null; // the usual case when the plug-in is not installed: no exception to catch every 100 ms
            }

            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[maxBytes];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = RandomAccess.Read(handle, buffer.AsSpan(total), total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return total == buffer.Length ? buffer : buffer.AsSpan(0, total).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
