using System.Buffers.Binary;

namespace SoundProtection.Core;

public enum SlotKind : uint
{
    Free = 0,
    Leveler = 1,
    SafetyCap = 2,
}

public enum ApoMode : uint
{
    Unknown = 0,
    Default = 1,
    Raw = 2,
    Communications = 3,
    Speech = 4,
    Media = 5,
    Movie = 6,
    Notification = 7,
    Other = 8,
}

/// <summary>The telemetry file's header. <c>HeartbeatUtc</c> is <see cref="DateTime.MinValue"/> when the plug-in has not stamped it.</summary>
public sealed record TelemetryHeader(
    uint WriterPid,
    uint ConfigSequence,
    ulong Heartbeat,
    DateTime HeartbeatUtc,
    uint Flags,
    float CapDb,
    float CeilingDb)
{
    /// <summary>A valid config.bin is in use (otherwise the plug-in runs on its defaults).</summary>
    public bool ConfigValid => (Flags & 0x1) != 0;

    /// <summary>The crash-loop guard tripped: the plug-in only clamps.</summary>
    public bool SafeMode => (Flags & 0x2) != 0;

    /// <summary>The config in use has protection switched on.</summary>
    public bool Enabled => (Flags & 0x4) != 0;
}

/// <summary>One running effect: a Leveler on one stream, or the Safety Cap on the whole mix.</summary>
public sealed record TelemetrySlot(
    SlotKind Kind,
    uint InstanceId,
    uint SampleRate,
    uint Channels,
    ApoMode Mode,
    uint ClipperHits,
    ulong FramesProcessed,
    DateTime LastProcessUtc,
    float InputDb,
    float GainReductionDb,
    float MaxGainReductionDb,
    float OutputPeakDb);

/// <summary>The header and the slots that are in use. Free slots are left out.</summary>
public sealed record TelemetrySnapshot(TelemetryHeader Header, IReadOnlyList<TelemetrySlot> Slots);

/// <summary>
/// Reads the plug-in's memory-mapped telemetry file: a 64-byte header and 32 slots of 64 bytes, 2112 bytes in all,
/// little-endian. The offsets are in native/apo/SpShared.h.
/// </summary>
public static class ApoTelemetry
{
    public const int Size = HeaderBytes + SlotCount * SlotBytes;

    private const int HeaderBytes = 64;
    private const int SlotBytes = 64;
    private const int SlotCount = 32;
    private const uint Magic = 0x4D545053; // "SPTM"
    private const uint Version = 1;

    // The newest FILETIME that DateTime can hold (year 9999); anything above it would throw.
    private const long MaxFileTime = 2_650_467_743_999_999_999L;

    /// <summary>The snapshot in the buffer, or null if it is too short or its magic or version is wrong. Never throws.</summary>
    public static TelemetrySnapshot? Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Size
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != Version)
        {
            return null;
        }

        var header = new TelemetryHeader(
            WriterPid: BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]),
            ConfigSequence: BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]),
            Heartbeat: BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]),
            HeartbeatUtc: FromFileTime(BinaryPrimitives.ReadInt64LittleEndian(bytes[32..])),
            Flags: BinaryPrimitives.ReadUInt32LittleEndian(bytes[40..]),
            CapDb: BinaryPrimitives.ReadSingleLittleEndian(bytes[44..]),
            CeilingDb: BinaryPrimitives.ReadSingleLittleEndian(bytes[48..]));

        var slots = new List<TelemetrySlot>();
        for (var i = 0; i < SlotCount; i++)
        {
            var slot = bytes.Slice(HeaderBytes + i * SlotBytes, SlotBytes);
            var kind = BinaryPrimitives.ReadUInt32LittleEndian(slot);
            if (kind is not ((uint)SlotKind.Leveler or (uint)SlotKind.SafetyCap))
            {
                continue; // free, or a kind this build does not know
            }

            var mode = BinaryPrimitives.ReadUInt32LittleEndian(slot[16..]);
            slots.Add(new TelemetrySlot(
                Kind: (SlotKind)kind,
                InstanceId: BinaryPrimitives.ReadUInt32LittleEndian(slot[4..]),
                SampleRate: BinaryPrimitives.ReadUInt32LittleEndian(slot[8..]),
                Channels: BinaryPrimitives.ReadUInt32LittleEndian(slot[12..]),
                Mode: mode is >= (uint)ApoMode.Default and <= (uint)ApoMode.Other ? (ApoMode)mode : ApoMode.Unknown,
                ClipperHits: BinaryPrimitives.ReadUInt32LittleEndian(slot[20..]),
                FramesProcessed: BinaryPrimitives.ReadUInt64LittleEndian(slot[24..]),
                LastProcessUtc: FromFileTime(BinaryPrimitives.ReadInt64LittleEndian(slot[32..])),
                InputDb: BinaryPrimitives.ReadSingleLittleEndian(slot[40..]),
                GainReductionDb: BinaryPrimitives.ReadSingleLittleEndian(slot[44..]),
                MaxGainReductionDb: BinaryPrimitives.ReadSingleLittleEndian(slot[48..]),
                OutputPeakDb: BinaryPrimitives.ReadSingleLittleEndian(slot[52..])));
        }

        return new TelemetrySnapshot(header, slots);
    }

    /// <summary>Reads the file even while the plug-in has it mapped. A missing, unreadable or invalid file gives null.</summary>
    public static TelemetrySnapshot? TryRead(string path)
    {
        var bytes = SharedFile.ReadUpTo(path, Size);
        return bytes is null ? null : Parse(bytes);
    }

    // 0 means "never stamped". A value that DateTime cannot hold is as good as never stamped.
    private static DateTime FromFileTime(long fileTime) =>
        fileTime <= 0 || fileTime > MaxFileTime ? DateTime.MinValue : DateTime.FromFileTimeUtc(fileTime);
}
