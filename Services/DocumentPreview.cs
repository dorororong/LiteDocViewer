using System.IO;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using LiteDocViewer.Models;
using Markdig;

namespace LiteDocViewer.Services;

internal static class DocumentPreview
{
    private enum Format { Unsupported, Html, Markdown, Text, Json }

    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 256
    };

    public static bool IsGenerated(string path) =>
        GetFormat(path) is Format.Markdown or Format.Text or Format.Json;

    public static bool IsSupported(string path) => GetFormat(path) != Format.Unsupported;

    private static Format GetFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => Format.Html,
        ".md" or ".markdown" => Format.Markdown,
        ".txt" => Format.Text,
        ".json" => Format.Json,
        _ => Format.Unsupported
    };

    public static string BuildPage(string path, string baseUrl, string text, ViewerTheme theme)
    {
        var body = GetFormat(path) switch
        {
            Format.Markdown => Markdown.ToHtml(text, MarkdownPipeline),
            Format.Json => RenderJson(text),
            _ => RenderText(WebUtility.HtmlEncode(text))
        };

        return $$"""
            <!doctype html>
            <html lang="ko">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <base href="{{WebUtility.HtmlEncode(baseUrl)}}">
              <title>{{WebUtility.HtmlEncode(Path.GetFileName(path))}}</title>
              <style id="litedoc-theme">{{theme.PreviewCss}}</style>
              <style>
                * { box-sizing: border-box; }
                html { background: var(--page); color: var(--text); }
                body { max-width: 980px; margin: 0 auto; padding: 36px 48px 80px; font: 16px/1.65 -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; overflow-wrap: break-word; }
                h1, h2 { border-bottom: 1px solid var(--border); padding-bottom: .3em; }
                h1, h2, h3, h4, h5, h6 { line-height: 1.25; margin: 1.5em 0 .65em; }
                h1:first-child { margin-top: 0; }
                a { color: var(--accent); text-decoration: none; } a:hover { text-decoration: underline; }
                img, video { max-width: 100%; height: auto; }
                pre { padding: 16px; overflow: auto; background: var(--code); border-radius: 6px; }
                code { font-family: Consolas, "Cascadia Code", monospace; background: var(--code); border-radius: 4px; padding: .15em .35em; }
                pre code { padding: 0; background: transparent; }
                blockquote { margin-left: 0; padding: 0 1em; color: var(--muted); border-left: .25em solid var(--border); }
                table { border-collapse: collapse; display: block; width: max-content; max-width: 100%; overflow: auto; }
                th, td { padding: 6px 13px; border: 1px solid var(--border); }
                tr:nth-child(2n) { background: var(--code); }
                hr { height: 1px; border: 0; background: var(--border); }
                input[type=checkbox] { margin-right: .5em; }
                .plain-text { margin: 0; background: transparent; padding: 0; white-space: pre-wrap; overflow-wrap: anywhere; tab-size: 4; }
                .notice { color: var(--muted); margin: 0 0 16px; }
                .json-key { color: var(--accent); }
                .json-string { color: var(--string); }
                .json-literal { color: var(--literal); }
                @media (max-width: 700px) { body { padding: 24px 22px 60px; } }
              </style>
            </head>
            <body>{{body}}</body>
            </html>
            """;
    }

    private static string RenderText(string encodedText) =>
        $"<pre class=\"plain-text\"><code>{encodedText}</code></pre>";

    private static string RenderJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = PrettyJson.MaxDepth });
            var formatted = JsonSerializer.SerializeToUtf8Bytes(document.RootElement, PrettyJson);
            if (formatted.Length > 256 * 1024)
                return RenderText(WebUtility.HtmlEncode(Encoding.UTF8.GetString(formatted)));

            return RenderText(HighlightJson(formatted));
        }
        catch (JsonException)
        {
            return "<p class=\"notice\">JSON을 해석할 수 없어 원문을 표시합니다.</p>" +
                RenderText(WebUtility.HtmlEncode(text));
        }
    }

    private static string HighlightJson(byte[] formatted)
    {
        var reader = new Utf8JsonReader(formatted, new JsonReaderOptions { MaxDepth = PrettyJson.MaxDepth });
        var html = new StringBuilder(formatted.Length);
        var written = 0;
        while (reader.Read())
        {
            var cssClass = reader.TokenType switch
            {
                JsonTokenType.PropertyName => "json-key",
                JsonTokenType.String => "json-string",
                JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null => "json-literal",
                _ => null
            };
            if (cssClass is null)
                continue;

            var start = (int)reader.TokenStartIndex;
            // PropertyName also consumes the colon. Color just the quoted key.
            var end = reader.TokenType == JsonTokenType.PropertyName
                ? start + reader.ValueSpan.Length + 2
                : (int)reader.BytesConsumed;
            AppendEncoded(html, formatted.AsSpan(written, start - written));
            html.Append("<span class=\"").Append(cssClass).Append("\">");
            AppendEncoded(html, formatted.AsSpan(start, end - start));
            html.Append("</span>");
            written = end;
        }

        AppendEncoded(html, formatted.AsSpan(written));
        return html.ToString();
    }

    private static void AppendEncoded(StringBuilder html, ReadOnlySpan<byte> utf8) =>
        html.Append(WebUtility.HtmlEncode(Encoding.UTF8.GetString(utf8)));
}
