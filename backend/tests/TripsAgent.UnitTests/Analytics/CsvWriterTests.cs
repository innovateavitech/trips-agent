using System.Text;
using FluentAssertions;
using TripsAgent.Application.Analytics;

namespace TripsAgent.UnitTests.Analytics;

/// <summary>
/// The CSV a report is written as.
/// </summary>
/// <remarks>
/// Two things are being protected here. A spreadsheet has to be able to add up the money columns,
/// which it cannot do if an amount arrives as text; and opening the file must not run anything,
/// which it will if a cell of agent-supplied text starts with an equals sign.
/// </remarks>
public class CsvWriterTests
{
    [Fact]
    public void Money_is_a_plain_decimal_a_spreadsheet_can_add_up()
    {
        CsvWriter.Money(150_000).Should().Be("1500.00");
        CsvWriter.Money(1).Should().Be("0.01");
        CsvWriter.Money(0).Should().Be("0.00");
        CsvWriter.Money(110_750).Should().Be("1107.50");
    }

    [Fact]
    public void A_negative_amount_keeps_its_sign()
    {
        CsvWriter.Money(-150_000).Should().Be("-1500.00");
        CsvWriter.Money(-1).Should().Be("-0.01");
    }

    [Fact]
    public void A_negative_amount_is_not_defused_into_text()
    {
        // Defusing a leading minus would turn every refund column into text and break the one
        // thing the file is for.
        var csv = new CsvWriter();
        csv.WriteHeader("Amount");
        csv.WriteRow(CsvWriter.Money(-150_000));

        Text(csv).Should().Contain("\"-1500.00\"").And.NotContain("'-1500.00");
    }

    [Fact]
    public void Free_text_that_a_spreadsheet_would_run_is_defused()
    {
        var csv = new CsvWriter();
        csv.WriteHeader("Item");
        csv.WriteRow("=cmd|'/c calc'!A1");

        Text(csv).Should().Contain("\"'=cmd");
    }

    [Fact]
    public void A_quote_inside_a_field_is_doubled()
    {
        var csv = new CsvWriter();
        csv.WriteHeader("Traveller");
        csv.WriteRow("""O'Brien, "Paddy" """.Trim());

        Text(csv).Should().Contain("\"O'Brien, \"\"Paddy\"\"\"");
    }

    [Fact]
    public void A_row_with_the_wrong_number_of_fields_is_refused()
    {
        var csv = new CsvWriter();
        csv.WriteHeader("A", "B");

        var act = () => csv.WriteRow("only one");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*2 columns*1*");
    }

    [Fact]
    public void The_row_count_excludes_the_header()
    {
        var csv = new CsvWriter();
        csv.WriteHeader("A");
        csv.WriteRow("1");
        csv.WriteRow("2");

        csv.RowCount.Should().Be(2, "the header is not a row anybody exported");
    }

    [Fact]
    public void The_file_starts_with_a_byte_order_mark_for_Excel()
    {
        var csv = new CsvWriter();
        csv.WriteHeader("Amount (NGN)");

        csv.ToBytes().Take(3).Should().Equal(Encoding.UTF8.GetPreamble());
    }

    [Fact]
    public void Percentages_say_so_when_there_is_nothing_to_divide_by()
    {
        CsvWriter.Percentage(250).Should().Be("2.50%");
        CsvWriter.Percentage(-1_250).Should().Be("-12.50%");
        CsvWriter.Percentage(null).Should().Be("—");
    }

    [Fact]
    public void Dates_are_ISO_so_every_locale_reads_them_the_same_way()
    {
        CsvWriter.Date(new DateOnly(2026, 3, 9)).Should().Be("2026-03-09");
        CsvWriter.Instant(new DateTimeOffset(2026, 3, 9, 8, 30, 0, TimeSpan.FromHours(1)))
            .Should().Be("2026-03-09T07:30:00Z");
    }

    private static string Text(CsvWriter csv) =>
        Encoding.UTF8.GetString(csv.ToBytes());
}
