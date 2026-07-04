using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using Docnet.Core;
using Docnet.Core.Converters;
using Docnet.Core.Models;
using RfpExtractor.Core.Abstractions;
using SkiaSharp;

namespace RfpExtractor.LibreOffice;

/// <summary>
/// docx/xlsx/pdf -> PDF (via a self-hosted Gotenberg container) -> one PNG per page
/// (Docnet.Core / PDFium rasteriser, SkiaSharp encoder). Documents never leave your network.
/// </summary>
public sealed class LibreOfficeRenderer : IDocumentRenderer
{
    private readonly HttpClient _http;
    private readonly string _gotenbergUrl;

    public LibreOfficeRenderer(HttpClient http, string gotenbergUrl)
    {
        _http = http;
        _gotenbergUrl = gotenbergUrl;
    }

    public async Task<IReadOnlyList<PageImage>> RenderToImagesAsync(string path, int dpi, CancellationToken ct)
    {
        // Gotenberg's LibreOffice route converts docx AND xlsx (and more) to PDF.
        byte[] pdf = path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            ? await File.ReadAllBytesAsync(path, ct)
            : await ConvertViaGotenbergAsync(path, ct);

        return Rasterize(pdf, dpi);
    }

    private async Task<byte[]> ConvertViaGotenbergAsync(string path, CancellationToken ct)
    {
        var endpoint = _gotenbergUrl.TrimEnd('/') + "/forms/libreoffice/convert";
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(await File.ReadAllBytesAsync(path, ct));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "files", Path.GetFileName(path));

        using var resp = await _http.PostAsync(endpoint, form, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Gotenberg returned {(int)resp.StatusCode} {resp.ReasonPhrase} at {endpoint}. {body}\n" +
                "Is the container running?  docker run -d -p 3000:3000 gotenberg/gotenberg:8");
        }
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    private static IReadOnlyList<PageImage> Rasterize(byte[] pdf, int dpi)
    {
        // PDFium is not thread-safe and DocLib.Instance is a shared singleton: never dispose it
        // per call, and hold the process-wide lock for the duration of the PDFium work.
        lock (Pdfium.Lock)
        {
            var lib = DocLib.Instance;
            using var reader = lib.GetDocReader(pdf, new PageDimensions(dpi / 72.0));

            var pages = new List<PageImage>();
            int count = reader.GetPageCount();
            for (int i = 0; i < count; i++)
            {
                using var pr = reader.GetPageReader(i);
                int w = pr.GetPageWidth();
                int h = pr.GetPageHeight();
                byte[] bgra = pr.GetImage(new NaiveTransparencyRemover(255, 255, 255));   // white background

                using var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
                Marshal.Copy(bgra, 0, bmp.GetPixels(), bgra.Length);
                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                pages.Add(new PageImage(i + 1, data.ToArray()));
            }
            return pages;
        }
    }
}
