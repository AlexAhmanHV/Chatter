/*
File: WavWaveformExtractorTests.cs

What this does:
- Purpose: Focused unit tests for WavWaveformExtractor.ExtractAmplitudes, covering the WAV parsing
  it does for a voice message's waveform.
*/

using Chatter.Core.Services;
using Xunit;

namespace Chatter.Client.Tests;

public class WavWaveformExtractorTests
{
    // Builds a minimal 16-bit PCM mono WAV file from the given samples.
    private static byte[] BuildWav(short[] samples)
    {
        var dataBytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, dataBytes, 0, dataBytes.Length);

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        void WriteChunkId(string id) => w.Write(System.Text.Encoding.ASCII.GetBytes(id));

        WriteChunkId("RIFF");
        w.Write(36 + dataBytes.Length); // file size - 8
        WriteChunkId("WAVE");

        WriteChunkId("fmt ");
        w.Write(16);              // fmt chunk size
        w.Write((short)1);        // PCM
        w.Write((short)1);        // mono
        w.Write(44100);           // sample rate
        w.Write(44100 * 2);       // byte rate
        w.Write((short)2);        // block align
        w.Write((short)16);       // bits per sample

        WriteChunkId("data");
        w.Write(dataBytes.Length);
        w.Write(dataBytes);

        return ms.ToArray();
    }

    [Fact]
    public void ExtractAmplitudes_ValidWav_ReturnsRequestedBarCount()
    {
        var samples = Enumerable.Range(0, 4410).Select(i => (short)(i % 2 == 0 ? 10000 : -10000)).ToArray();
        var wav = BuildWav(samples);

        var bars = WavWaveformExtractor.ExtractAmplitudes(wav, 40);

        Assert.Equal(40, bars.Length);
    }

    [Fact]
    public void ExtractAmplitudes_LoudestBarNormalizesToOne()
    {
        var samples = new short[1000];
        Array.Fill(samples, (short)1000);
        samples[500] = short.MaxValue; // one very loud sample, should dominate its bar
        var wav = BuildWav(samples);

        var bars = WavWaveformExtractor.ExtractAmplitudes(wav, 10);

        Assert.Contains(bars, b => b >= 0.99f);
        Assert.All(bars, b => Assert.InRange(b, 0f, 1f));
    }

    [Fact]
    public void ExtractAmplitudes_Silence_ReturnsFlatMinimumBars()
    {
        var samples = new short[1000]; // all zero = silence
        var wav = BuildWav(samples);

        var bars = WavWaveformExtractor.ExtractAmplitudes(wav, 10);

        Assert.All(bars, b => Assert.Equal(0.08f, b, 3));
    }

    [Fact]
    public void ExtractAmplitudes_NotAWavFile_FallsBackToFlatPlaceholderWithoutThrowing()
    {
        var garbage = new byte[] { 1, 2, 3, 4, 5 };

        var bars = WavWaveformExtractor.ExtractAmplitudes(garbage, 20);

        Assert.Equal(20, bars.Length);
        Assert.All(bars, b => Assert.Equal(0.15f, b));
    }

    [Fact]
    public void ExtractAmplitudes_EmptyData_FallsBackToFlatPlaceholder()
    {
        var bars = WavWaveformExtractor.ExtractAmplitudes(Array.Empty<byte>(), 15);

        Assert.Equal(15, bars.Length);
        Assert.All(bars, b => Assert.Equal(0.15f, b));
    }
}
