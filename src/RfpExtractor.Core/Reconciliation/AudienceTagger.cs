using System.Text.RegularExpressions;
using RfpExtractor.Core.Models;

namespace RfpExtractor.Core.Reconciliation;

/// <summary>
/// Tags each question (and schema section) as <see cref="Audience.Applicant"/> or
/// <see cref="Audience.Internal"/> from its section heading. Internal sections are completed by the
/// RECEIVING firm, not the responder — e.g. "BGFML SECTION ONLY*", "For internal use only", "To be
/// completed by …". Nothing is dropped: the questions stay in the output so the extraction is
/// complete, but they are counted separately so the headline "questions to answer" reflects only the
/// applicant-facing ones (matching what a human/Copilot counts). Deterministic — overrides whatever
/// the model guessed.
/// </summary>
public static class AudienceTagger
{
    private static readonly Regex InternalHeading = new(
        @"section only|internal use|office use|to be completed by|for completion by|for official use",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsInternal(string? sectionName) =>
        !string.IsNullOrWhiteSpace(sectionName) && InternalHeading.IsMatch(sectionName);

    /// <summary>Rewrites Audience on every question and section in place.</summary>
    public static void Tag(ExtractionResult r)
    {
        for (int i = 0; i < r.Questions.Count; i++)
        {
            var q = r.Questions[i];
            var audience = IsInternal(q.SectionPath) ? Audience.Internal : Audience.Applicant;
            if (q.Audience != audience) r.Questions[i] = q with { Audience = audience };
        }

        var sections = r.DocumentSchema.Sections;
        for (int i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            var audience = IsInternal(s.Name) ? Audience.Internal : Audience.Applicant;
            if (s.Audience != audience) sections[i] = s with { Audience = audience };
        }
    }
}
