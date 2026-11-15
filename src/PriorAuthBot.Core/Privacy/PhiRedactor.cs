using System.Text.RegularExpressions;

namespace PriorAuthBot.Core.Privacy;

public sealed record RedactionResult(string Text, IReadOnlyDictionary<string, int> Counts)
{
    public int Total => Counts.Values.Sum();
}

/// <summary>
/// Replaces identifiers in text with labeled placeholders such as <c>[MEMBER_ID]</c> (chapter 4).
/// Patterns catch identifiers with a known shape; <paramref name="knownValues"/> catches the ones
/// without one, such as a member's name, when you already know them from the record you are
/// working on. Pattern-based redaction misses things (a name in free text, a date written in
/// words), so it is a second line of defense behind not sending PHI where it is not allowed.
/// </summary>
public static partial class PhiRedactor
{
    public static RedactionResult Redact(string text, IEnumerable<string>? knownValues = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        string Replace(string input, Regex pattern, string label) => pattern.Replace(input, _ =>
        {
            counts[label] = counts.GetValueOrDefault(label) + 1;
            return $"[{label}]";
        });

        // Known values first, longest first, so "Jordan A. Whitaker" goes before "Jordan".
        foreach (var value in (knownValues ?? []).Where(v => v.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(v => v.Length))
        {
            text = Replace(text, new Regex(Regex.Escape(value), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)), "NAME");
        }

        text = Replace(text, MemberId(), "MEMBER_ID");
        text = Replace(text, Ssn(), "SSN");
        text = Replace(text, Mrn(), "MRN");
        text = Replace(text, Email(), "EMAIL");
        text = Replace(text, Phone(), "PHONE");
        text = Replace(text, Date(), "DATE");
        return new RedactionResult(text, counts);
    }

    /// <summary>
    /// Redaction on the way out (chapter 12). An identifier may appear in an answer only if it was in
    /// what the model was given (<paramref name="ground"/>): a model has no business producing a member
    /// ID or phone number it was not shown, whether it remembered one, guessed one, or was talked into
    /// one. Social Security numbers are always redacted, because no answer needs one.
    /// </summary>
    public static RedactionResult RedactUngrounded(string text, string ground)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(ground);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        string Replace(string input, Regex pattern, string label, bool always = false) => pattern.Replace(input, m =>
        {
            if (!always && ground.Contains(m.Value, StringComparison.OrdinalIgnoreCase))
            {
                return m.Value;
            }

            counts[label] = counts.GetValueOrDefault(label) + 1;
            return $"[{label}]";
        });

        text = Replace(text, Ssn(), "SSN", always: true);
        text = Replace(text, MemberId(), "MEMBER_ID");
        text = Replace(text, Mrn(), "MRN");
        text = Replace(text, Email(), "EMAIL");
        text = Replace(text, Phone(), "PHONE");
        return new RedactionResult(text, counts);
    }

    [GeneratedRegex(@"\bDD-\d{7}\b")]
    private static partial Regex MemberId();

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b")]
    private static partial Regex Ssn();

    [GeneratedRegex(@"\bMRN[:#\s]*\d{6,10}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Mrn();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b")]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<!\d)(\+1[\s.-]?)?\(?\d{3}\)?[\s.-]?\d{3}[\s.-]\d{4}(?!\d)")]
    private static partial Regex Phone();

    // Dates are PHI under HIPAA's Safe Harbor rule when they relate to a person (birth, admission, service).
    // Policy text has dates too, such as effective dates; redacting them costs little.
    [GeneratedRegex(@"\b(\d{1,2}/\d{1,2}/\d{2,4}|\d{4}-\d{2}-\d{2})\b")]
    private static partial Regex Date();
}
