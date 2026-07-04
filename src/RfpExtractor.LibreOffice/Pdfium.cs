namespace RfpExtractor.LibreOffice;

/// <summary>
/// Docnet's <c>DocLib.Instance</c> is a process-wide singleton and PDFium itself is NOT
/// thread-safe. Never dispose the instance per call (a <c>using</c> would tear down the shared
/// native library while another leg may be using it — the vision and text legs now run
/// concurrently), and serialize all PDFium work through this lock.
/// </summary>
internal static class Pdfium
{
    internal static readonly object Lock = new();
}
