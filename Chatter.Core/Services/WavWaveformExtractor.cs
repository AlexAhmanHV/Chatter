/*
File: WavWaveformExtractor.cs

What this does:
- Purpose: Turns a voice message's raw bytes into a small array of normalized amplitude peaks
  (one per bar) for the waveform drawn in the message bubble.
- How: Parses a standard RIFF/WAVE header to find the "fmt " and "data" chunks, then reads 16-bit
  PCM samples directly - no decoding library needed, since the recorder (Plugin.Maui.Audio) is
  asked for "audio/wav" specifically (see ChatViewModel.SendRecordedVoiceMessageAsync). Bytes that
  don't parse as a valid/supported WAV (wrong header, or some other bit depth/encoding) fall back
  to a flat placeholder waveform instead of throwing - a wrong-looking waveform is a cosmetic
  problem, not one worth failing playback over. Lives in Chatter.Core (not Chatter.Client) purely
  so it's unit-testable without the MAUI workloads Chatter.Client.Tests deliberately avoids.
- Where used: ChatViewModel.PlayVoiceMessageAsync, once a voice message's bytes are fetched.
*/

namespace Chatter.Core.Services;

public static class WavWaveformExtractor
{
    public static float[] ExtractAmplitudes(byte[] wavBytes, int barCount)
    {
        try
        {
            var bars = TryExtract(wavBytes, barCount);
            if (bars is not null) return bars;
        }
        catch
        {
            // Fall through to the flat placeholder below.
        }

        return FlatPlaceholder(barCount);
    }

    private static float[] FlatPlaceholder(int barCount)
    {
        var bars = new float[barCount];
        Array.Fill(bars, 0.15f);
        return bars;
    }

    private static float[]? TryExtract(byte[] wav, int barCount)
    {
        if (wav.Length < 44) return null; // shorter than the smallest possible WAV header
        if (wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F') return null;
        if (wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E') return null;

        int pos = 12;
        short bitsPerSample = 16;
        short channels = 1;
        int dataOffset = -1, dataLength = 0;

        while (pos + 8 <= wav.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            var chunkSize = BitConverter.ToInt32(wav, pos + 4);
            var chunkDataStart = pos + 8;

            if (chunkId == "fmt " && chunkDataStart + 16 <= wav.Length)
            {
                channels = BitConverter.ToInt16(wav, chunkDataStart + 2);
                bitsPerSample = BitConverter.ToInt16(wav, chunkDataStart + 14);
            }
            else if (chunkId == "data")
            {
                dataOffset = chunkDataStart;
                dataLength = Math.Min(chunkSize, wav.Length - chunkDataStart);
                break; // "data" is (in practice) always the last chunk we care about
            }

            pos = chunkDataStart + chunkSize + (chunkSize % 2); // chunks are word-aligned
        }

        if (dataOffset < 0 || dataLength <= 0 || bitsPerSample != 16 || channels < 1)
            return null; // only handling the common 16-bit PCM case, matching most recorders

        var bytesPerFrame = 2 * channels;
        var frameCount = dataLength / bytesPerFrame;
        if (frameCount == 0) return null;

        var bars = new float[barCount];
        var framesPerBar = Math.Max(1, frameCount / barCount);

        for (int bar = 0; bar < barCount; bar++)
        {
            var startFrame = bar * framesPerBar;
            var endFrame = Math.Min(frameCount, startFrame + framesPerBar);
            short peak = 0;

            for (int frame = startFrame; frame < endFrame; frame++)
            {
                var sampleOffset = dataOffset + frame * bytesPerFrame;
                if (sampleOffset + 2 > wav.Length) break;
                var sample = BitConverter.ToInt16(wav, sampleOffset);
                var abs = Math.Abs((int)sample);
                if (abs > peak) peak = (short)Math.Min(abs, short.MaxValue);
            }

            bars[bar] = peak / (float)short.MaxValue;
        }

        // Give even a near-silent clip a visible minimum bar height, and normalize so the loudest
        // bar in this clip reaches 1.0 - otherwise a quiet recording renders as a flat line.
        var max = bars.Length > 0 ? bars.Max() : 0f;
        if (max > 0.01f)
            for (int i = 0; i < bars.Length; i++)
                bars[i] = Math.Max(0.08f, bars[i] / max);
        else
            Array.Fill(bars, 0.08f);

        return bars;
    }
}
