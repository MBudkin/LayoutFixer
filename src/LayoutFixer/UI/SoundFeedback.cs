using System;
using System.IO;
using System.Media;
using System.Reflection;
using LayoutFixer.Audio;

namespace LayoutFixer.UI;

internal static class SoundFeedback
{
    private static readonly object _gate = new();
    private static bool _enabled = true;
    private static string _variant = "sound1";
    private static string _customPath = string.Empty;
    private static int _volume = 100;
    private static byte[]? _sound1Cache;
    private static byte[]? _sound2Cache;
    private static string _customCachePath = string.Empty;
    private static DateTime _customCacheLastWriteUtc;
    private static long _customCacheLength;
    private static byte[]? _customCacheBytes;

    public static void Configure(bool enabled, string variant, string customPath, int volume)
    {
        lock (_gate)
        {
            _enabled = enabled;
            _variant = string.IsNullOrEmpty(variant) ? "sound1" : variant;
            _customPath = customPath ?? string.Empty;
            _volume = Math.Clamp(volume, 0, 100);
        }
    }

    public static void PlaySuccess()
    {
        lock (_gate)
        {
            if (!_enabled)
            {
                return;
            }

            Play(_variant, _customPath, _volume);
        }
    }

    public static void PlayRevert()
    {
        // Same chosen sound for both outcomes (the visible popup distinguishes them).
        PlaySuccess();
    }

    // Preview a sound from the settings UI without touching the live configuration.
    public static void Preview(string variant, string customPath, int volume)
    {
        Play(variant, customPath, Math.Clamp(volume, 0, 100));
    }

    private static void Play(string variant, string customPath, int volume)
    {
        byte[]? wav;
        lock (_gate)
        {
            wav = LoadWav(variant, customPath);
        }

        if (wav is null)
        {
            return;
        }

        byte[] scaled = WavVolume.Scale(wav, volume / 100f);
        try
        {
            // SoundPlayer reads the stream fully before async playback; the stream
            // is intentionally not disposed here so an in-flight read stays valid.
            var player = new SoundPlayer(new MemoryStream(scaled));
            player.Play();
        }
        catch
        {
            // Sound must never disturb the user.
        }
    }

    private static byte[]? LoadWav(string variant, string customPath)
    {
        try
        {
            if (variant == "custom" && !string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath))
            {
                return LoadCustomWav(customPath);
            }

            string resourceName = variant == "sound2"
                ? "LayoutFixer.Sounds.sound2.wav"
                : "LayoutFixer.Sounds.sound1.wav";

            if (variant == "sound2")
            {
                _sound2Cache ??= LoadEmbeddedWav(resourceName);
                return _sound2Cache;
            }

            _sound1Cache ??= LoadEmbeddedWav(resourceName);
            return _sound1Cache;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? LoadCustomWav(string customPath)
    {
        var info = new FileInfo(customPath);
        string fullPath = info.FullName;

        if (_customCacheBytes is not null &&
            string.Equals(_customCachePath, fullPath, StringComparison.OrdinalIgnoreCase) &&
            _customCacheLastWriteUtc == info.LastWriteTimeUtc &&
            _customCacheLength == info.Length)
        {
            return _customCacheBytes;
        }

        byte[] bytes = File.ReadAllBytes(fullPath);
        _customCachePath = fullPath;
        _customCacheLastWriteUtc = info.LastWriteTimeUtc;
        _customCacheLength = info.Length;
        _customCacheBytes = bytes;
        return _customCacheBytes;
    }

    private static byte[]? LoadEmbeddedWav(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        var bytes = new byte[stream.Length];
        int read = stream.Read(bytes, 0, bytes.Length);
        if (read < bytes.Length)
        {
            Array.Resize(ref bytes, read);
        }

        return bytes;
    }
}
