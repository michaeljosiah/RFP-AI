namespace RfpExtractor.Core.Models;

public enum ItemType { OpenQuestion, DocumentRequest, Table, YesNo, Instruction }
public enum AnswerType { Text, LongText, Number, Currency, Percentage, Date, YesNo, DocumentUpload }
public enum QuestionSource { Body, TableCell, DocumentRequest }
public enum FoundBy { Vision, Text, Both }
public enum Confidence { High, Medium, Low }

/// <summary>Who fills a question. <c>Internal</c> = a receiving-firm-only section (e.g. "BGFML
/// SECTION ONLY*") that the responder does NOT answer; kept in the output but counted separately.</summary>
public enum Audience { Applicant, Internal }

/// <summary>
/// Output granularity for the flat question list (a presentation of the same extraction):
///  - <c>Atomic</c>: one entry per distinct ask (compound prompts fully split). Finest; best as
///    retrieval units.
///  - <c>Bundled</c>: one entry per printed prompt; the atomic asks listed as <c>sub_questions</c>
///    strings. Matches the printed form / count.
///  - <c>Hybrid</c>: one entry per printed prompt (the bundled question + verbatim) with the atomic
///    breakdown nested in <c>parts</c>, each part keeping its own retrieval hint. Both at once.
/// </summary>
public enum Granularity { Atomic, Bundled, Hybrid }

/// <summary>Coarse topic bucket for a question — for grouping and routing an answer-retrieval stage.</summary>
public enum QuestionCategory
{
    FirmProfile, Team, InvestmentProcess, Performance, Risk,
    Esg, Operations, Compliance, Fees, ClientService, Other
}

/// <summary>The shape of answer a retrieval stage should look for (a retrieval-oriented view of answer_type).</summary>
public enum ExpectedFormat { Narrative, ShortText, Value, Boolean, Date, Document, Table }

public sealed record MetadataField(string Label, string Value);

public sealed record DocumentSchema
{
    public List<MetadataField> Metadata { get; init; } = new();
    public List<Section> Sections { get; init; } = new();
    public string? Notes { get; init; }
}

public sealed record Section
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Subsection { get; init; }
    public int? Page { get; init; }
    public Audience Audience { get; init; } = Audience.Applicant;
    public List<Item> Items { get; init; } = new();
}

public sealed record Item
{
    public string Id { get; init; } = "";
    public ItemType Type { get; init; }
    public string Verbatim { get; init; } = "";
    public string? AnswerTarget { get; init; }
    public AnswerType? AnswerType { get; init; }
    public bool Truncated { get; init; }
    public bool Unreadable { get; init; }
    public TableSpec? Table { get; init; }
}

public sealed record TableSpec
{
    public string Classification { get; init; } = "data_entry";
    public string? IntroVerbatim { get; init; }
    public List<string> ColumnHeaders { get; init; } = new();
    public List<string> RowHeaders { get; init; } = new();
    public List<TableCell> Cells { get; init; } = new();
}

public sealed record TableCell
{
    public string AnswerTarget { get; init; } = "";
    public string Row { get; init; } = "";
    public string Column { get; init; } = "";
    public AnswerType AnswerType { get; init; } = AnswerType.Text;
}

public sealed record SchemaRef
{
    public string SectionId { get; init; } = "";
    public string ItemId { get; init; } = "";
    public string? Row { get; init; }
    public string? Column { get; init; }
    public int? Page { get; init; }
}

/// <summary>Retrieval-oriented enrichment added AFTER reconciliation (see <c>AgentRetrievalEnricher</c>)
/// so an answer-retrieval stage can query knowledge sources per question. Nullable like
/// <see cref="Binding"/> so it stays out of the extraction schema.</summary>
public sealed record RetrievalHint
{
    public QuestionCategory Category { get; init; } = QuestionCategory.Other;
    public ExpectedFormat ExpectedFormat { get; init; } = ExpectedFormat.Narrative;
    /// <summary>Expected unit for a numeric answer (e.g. "S$ million", "%", "years"); null otherwise.</summary>
    public string? Units { get; init; }
    /// <summary>True when the answer needs input from OUTSIDE the questionnaire (firm records/data,
    /// past RFPs, SME/management judgement) - i.e. it is not answerable from the document alone.</summary>
    public bool RequiresExternalInput { get; init; }
    /// <summary>Short actionable note from the AI to the answering team - what source/input is needed
    /// or a caveat; null when there is nothing useful to add.</summary>
    public string? AiComment { get; init; }
}

public sealed record Binding
{
    public string Kind { get; init; } = "";   // content_control | cell | form_field | coords | table_cell
    public string? Tag { get; init; }
    public string? Sheet { get; init; }
    public string? Address { get; init; }
    public string? Name { get; init; }
    public int? Page { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? W { get; init; }
    public double? H { get; init; }
}

/// <summary>An atomic sub-ask nested under a printed question in <c>Granularity.Hybrid</c> output.
/// Carries its own retrieval hint so it can be a retrieval unit while the parent stays the answer box.</summary>
public sealed record QuestionPart
{
    public string PartId { get; init; } = "";          // e.g. "Q008.1"
    public string AnswerTarget { get; init; } = "";     // the atomic answer target it came from
    public string QuestionText { get; init; } = "";
    public AnswerType AnswerType { get; init; } = AnswerType.Text;
    public RetrievalHint? Retrieval { get; init; }
}

public sealed record Question
{
    public string QuestionId { get; init; } = "";
    public string AnswerTarget { get; init; } = "";
    public string QuestionText { get; init; } = "";
    public string VerbatimSource { get; init; } = "";
    public List<string> SubQuestions { get; init; } = new();
    /// <summary>Atomic breakdown nested under this printed question (Granularity.Hybrid only; empty otherwise).</summary>
    public List<QuestionPart> Parts { get; init; } = new();
    public AnswerType AnswerType { get; init; } = AnswerType.Text;
    public string SectionPath { get; init; } = "";
    public QuestionSource Source { get; init; }
    public SchemaRef SchemaRef { get; init; } = new();
    public Binding? Binding { get; init; }
    public FoundBy FoundBy { get; init; } = FoundBy.Both;
    public Confidence Confidence { get; init; } = Confidence.High;
    public bool NeedsReview { get; init; }
    /// <summary>Applicant (default) vs internal-only. Set deterministically by <c>AudienceTagger</c>
    /// from the section heading; the model's value is overridden.</summary>
    public Audience Audience { get; init; } = Audience.Applicant;
    /// <summary>Retrieval enrichment; null until the (optional) enrichment pass runs. Only
    /// applicant-facing questions are enriched.</summary>
    public RetrievalHint? Retrieval { get; init; }
}

public sealed record ExtractionResult
{
    public DocumentSchema DocumentSchema { get; init; } = new();
    public List<Question> Questions { get; init; } = new();
}

public sealed record ReconciliationReport
{
    public int PrimaryCount { get; set; }
    public int SecondaryCount { get; set; }
    public int MergedCount { get; set; }
    public int AgreedCount { get; set; }
    public int PrimaryOnlyCount { get; set; }
    public int SecondaryOnlyCount { get; set; }

    // --- two framings of "how many questions" (see ReportMetrics) ---
    /// <summary>Total answer slots to fill = MergedCount. This is what a fill-back tool consumes.</summary>
    public int AnswerSlots { get; set; }
    /// <summary>Answer slots the responder fills (audience = applicant). The headline count to answer.</summary>
    public int ApplicantSlots { get; set; }
    /// <summary>Answer slots in receiving-firm-only sections (e.g. "BGFML SECTION ONLY*"); not answered by the responder.</summary>
    public int InternalSlots { get; set; }
    /// <summary>Distinct question texts after deduping cross-section repeats (e.g. per-fund copies).</summary>
    public int UniqueQuestionTexts { get; set; }
    /// <summary>Distinct PRINTED questions = atomic asks grouped back by (section, printed prompt). This
    /// is the bundled/hybrid top-level count; <c>AnswerSlots</c> is the atomic count. Both are shown so
    /// the two framings are always visible regardless of the chosen output granularity.</summary>
    public int PrintedQuestions { get; set; }
    public int BodyQuestions { get; set; }
    public int TableCells { get; set; }
    public int DocumentRequests { get; set; }
    /// <summary>Count of data-entry tables (grids), i.e. table cells collapsed to their tables.</summary>
    public int DataEntryTables { get; set; }

    public List<string> Warnings { get; init; } = new();
}

public sealed record ReconciledResult
{
    public ExtractionResult Merged { get; init; } = new();
    public List<Question> ReviewQueue { get; init; } = new();
    public ReconciliationReport Report { get; init; } = new();

    public static ReconciledResult FromSingle(ExtractionResult r) => new()
    {
        Merged = r,
        Report = new ReconciliationReport { MergedCount = r.Questions.Count }
    };
}
