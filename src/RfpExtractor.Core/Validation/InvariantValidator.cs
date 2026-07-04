using RfpExtractor.Core.Models;

namespace RfpExtractor.Core.Validation;

public static class InvariantValidator
{
    /// <summary>Returns a list of invariant violations; empty means the 1:1 mapping holds.</summary>
    public static IReadOnlyList<string> Validate(ExtractionResult r)
    {
        var errors = new List<string>();
        var schema = new HashSet<string>();

        foreach (var s in r.DocumentSchema.Sections)
        foreach (var it in s.Items)
        {
            if (it.Type == ItemType.Table && it.Table is not null)
                foreach (var c in it.Table.Cells) Add(schema, c.AnswerTarget, errors);
            else if (!string.IsNullOrEmpty(it.AnswerTarget))
                Add(schema, it.AnswerTarget!, errors);
        }

        var q = new HashSet<string>();
        foreach (var it in r.Questions)
        {
            if (!q.Add(it.AnswerTarget)) errors.Add($"Duplicate question target: {it.AnswerTarget}");
            if (!schema.Contains(it.AnswerTarget)) errors.Add($"Question {it.QuestionId} -> missing schema target {it.AnswerTarget}");
        }
        foreach (var t in schema)
            if (!q.Contains(t)) errors.Add($"Schema target {t} has no question");

        return errors;

        static void Add(HashSet<string> set, string t, List<string> e)
        {
            if (string.IsNullOrEmpty(t)) { e.Add("Empty answer_target in schema"); return; }
            if (!set.Add(t)) e.Add($"Duplicate schema target: {t}");
        }
    }
}
