using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZZZ.Services;

public sealed class FaviconCacheService
{
    private static readonly byte[] Header = [0x5A, 0x46, 0x41, 0x56, 0x01];
    private static readonly byte[] Entropy = SHA256.Create().ComputeHash(Encoding.UTF8.GetBytes("ZZZ.FaviconCache.v1"));
    private readonly Dictionary<string, ImageSource> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ImageSource? GetCached(string url)
    {
        var key = CacheKey(url);
        if (key is null) return null;
        lock (_gate)
            if (_memory.TryGetValue(key, out var cached)) return cached;

        var path = Path.Combine(AppPaths.Favicons, key + ".dat");
        byte[] plaintext = [];
        try
        {
            var stored = File.ReadAllBytes(path);
            if (stored.Length <= Header.Length || !Header.SequenceEqual(stored.Take(Header.Length))) return null;
            plaintext = ProtectedData.Unprotect(stored.Skip(Header.Length).ToArray(), Entropy, DataProtectionScope.CurrentUser);
            var image = Decode(plaintext);
            if (image is null) return null;
            lock (_gate) _memory[key] = image;
            return image;
        }
        catch { return null; }
        finally { if (plaintext.Length > 0) Array.Clear(plaintext, 0, plaintext.Length); }
    }

    public async Task<ImageSource?> CaptureAsync(string url, Stream stream, bool persist)
    {
        var key = CacheKey(url);
        if (key is null) return null;
        var source = await ReadBoundedAsync(stream, 512 * 1024);
        if (source.Length == 0) return null;
        var normalized = NormalizePng(source);
        Array.Clear(source, 0, source.Length);
        if (normalized.Length == 0) return null;
        var image = Decode(normalized);
        if (image is null) { Array.Clear(normalized, 0, normalized.Length); return null; }
        if (!persist) { Array.Clear(normalized, 0, normalized.Length); return image; }

        lock (_gate) _memory[key] = image;
        byte[] protectedBytes;
        try { protectedBytes = ProtectedData.Protect(normalized, Entropy, DataProtectionScope.CurrentUser); }
        finally { Array.Clear(normalized, 0, normalized.Length); }
        try { await WriteAtomicAsync(Path.Combine(AppPaths.Favicons, key + ".dat"), protectedBytes); }
        finally { Array.Clear(protectedBytes, 0, protectedBytes.Length); }
        return image;
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (output.Length <= maximumBytes)
        {
            var read = await stream.ReadAsync(buffer, 0, buffer.Length);
            if (read <= 0) break;
            if (output.Length + read > maximumBytes) return [];
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static byte[] NormalizePng(byte[] source)
    {
        try
        {
            using var input = new MemoryStream(source, false);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return [];
            BitmapSource bitmap = decoder.Frames[0];
            if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || bitmap.PixelWidth > 2048 || bitmap.PixelHeight > 2048) return [];
            var scale = Math.Min(1d, 64d / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight));
            if (scale < 1d) bitmap = new TransformedBitmap(bitmap, new ScaleTransform(scale, scale));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.Length <= 256 * 1024 ? output.ToArray() : [];
        }
        catch { return []; }
    }

    private static ImageSource? Decode(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png, false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 32;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private static string? CacheKey(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.IdnHost)) return null;
        var identity = uri.IdnHost.ToLowerInvariant();
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(identity)).Select(x => x.ToString("x2")));
    }

    private static async Task WriteAtomicAsync(string path, byte[] payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await file.WriteAsync(Header, 0, Header.Length);
                await file.WriteAsync(payload, 0, payload.Length);
                file.Flush(true);
            }
            if (File.Exists(path))
            {
                try { File.Replace(temp, path, null, true); return; }
                catch (PlatformNotSupportedException) { }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
    }
}
