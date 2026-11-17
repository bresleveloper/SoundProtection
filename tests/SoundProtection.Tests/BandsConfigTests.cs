using System.Buffers.Binary;
using SoundProtection.Core;

namespace SoundProtection.Tests;

public sealed class BandsConfigTests : IDisposable
{
    // The golden files from native/apo/SpShared.h, byte for byte.
    private static readonly byte[] GoldenB1 = Convert.FromHexString(
        "53504244" + "01000000" + "01000000" + "03000000" + "0000F0C1" + "00000CC2" + "00000000" + "3A069816");

    private static readonly byte[] GoldenB2 = Convert.FromHexString(
        "53504244" + "01000000" + "2A000000" + "02000000" + "00000000" + "0000DCC1" + "00000000" + "4A2C0A69");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-bands-" + Guid.NewGuid().ToString("N"));

    public BandsConfigTests() => Directory.CreateDirectory(_dir);

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

    private string BandsPath => Path.Combine(_dir, "bands.bin");

    private static byte[] With(byte[] bytes, int index, byte value)
    {
        var copy = (byte[])bytes.Clone();
        copy[index] = value;
        return copy;
    }

    // A file the plug-in would accept, apart from whatever the test changed: the checksum is made right again.
    private static byte[] WithRecomputedChecksum(byte[] bytes)
    {
        var copy = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(28), ProtectionConfigFile.Fnv1a(copy.AsSpan(0, 28)));
        return copy;
    }

    private static byte[] B1WithCaps(float bassCapDb, float trebleCapDb)
    {
        var bytes = (byte[])GoldenB1.Clone();
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(16), bassCapDb);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(20), trebleCapDb);
        return WithRecomputedChecksum(bytes);
    }

    private static byte[] B1WithFlags(uint flags)
    {
        var bytes = (byte[])GoldenB1.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), flags);
        return WithRecomputedChecksum(bytes);
    }

    // ---- The bytes ----

    [Fact]
    public void ToBytes_GoldenB1() =>
        Assert.Equal(GoldenB1, BandsConfigFile.ToBytes(new(1, true, -30f, true, -35f)));

    [Fact]
    public void ToBytes_GoldenB2() =>
        Assert.Equal(GoldenB2, BandsConfigFile.ToBytes(new(42, false, 0f, true, -27.5f)));

    [Fact]
    public void FromBytes_GoldenB1_RoundTrips()
    {
        var read = BandsConfigFile.FromBytes(GoldenB1);

        Assert.Equal(new BandsConfig(1, true, -30f, true, -35f), read);
        Assert.Equal(GoldenB1, BandsConfigFile.ToBytes(read!));
    }

    [Fact]
    public void FromBytes_GoldenB2_ReadsTheTrebleFlagAlone() =>
        Assert.Equal(new BandsConfig(42, false, 0f, true, -27.5f), BandsConfigFile.FromBytes(GoldenB2));

    [Theory]
    [InlineData(1u, true, -30.0f, true, -35.0f)]
    [InlineData(42u, false, 0.0f, true, -27.5f)]
    [InlineData(uint.MaxValue, true, -60.0f, false, 0.0f)]
    [InlineData(7u, false, 0.0f, false, 0.0f)]
    [InlineData(123456u, true, -12.25f, true, -59.5f)]
    public void FromBytes_RoundTrip(uint sequence, bool bassOn, float bass, bool trebleOn, float treble)
    {
        var original = new BandsConfig(sequence, bassOn, bass, trebleOn, treble);

        Assert.Equal(original, BandsConfigFile.FromBytes(BandsConfigFile.ToBytes(original)));
    }

    public static TheoryData<string, byte[]> RejectedFiles => new()
    {
        { "31 bytes", GoldenB1[..31] },
        { "33 bytes", [.. GoldenB1, 0] }, // the checksum covers bytes 0 to 27 only, so the size is what must reject this
        { "a changed magic", WithRecomputedChecksum(With(GoldenB1, 0, (byte)'X')) },
        { "version 2", WithRecomputedChecksum(With(GoldenB1, 4, 2)) },
        { "a flipped checksum byte", With(GoldenB1, 31, (byte)(GoldenB1[31] ^ 0x01)) },
    };

    [Theory]
    [MemberData(nameof(RejectedFiles))]
    public void FromBytes_Rejects(string what, byte[] bytes) =>
        Assert.True(BandsConfigFile.FromBytes(bytes) is null, what + " must not be read as a bands file");

    [Fact]
    public void FromBytes_ClampsCaps()
    {
        var read = BandsConfigFile.FromBytes(B1WithCaps(-80f, 5f));

        Assert.Equal(new BandsConfig(1, true, -60f, true, 0f), read);
    }

    [Fact]
    public void FromBytes_NaNCapReadsZero()
    {
        // A cap that is not a number is read as 0 and the file is still used, as the plug-in does; the other cap is untouched.
        Assert.Equal(new BandsConfig(1, true, 0f, true, -35f), BandsConfigFile.FromBytes(B1WithCaps(float.NaN, -35f)));
        Assert.Equal(new BandsConfig(1, true, -30f, true, 0f), BandsConfigFile.FromBytes(B1WithCaps(-30f, float.NaN)));
    }

    [Theory]
    [InlineData(0x0u, false, false)]
    [InlineData(0x1u, true, false)]
    [InlineData(0x2u, false, true)]
    [InlineData(0x3u, true, true)]
    [InlineData(0xFFFFFFFCu, false, false)] // bits the plug-in does not know are ignored
    [InlineData(0xFFFFFFFFu, true, true)]
    public void FromBytes_ReadsOnlyBitZeroAsBassAndBitOneAsTreble(uint flags, bool bassOn, bool trebleOn)
    {
        var read = BandsConfigFile.FromBytes(B1WithFlags(flags));

        Assert.NotNull(read);
        Assert.Equal(bassOn, read.BassOn);
        Assert.Equal(trebleOn, read.TrebleOn);
    }

    [Theory]
    [InlineData(-30.0, 0.0, true, -30.0f, false, 0.0f)]
    [InlineData(0.0, -35.0, false, 0.0f, true, -35.0f)]
    [InlineData(-0.5, -60.0, true, -0.5f, true, -60.0f)]
    [InlineData(-80.0, 5.0, true, -60.0f, false, 0.0f)] // held to the range first, then on only if still below 0
    [InlineData(double.NaN, double.NaN, false, 0.0f, false, 0.0f)]
    public void FromSliders_OnWhenBelowZero(
        double bassSlider, double trebleSlider, bool bassOn, float bassCap, bool trebleOn, float trebleCap) =>
        Assert.Equal(
            new BandsConfig(3, bassOn, bassCap, trebleOn, trebleCap),
            BandsConfigFile.FromSliders(3, bassSlider, trebleSlider));

    // ---- The file ----

    [Fact]
    public void Write_ThenTryRead_RoundTrip()
    {
        var config = new BandsConfig(5, true, -33f, false, 0f);

        BandsConfigFile.Write(BandsPath, config);

        Assert.Equal(config, BandsConfigFile.TryRead(BandsPath));
        Assert.Equal(BandsConfigFile.Size, new FileInfo(BandsPath).Length);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void TryRead_MissingFile_ReturnsNull() => Assert.Null(BandsConfigFile.TryRead(BandsPath));

    [Fact]
    public void TryRead_GarbageOrWrongSizedFile_ReturnsNull()
    {
        File.WriteAllBytes(BandsPath, new byte[32]);
        Assert.Null(BandsConfigFile.TryRead(BandsPath));

        File.WriteAllBytes(BandsPath, [.. GoldenB1, 0]); // the plug-in wants exactly 32 bytes
        Assert.Null(BandsConfigFile.TryRead(BandsPath));

        File.WriteAllBytes(BandsPath, GoldenB1[..20]);
        Assert.Null(BandsConfigFile.TryRead(BandsPath));
    }

    // ---- The writer ----

    [Fact]
    public void Writer_WritesWhenMissing_ThenSkipsSame()
    {
        var writer = new BandsConfigWriter(BandsPath);

        Assert.True(writer.Sync(-30, 0));
        Assert.Equal(new BandsConfig(1, true, -30f, false, 0f), BandsConfigFile.TryRead(BandsPath));

        var before = File.ReadAllBytes(BandsPath);
        Assert.False(writer.Sync(-30, 0));
        Assert.Equal(before, File.ReadAllBytes(BandsPath));

        Assert.True(writer.Sync(-30, -35));
        Assert.Equal(new BandsConfig(2, true, -30f, true, -35f), BandsConfigFile.TryRead(BandsPath));
    }

    [Fact]
    public void Writer_RapidChanges_EndWithLast()
    {
        var writer = new BandsConfigWriter(BandsPath);

        for (var treble = -20; treble >= -39; treble--)
        {
            writer.Sync(-30, treble);
        }

        var read = BandsConfigFile.TryRead(BandsPath);
        Assert.NotNull(read);
        Assert.Equal(-39f, read.TrebleCapDb);
        Assert.Equal(20u, read.Sequence); // each of the twenty calls had something new to write
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Writer_BackToOff()
    {
        var writer = new BandsConfigWriter(BandsPath);
        writer.Sync(-30, -35);

        Assert.True(writer.Sync(0, 0));

        var bytes = File.ReadAllBytes(BandsPath);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, bytes[12..16]); // flags: both caps off
        Assert.Equal(new BandsConfig(2, false, 0f, false, 0f), BandsConfigFile.TryRead(BandsPath));
    }

    [Fact]
    public void Writer_WithoutTheDataFolder_WritesNothingAndCreatesNothing()
    {
        var missing = Path.Combine(_dir, "not-installed", "bands.bin");

        var written = new BandsConfigWriter(missing).Sync(-30, -35);

        Assert.False(written);
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing))); // the data folder belongs to the installer
    }

    [Fact]
    public void Writer_ANewWriter_FindsTheFileAsItIs_AndContinuesItsSequence()
    {
        BandsConfigFile.Write(BandsPath, new BandsConfig(41, true, -30f, false, 0f));

        var writer = new BandsConfigWriter(BandsPath);
        Assert.False(writer.Sync(-30, 0)); // a new run of the tray finds the same file
        Assert.Equal(41u, BandsConfigFile.TryRead(BandsPath)?.Sequence);

        Assert.True(writer.Sync(-30, -35));
        Assert.Equal(new BandsConfig(42, true, -30f, true, -35f), BandsConfigFile.TryRead(BandsPath));
    }

    [Fact]
    public void Writer_ACorruptFile_IsReplaced_AndTheSequenceKeepsCounting()
    {
        var writer = new BandsConfigWriter(BandsPath);
        writer.Sync(-30, 0);
        writer.Sync(-30, -35); // sequence 2
        File.WriteAllBytes(BandsPath, new byte[32]);

        Assert.True(writer.Sync(-30, -35));

        Assert.Equal(new BandsConfig(3, true, -30f, true, -35f), BandsConfigFile.TryRead(BandsPath));
    }

    [Fact]
    public void Writer_ACapOutsideTheRange_IsWrittenInsideItAndThenLeftAlone()
    {
        var writer = new BandsConfigWriter(BandsPath);

        Assert.True(writer.Sync(-75, 6));
        Assert.False(writer.Sync(-75, 6)); // compared as written (-60 and off), so it does not rewrite the file every time

        Assert.Equal(new BandsConfig(1, true, -60f, false, 0f), BandsConfigFile.TryRead(BandsPath));
    }

    [Fact]
    public void Writer_LastAttemptFailed_IsFalseUntilAWriteFails()
    {
        var writer = new BandsConfigWriter(BandsPath);
        Assert.False(writer.LastAttemptFailed);

        writer.Sync(-30, 0);
        writer.Sync(-30, -35);

        Assert.False(writer.LastAttemptFailed);
    }

    [Fact]
    public void Writer_LastAttemptFailed_IsTrueWhenAWriteGivesUp_AndFalseAfterTheNextWrite()
    {
        var writer = new BandsConfigWriter(BandsPath);
        writer.Sync(-30, 0);
        using (new FileStream(BandsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<Exception>(() => writer.Sync(-30, -35));
            Assert.True(writer.LastAttemptFailed);
        }

        Assert.True(writer.Sync(-30, -35));
        Assert.False(writer.LastAttemptFailed);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Writer_LastAttemptFailed_IsClearedByASyncThatFindsNothingToWrite()
    {
        var writer = new BandsConfigWriter(BandsPath);
        writer.Sync(-30, 0);
        using (new FileStream(BandsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<Exception>(() => writer.Sync(-30, -35));
            Assert.True(writer.LastAttemptFailed);
        }

        Assert.False(writer.Sync(-30, 0)); // the file still holds what the settings ask for
        Assert.False(writer.LastAttemptFailed);
    }
}
