using System.IO;
using System.Text;

namespace LiteDocViewer.Services;

internal sealed class EditableDocument
{
    private static readonly Encoding[] EncodingsWithBom =
    [
        new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: true),
        new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: true),
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true),
        new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true),
        new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true)
    ];

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string _targetPath;
    private readonly byte[] _original;
    private readonly Encoding _encoding;
    private readonly string _lineEnding;

    private EditableDocument(string path, string targetPath, byte[] original, Encoding encoding)
    {
        Path = path;
        _targetPath = targetPath;
        _original = original;
        _encoding = encoding;

        var text = encoding.GetString(original.AsSpan(encoding.Preamble.Length));
        if (text.Contains('\0'))
            throw new DecoderFallbackException("편집할 수 있는 텍스트 인코딩을 확인하지 못했습니다.");

        _lineEnding = DetectLineEnding(text);
        Text = ConvertLineEndings(text, Environment.NewLine);
    }

    public string Path { get; }
    public string Text { get; }

    public static async Task<EditableDocument> OpenAsync(string path)
    {
        var targetPath = ResolveTarget(path);
        var bytes = await File.ReadAllBytesAsync(targetPath).ConfigureAwait(false);
        var encoding = EncodingsWithBom.FirstOrDefault(candidate => bytes.AsSpan().StartsWith(candidate.Preamble)) ?? Utf8;
        return new EditableDocument(path, targetPath, bytes, encoding);
    }

    public void Save(string text)
    {
        var content = _encoding.GetBytes(ConvertLineEndings(text, _lineEnding));
        var directory = System.IO.Path.GetDirectoryName(_targetPath)!;
        var tempPath = System.IO.Path.Combine(directory, $".litedocviewer-{Guid.NewGuid():N}.tmp");
        var backupPath = tempPath + ".bak";
        var created = false;
        var replaced = false;
        try
        {
            using (var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write))
            {
                created = true;
                temp.Write(_encoding.Preamble);
                temp.Write(content);
                temp.Flush(flushToDisk: true);
            }

            using var current = new FileStream(_targetPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (!string.Equals(ResolveTarget(Path), _targetPath, StringComparison.OrdinalIgnoreCase) ||
                !MatchesOriginal(current))
                throw new IOException("편집을 시작한 뒤 파일이 변경되었습니다. 입력한 내용을 복사해 둔 뒤 최신 파일을 다시 여세요.");

            File.Replace(tempPath, _targetPath, backupPath);
            replaced = true;
        }
        catch (IOException ex) when (File.Exists(backupPath))
        {
            throw new IOException($"파일 교체가 완료되지 않았습니다. 이전 내용은 다음 파일에 있습니다.\n{backupPath}", ex);
        }
        finally
        {
            if (created)
                DeleteTemporaryFile(tempPath);
            if (replaced)
                DeleteTemporaryFile(backupPath);
        }
    }

    private bool MatchesOriginal(Stream current)
    {
        if (current.Length != _original.Length)
            return false;

        Span<byte> buffer = stackalloc byte[4096];
        for (var start = 0; start < _original.Length; start += buffer.Length)
        {
            var chunk = buffer[..Math.Min(buffer.Length, _original.Length - start)];
            current.ReadExactly(chunk);
            if (!chunk.SequenceEqual(_original.AsSpan(start, chunk.Length)))
                return false;
        }

        return true;
    }

    private static string ResolveTarget(string path) =>
        File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;

    private static string DetectLineEnding(string text)
    {
        var firstBreak = text.IndexOfAny(['\r', '\n']);
        if (firstBreak < 0)
            return Environment.NewLine;

        return text.AsSpan(firstBreak).StartsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : text[firstBreak].ToString();
    }

    private static void DeleteTemporaryFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file should not mask the outcome of the save.
        }
    }

    private static string ConvertLineEndings(string text, string newline) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\n", newline, StringComparison.Ordinal);
}
