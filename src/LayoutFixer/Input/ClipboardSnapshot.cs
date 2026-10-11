using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using LayoutFixer.Diagnostics;

namespace LayoutFixer.Input;

/// <summary>
/// A copy of every memory-based clipboard format (text, HTML, RTF, images as
/// DIB/PNG, file lists, ...) so the clipboard can be put back exactly as the
/// user left it after LayoutFixer had to borrow it to read a selection.
/// </summary>
/// <remarks>
/// GDI-handle formats (CF_BITMAP, metafiles, palettes) cannot be copied as
/// bytes; Windows synthesises CF_BITMAP from the saved CF_DIB anyway. Very
/// large clipboards are not copied (the text is still kept) to avoid holding
/// hundreds of megabytes for a moment.
/// </remarks>
internal sealed class ClipboardSnapshot
{
    private const long MaxTotalBytes = 64L * 1024 * 1024;
    private const int OpenAttempts = 10;
    private const int OpenRetryDelayMs = 5;

    private readonly List<(uint Format, byte[] Data)> _formats = new();

    private ClipboardSnapshot()
    {
    }

    public bool IsEmpty => _formats.Count == 0;

    // A window of this process to own the clipboard while restoring it. With
    // a NULL owner, EmptyClipboard clears the owner and SetClipboardData is
    // documented to fail. Set once at startup by the tray context.
    public static IntPtr OwnerWindow { get; set; }

    public static ClipboardSnapshot Capture()
    {
        var snapshot = new ClipboardSnapshot();
        if (!TryOpen())
        {
            Logger.Log("ClipboardSnapshot: OpenClipboard failed, nothing captured");
            return snapshot;
        }

        try
        {
            long total = 0;
            uint format = 0;
            while ((format = NativeMethods.EnumClipboardFormats(format)) != 0)
            {
                if (IsHandleFormat(format))
                {
                    continue;
                }

                IntPtr handle = NativeMethods.GetClipboardData(format);
                if (handle == IntPtr.Zero)
                {
                    continue;
                }

                long size = (long)(ulong)NativeMethods.GlobalSize(handle);
                if (size <= 0 || total + size > MaxTotalBytes)
                {
                    continue;
                }

                IntPtr pointer = NativeMethods.GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    var data = new byte[size];
                    Marshal.Copy(pointer, data, 0, data.Length);
                    snapshot._formats.Add((format, data));
                    total += size;
                }
                finally
                {
                    NativeMethods.GlobalUnlock(handle);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"ClipboardSnapshot: capture failed: {ex.Message}");
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }

        return snapshot;
    }

    /// <summary>Puts the captured formats back (an empty snapshot empties the clipboard).</summary>
    public void Restore()
    {
        if (!TryOpen())
        {
            Logger.Log("ClipboardSnapshot: OpenClipboard failed, clipboard not restored");
            return;
        }

        try
        {
            NativeMethods.EmptyClipboard();
            foreach ((uint format, byte[] data) in _formats)
            {
                IntPtr handle = NativeMethods.GlobalAlloc(NativeMethods.GmemMoveable, (UIntPtr)data.Length);
                if (handle == IntPtr.Zero)
                {
                    continue;
                }

                IntPtr pointer = NativeMethods.GlobalLock(handle);
                if (pointer == IntPtr.Zero)
                {
                    NativeMethods.GlobalFree(handle);
                    continue;
                }

                Marshal.Copy(data, 0, pointer, data.Length);
                NativeMethods.GlobalUnlock(handle);

                // On success the clipboard owns the memory; free it otherwise.
                if (NativeMethods.SetClipboardData(format, handle) == IntPtr.Zero)
                {
                    NativeMethods.GlobalFree(handle);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"ClipboardSnapshot: restore failed: {ex.Message}");
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private static bool IsHandleFormat(uint format)
    {
        return format is 2 /* CF_BITMAP */ or 3 /* CF_METAFILEPICT */ or 9 /* CF_PALETTE */
            or 14 /* CF_ENHMETAFILE */ or 0x80 /* CF_OWNERDISPLAY */ or 0x82 /* CF_DSPBITMAP */
            or 0x83 /* CF_DSPMETAFILEPICT */ or 0x8E /* CF_DSPENHMETAFILE */
            or >= 0x300 and <= 0x3FF /* CF_GDIOBJFIRST..CF_GDIOBJLAST */;
    }

    private static bool TryOpen()
    {
        for (int attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (NativeMethods.OpenClipboard(OwnerWindow))
            {
                return true;
            }

            Thread.Sleep(OpenRetryDelayMs);
        }

        return false;
    }
}
