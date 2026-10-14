using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PlantOps.Application.Knowledge;

public sealed record ParsedDocumentChunk(int Ordinal, string Section, int StartLine, int EndLine, string Text, string Sha256);

public sealed record ParsedDocument(string Status, IReadOnlyList<ParsedDocumentChunk> Chunks);

public static class TextDocumentParser
{
    public const string Version = "text-v1";
    public const int MaximumBytes = 256 * 1024;
    public const int MaximumChunks = 256;
    public const int MaximumChunkCharacters = 2000;

    public static ParsedDocument Parse(string fileName, string mediaType, byte[] content)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!(extension == ".md" && mediaType is "text/markdown" or "text/plain" || extension == ".txt" && mediaType == "text/plain"))
        {
            return new("NeedsText", []);
        }
        if (content.Length > MaximumBytes)
        {
            return new("TooLarge", []);
        }
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(content).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException) { return new("InvalidText", []); }
        if (text.Contains('\0'))
        {
            return new("InvalidText", []);
        }
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var chunks = new List<ParsedDocumentChunk>();
        var headings = new string[6];
        var section = "";
        var buffer = new StringBuilder();
        var start = 1;
        var end = 1;
        void Flush()
        {
            var value = buffer.ToString().Trim();
            if (value.Length > 0)
            {
                chunks.Add(new(chunks.Count, section, start, end, value,
                    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))));
            }
            buffer.Clear();
        }
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var heading = extension == ".md" ? Regex.Match(line, @"^ {0,3}(#{1,6})\s+(.+)$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) : Match.Empty;
            if (heading.Success)
            {
                Flush();
                var level = heading.Groups[1].Length - 1;
                headings[level] = heading.Groups[2].Value.Trim();
                Array.Clear(headings, level + 1, headings.Length - level - 1);
                section = string.Join(" / ", headings.Where(x => !string.IsNullOrEmpty(x)));
                if (section.Length > 400)
                {
                    section = section[..(char.IsHighSurrogate(section[399]) ? 399 : 400)];
                }
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }
            var offset = 0;
            while (offset < line.Length)
            {
                if (buffer.Length > 0 && buffer.Length + line.Length - offset + 1 > MaximumChunkCharacters)
                {
                    Flush();
                }
                if (buffer.Length == 0)
                {
                    start = index + 1;
                }
                else
                {
                    buffer.Append('\n');
                }
                var length = Math.Min(line.Length - offset, MaximumChunkCharacters - buffer.Length);
                if (offset + length < line.Length && char.IsHighSurrogate(line[offset + length - 1]))
                {
                    length--;
                }
                buffer.Append(line.AsSpan(offset, length));
                end = index + 1;
                offset += length;
                if (offset < line.Length)
                {
                    Flush();
                }
            }
            if (chunks.Count > MaximumChunks)
            {
                return new("TooLarge", []);
            }
        }
        Flush();
        return chunks.Count > MaximumChunks ? new("TooLarge", []) : new(chunks.Count == 0 ? "Empty" : "Ready", chunks);
    }
}
