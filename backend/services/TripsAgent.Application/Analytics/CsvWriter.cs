using System.Globalization;
using System.Text;

namespace TripsAgent.Application.Analytics;

/// <summary>
/// Writes a report as CSV.
/// </summary>
/// <remarks>
/// <para>
/// RFC 4180 quoting: every field is quoted and a quote inside one is doubled, so a traveller
/// called <c>O'Brien, "Paddy"</c> does not become three columns.
/// </para>
/// <para>
/// <b>Free text is defused.</b> Excel and Google Sheets execute a cell that begins with
/// <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c>, so a booking titled <c>=cmd|...</c> becomes something
/// an accountant is prompted to run. Text fields get a leading apostrophe, which forces the cell to
/// be read as text. Numbers deliberately do not: defusing a leading minus would turn every refund
/// into text and break the one thing a spreadsheet is for. The console's wallet statement takes the
/// same line, for the same reasons.
/// </para>
/// <para>
/// <b>Money is written as a plain decimal.</b> <c>150000</c> kobo becomes <c>1500.00</c>, with no
/// thousands separator and no currency symbol: a spreadsheet has to parse it back into a number,
/// and <c>"₦1,500.00"</c> arrives as text and silently breaks every SUM over the column. The
/// currency is named once, in the header. The conversion is integer division and a remainder —
/// nothing here turns money into a floating-point value (CLAUDE.md rule 2).
/// </para>
/// </remarks>
public sealed class CsvWriter
{
    /// <summary>Minor units per major unit. 100 kobo to the naira.</summary>
    private const long MinorUnitsPerMajor = 100;

    private readonly StringBuilder _builder = new();

    private int _columns;

    /// <summary>How many data rows have been written, not counting the header.</summary>
    public int RowCount { get; private set; }

    /// <summary>Writes the header. Called once, before any row.</summary>
    public void WriteHeader(params string[] columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        if (_columns != 0)
        {
            throw new InvalidOperationException("The header has already been written.");
        }

        _columns = columns.Length;
        WriteLine(columns);
    }

    /// <summary>Writes one data row. Must have as many fields as the header had columns.</summary>
    public void WriteRow(params string[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        if (fields.Length != _columns)
        {
            throw new ArgumentException(
                $"This report's header has {_columns} columns; the row has {fields.Length}. "
                + "A ragged CSV is a file somebody's spreadsheet will silently misread.",
                nameof(fields));
        }

        WriteLine(fields);
        RowCount++;
    }

    /// <summary>The finished file's bytes, UTF-8 with a byte-order mark.</summary>
    /// <remarks>
    /// The BOM is there for Excel on Windows, which otherwise reads a UTF-8 file as the system
    /// code page and turns every naira sign and accented name into rubbish. Every other tool
    /// ignores it.
    /// </remarks>
    public byte[] ToBytes() =>
        [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(_builder.ToString())];

    /// <summary>Minor units as a plain, ungrouped decimal: <c>150000</c> becomes <c>1500.00</c>.</summary>
    public static string Money(long amountMinor)
    {
        var sign = amountMinor < 0 ? "-" : string.Empty;
        var absolute = Math.Abs(amountMinor);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{sign}{absolute / MinorUnitsPerMajor}.{absolute % MinorUnitsPerMajor:00}");
    }

    /// <summary>A whole number, invariantly formatted so no locale inserts a separator.</summary>
    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Basis points as a percentage with two decimals, or an em dash when there is none.</summary>
    public static string Percentage(int? basisPoints) =>
        basisPoints is null
            ? "—"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{(basisPoints.Value < 0 ? "-" : string.Empty)}{Math.Abs(basisPoints.Value) / 100}.{Math.Abs(basisPoints.Value) % 100:00}%");

    /// <summary>A date as ISO-8601, which every spreadsheet and every human can read.</summary>
    public static string Date(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>An instant as ISO-8601 in UTC.</summary>
    public static string Instant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private void WriteLine(string[] fields)
    {
        for (var index = 0; index < fields.Length; index++)
        {
            if (index > 0)
            {
                _builder.Append(',');
            }

            _builder.Append(Quote(fields[index]));
        }

        // CRLF, which RFC 4180 specifies and Excel is happiest with.
        _builder.Append("\r\n");
    }

    /// <summary>Quotes a field, doubling any quote inside it and defusing a formula.</summary>
    private static string Quote(string? value)
    {
        var text = value ?? string.Empty;

        if (text.Length > 0 && "=+-@\t\r".Contains(text[0], StringComparison.Ordinal) && !IsNumeric(text))
        {
            text = "'" + text;
        }

        return $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    /// <summary>
    /// True when the field is a number this writer produced, so a leading minus is a sign.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow: digits, at most one decimal point, and an optional leading minus. A
    /// value that merely starts with a minus and then says something else is still defused.
    /// </remarks>
    private static bool IsNumeric(string text) =>
        decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out _);
}
