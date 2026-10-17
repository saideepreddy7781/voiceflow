using System.Runtime.InteropServices;
using System.Text;
using static VoiceFlow.Windows.Native.NativeMethods;

namespace VoiceFlow.Windows.Native;

/// <summary>One saved clipboard format.</summary>
internal sealed record ClipboardItem(uint Format, string? Name, byte[] Data);

/// <summary>
/// The Windows clipboard through Win32 directly, so everything on it can be saved and put back byte for byte.
/// Must be used on the UI thread: the owner window has to pump messages, because other apps send it
/// WM_DESTROYCLIPBOARD while they empty the clipboard.
/// </summary>
internal sealed class ClipboardService(IntPtr ownerWindow)
{
    /// <summary>Clipboard managers skip content carrying this format (and password managers set it on secrets).</summary>
    public const string ExcludeFromMonitoring = "ExcludeClipboardContentFromMonitorProcessing";
    /// <summary>DWORD 0: keep out of Win+V clipboard history.</summary>
    public const string CanIncludeInHistory = "CanIncludeInClipboardHistory";
    /// <summary>DWORD 0: do not sync to other devices through cloud clipboard.</summary>
    public const string CanUploadToCloud = "CanUploadToCloudClipboard";
    const string ClipboardViewerIgnore = "Clipboard Viewer Ignore";

    /// <summary>Skip anything bigger than this when saving (a huge screenshot is kept by its owner, not copied).</summary>
    const long MaxSavedBytes = 64L * 1024 * 1024;

    static uint Format(string name) => RegisterClipboardFormatW(name);

    /// <summary>OpenClipboard fails while another app holds it; try for about half a second.</summary>
    async Task<bool> OpenAsync()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if (OpenClipboard(ownerWindow)) return true;
            await Task.Delay(25);
        }
        Logger.Warn($"Clipboard is locked by another app (error {Marshal.GetLastWin32Error()})");
        return false;
    }

    public static uint SequenceNumber => GetClipboardSequenceNumber();

    /// <summary>Everything currently on the clipboard that is plain memory (GDI handles are re-synthesized by Windows).</summary>
    public async Task<List<ClipboardItem>?> SaveAsync()
    {
        if (!await OpenAsync()) return null;
        var items = new List<ClipboardItem>();
        try
        {
            var hasUnicode = IsClipboardFormatAvailable(CF_UNICODETEXT);
            uint format = 0;
            long total = 0;
            while ((format = EnumClipboardFormats(format)) != 0)
            {
                if (IsHandleFormat(format)) continue;
                // Windows synthesizes ANSI and OEM text from Unicode text.
                if (hasUnicode && format is CF_TEXT or CF_OEMTEXT) continue;
                var handle = GetClipboardData(format);
                if (handle == IntPtr.Zero) continue;
                var size = (long)GlobalSize(handle).ToUInt64();
                if (size <= 0 || total + size > MaxSavedBytes) continue;
                var ptr = GlobalLock(handle);
                if (ptr == IntPtr.Zero) continue;
                try
                {
                    var data = new byte[size];
                    Marshal.Copy(ptr, data, 0, (int)size);
                    items.Add(new ClipboardItem(format, FormatName(format), data));
                    total += size;
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
        }
        finally
        {
            CloseClipboard();
        }
        return items;
    }

    static bool IsHandleFormat(uint f) =>
        f is CF_BITMAP or CF_METAFILEPICT or CF_PALETTE or CF_ENHMETAFILE or CF_OWNERDISPLAY or CF_DSPBITMAP
            or CF_DSPMETAFILEPICT or CF_DSPENHMETAFILE
        || (f >= CF_PRIVATEFIRST && f <= CF_GDIOBJLAST);

    static string? FormatName(uint format)
    {
        if (format < 0xC000) return null;
        var sb = new StringBuilder(256);
        return GetClipboardFormatNameW(format, sb, sb.Capacity) > 0 ? sb.ToString() : null;
    }

    /// <summary>
    /// True when the saved content was marked private by the app that copied it (password managers do this and
    /// clear it themselves). Putting it back would undo that, so it is dropped instead, as on macOS.
    /// </summary>
    public static bool IsConcealed(IEnumerable<ClipboardItem> items) =>
        items.Any(i => i.Name is ExcludeFromMonitoring or ClipboardViewerIgnore);

    /// <summary>Puts <paramref name="text"/> on the clipboard. Returns the clipboard sequence number afterwards, or null.</summary>
    /// <param name="transient">Keep it out of clipboard managers and Win+V history (a temporary paste).</param>
    /// <param name="noCloud">Never sync it through cloud clipboard.</param>
    public async Task<uint?> SetTextAsync(string text, bool transient, bool noCloud = true)
    {
        if (!await OpenAsync()) return null;
        try
        {
            if (!EmptyClipboard()) return null;
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            if (!SetBytes(CF_UNICODETEXT, bytes)) return null;
            if (transient)
            {
                SetBytes(Format(ExcludeFromMonitoring), BitConverter.GetBytes(0));
                SetBytes(Format(CanIncludeInHistory), BitConverter.GetBytes(0));
            }
            if (transient || noCloud) SetBytes(Format(CanUploadToCloud), BitConverter.GetBytes(0));
        }
        finally
        {
            CloseClipboard();
        }
        return SequenceNumber;
    }

    /// <summary>Puts saved content back. Restored data is kept out of history (it is already there from the original copy).</summary>
    public async Task<bool> RestoreAsync(List<ClipboardItem> items)
    {
        if (IsConcealed(items)) return await ClearAsync();
        if (!await OpenAsync()) return false;
        try
        {
            if (!EmptyClipboard()) return false;
            foreach (var item in items)
            {
                var format = item.Name != null ? Format(item.Name) : item.Format;
                if (format != 0) SetBytes(format, item.Data);
            }
            if (items.Count > 0)
            {
                SetBytes(Format(CanIncludeInHistory), BitConverter.GetBytes(0));
                SetBytes(Format(CanUploadToCloud), BitConverter.GetBytes(0));
            }
        }
        finally
        {
            CloseClipboard();
        }
        return true;
    }

    async Task<bool> ClearAsync()
    {
        if (!await OpenAsync()) return false;
        try { return EmptyClipboard(); }
        finally { CloseClipboard(); }
    }

    public async Task<string?> GetTextAsync()
    {
        if (!await OpenAsync()) return null;
        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero) return null;
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero) return null;
            try
            {
                var size = (int)Math.Min(int.MaxValue, (long)GlobalSize(handle).ToUInt64());
                return Marshal.PtrToStringUni(ptr, size / 2).Split('\0')[0];
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>True if the format named <paramref name="name"/> is on the clipboard now.</summary>
    public static bool HasFormat(string name) => IsClipboardFormatAvailable(Format(name));

    /// <summary>Copies bytes into movable global memory and hands it to the clipboard (which then owns it).</summary>
    static bool SetBytes(uint format, byte[] data)
    {
        var mem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)Math.Max(1, data.Length));
        if (mem == IntPtr.Zero) return false;
        var ptr = GlobalLock(mem);
        if (ptr == IntPtr.Zero)
        {
            GlobalFree(mem);
            return false;
        }
        Marshal.Copy(data, 0, ptr, data.Length);
        GlobalUnlock(mem);
        if (SetClipboardData(format, mem) == IntPtr.Zero)
        {
            GlobalFree(mem);
            return false;
        }
        return true;
    }
}
