using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace CxShell.Services;

public sealed record RdpClipboardImagePayload(byte[]? PngBytes, byte[]? DibBytes, string Hash);

public static class RdpClipboardImageHelper
{
    private const uint CfDib = 8;
    private const uint CfHDrop = 15;
    private const uint GmemMoveable = 0x0002;
    private static uint _cfPng;
    private static uint _cfHtml;
    private static uint _cfRtf;

    public static uint GetWindowsClipboardSequenceNumber()
    {
        return OperatingSystem.IsWindows() ? GetClipboardSequenceNumber() : 0u;
    }

    private static bool TryOpenClipboardWithRetry(IntPtr ownerHwnd = default, int maxAttempts = 10, int delayMs = 10)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (OpenClipboard(ownerHwnd))
                return true;

            if (attempt + 1 < maxAttempts)
                System.Threading.Thread.Sleep(delayMs);
        }

        return false;
    }

    public static async Task<(byte[]? HtmlBytes, byte[]? RtfBytes)> TryReadLocalRichTextAsync(IClipboard? clipboard)
    {
        if (OperatingSystem.IsWindows())
        {
            if (TryOpenClipboardWithRetry())
            {
                try
                {
                    if (_cfHtml == 0)
                        _cfHtml = RegisterClipboardFormatW("HTML Format");
                    if (_cfRtf == 0)
                        _cfRtf = RegisterClipboardFormatW("Rich Text Format");

                    var html = _cfHtml != 0 ? ReadWindowsClipboardBytes(_cfHtml) : null;
                    var rtf = _cfRtf != 0 ? ReadWindowsClipboardBytes(_cfRtf) : null;
                    if (html != null || rtf != null)
                        return (html, rtf);
                }
                catch
                {
                    // Ignore platform read errors.
                }
                finally
                {
                    CloseClipboard();
                }
            }
        }

        if (clipboard == null)
            return (null, null);

        try
        {
            var transfer = await clipboard.TryGetDataAsync();
            if (transfer == null)
                return (null, null);

            var html = await TryExtractBytesFromDataTransferAsync(transfer, "text/html");
            var rtf = await TryExtractBytesFromDataTransferAsync(transfer, "text/rtf");
            return (html, rtf);
        }
        catch
        {
            return (null, null);
        }
    }

    public static async Task<string[]?> TryReadLocalFilesAsync(IClipboard? clipboard)
    {
        if (OperatingSystem.IsWindows())
        {
            var winFiles = TryReadWindowsClipboardFiles();
            if (winFiles is { Length: > 0 })
                return winFiles;
        }

        if (clipboard == null)
            return null;

        try
        {
            var files = await clipboard.TryGetFilesAsync();
            if (files == null)
                return null;

            var paths = new System.Collections.Generic.List<string>();
            foreach (var item in files)
            {
                var path = item.Path?.LocalPath;
                if (!string.IsNullOrWhiteSpace(path))
                    paths.Add(path);
            }

            return paths.Count > 0 ? paths.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<bool> TryWriteLocalFilesAsync(IClipboard? clipboard, string[] paths, IntPtr ownerHwnd = default)
    {
        if (paths == null || paths.Length == 0)
            return false;

        if (OperatingSystem.IsWindows())
            return TryWriteWindowsClipboardFiles(paths, ownerHwnd);

        if (clipboard == null)
            return false;

        try
        {
            var uris = new System.Collections.Generic.List<string>(paths.Length);
            foreach (var p in paths)
            {
                if (!string.IsNullOrWhiteSpace(p))
                    uris.Add(new Uri(Path.GetFullPath(p)).AbsoluteUri);
            }

            if (uris.Count == 0)
                return false;

            var transfer = new DataTransfer();
            var item = new DataTransferItem();
            var uriListText = string.Join("\r\n", uris) + "\r\n";
            item.Set(
                DataFormat.CreateBytesPlatformFormat("text/uri-list"),
                System.Text.Encoding.UTF8.GetBytes(uriListText));
            if (OperatingSystem.IsMacOS())
                item.Set(DataFormat.CreateStringPlatformFormat("public.file-url"), uris[0]);

            transfer.Add(item);
            await clipboard.SetDataAsync(transfer);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string[]? TryReadWindowsClipboardFiles()
    {
        if (!TryOpenClipboardWithRetry())
            return null;

        try
        {
            if (!IsClipboardFormatAvailable(CfHDrop))
                return null;

            var hDrop = GetClipboardData(CfHDrop);
            if (hDrop == IntPtr.Zero)
                return null;

            var count = DragQueryFileW(hDrop, 0xFFFFFFFFu, null, 0);
            if (count == 0)
                return null;

            var result = new System.Collections.Generic.List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                var length = DragQueryFileW(hDrop, i, null, 0);
                if (length == 0)
                    continue;
                var buffer = new char[length + 1];
                DragQueryFileW(hDrop, i, buffer, length + 1);
                var path = new string(buffer, 0, (int)length);
                if (!string.IsNullOrWhiteSpace(path))
                    result.Add(path);
            }

            return result.Count > 0 ? result.ToArray() : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool TryWriteWindowsClipboardFiles(string[] paths, IntPtr ownerHwnd = default)
    {
        if (!TryOpenClipboardWithRetry(ownerHwnd))
            return false;

        try
        {
            if (!EmptyClipboard())
                return false;

            // DROPFILES header (20 bytes) + UTF-16LE null-separated paths + final null
            var sb = new System.Text.StringBuilder();
            foreach (var p in paths)
            {
                if (!string.IsNullOrWhiteSpace(p))
                {
                    sb.Append(p);
                    sb.Append('\0');
                }
            }
            sb.Append('\0');

            var textBytes = System.Text.Encoding.Unicode.GetBytes(sb.ToString());
            var payload = new byte[20 + textBytes.Length];
            BitConverter.TryWriteBytes(payload.AsSpan(0, 4), 20); // pFiles offset
            BitConverter.TryWriteBytes(payload.AsSpan(16, 4), 1); // fWide = TRUE
            Buffer.BlockCopy(textBytes, 0, payload, 20, textBytes.Length);

            return WriteWindowsClipboardBytes(CfHDrop, payload);
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseClipboard();
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, [Out] char[]? lpszFile, uint cch);

    public static string ComputeHash(byte[]? pngBytes, byte[]? dibBytes)
    {
        var source = (pngBytes is { Length: > 0 } ? pngBytes : dibBytes) ?? Array.Empty<byte>();
        return Convert.ToHexString(SHA256.HashData(source));
    }

    public static async Task<RdpClipboardImagePayload?> TryReadLocalImageAsync(IClipboard? clipboard)
    {
        if (OperatingSystem.IsWindows())
        {
            var winPayload = TryReadWindowsClipboardImage();
            if (winPayload != null)
                return winPayload;
        }

        if (clipboard == null)
            return null;

        try
        {
            var dataTransfer = await clipboard.TryGetDataAsync();
            if (dataTransfer == null)
                return null;

            foreach (var format in new[] { "image/png", "PNG", "public.png" })
            {
                var raw = await TryExtractBytesFromDataTransferAsync(dataTransfer, format);
                if (raw is { Length: > 8 })
                {
                    var dib = TryConvertPngToDib(raw);
                    return new RdpClipboardImagePayload(raw, dib, ComputeHash(raw, dib));
                }
            }
        }
        catch
        {
            // Ignore platform clipboard read failures.
        }

        return null;
    }

    public static async Task<bool> TryWriteLocalImageAsync(IClipboard? clipboard, uint imageFormat, byte[] data, IntPtr ownerHwnd = default)
    {
        if (data == null || data.Length == 0)
            return false;

        byte[]? pngBytes = null;
        byte[]? dibBytes = null;

        if (imageFormat == RdpBridgeClient.ClipboardImageFormatPng)
        {
            pngBytes = data;
            dibBytes = TryConvertPngToDib(data);
        }
        else if (imageFormat == RdpBridgeClient.ClipboardImageFormatDib)
        {
            dibBytes = data;
            pngBytes = TryConvertDibToPng(data);
        }

        if (OperatingSystem.IsWindows())
        {
            if (TryWriteWindowsClipboardImage(pngBytes, dibBytes, ownerHwnd))
                return true;
        }

        if (clipboard != null && pngBytes is { Length: > 0 })
        {
            try
            {
                var transfer = new DataTransfer();
                var item = new DataTransferItem();
                item.Set(DataFormat.CreateBytesPlatformFormat("image/png"), pngBytes);
                transfer.Add(item);
                await clipboard.SetDataAsync(transfer);
                return true;
            }
            catch
            {
                // Ignore platform clipboard write failures.
            }
        }

        return false;
    }

    public static byte[]? TryConvertDibToPng(byte[] dibBytes)
    {
        if (dibBytes.Length < 40)
            return null;

        try
        {
            var headerSize = BitConverter.ToInt32(dibBytes, 0);
            if (headerSize < 40 || headerSize > dibBytes.Length)
                return null;

            var bitCount = BitConverter.ToUInt16(dibBytes, 14);
            var compression = BitConverter.ToUInt32(dibBytes, 16);
            var colorsUsed = BitConverter.ToUInt32(dibBytes, 32);

            uint colorTableSize = 0;
            if (bitCount <= 8)
            {
                colorTableSize = (colorsUsed == 0 ? (1u << bitCount) : colorsUsed) * 4u;
            }
            else if (compression == 3 && headerSize == 40) // BI_BITFIELDS
            {
                colorTableSize = 12;
            }

            var pixelOffset = 14 + headerSize + (int)colorTableSize;
            var fileSize = 14 + dibBytes.Length;
            var bmp = new byte[fileSize];
            bmp[0] = (byte)'B';
            bmp[1] = (byte)'M';
            BitConverter.TryWriteBytes(bmp.AsSpan(2, 4), fileSize);
            BitConverter.TryWriteBytes(bmp.AsSpan(10, 4), pixelOffset);
            Buffer.BlockCopy(dibBytes, 0, bmp, 14, dibBytes.Length);

            using var bmpStream = new MemoryStream(bmp);
            using var bitmap = new Bitmap(bmpStream);
            using var pngStream = new MemoryStream();
#pragma warning disable CS0618
            bitmap.Save(pngStream);
#pragma warning restore CS0618
            return pngStream.ToArray();
        }
        catch
        {
            return null;
        }
    }

    public static unsafe byte[]? TryConvertPngToDib(byte[] pngBytes)
    {
        if (pngBytes.Length < 8)
            return null;

        try
        {
            using var pngStream = new MemoryStream(pngBytes);
            using var writeable = WriteableBitmap.Decode(pngStream);
            var width = writeable.PixelSize.Width;
            var height = writeable.PixelSize.Height;
            if (width <= 0 || height <= 0)
                return null;

            using var fb = writeable.Lock();
            var stride = width * 4;
            var imageSize = stride * height;
            var dib = new byte[40 + imageSize];

            // BITMAPINFOHEADER (40 bytes)
            BitConverter.TryWriteBytes(dib.AsSpan(0, 4), 40);          // biSize
            BitConverter.TryWriteBytes(dib.AsSpan(4, 4), width);       // biWidth
            BitConverter.TryWriteBytes(dib.AsSpan(8, 4), height);      // biHeight (positive = bottom-up)
            BitConverter.TryWriteBytes(dib.AsSpan(12, 2), (ushort)1);  // biPlanes
            BitConverter.TryWriteBytes(dib.AsSpan(14, 2), (ushort)32); // biBitCount
            BitConverter.TryWriteBytes(dib.AsSpan(16, 4), 0);          // biCompression = BI_RGB
            BitConverter.TryWriteBytes(dib.AsSpan(20, 4), imageSize);  // biSizeImage

            var isRgba = fb.Format == PixelFormat.Rgba8888;
            var srcBase = (byte*)fb.Address;
            for (var y = 0; y < height; y++)
            {
                var srcRow = srcBase + (long)y * fb.RowBytes;
                var dstOffset = 40 + (height - 1 - y) * stride;
                if (!isRgba)
                {
                    Marshal.Copy((IntPtr)srcRow, dib, dstOffset, stride);
                }
                else
                {
                    for (var x = 0; x < width; x++)
                    {
                        var si = x * 4;
                        var di = dstOffset + si;
                        dib[di] = srcRow[si + 2];     // B
                        dib[di + 1] = srcRow[si + 1]; // G
                        dib[di + 2] = srcRow[si];     // R
                        dib[di + 3] = srcRow[si + 3]; // A
                    }
                }
            }

            return dib;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<byte[]?> TryExtractBytesFromDataTransferAsync(IAsyncDataTransfer dataTransfer, string formatName)
    {
        try
        {
            var format = DataFormat.CreateBytesPlatformFormat(formatName);
            foreach (var item in dataTransfer.Items)
            {
                var value = await item.TryGetRawAsync(format);
                if (value is byte[] bytes && bytes.Length > 0)
                    return bytes;
            }
        }
        catch
        {
            // Ignore unsupported format errors.
        }

        return null;
    }

    private static uint GetWindowsPngFormat()
    {
        if (_cfPng == 0)
            _cfPng = RegisterClipboardFormatW("PNG");
        return _cfPng;
    }

    private static RdpClipboardImagePayload? TryReadWindowsClipboardImage()
    {
        if (!TryOpenClipboardWithRetry())
            return null;

        try
        {
            var pngFormat = GetWindowsPngFormat();
            var pngBytes = pngFormat != 0 ? ReadWindowsClipboardBytes(pngFormat) : null;
            var dibBytes = ReadWindowsClipboardBytes(CfDib);

            if (pngBytes == null && dibBytes == null)
                return null;

            pngBytes ??= TryConvertDibToPng(dibBytes!);
            dibBytes ??= TryConvertPngToDib(pngBytes!);

            if (pngBytes == null && dibBytes == null)
                return null;

            return new RdpClipboardImagePayload(pngBytes, dibBytes, ComputeHash(pngBytes, dibBytes));
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool TryWriteWindowsClipboardImage(byte[]? pngBytes, byte[]? dibBytes, IntPtr ownerHwnd = default)
    {
        if (pngBytes == null && dibBytes == null)
            return false;

        if (!TryOpenClipboardWithRetry(ownerHwnd))
            return false;

        try
        {
            if (!EmptyClipboard())
                return false;

            var wroteAny = false;
            if (dibBytes is { Length: > 0 })
                wroteAny |= WriteWindowsClipboardBytes(CfDib, dibBytes);

            var pngFormat = GetWindowsPngFormat();
            if (pngFormat != 0 && pngBytes is { Length: > 0 })
                wroteAny |= WriteWindowsClipboardBytes(pngFormat, pngBytes);

            return wroteAny;
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static byte[]? ReadWindowsClipboardBytes(uint format)
    {
        if (!IsClipboardFormatAvailable(format))
            return null;

        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero)
            return null;

        var size = (int)GlobalSize(handle);
        if (size <= 0)
            return null;

        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero)
            return null;

        try
        {
            var buffer = new byte[size];
            Marshal.Copy(ptr, buffer, 0, size);
            return buffer;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static bool WriteWindowsClipboardBytes(uint format, byte[] data)
    {
        var hGlobal = GlobalAlloc(GmemMoveable, (UIntPtr)data.Length);
        if (hGlobal == IntPtr.Zero)
            return false;

        var ptr = GlobalLock(hGlobal);
        if (ptr == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
            return false;
        }

        try
        {
            Marshal.Copy(data, 0, ptr, data.Length);
        }
        finally
        {
            GlobalUnlock(hGlobal);
        }

        if (SetClipboardData(format, hGlobal) == IntPtr.Zero)
        {
            GlobalFree(hGlobal);
            return false;
        }

        return true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr GlobalSize(IntPtr hMem);
}
