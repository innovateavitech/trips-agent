namespace TripsAgent.IntegrationTests;

/// <summary>
/// Placeholder so this project contains at least one test.
/// </summary>
/// <remarks>
/// <para>
/// A test project with no tests in it makes <c>dotnet test</c> exit non-zero — the runner
/// reports "No test is available" and treats that as a failure. Because CI runs
/// <c>dotnet test</c> across the whole solution, an empty project here would fail every
/// pull request.
/// </para>
/// <para>
/// Delete this the moment the first real integration test lands. That arrives with issue #10,
/// which adds Testcontainers.PostgreSql and starts a real database per test class.
/// </para>
/// </remarks>
public class IntegrationTestHostSmokeTest
{
    [Fact]
    public void TestHostRuns()
    {
        Assert.True(true);
    }
}
