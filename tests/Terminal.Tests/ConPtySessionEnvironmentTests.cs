using Terminal.Sessions;

namespace Terminal.Tests;

public sealed class ConPtySessionEnvironmentTests
{
    [Fact]
    public void BuildEnvironmentVariablesAddsOverrides()
    {
        string[] variables = ConPtySession.BuildEnvironmentVariables(
            new Dictionary<string, string?>
            {
                ["GIT_CONFIG_COUNT"] = "1",
                ["GIT_CONFIG_KEY_0"] = "core.quotepath",
                ["GIT_CONFIG_VALUE_0"] = "false"
            });

        Assert.Contains("GIT_CONFIG_COUNT=1", variables);
        Assert.Contains("GIT_CONFIG_KEY_0=core.quotepath", variables);
        Assert.Contains("GIT_CONFIG_VALUE_0=false", variables);
    }

    [Fact]
    public void BuildEnvironmentVariablesCanRemoveInheritedVariables()
    {
        const string variableName = "TERMINAL_TEST_REMOVE_ME";
        Environment.SetEnvironmentVariable(variableName, "present");
        try
        {
            string[] variables = ConPtySession.BuildEnvironmentVariables(
                new Dictionary<string, string?>
                {
                    [variableName] = null
                });

            Assert.DoesNotContain(variables, item => item.StartsWith(variableName + "=", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, null);
        }
    }
}
