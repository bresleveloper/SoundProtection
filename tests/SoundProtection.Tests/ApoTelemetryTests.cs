using System.Buffers.Binary;
using SoundProtection.Core;

namespace SoundProtection.Tests;

/// <summary>Builds a telemetry buffer with the offsets written out in native/apo/SpShared.h, so the tests do not share the parser's constants.</summary>
internal sealed class TelemetryBuffer
{
    public const int HeaderBytes = 64;
    public const int SlotBytes = 64;

    public byte[] Bytes { get; } = new byte[2112];

    public TelemetryBuffer()
    {
        U32(0, 0x4D545053); // "SPTM"
        U32(4, 1);          // version
        U32(8, 32);         // slotCount
        U32(12, 64);        // slotSize
    }

    public TelemetryBuffer Header(
        uint writerPid, uint configSequence, ulong heartbeat, long heartbeatFileTime, uint flags, float capDb, float ceilingDb)
    {
        U32(16, writerPid);
        U32(20, configSequence);
        U64(24, heartbeat);
        I64(32, heartbeatFileTime);
        U32(40, flags);
        F32(44, capDb);
        F32(48, ceilingDb);
        return this;
    }

    public TelemetryBuffer Slot(
        int index,
        uint kind,
        uint instanceId,
        uint sampleRate,
        uint channels,
        uint mode,
        uint clipperHits,
        ulong framesProcessed,
        long lastProcessFileTime,
        float inputDb,
        float gainReductionDb,
        float maxGainReductionDb,
        float outputPeakDb)
    {
        var o = HeaderBytes + index * SlotBytes;
        U32(o + 0, kind);
        U32(o + 4, instanceId);
        U32(o + 8, sampleRate);
        U32(o + 12, channels);
        U32(o + 16, mode);
        U32(o + 20, clipperHits);
        U64(o + 24, framesProcessed);
        I64(o + 32, lastProcessFileTime);
        F32(o + 40, inputDb);
        F32(o + 44, gainReductionDb);
        F32(o + 48, maxGainReductionDb);
        F32(o + 52, outputPeakDb);
        return this;
    }

    public TelemetryBuffer U32(int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(offset), value);
        return this;
    }

    private void U64(int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Bytes.AsSpan(offset), value);

    private void I64(int offset, long value) => BinaryPrimitives.WriteInt64LittleEndian(Bytes.AsSpan(offset), value);

    private void F32(int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(Bytes.AsSpan(offset), value);
}

public sealed class ApoTelemetryTests : IDisposable
{
    private static readonly DateTime Beat = new(2026, 11, 10, 12, 0, 0, 250, DateTimeKind.Utc);
    private static readonly DateTime Process = new(2026, 11, 10, 12, 0, 0, 200, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-tel-" + Guid.NewGuid().ToString("N"));

    public ApoTelemetryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Size_IsTheHeaderPlusThirtyTwoSlots() => Assert.Equal(64 + 32 * 64, ApoTelemetry.Size);

    [Fact]
    public void Parse_ReadsHeaderAndSlots()
    {
        var buffer = new TelemetryBuffer()
            .Header(4242, 42, 123_456_789_012UL, Beat.ToFileTimeUtc(), flags: 7, capDb: -20.0f, ceilingDb: -8.0f)
            .Slot(0, 1, 1, 48000, 2, 1, 0, 480_000UL, Process.ToFileTimeUtc(), -14.5f, 6.25f, 7.5f, -20.5f)
            .Slot(3, 2, 2, 44100, 1, 5, 9, 9_876_543_210UL, Process.AddMilliseconds(30).ToFileTimeUtc(), -9.0f, 2.5f, 3.0f, -8.25f);

        var snapshot = ApoTelemetry.Parse(buffer.Bytes);

        Assert.NotNull(snapshot);
        var h = snapshot.Header;
        Assert.Equal(4242u, h.WriterPid);
        Assert.Equal(42u, h.ConfigSequence);
        Assert.Equal(123_456_789_012UL, h.Heartbeat);
        Assert.Equal(Beat, h.HeartbeatUtc);
        Assert.Equal(7u, h.Flags);
        Assert.Equal(-20.0f, h.CapDb);
        Assert.Equal(-8.0f, h.CeilingDb);
        Assert.True(h.ConfigValid);
        Assert.True(h.SafeMode);
        Assert.True(h.Enabled);

        Assert.Equal(2, snapshot.Slots.Count);

        var leveler = snapshot.Slots[0];
        Assert.Equal(SlotKind.Leveler, leveler.Kind);
        Assert.Equal(1u, leveler.InstanceId);
        Assert.Equal(48000u, leveler.SampleRate);
        Assert.Equal(2u, leveler.Channels);
        Assert.Equal(ApoMode.Default, leveler.Mode);
        Assert.Equal(0u, leveler.ClipperHits);
        Assert.Equal(480_000UL, leveler.FramesProcessed);
        Assert.Equal(Process, leveler.LastProcessUtc);
        Assert.Equal(-14.5f, leveler.InputDb);
        Assert.Equal(6.25f, leveler.GainReductionDb);
        Assert.Equal(7.5f, leveler.MaxGainReductionDb);
        Assert.Equal(-20.5f, leveler.OutputPeakDb);

        var cap = snapshot.Slots[1];
        Assert.Equal(SlotKind.SafetyCap, cap.Kind);
        Assert.Equal(2u, cap.InstanceId);
        Assert.Equal(44100u, cap.SampleRate);
        Assert.Equal(1u, cap.Channels);
        Assert.Equal(ApoMode.Media, cap.Mode);
        Assert.Equal(9u, cap.ClipperHits);
        Assert.Equal(9_876_543_210UL, cap.FramesProcessed); // more than 32 bits
        Assert.Equal(Process.AddMilliseconds(30), cap.LastProcessUtc);
        Assert.Equal(-9.0f, cap.InputDb);
        Assert.Equal(2.5f, cap.GainReductionDb);
        Assert.Equal(3.0f, cap.MaxGainReductionDb);
        Assert.Equal(-8.25f, cap.OutputPeakDb);
    }

    [Theory]
    [InlineData(0u, false, false, false)]
    [InlineData(1u, true, false, false)]
    [InlineData(2u, false, true, false)]
    [InlineData(4u, false, false, true)]
    [InlineData(5u, true, false, true)]
    public void Flags_MapToTheThreeProperties(uint flags, bool configValid, bool safeMode, bool enabled)
    {
        var buffer = new TelemetryBuffer().Header(1, 1, 1, Beat.ToFileTimeUtc(), flags, -20.0f, -8.0f);

        var h = ApoTelemetry.Parse(buffer.Bytes)!.Header;

        Assert.Equal(configValid, h.ConfigValid);
        Assert.Equal(safeMode, h.SafeMode);
        Assert.Equal(enabled, h.Enabled);
    }

    [Fact]
    public void Parse_SkipsFreeSlots()
    {
        var buffer = new TelemetryBuffer().Header(1, 1, 1, Beat.ToFileTimeUtc(), 5, -20.0f, -8.0f);
        buffer.Slot(0, 1, 11, 48000, 2, 1, 0, 1, Process.ToFileTimeUtc(), -20f, 0f, 0f, -30f);
        buffer.Slot(5, 2, 22, 48000, 2, 1, 0, 1, Process.ToFileTimeUtc(), -20f, 0f, 0f, -30f);
        buffer.Slot(31, 1, 33, 48000, 2, 1, 0, 1, Process.ToFileTimeUtc(), -20f, 0f, 0f, -30f);
        buffer.Slot(7, 0, 99, 48000, 2, 1, 0, 1, Process.ToFileTimeUtc(), -20f, 0f, 0f, -30f); // kind 0 = free, even with data in it

        var snapshot = ApoTelemetry.Parse(buffer.Bytes);

        Assert.NotNull(snapshot);
        Assert.Equal(new uint[] { 11, 22, 33 }, snapshot.Slots.Select(s => s.InstanceId).ToArray());
    }

    [Fact]
    public void Parse_AllSlotsFree_GivesAnEmptyList()
    {
        var snapshot = ApoTelemetry.Parse(new TelemetryBuffer().Bytes);

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.Slots);
    }

    [Fact]
    public void Parse_UnknownKindOrMode_IsHandledWithoutThrowing()
    {
        var buffer = new TelemetryBuffer().Header(1, 1, 1, Beat.ToFileTimeUtc(), 5, -20.0f, -8.0f);
        buffer.Slot(0, 77, 1, 48000, 2, 1, 0, 1, Process.ToFileTimeUtc(), -20f, 0f, 0f, -30f);  // a kind this build does not know
        buffer.Slot(1, 1, 2, 48000, 2, 9999, 0, 1, Process.ToFileTimeUtc(), -20f, 0f, 0f, -30f); // a mode this build does not know

        var snapshot = ApoTelemetry.Parse(buffer.Bytes);

        Assert.NotNull(snapshot);
        var only = Assert.Single(snapshot.Slots);
        Assert.Equal(2u, only.InstanceId);
        Assert.Equal(ApoMode.Unknown, only.Mode);
    }

    [Fact]
    public void Parse_WrongMagic_ReturnsNull()
    {
        var buffer = new TelemetryBuffer().U32(0, 0x12345678);

        Assert.Null(ApoTelemetry.Parse(buffer.Bytes));
    }

    [Fact]
    public void Parse_WrongVersion_ReturnsNull()
    {
        var buffer = new TelemetryBuffer().U32(4, 2);

        Assert.Null(ApoTelemetry.Parse(buffer.Bytes));
    }

    [Fact]
    public void Parse_Short_ReturnsNull()
    {
        var good = new TelemetryBuffer().Bytes;

        Assert.Null(ApoTelemetry.Parse(good.AsSpan(0, ApoTelemetry.Size - 1)));
        Assert.Null(ApoTelemetry.Parse(good.AsSpan(0, 64)));
        Assert.Null(ApoTelemetry.Parse(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Parse_LongerBuffer_StillParsesTheFirstPart()
    {
        var longer = new byte[ApoTelemetry.Size + 100];
        new TelemetryBuffer().Header(77, 1, 1, Beat.ToFileTimeUtc(), 5, -20.0f, -8.0f).Bytes.CopyTo(longer, 0);

        Assert.Equal(77u, ApoTelemetry.Parse(longer)?.Header.WriterPid);
    }

    [Fact]
    public void Parse_ZeroFileTime_IsMinValue()
    {
        var buffer = new TelemetryBuffer()
            .Header(1, 1, 1, heartbeatFileTime: 0, flags: 5, capDb: -20.0f, ceilingDb: -8.0f)
            .Slot(0, 1, 1, 48000, 2, 1, 0, 0, lastProcessFileTime: 0, -120f, 0f, 0f, -120f);

        var snapshot = ApoTelemetry.Parse(buffer.Bytes);

        Assert.NotNull(snapshot);
        Assert.Equal(DateTime.MinValue, snapshot.Header.HeartbeatUtc);
        Assert.Equal(DateTime.MinValue, snapshot.Slots[0].LastProcessUtc);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void Parse_AbsurdFileTimes_AreMinValueNotAnException(long fileTime)
    {
        var buffer = new TelemetryBuffer()
            .Header(1, 1, 1, fileTime, 5, -20.0f, -8.0f)
            .Slot(0, 1, 1, 48000, 2, 1, 0, 1, fileTime, -20f, 0f, 0f, -30f);

        var snapshot = ApoTelemetry.Parse(buffer.Bytes);

        Assert.NotNull(snapshot);
        Assert.Equal(DateTime.MinValue, snapshot.Header.HeartbeatUtc);
        Assert.Equal(DateTime.MinValue, snapshot.Slots[0].LastProcessUtc);
    }

    [Fact]
    public void TryRead_ReadsAFileThatItsWriterStillHasOpen()
    {
        var path = Path.Combine(_dir, "apo.bin");
        var buffer = new TelemetryBuffer()
            .Header(4242, 42, 5, Beat.ToFileTimeUtc(), 5, -20.0f, -8.0f)
            .Slot(0, 2, 1, 48000, 2, 1, 0, 1, Process.ToFileTimeUtc(), -20f, 1f, 1f, -30f);
        using var writer = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
        writer.Write(buffer.Bytes);
        writer.Flush();

        var snapshot = ApoTelemetry.TryRead(path);

        Assert.NotNull(snapshot);
        Assert.Equal(4242u, snapshot.Header.WriterPid);
        Assert.Equal(SlotKind.SafetyCap, Assert.Single(snapshot.Slots).Kind);
    }

    [Fact]
    public void TryRead_MissingEmptyOrWrongFile_ReturnsNull()
    {
        var path = Path.Combine(_dir, "apo.bin");
        Assert.Null(ApoTelemetry.TryRead(path));
        Assert.Null(ApoTelemetry.TryRead(Path.Combine(_dir, "no-such-folder", "apo.bin")));

        File.WriteAllBytes(path, Array.Empty<byte>());
        Assert.Null(ApoTelemetry.TryRead(path));

        File.WriteAllBytes(path, new byte[ApoTelemetry.Size]); // right size, no magic
        Assert.Null(ApoTelemetry.TryRead(path));
    }
}
