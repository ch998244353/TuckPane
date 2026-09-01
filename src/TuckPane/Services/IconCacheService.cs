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
    private const int MaximumShortcutIconDepth = 4;
    private const string CacheVersion = "v7-item-metadata";
    private readonly Dictionary<string, (string Identity, BitmapImage Image)> _memoryCache = new(StringComparer.OrdinalIgnoreCase);

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
        string identity = BuildCacheIdentity(key);
        if (_memoryCache.TryGetValue(key, out var cached) &&
            (!refresh || string.Equals(cached.Identity, identity, StringComparison.Ordinal)))
        {
            return cached.Image;
        }
        _memoryCache.Remove(key);

        string cachePath = Path.Combine(AppPaths.IconCacheRoot, $"{Hash(identity)}.png");
        if (!File.Exists(cachePath))
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
            _memoryCache[key] = (identity, image);
            return image;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"图标缓存读取失败：{cachePath}", ex);
            return null;
        }
    }

    internal static string BuildCacheIdentity(string path)
    {
        string fullPath = Path.GetFullPath(path);
        try
        {
            if (File.Exists(fullPath))
            {
                var file = new FileInfo(fullPath);
                return $"{fullPath}|file|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
            }
            if (Directory.Exists(fullPath))
            {
                var directory = new DirectoryInfo(fullPath);
                return $"{fullPath}|directory|0|{directory.LastWriteTimeUtc.Ticks}";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{fullPath}|unavailable|0|0";
        }
        return $"{fullPath}|missing|0|0";
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
        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".url", StringComparison.OrdinalIgnoreCase)) return null;
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

    internal static IconSnapshot ExtractShellIconPixels(string path)
    {
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".url", StringComparison.OrdinalIgnoreCase) &&
            TryExtractShellItemImage(path, JumboSize, out IconSnapshot shellItemImage))
        {
            return shellItemImage;
        }

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
        if (TryGetShellLinkIcon(path, requestedSize, out IntPtr shellLinkIcon))
        {
            sourceSize = requestedSize;
            return shellLinkIcon;
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

    private static bool TryGetShellLinkIcon(string path, int size, out IntPtr icon)
    {
        icon = IntPtr.Zero;
        if (!Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string current = Path.GetFullPath(path);
        for (int depth = 0; depth < MaximumShortcutIconDepth && visited.Add(current); depth++)
        {
            if (!TryReadShellLinkIconLocation(current, out string iconPath, out int iconIndex)) return false;
            iconPath = Environment.ExpandEnvironmentVariables(iconPath.Trim());
            if (!Path.IsPathFullyQualified(iconPath))
                iconPath = Path.Combine(Path.GetDirectoryName(current)!, iconPath);
            iconPath = Path.GetFullPath(iconPath);
            if (iconPath.StartsWith(@"\\", StringComparison.Ordinal) || !File.Exists(iconPath)) return false;
            if (Path.GetExtension(iconPath).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                current = iconPath;
                continue;
            }

            int result = NativeMethods.SHDefExtractIcon(iconPath, iconIndex, 0, out icon, out IntPtr smallIcon, (uint)size);
            if (smallIcon != IntPtr.Zero) _ = NativeMethods.DestroyIcon(smallIcon);
            if (result == 0 && icon != IntPtr.Zero) return true;
            if (icon != IntPtr.Zero) _ = NativeMethods.DestroyIcon(icon);
            icon = IntPtr.Zero;
            return false;
        }
        return false;
    }

    private static bool TryReadShellLinkIconLocation(string shortcutPath, out string iconPath, out int iconIndex)
    {
        iconPath = string.Empty;
        iconIndex = 0;
        NativeMethods.IShellLinkW? shellLink = null;
        try
        {
            shellLink = (NativeMethods.IShellLinkW)new NativeMethods.ShellLink();
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)shellLink).Load(shortcutPath, 0);
            var value = new StringBuilder(32768);
            if (shellLink.GetIconLocation(value, value.Capacity, out iconIndex) < 0 || value.Length == 0) return false;
            iconPath = value.ToString();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (shellLink is not null && Marshal.IsComObject(shellLink)) _ = Marshal.FinalReleaseComObject(shellLink);
        }
    }

    private static bool TryExtractShellItemImage(string path, int size, out IconSnapshot snapshot)
    {
        snapshot = null!;
        if (!File.Exists(path) && !Directory.Exists(path)) return false;

        NativeMethods.IShellItemImageFactory? imageFactory = null;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            Guid interfaceId = typeof(NativeMethods.IShellItemImageFactory).GUID;
            if (NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref interfaceId, out imageFactory) < 0)
                return false;
            if (imageFactory.GetImage(
                    new NativeMethods.SIZE { Width = size, Height = size },
                    NativeMethods.SIIGBF_ICONONLY,
                    out bitmap) < 0 || bitmap == IntPtr.Zero)
            {
                return false;
            }
            if (NativeMethods.GetObject(bitmap, Marshal.SizeOf<NativeMethods.BITMAP>(), out NativeMethods.BITMAP details) == 0 ||
                details.Width <= 0 || details.Height == 0)
            {
                return false;
            }

            int width = details.Width;
            int height = Math.Abs(details.Height);
            byte[] pixels = new byte[checked(width * height * 4)];
            var bitmapInfo = new NativeMethods.BITMAPINFO
            {
                Header = new NativeMethods.BITMAPINFOHEADER
                {
                    Size = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = NativeMethods.BI_RGB,
                    SizeImage = (uint)pixels.Length
                }
            };
            IntPtr dc = NativeMethods.CreateCompatibleDC(IntPtr.Zero);
            try
            {
                if (dc == IntPtr.Zero || NativeMethods.GetDIBits(
                        dc,
                        bitmap,
                        0,
                        (uint)height,
                        pixels,
                        ref bitmapInfo,
                        NativeMethods.DIB_RGB_COLORS) == 0)
                {
                    return false;
                }
            }
            finally
            {
                if (dc != IntPtr.Zero) _ = NativeMethods.DeleteDC(dc);
            }

            RepairMissingAlpha(pixels);
            snapshot = new IconSnapshot(pixels, width, height);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (bitmap != IntPtr.Zero) _ = NativeMethods.DeleteObject(bitmap);
            if (imageFactory is not null && Marshal.IsComObject(imageFactory)) _ = Marshal.FinalReleaseComObject(imageFactory);
        }
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
            NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SYSICONINDEX);
        if (result == UIntPtr.Zero) return false;

        if (shellInfo.Icon != IntPtr.Zero) _ = NativeMethods.DestroyIcon(shellInfo.Icon);

        Guid interfaceId = typeof(NativeMethods.IImageList).GUID;
        int hr = NativeMethods.SHGetImageList(NativeMethods.SHIL_JUMBO, ref interfaceId, out NativeMethods.IImageList imageList);
        if (hr < 0) return false;
        try
        {
            int imageIndex = shellInfo.IconIndex & 0x00FFFFFF;
            return imageList.GetIcon(imageIndex, NativeMethods.ILD_TRANSPARENT, out icon) >= 0 && icon != IntPtr.Zero;
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
            NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);
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
