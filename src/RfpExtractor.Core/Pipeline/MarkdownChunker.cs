using System.Text;

namespace RfpExtractor.Core.Pipeline;

/// <summary>
/// Splits document markdown into chunks small enough that each LLM call stays well inside
/// gateway timeout windows and response-token limits (the GenCore mitigation: bound every
/// request instead of sending a 50-page document in one call).
///
/// Splitting only happens at block boundaries: headings start a new block, markdown tables
/// are kept whole (a split table would corrupt column_headers/row detection), paragraphs end
/// at blank lines. A single block larger than <c>maxChars</c> (e.g. a giant table) becomes its
/// own oversized chunk rather than being broken.
/// </summary>
public static class MarkdownChunker
{
    public static IReadOnlyList<string> Chunk(string markdown, int maxChars = 24_000)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return Array.Empty<string>();
        if (markdown.Length <= maxChars) return new[] { markdown };

        var blocks = SplitBlocks(markdown);
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var block in blocks)
        {
            if (current.Length > 0 && current.Length + block.Length > maxChars)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
            current.Append(block);
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }

    // A block = a heading line, a whole markdown table (consecutive '|' lines), or a paragraph
    // terminated by a blank line.
    private static List<string> SplitBlocks(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var blocks = new List<string>();
        var current = new StringBuilder();
        bool inTable = false;

        void Flush()
        {
            if (current.Length > 0) { blocks.Add(current.ToString()); current.Clear(); }
        }

        foreach (var line in lines)
        {
            bool isTableLine = line.TrimStart().StartsWith('|');
            bool isHeading = line.StartsWith('#');

            // boundary: entering/leaving a table, or a heading
            if (isHeading || isTableLine != inTable) Flush();
            inTable = isTableLine;

            current.Append(line).Append('\n');

            // blank line ends a paragraph block
            if (!isTableLine && line.Trim().Length == 0) Flush();
        }
        Flush();
        return blocks;
    }
}
