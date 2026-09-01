using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace TuckPane.Services;

public sealed class IconCacheService
{
    private const int JumboSize = 256;
    private const int FallbackSize = 32;
    private const string CacheVersion = "v4-image-thumbnail";
    private readonly Dictionary<string, BitmapImage> _memoryCache = new(StringComparer.OrdinalIgnoreCase);

    internal static IntPtr CreateDragBitmap(string path, int size)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        IntPtr icon = GetPreferredIcon(path, size, out _);

        try
        {
            byte[] pixels = DrawIconPixels(icon, size);
            UnpremultiplyAlpha(pixels);
            return CreateBitmap(pixels, size);
        }
        finally
        {
            _ = NativeMethods.DestroyIcon(icon);
        }
    }

    public async Task<BitmapImage?> GetIconAsync(string path, bool refresh = false)
    {
        AppPaths.EnsureCreated();
        string key = Path.GetFullPath(path);
        if (refresh) _memoryCache.Remove(key);
        if (!refresh && _memoryCache.TryGetValue(key, out BitmapImage? cached)) return cached;

        string cachePath = Path.Combine(AppPaths.IconCacheRoot, $"{Hash(key)}.png");
        if (refresh || !File.Exists(cachePath))
        {
            try
            {
                await RefreshAsync(key, cachePath);
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Shell 图标提取失败：{key}", ex);
            }
        }

        if (!File.Exists(cachePath)) return null;
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(cachePath);
            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            _memoryCache[key] = image;
            return image;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"图标缓存读取失败：{cachePath}", ex);
            return null;
        }
    }

    private static async Task RefreshAsync(string path, string cachePath)
    {
        IconSnapshot snapshot = await TryExtractImageThumbnailAsync(path)
            ?? await Task.Run(() => ExtractShellIconPixels(path));
        StorageFolder cacheFolder = await StorageFolder.GetFolderFromPathAsync(AppPaths.IconCacheRoot);
        string temporaryName = $"{Path.GetFileNameWithoutExtension(cachePath)}.{Guid.NewGuid():N}.tmp";
        StorageFile temporary = await cacheFolder.CreateFileAsync(temporaryName, CreationCollisionOption.FailIfExists);
        try
        {
            using IRandomAccessStream output = await temporary.OpenAsync(FileAccessMode.ReadWrite);
            using SoftwareBitmap bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                snapshot.Pixels.AsBuffer(),
                BitmapPixelFormat.Bgra8,
                snapshot.Width,
                snapshot.Height,
                BitmapAlphaMode.Premultiplied);
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            File.Move(temporary.Path, cachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary.Path)) File.Delete(temporary.Path);
        }
    }

    private static async Task<IconSnapshot?> TryExtractImageThumbnailAsync(string path)
    {
        if (!File.Exists(path)) return null;
        string extension = Path.GetExtension(path);
        if (!ImageThumbnailExtensions.Contains(extension)) return null;
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            if (!file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;

            using StorageItemThumbnail thumbnail = await file.GetThumbnailAsync(
                ThumbnailMode.SingleItem,
                JumboSize,
                ThumbnailOptions.ResizeThumbnail);
            if (thumbnail is null || thumbnail.Type != ThumbnailType.Image) return null;

            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(thumbnail);
            using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied);
            uint byteCount = checked((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
            var buffer = new Windows.Storage.Streams.Buffer(byteCount);
            bitmap.CopyToBuffer(buffer);
            return new IconSnapshot(buffer.ToArray(), bitmap.PixelWidth, bitmap.PixelHeight);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"图片缩略图提取失败，已回退 Shell 图标：{path}", ex);
            return null;
        }
    }

    private static readonly HashSet<string> ImageThumbnailExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".jxl"
    };

    internal static IconSnapshot ExtractShellIconPixels(string path)
    {
        IntPtr icon = GetPreferredIcon(path, JumboSize, out int sourceSize);
        try
        {
            return new(DrawIconPixels(icon, sourceSize), sourceSize, sourceSize);
        }
        finally
        {
            _ = NativeMethods.DestroyIcon(icon);
        }
    }

    private static IntPtr GetPreferredIcon(string path, int requestedSize, out int sourceSize)
    {
        if (TryGetInternetShortcutIcon(path, requestedSize, out IntPtr internetShortcutIcon))
        {
            sourceSize = requestedSize;
            return internetShortcutIcon;
        }
        if (TryGetJumboIcon(path, out IntPtr jumboIcon))
        {
            sourceSize = JumboSize;
            return jumboIcon;
        }
        sourceSize = FallbackSize;
        return GetFallbackIcon(path);
    }

    private static bool TryGetInternetShortcutIcon(string path, int size, out IntPtr icon)
    {
        icon = IntPtr.Zero;
        if (!Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
        try
        {
            string iconFile = ReadInternetShortcutValue(path, "IconFile");
            if (string.IsNullOrWhiteSpace(iconFile)) return false;
            iconFile = Environment.ExpandEnvironmentVariables(iconFile.Trim());
            if (!Path.IsPathFullyQualified(iconFile))
                iconFile = Path.Combine(Path.GetDirectoryName(path)!, iconFile);
            iconFile = Path.GetFullPath(iconFile);
            if (iconFile.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(iconFile)) return false;

            int iconIndex = int.TryParse(
                ReadInternetShortcutValue(path, "IconIndex"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int parsedIndex)
                ? parsedIndex
                : 0;
            int result = NativeMethods.SHDefExtractIcon(iconFile, iconIndex, 0, out icon, out IntPtr smallIcon, (uint)size);
            if (smallIcon != IntPtr.Zero) _ = NativeMethods.DestroyIcon(smallIcon);
            if (result == 0 && icon != IntPtr.Zero) return true;
            if (icon != IntPtr.Zero) _ = NativeMethods.DestroyIcon(icon);
            icon = IntPtr.Zero;
        }
        catch
        {
            icon = IntPtr.Zero;
        }
        return false;
    }

    private static string ReadInternetShortcutValue(string path, string key)
    {
        var value = new StringBuilder(32768);
        _ = NativeMethods.GetPrivateProfileString("InternetShortcut", key, string.Empty, value, (uint)value.Capacity, path);
        return value.ToString();
    }

    private static bool TryGetJumboIcon(string path, out IntPtr icon)
    {
        icon = IntPtr.Zero;
        var shellInfo = new NativeMethods.SHFILEINFO { DisplayName = string.Empty, TypeName = string.Empty };
        UIntPtr result = NativeMethods.SHGetFileInfo(
            path,
            0,
            ref shellInfo,
            (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
            NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SYSICONINDEX | NativeMethods.SHGFI_ADDOVERLAYS | NativeMethods.SHGFI_OVERLAYINDEX);
        if (result == UIntPtr.Zero) return false;

        if (shellInfo.Icon != IntPtr.Zero) _ = NativeMethods.DestroyIcon(shellInfo.Icon);

        Guid interfaceId = typeof(NativeMethods.IImageList).GUID;
        int hr = NativeMethods.SHGetImageList(NativeMethods.SHIL_JUMBO, ref interfaceId, out NativeMethods.IImageList imageList);
        if (hr < 0) return false;
        try
        {
            int imageIndex = shellInfo.IconIndex & 0x00FFFFFF;
            int overlayIndex = (shellInfo.IconIndex >> 24) & 0xFF;
            uint flags = NativeMethods.ILD_TRANSPARENT | ((uint)overlayIndex << 8);
            return imageList.GetIcon(imageIndex, flags, out icon) >= 0 && icon != IntPtr.Zero;
        }
        finally
        {
            if (Marshal.IsComObject(imageList)) _ = Marshal.FinalReleaseComObject(imageList);
        }
    }

    private static IntPtr GetFallbackIcon(string path)
    {
        var shellInfo = new NativeMethods.SHFILEINFO { DisplayName = string.Empty, TypeName = string.Empty };
        UIntPtr result = NativeMethods.SHGetFileInfo(
            path,
            0,
            ref shellInfo,
            (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
            NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON | NativeMethods.SHGFI_ADDOVERLAYS);
        if (result == UIntPtr.Zero || shellInfo.Icon == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Windows Shell 未返回图标：{path}");
        }
        return shellInfo.Icon;
    }

    private static byte[] DrawIconPixels(IntPtr icon, int size)
    {
        var bitmapInfo = new NativeMethods.BITMAPINFO
        {
            Header = new NativeMethods.BITMAPINFOHEADER
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                Width = size,
                Height = -size,
                Planes = 1,
                BitCount = 32,
                Compression = NativeMethods.BI_RGB,
                SizeImage = (uint)(size * size * 4)
            }
        };
        IntPtr dc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
        IntPtr bitmap = NativeMethods.CreateDIBSection(dc, ref bitmapInfo, NativeMethods.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr previous = IntPtr.Zero;
        try
        {
            if (dc == IntPtr.Zero || bitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                throw new InvalidOperationException("无法创建图标缓冲区。");
            }
            previous = NativeMethods.SelectObject(dc, bitmap);
            byte[] pixels = new byte[size * size * 4];
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            if (!NativeMethods.DrawIconEx(dc, 0, 0, icon, size, size, 0, IntPtr.Zero, NativeMethods.DI_NORMAL))
            {
                throw new InvalidOperationException("无法绘制 Shell 图标。");
            }
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            RepairMissingAlpha(pixels);
            return pixels;
        }
        finally
        {
            if (previous != IntPtr.Zero && dc != IntPtr.Zero) _ = NativeMethods.SelectObject(dc, previous);
            if (bitmap != IntPtr.Zero) _ = NativeMethods.DeleteObject(bitmap);
            if (dc != IntPtr.Zero) _ = NativeMethods.DeleteDC(dc);
        }
    }

    private static void RepairMissingAlpha(byte[] pixels)
    {
        bool hasAlpha = false;
        for (int index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] != 0)
            {
                hasAlpha = true;
                break;
            }
        }
        if (hasAlpha) return;
        for (int index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index] != 0 || pixels[index + 1] != 0 || pixels[index + 2] != 0) pixels[index + 3] = 255;
        }
    }

    private static void UnpremultiplyAlpha(byte[] pixels)
    {
        for (int index = 0; index < pixels.Length; index += 4)
        {
            int alpha = pixels[index + 3];
            if (alpha is 0 or 255) continue;
            pixels[index] = (byte)Math.Min(255, pixels[index] * 255 / alpha);
            pixels[index + 1] = (byte)Math.Min(255, pixels[index + 1] * 255 / alpha);
            pixels[index + 2] = (byte)Math.Min(255, pixels[index + 2] * 255 / alpha);
        }
    }

    private static IntPtr CreateBitmap(byte[] pixels, int size)
    {
        var bitmapInfo = new NativeMethods.BITMAPINFO
        {
            Header = new NativeMethods.BITMAPINFOHEADER
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                Width = size,
                Height = -size,
                Planes = 1,
                BitCount = 32,
                Compression = NativeMethods.BI_RGB,
                SizeImage = (uint)pixels.Length
            }
        };
        IntPtr bitmap = NativeMethods.CreateDIBSection(IntPtr.Zero, ref bitmapInfo, NativeMethods.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
        {
            if (bitmap != IntPtr.Zero) _ = NativeMethods.DeleteObject(bitmap);
            throw new InvalidOperationException("无法创建Shell拖动图像。");
        }
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        return bitmap;
    }

    private static string Hash(string path)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{CacheVersion}|{path.ToUpperInvariant()}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    internal sealed record IconSnapshot(byte[] Pixels, int Width, int Height)
    {
        internal int Size => Width;
    }
}
