using System.Diagnostics;
using System.Text;
using SoundProtection.Core;

namespace SoundProtection.Tests;

public sealed class ProtectionConfigTests : IDisposable
{
    // The golden files from native/apo/SpShared.h, byte for byte.
    private static readonly byte[] Golden1 = Convert.FromHexString(
        "53504346" + "01000000" + "01000000" + "01000000" + "0000A0C1" + "00004041" + "00000000" + "EAFACF1E");

    private static readonly byte[] Golden2 = Convert.FromHexString(
        "53504346" + "01000000" + "2A000000" + "00000000" + "00000EC2" + "0000C040" + "00000000" + "5806440E");

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-cfg-" + Guid.NewGuid().ToString("N"));

    public ProtectionConfigTests() => Directory.CreateDirectory(_dir);

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

    private string ConfigPath => Path.Combine(_dir, "config.bin");

    private static byte[] WithRecomputedChecksum(byte[] bytes)
    {
        var copy = (byte[])bytes.Clone();
        BitConverter.TryWriteBytes(copy.AsSpan(28), ProtectionConfigFile.Fnv1a(copy.AsSpan(0, 28)));
        return copy;
    }

    [Fact]
    public void ToBytes_Golden1() =>
        Assert.Equal(Golden1, ProtectionConfigFile.ToBytes(new ProtectionConfig(1, true, -20.0f, 12.0f)));

    [Fact]
    public void ToBytes_Golden2() =>
        Assert.Equal(Golden2, ProtectionConfigFile.ToBytes(new ProtectionConfig(42, false, -35.5f, 6.0f)));

    [Fact]
    public void FromBytes_ReadsTheGoldenFiles()
    {
        Assert.Equal(new ProtectionConfig(1, true, -20.0f, 12.0f), ProtectionConfigFile.FromBytes(Golden1));
        Assert.Equal(new ProtectionConfig(42, false, -35.5f, 6.0f), ProtectionConfigFile.FromBytes(Golden2));
    }

    [Theory]
    [InlineData(1u, true, -20.0f, 12.0f)]
    [InlineData(42u, false, -35.5f, 6.0f)]
    [InlineData(uint.MaxValue, true, -60.0f, 0.0f)]
    [InlineData(7u, true, 0.0f, 30.0f)]
    [InlineData(123456u, false, -12.25f, 3.5f)]
    public void FromBytes_RoundTrip(uint sequence, bool enabled, float cap, float margin)
    {
        var original = new ProtectionConfig(sequence, enabled, cap, margin);

        Assert.Equal(original, ProtectionConfigFile.FromBytes(ProtectionConfigFile.ToBytes(original)));
    }

    [Fact]
    public void FromBytes_BadChecksum_ReturnsNull()
    {
        var lastByteChanged = (byte[])Golden1.Clone();
        lastByteChanged[31] ^= 0x01;
        var payloadChanged = (byte[])Golden1.Clone();
        payloadChanged[8] = 2; // the sequence changed, the checksum did not

        Assert.Null(ProtectionConfigFile.FromBytes(lastByteChanged));
        Assert.Null(ProtectionConfigFile.FromBytes(payloadChanged));
    }

    [Fact]
    public void FromBytes_WrongMagic_ReturnsNull()
    {
        var wrong = (byte[])Golden1.Clone();
        wrong[0] = (byte)'X';

        // With the checksum recomputed, only the magic is wrong.
        Assert.Null(ProtectionConfigFile.FromBytes(WithRecomputedChecksum(wrong)));
    }

    [Fact]
    public void FromBytes_WrongVersion_ReturnsNull()
    {
        var wrong = (byte[])Golden1.Clone();
        wrong[4] = 2;

        Assert.Null(ProtectionConfigFile.FromBytes(WithRecomputedChecksum(wrong)));
    }

    [Fact]
    public void FromBytes_Short_ReturnsNull()
    {
        Assert.Null(ProtectionConfigFile.FromBytes(Golden1.AsSpan(0, 31)));
        Assert.Null(ProtectionConfigFile.FromBytes(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void FromBytes_NonFiniteLevels_ReturnNull()
    {
        var nan = (byte[])Golden1.Clone();
        BitConverter.TryWriteBytes(nan.AsSpan(16), float.NaN);

        Assert.Null(ProtectionConfigFile.FromBytes(WithRecomputedChecksum(nan)));
    }

    [Theory]
    [InlineData(-80.0f, 12.0f, -60.0f, 12.0f)]
    [InlineData(5.0f, 12.0f, 0.0f, 12.0f)]
    [InlineData(-20.0f, 40.0f, -20.0f, 30.0f)]
    [InlineData(-20.0f, -1.0f, -20.0f, 0.0f)]
    public void ToBytes_Clamps(float cap, float margin, float expectedCap, float expectedMargin)
    {
        var read = ProtectionConfigFile.FromBytes(ProtectionConfigFile.ToBytes(new ProtectionConfig(3, true, cap, margin)));

        Assert.NotNull(read);
        Assert.Equal(expectedCap, read.CapDb);
        Assert.Equal(expectedMargin, read.MarginDb);
    }

    [Fact]
    public void ToBytes_NotANumber_FallsBackToTheDefaults()
    {
        var read = ProtectionConfigFile.FromBytes(
            ProtectionConfigFile.ToBytes(new ProtectionConfig(3, true, float.NaN, float.NaN)));

        Assert.NotNull(read);
        Assert.Equal(-20.0f, read.CapDb);
        Assert.Equal(12.0f, read.MarginDb);
    }

    [Fact]
    public void Fnv1a_KnownVectors()
    {
        Assert.Equal(0x811C9DC5u, ProtectionConfigFile.Fnv1a(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0xE40C292Cu, ProtectionConfigFile.Fnv1a(Encoding.ASCII.GetBytes("a")));
        Assert.Equal(0xBF9CF968u, ProtectionConfigFile.Fnv1a(Encoding.ASCII.GetBytes("foobar")));
    }

    [Fact]
    public void CeilingDb_Rule()
    {
        Assert.Equal(-8.0f, ProtectionConfigFile.CeilingDb(-20.0f, 12.0f));
        Assert.Equal(-1.0f, ProtectionConfigFile.CeilingDb(-5.0f, 12.0f));
        Assert.Equal(-60.0f, ProtectionConfigFile.CeilingDb(-60.0f, 0.0f));
        Assert.Equal(-1.0f, ProtectionConfigFile.CeilingDb(0.0f, 30.0f));
    }

    [Fact]
    public void Write_ThenTryRead_RoundTrip()
    {
        var config = new ProtectionConfig(5, true, -33.0f, 12.0f);

        ProtectionConfigFile.Write(ConfigPath, config);

        Assert.Equal(config, ProtectionConfigFile.TryRead(ConfigPath));
        Assert.Equal(ProtectionConfigFile.Size, new FileInfo(ConfigPath).Length);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Write_ReplacesAnExistingFile()
    {
        ProtectionConfigFile.Write(ConfigPath, new ProtectionConfig(1, true, -20.0f, 12.0f));
        ProtectionConfigFile.Write(ConfigPath, new ProtectionConfig(2, false, -30.0f, 12.0f));

        Assert.Equal(new ProtectionConfig(2, false, -30.0f, 12.0f), ProtectionConfigFile.TryRead(ConfigPath));
    }

    [Fact]
    public void TryRead_MissingFile_ReturnsNull() => Assert.Null(ProtectionConfigFile.TryRead(ConfigPath));

    [Fact]
    public void TryRead_GarbageOrWrongSizedFile_ReturnsNull()
    {
        File.WriteAllBytes(ConfigPath, new byte[32]);
        Assert.Null(ProtectionConfigFile.TryRead(ConfigPath));

        File.WriteAllBytes(ConfigPath, Golden1.Concat(new byte[] { 0 }).ToArray()); // the plug-in wants exactly 32 bytes
        Assert.Null(ProtectionConfigFile.TryRead(ConfigPath));

        File.WriteAllBytes(ConfigPath, Golden1.AsSpan(0, 20).ToArray());
        Assert.Null(ProtectionConfigFile.TryRead(ConfigPath));
    }

    [Fact]
    public void TryRead_WorksWhileAnotherProcessHasTheFileOpenForWriting()
    {
        ProtectionConfigFile.Write(ConfigPath, new ProtectionConfig(9, true, -25.0f, 12.0f));
        using var other = new FileStream(ConfigPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        Assert.Equal(9u, ProtectionConfigFile.TryRead(ConfigPath)?.Sequence);
    }

    [Fact]
    public void Write_RetriesWhileLocked()
    {
        ProtectionConfigFile.Write(ConfigPath, new ProtectionConfig(1, true, -20.0f, 12.0f));
        using var opened = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var held = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            opened.Set();
            Thread.Sleep(200);
        });
        holder.Start();
        opened.Wait();

        var watch = Stopwatch.StartNew();
        ProtectionConfigFile.Write(ConfigPath, new ProtectionConfig(2, true, -30.0f, 12.0f));
        watch.Stop();
        holder.Join();

        Assert.Equal(new ProtectionConfig(2, true, -30.0f, 12.0f), ProtectionConfigFile.TryRead(ConfigPath));
        Assert.True(watch.ElapsedMilliseconds >= 100, $"the write should have had to wait for the lock, but took {watch.ElapsedMilliseconds} ms");
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Write_GivesUpAfterTenRetriesAndThrows()
    {
        ProtectionConfigFile.Write(ConfigPath, new ProtectionConfig(1, true, -20.0f, 12.0f));
        using var held = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        var watch = Stopwatch.StartNew();
        var error = Record.Exception(() => ProtectionConfigFile.Write(ConfigPath, new ProtectionConfig(2, true, -30.0f, 12.0f)));
        watch.Stop();

        Assert.True(error is IOException or UnauthorizedAccessException, $"unexpected: {error}");
        Assert.InRange(watch.ElapsedMilliseconds, 400, 5000); // ten retries, 50 ms apart
        held.Dispose();
        Assert.Equal(1u, ProtectionConfigFile.TryRead(ConfigPath)?.Sequence); // the old file is untouched
    }

    [Fact]
    public void Write_MissingFolder_ThrowsAtOnceAndCreatesNothing()
    {
        var missing = Path.Combine(_dir, "not-created", "config.bin");

        var watch = Stopwatch.StartNew();
        var error = Record.Exception(() => ProtectionConfigFile.Write(missing, new ProtectionConfig(1, true, -20.0f, 12.0f)));
        watch.Stop();

        Assert.IsType<DirectoryNotFoundException>(error);
        Assert.True(watch.ElapsedMilliseconds < 300, "a missing folder is not a sharing problem, so there is nothing to retry");
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)), "the data folder belongs to the installer: the tray must never create it");
    }
}

public sealed class ProtectionConfigWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-cfgw-" + Guid.NewGuid().ToString("N"));

    public ProtectionConfigWriterTests() => Directory.CreateDirectory(_dir);

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

    private string ConfigPath => Path.Combine(_dir, "config.bin");

    [Fact]
    public void Sync_WithoutTheDataFolder_WritesNothingAndCreatesNothing()
    {
        var missing = Path.Combine(_dir, "not-installed", "config.bin");

        var written = new ProtectionConfigWriter(missing).Sync(true, -20.0);

        Assert.False(written);
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
    }

    [Fact]
    public void Sync_FirstWrite_IsSequenceOneWithTheDefaultMargin()
    {
        Assert.True(new ProtectionConfigWriter(ConfigPath).Sync(true, -25.5));

        Assert.Equal(new ProtectionConfig(1, true, -25.5f, 12.0f), ProtectionConfigFile.TryRead(ConfigPath));
    }

    [Fact]
    public void Sync_WhenTheFileAlreadyHasTheSettings_DoesNotWriteAgain()
    {
        var writer = new ProtectionConfigWriter(ConfigPath);
        writer.Sync(true, -25.5);

        Assert.False(writer.Sync(true, -25.5));
        Assert.False(new ProtectionConfigWriter(ConfigPath).Sync(true, -25.5)); // a new run of the app finds the same file

        Assert.Equal(1u, ProtectionConfigFile.TryRead(ConfigPath)?.Sequence);
    }

    [Fact]
    public void Sync_AChangedCapOrSwitch_WritesTheExistingSequencePlusOne()
    {
        var writer = new ProtectionConfigWriter(ConfigPath);
        writer.Sync(true, -20.0);

        Assert.True(writer.Sync(true, -30.0));
        Assert.True(writer.Sync(false, -30.0));

        Assert.Equal(new ProtectionConfig(3, false, -30.0f, 12.0f), ProtectionConfigFile.TryRead(ConfigPath));
    }

    [Fact]
    public void Sync_ANewWriter_ContinuesFromTheSequenceOnDisk()
    {
        ProtectionConfigFile.Write(ConfigPath, new ProtectionConfig(41, true, -20.0f, 12.0f));

        Assert.True(new ProtectionConfigWriter(ConfigPath).Sync(true, -33.0));

        Assert.Equal(new ProtectionConfig(42, true, -33.0f, 12.0f), ProtectionConfigFile.TryRead(ConfigPath));
    }

    [Fact]
    public void Sync_AFileWithAnotherMargin_IsCorrected()
    {
        ProtectionConfigFile.Write(ConfigPath, new ProtectionConfig(7, true, -20.0f, 6.0f));

        Assert.True(new ProtectionConfigWriter(ConfigPath).Sync(true, -20.0));

        Assert.Equal(new ProtectionConfig(8, true, -20.0f, 12.0f), ProtectionConfigFile.TryRead(ConfigPath));
    }

    [Fact]
    public void Sync_ACorruptFile_IsReplaced_AndTheSequenceKeepsCounting()
    {
        var writer = new ProtectionConfigWriter(ConfigPath);
        writer.Sync(true, -20.0);
        writer.Sync(true, -30.0); // sequence 2
        File.WriteAllBytes(ConfigPath, new byte[32]);

        Assert.True(writer.Sync(true, -30.0));

        Assert.Equal(new ProtectionConfig(3, true, -30.0f, 12.0f), ProtectionConfigFile.TryRead(ConfigPath));
    }

    [Fact]
    public void Sync_ACapOutsideTheRange_IsWrittenInsideItAndThenLeftAlone()
    {
        var writer = new ProtectionConfigWriter(ConfigPath);

        Assert.True(writer.Sync(true, -75.0));
        Assert.False(writer.Sync(true, -75.0));

        Assert.Equal(-60.0f, ProtectionConfigFile.TryRead(ConfigPath)?.CapDb);
    }

    [Fact]
    public void Sync_WhenTheFileIsLockedForTooLong_Throws_AndTheNextSyncTriesAgain()
    {
        var writer = new ProtectionConfigWriter(ConfigPath);
        writer.Sync(true, -20.0);
        var held = new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var error = Record.Exception(() => writer.Sync(true, -30.0));
            Assert.True(error is IOException or UnauthorizedAccessException, $"unexpected: {error}");
        }
        finally
        {
            held.Dispose();
        }

        Assert.True(writer.Sync(true, -30.0));
        Assert.Equal(new ProtectionConfig(2, true, -30.0f, 12.0f), ProtectionConfigFile.TryRead(ConfigPath));
    }

    // ---- Fix round 1, ruling 8c: the panel says when the settings could not be written ----

    [Fact]
    public void LastAttemptFailed_IsFalseUntilAWriteFails()
    {
        var writer = new ProtectionConfigWriter(ConfigPath);
        Assert.False(writer.LastAttemptFailed);

        writer.Sync(true, -20.0);
        writer.Sync(true, -30.0);

        Assert.False(writer.LastAttemptFailed);
    }

    [Fact]
    public void LastAttemptFailed_IsTrueWhenAWriteGivesUp_AndFalseAfterTheNextWrite()
    {
        var writer = new ProtectionConfigWriter(ConfigPath);
        writer.Sync(true, -20.0);
        using (new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<Exception>(() => writer.Sync(true, -30.0));
            Assert.True(writer.LastAttemptFailed);
        }

        Assert.True(writer.Sync(true, -30.0));
        Assert.False(writer.LastAttemptFailed);
    }

    [Fact]
    public void LastAttemptFailed_IsClearedByASyncThatFindsNothingToWrite()
    {
        var writer = new ProtectionConfigWriter(ConfigPath);
        writer.Sync(true, -20.0);
        using (new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<Exception>(() => writer.Sync(true, -30.0));
            Assert.True(writer.LastAttemptFailed);
        }

        Assert.False(writer.Sync(true, -20.0)); // the file still holds what the settings ask for
        Assert.False(writer.LastAttemptFailed);
    }

    [Fact]
    public void LastAttemptFailed_IsClearedWhenTheDataFolderIsGone()
    {
        var data = Path.Combine(_dir, "installed");
        Directory.CreateDirectory(data);
        var path = Path.Combine(data, "config.bin");
        var writer = new ProtectionConfigWriter(path);
        writer.Sync(true, -20.0);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<Exception>(() => writer.Sync(true, -30.0));
            Assert.True(writer.LastAttemptFailed);
        }

        Directory.Delete(data, recursive: true); // uninstalled: there is nothing to save the settings to

        Assert.False(writer.Sync(true, -30.0));
        Assert.False(writer.LastAttemptFailed);
        Assert.False(Directory.Exists(data));
    }

    [Fact]
    public void NextSequence_CountsUp_AndSkipsZeroAtTheEnd()
    {
        Assert.Equal(1u, ProtectionConfigFile.NextSequence(0));
        Assert.Equal(42u, ProtectionConfigFile.NextSequence(41));
        Assert.Equal(1u, ProtectionConfigFile.NextSequence(uint.MaxValue));
    }

    [Theory]
    [InlineData(-20.0, -20.0f)]
    [InlineData(-75.0, -60.0f)]
    [InlineData(3.0, 0.0f)]
    [InlineData(double.NaN, -20.0f)]
    [InlineData(double.NegativeInfinity, -60.0f)]
    public void EffectiveCapDb_IsWhatTheFileWillHold(double setting, float expected) =>
        Assert.Equal(expected, ProtectionConfigFile.EffectiveCapDb(setting));
}
