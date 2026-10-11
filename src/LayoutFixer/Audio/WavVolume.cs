using System;

namespace LayoutFixer.Audio;

// Scales the amplitude of a 16-bit PCM WAV so a single SoundPlayer (which has no
// volume API) can play back at a chosen level. Other formats are returned as-is.
internal static class WavVolume
{
    public static byte[] Scale(byte[] wav, float volume)
    {
        if (wav.Length < 12 || volume >= 1f)
        {
            return wav;
        }

        if (volume < 0f)
        {
            volume = 0f;
        }

        if (!TryFindDataChunk(wav, out int dataStart, out int dataLength, out int bitsPerSample, out int audioFormat) ||
            audioFormat != 1 ||
            bitsPerSample != 16)
        {
            return wav;
        }

        // Clone so the cached source bytes are never mutated.
        byte[] copy = (byte[])wav.Clone();
        int samples = dataLength / 2;
        for (int i = 0; i < samples; i++)
        {
            int offset = dataStart + i * 2;
            short sample = (short)(copy[offset] | (copy[offset + 1] << 8));
            sample = (short)(sample * volume);
            copy[offset] = (byte)(sample & 0xFF);
            copy[offset + 1] = (byte)((sample >> 8) & 0xFF);
        }

        return copy;
    }

    private static bool TryFindDataChunk(byte[] wav, out int dataStart, out int dataLength, out int bitsPerSample, out int audioFormat)
    {
        dataStart = 0;
        dataLength = 0;
        bitsPerSample = 0;
        audioFormat = 0;

        int i = 12;
        bool haveFmt = false;
        while (i + 8 <= wav.Length)
        {
            string chunkId = System.Text.Encoding.ASCII.GetString(wav, i, 4);
            uint chunkSize = BitConverter.ToUInt32(wav, i + 4);
            int payload = i + 8;

            if (chunkId == "fmt ")
            {
                audioFormat = BitConverter.ToUInt16(wav, payload);
                if (payload + 16 <= wav.Length)
                {
                    bitsPerSample = BitConverter.ToUInt16(wav, payload + 14);
                }

                haveFmt = true;
            }
            else if (chunkId == "data")
            {
                dataStart = payload;
                dataLength = (int)Math.Min(chunkSize, wav.Length - payload);
                return haveFmt;
            }

            // Chunks are word-aligned: pad to even size.
            i += 8 + (int)chunkSize + (((int)chunkSize & 1) == 1 ? 1 : 0);
        }

        return false;
    }
}
