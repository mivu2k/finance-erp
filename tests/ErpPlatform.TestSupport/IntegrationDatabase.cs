using ErpPlatform.TestSupport;
using Xunit;

namespace ErpPlatform.TestSupport;

/// <summary>
/// Guards integration tests that need a real database.
/// </summary>
/// <remarks>
/// These suites used to start with <c>IntegrationDatabase.Require(_available);</c>, which meant an
/// unreachable database produced a <em>passing</em> run that asserted nothing. That is
/// worse than a failure: it reports confidence that was never earned, and it bit this
/// repo for real — nine tests "passed" in 119ms while executing none of their bodies,
/// because one wildcard grant was missing from <c>dev.sh</c>.
/// <para>
/// So the behaviour now depends on where the test runs. On a developer's machine a
/// missing database is a fact of life, and the test is <b>skipped</b> — visibly, in the
/// runner's skip count. In CI the database is part of the contract, so its absence is a
/// <b>failure</b>: a pipeline that cannot reach its database must go red, not green.
/// </para>
/// </remarks>
public static class IntegrationDatabase
{
    /// <summary>
    /// True when running under a build server. Honours the convention shared by GitHub
    /// Actions, GitLab, Azure DevOps and most others.
    /// </summary>
    public static bool IsCi =>
        // Non-empty, not merely present: `CI= dotnet test` exports an empty string, and
        // treating that as "we are in CI" turns a developer's skip into a failure.
        IsTruthy("CI") || IsSet("GITHUB_ACTIONS") || IsSet("TF_BUILD");

    private static bool IsSet(string name) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));

    private static bool IsTruthy(string name) =>
        Environment.GetEnvironmentVariable(name) is { } v
        && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1");

    /// <summary>
    /// Call at the top of every test that needs the database. Skips locally, throws in CI.
    /// </summary>
    public static void Require(bool available, string? detail = null)
    {
        if (available) return;

        var message =
            "This test needs a MariaDB server reachable at the configured connection string, "
            + "with rights to create a throwaway database (see db_ensure in dev.sh). "
            + (detail ?? string.Empty);

        if (IsCi)
            throw new InvalidOperationException(
                "Integration database unavailable in CI. " + message
                + "A pipeline that cannot reach its database must fail rather than "
                + "silently report a green run over tests that never executed.");

        Skip.If(true, message.TrimEnd());
    }
}
