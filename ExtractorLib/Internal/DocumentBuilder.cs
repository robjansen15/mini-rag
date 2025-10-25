using System;
using System.IO;
using System.Text;

namespace ExtractorLib.Internal;

static class DocumentBuilder
{
    public readonly struct FileText
    {
        public FileText(string content, int size)
        {
            Content = content;
            Size = size;
        }

        public string Content { get; }
        public int Size { get; }
    }

    public static FileText? ReadTextFile(string path, long maxBytes, bool truncate)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = fs.Length;
            if (!truncate && maxBytes > 0 && length > maxBytes)
            {
                return null;
            }

            var limit = maxBytes > 0 ? Math.Min(length, maxBytes) : length;
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 8192, leaveOpen: true);

            if (truncate && limit < length)
            {
                var sb = new StringBuilder((int)limit);
                var buffer = new char[Math.Min((int)limit, 4096)];
                var remaining = limit;
                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(buffer.Length, remaining);
                    var read = reader.Read(buffer, 0, toRead);
                    if (read <= 0)
                    {
                        break;
                    }
                    sb.Append(buffer, 0, read);
                    remaining -= read;
                }

                return new FileText(sb.ToString(), (int)limit);
            }

            var content = reader.ReadToEnd();
            return new FileText(content, (int)Math.Min(int.MaxValue, length));
        }
        catch
        {
            return null;
        }
    }

    public static string BuildTextWithContext(string root, string relativePath, string absolutePath, string language, string content)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRel = FileRules.NormalizeRelativePath(relativePath);

        var sb = new StringBuilder(content.Length + 256);
        sb.AppendLine($"# path: {normalizedRel}");
        sb.AppendLine($"# abs_path: {absolutePath}");
        sb.AppendLine($"# root: {normalizedRoot}");
        sb.AppendLine($"# lang: {language}");
        sb.AppendLine();
        sb.Append(content);
        return sb.ToString();
    }
}
