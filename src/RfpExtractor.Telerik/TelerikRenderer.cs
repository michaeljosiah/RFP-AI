using RfpExtractor.Core.Abstractions;
using Telerik.Documents.Fixed.FormatProviders.Image.Skia;
using Telerik.Windows.Documents.Fixed.Model;
using DocxProvider = Telerik.Windows.Documents.Flow.FormatProviders.Docx.DocxFormatProvider;
using FlowToPdf = Telerik.Windows.Documents.Flow.FormatProviders.Pdf.PdfFormatProvider;
using FixedPdf = Telerik.Windows.Documents.Fixed.FormatProviders.Pdf.PdfFormatProvider;
using XlsxProvider = Telerik.Windows.Documents.Spreadsheet.FormatProviders.OpenXml.Xlsx.XlsxFormatProvider;
using SheetToPdf = Telerik.Windows.Documents.Spreadsheet.FormatProviders.Pdf.PdfFormatProvider;

namespace RfpExtractor.Telerik;

/// <summary>docx/xlsx/pdf -> PDF -> one PNG per page (Telerik + Skia).</summary>
public sealed class TelerikRenderer : IDocumentRenderer
{
    public Task<IReadOnlyList<PageImage>> RenderToImagesAsync(string path, int dpi, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(120);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        byte[] pdf;

        if (ext == ".pdf")
        {
            pdf = File.ReadAllBytes(path);
        }
        else if (ext is ".xlsx" or ".xlsm" or ".xls")
        {
            using var fs = File.OpenRead(path);
            var wb = new XlsxProvider().Import(fs, timeout);
            using var ms = new MemoryStream();
            new SheetToPdf().Export(wb, ms, timeout);
            pdf = ms.ToArray();
        }
        else
        {
            using var fs = File.OpenRead(path);
            var flow = new DocxProvider().Import(fs, timeout);
            pdf = new FlowToPdf().Export(flow, timeout);
        }

        RadFixedDocument fixedDoc = new FixedPdf().Import(pdf, timeout);
        var provider = new SkiaImageFormatProvider();
        provider.ExportSettings.ScaleFactor = dpi / 72.0;

        var pages = new List<PageImage>();
        for (int i = 0; i < fixedDoc.Pages.Count; i++)
            pages.Add(new PageImage(i + 1, provider.Export(fixedDoc.Pages[i], timeout)));

        return Task.FromResult<IReadOnlyList<PageImage>>(pages);
    }
}
