using System;
using System.Collections.Generic;
using System.IO;

namespace AHRI8Backup;

/// <summary>支持的图片格式。站点现在会混有 GIF 动图，也顺带支持 PNG / WebP。</summary>
internal enum ImageKind
{
    Unknown = 0,
    Jpeg,
    Png,
    Gif,
    Webp,
}

/// <summary>
/// 图片格式识别与完整性校验。
///
/// 下载时先写 <c>0001.jpg.part</c>，完成后必须通过这里的校验才会改成正式文件名；
/// 文件名扩展名以**实际内容**为准（URL 写 .jpg 但内容是 GIF 时存成 0001.gif），
/// 这样本地播放器/看图软件都能正常打开动图。
/// </summary>
internal static class ImageIntegrity
{
    public const long MinBytes = 100;
    private const int TailProbeBytes = 4096;

    /// <summary>查找已存在图片时尝试的扩展名（顺序即优先级）。</summary>
    public static readonly string[] SupportedExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".webp" };

    public static bool IsSupportedExtension(string extension) =>
        Array.IndexOf(SupportedExtensions, (extension ?? "").ToLowerInvariant()) >= 0;

    public static string ExtensionOfKind(ImageKind kind) => kind switch
    {
        ImageKind.Jpeg => ".jpg",
        ImageKind.Png => ".png",
        ImageKind.Gif => ".gif",
        ImageKind.Webp => ".webp",
        _ => ".jpg",
    };

    public static ImageKind KindOfExtension(string extension) => (extension ?? "").ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => ImageKind.Jpeg,
        ".png" => ImageKind.Png,
        ".gif" => ImageKind.Gif,
        ".webp" => ImageKind.Webp,
        _ => ImageKind.Unknown,
    };

    public static string NameOfKind(ImageKind kind) => kind switch
    {
        ImageKind.Jpeg => "JPEG",
        ImageKind.Png => "PNG",
        ImageKind.Gif => "GIF",
        ImageKind.Webp => "WebP",
        _ => "未知格式",
    };

    /// <summary>从图片 URL 推断保存扩展名；无法判断时回退 .jpg（下载后会按实际内容纠正）。</summary>
    public static string ExtensionFromUrl(string url)
    {
        try
        {
            var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
            var extension = Path.GetExtension(path).ToLowerInvariant();
            return IsSupportedExtension(extension) ? extension : ".jpg";
        }
        catch
        {
            return ".jpg";
        }
    }

    public static string PageFile(string folder, int page, string extension) =>
        Path.Combine(folder, page.ToString("D4") + (string.IsNullOrWhiteSpace(extension) ? ".jpg" : extension));

    public static string PartFile(string folder, int page, string extension) => PageFile(folder, page, extension) + ".part";

    /// <summary>在文件夹里查找某一页的现有图片（任意支持的扩展名）。</summary>
    public static string? FindPageFile(string folder, int page)
    {
        foreach (var extension in SupportedExtensions)
        {
            var path = PageFile(folder, page, extension);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>查找某一页的未完成临时文件（<c>0001.jpg.part</c> / <c>0001.gif.part</c> …）。</summary>
    public static string? FindPartFile(string folder, int page)
    {
        if (!Directory.Exists(folder)) return null;
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, page.ToString("D4") + ".*.part"))
                return path;
        }
        catch
        {
            // 目录不可读时按没有临时文件处理。
        }
        return null;
    }

    private static ImageKind DetectHead(byte[] head, int count)
    {
        // JPEG: FF D8 FF
        if (count >= 2 && head[0] == 0xFF && head[1] == 0xD8) return ImageKind.Jpeg;
        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (count >= 8 && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
            && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A) return ImageKind.Png;
        // GIF87a / GIF89a
        if (count >= 6
            && head[0] == (byte)'G' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'8'
            && (head[4] == (byte)'7' || head[4] == (byte)'9') && head[5] == (byte)'a') return ImageKind.Gif;
        // WebP: RIFF....WEBP
        if (count >= 12
            && head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F'
            && head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P') return ImageKind.Webp;
        return ImageKind.Unknown;
    }

    /// <summary>校验文件确实是完整的受支持图片；<paramref name="kind"/> 返回实际格式。</summary>
    public static bool Validate(string file, out ImageKind kind, out long length)
    {
        kind = ImageKind.Unknown;
        length = 0;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length < MinBytes) return false;
            length = info.Length;

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            var head = new byte[12];
            int headRead = ReadFully(stream, head, 0, head.Length);
            kind = DetectHead(head, headRead);
            if (kind == ImageKind.Unknown) return false;

            // WebP 的 RIFF 头里写着文件长度（不含前 8 字节），可以直接核对是否被截断。
            if (kind == ImageKind.Webp && headRead >= 12)
            {
                var declared = BitConverter.ToInt32(head, 4) + 8L;
                // 允许少量尾部补齐；明显短于声明长度说明还没下完。
                if (declared > 0 && length + 16 < declared) return false;
            }

            var tailStart = Math.Max(0, length - TailProbeBytes);
            stream.Position = tailStart;
            var tail = new byte[length - tailStart];
            int tailRead = ReadFully(stream, tail, 0, tail.Length);

            return kind switch
            {
                ImageKind.Jpeg => ContainsSequence(tail, tailRead, new byte[] { 0xFF, 0xD9 }),
                ImageKind.Png => ContainsAscii(tail, tailRead, "IEND"),
                ImageKind.Gif => ContainsByte(tail, tailRead, 0x3B, 32),     // GIF 以 0x3B trailer 结束
                ImageKind.Webp => true,                                     // RIFF 长度已在上面核对
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }

    private static int ReadFully(Stream stream, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read = stream.Read(buffer, offset + total, count - total);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }

    private static bool ContainsSequence(byte[] buffer, int count, byte[] needle)
    {
        for (int i = count - needle.Length; i >= 0; i--)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (buffer[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    private static bool ContainsAscii(byte[] buffer, int count, string text)
    {
        var needle = System.Text.Encoding.ASCII.GetBytes(text);
        return ContainsSequence(buffer, count, needle);
    }

    /// <summary>在末尾 <paramref name="window"/> 字节内找某个单字节标记（GIF 的 0x3B）。</summary>
    private static bool ContainsByte(byte[] buffer, int count, byte value, int window)
    {
        for (int i = count - 1, stop = Math.Max(0, count - window); i >= stop; i--)
            if (buffer[i] == value) return true;
        return false;
    }

}
