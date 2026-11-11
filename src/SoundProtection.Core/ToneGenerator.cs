namespace SoundProtection.Core;

/// <summary>
/// A short sine tone with a fade in and a fade out, for testing the cap. The level is the digital peak in dBFS,
/// the same on both channels. Reading in any chunk size gives the same samples.
/// </summary>
public sealed class ToneGenerator
{
    private readonly double _amplitude;
    private readonly double _phaseStep;
    private readonly int _totalFrames;
    private readonly int _rampFrames;
    private int _index;

    public ToneGenerator(int sampleRate, double frequencyHz, double levelDbfs, int durationMs, int rampMs)
    {
        _amplitude = Db.ToLinear(levelDbfs);
        _phaseStep = 2.0 * Math.PI * frequencyHz / sampleRate;
        _totalFrames = (int)((long)sampleRate * durationMs / 1000);
        _rampFrames = Math.Min((int)((long)sampleRate * rampMs / 1000), _totalFrames / 2);
    }

    /// <summary>
    /// Writes interleaved stereo frames (the same sample in both channels) and returns how many floats it wrote.
    /// Returns 0 when the tone is over.
    /// </summary>
    public int FillStereo(float[] buffer, int offset, int count) => FillStereo(buffer.AsSpan(offset, count));

    /// <inheritdoc cref="FillStereo(float[], int, int)"/>
    public int FillStereo(Span<float> buffer)
    {
        var written = 0;
        while (written + 2 <= buffer.Length && _index < _totalFrames)
        {
            double envelope = 1.0;
            if (_index < _rampFrames)
            {
                envelope = (double)_index / _rampFrames;
            }
            else if (_index >= _totalFrames - _rampFrames)
            {
                envelope = (double)(_totalFrames - 1 - _index) / _rampFrames;
            }

            var sample = (float)(Math.Sin(_phaseStep * _index) * _amplitude * envelope);
            buffer[written++] = sample;
            buffer[written++] = sample;
            _index++;
        }

        return written;
    }
}
