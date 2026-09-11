using FluentAssertions;
using TripsAgent.Analyzers;

namespace TripsAgent.AnalyzerTests;

/// <summary>
/// Checks the real allowlist files in the repository, not snippets.
/// </summary>
/// <remarks>
/// Both files promise that every entry carries a reason. An exception with no reason is one
/// nobody can later judge, so it tends to outlive whatever justified it.
/// </remarks>
public class AllowlistFileTests
{
    [Theory]
    [InlineData(MoneyTypeAnalyzer.AllowlistFileName)]
    [InlineData(IgnoreQueryFiltersAnalyzer.AllowlistFileName)]
    public void The_allowlist_file_exists_where_the_build_expects_it(string fileName)
    {
        // Directory.Build.props hands backend/<file> to the compiler. If it moved, the analyser
        // would quietly load an empty list — strict, but no longer the list anyone is editing.
        File.Exists(Path.Combine(BackendRoot(), fileName)).Should().BeTrue();
    }

    [Theory]
    [InlineData(MoneyTypeAnalyzer.AllowlistFileName)]
    [InlineData(IgnoreQueryFiltersAnalyzer.AllowlistFileName)]
    public void Every_entry_says_why(string fileName)
    {
        var lines = File.ReadAllLines(Path.Combine(BackendRoot(), fileName));
        var unexplained = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            // A reason may sit on the entry's own line or on the comment line just above it.
            var inlineReason = line.Contains('#', StringComparison.Ordinal);
            var reasonAbove = i > 0 && lines[i - 1].Trim().StartsWith('#') && lines[i - 1].Trim().Length > 1;

            if (!inlineReason && !reasonAbove)
            {
                unexplained.Add(line);
            }
        }

        unexplained.Should().BeEmpty($"every entry in {fileName} must carry a comment saying why it is an exception");
    }

    private static string BackendRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TripsAgent.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find backend/ above the test binaries.");
    }
}
