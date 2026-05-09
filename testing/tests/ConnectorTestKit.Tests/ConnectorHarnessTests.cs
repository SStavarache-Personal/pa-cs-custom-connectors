using ConnectorTestKit;

namespace ConnectorTestKit.Tests;

public sealed class ConnectorHarnessTests
{
    public static IEnumerable<object[]> Connectors()
    {
        string root = ConnectorRepository.FindRepositoryRoot();
        return ConnectorRepository.LoadConnectors(root)
            .Select(connector => new object[] { connector });
    }

    public static IEnumerable<object[]> ConnectorTestCases()
    {
        string root = ConnectorRepository.FindRepositoryRoot();
        foreach (ConnectorDefinition connector in ConnectorRepository.LoadConnectors(root))
        {
            foreach (ConnectorTestCaseDescriptor testCase in ConnectorRepository.LoadTestCases(connector))
            {
                if (!testCase.TestCase.IncludeInAutomatedRun)
                {
                    continue;
                }

                yield return new object[] { testCase };
            }
        }
    }

    [Theory]
    [MemberData(nameof(Connectors))]
    public void Connectors_match_platform_constraints(ConnectorDefinition connector)
    {
        ConnectorValidationResult validation = ConnectorValidator.Validate(connector);
        Assert.True(
            validation.IsValid,
            $"Connector '{connector.Name}' failed validation:{Environment.NewLine}{string.Join(Environment.NewLine, validation.Issues.Select(issue => $"- [{issue.Severity}] {issue.Message}"))}");
    }

    [Theory]
    [MemberData(nameof(ConnectorTestCases))]
    public async Task Connector_test_cases_execute(ConnectorTestCaseDescriptor descriptor)
    {
        var runner = new ConnectorTestRunner();
        ConnectorTestRunResult result = await runner.RunAsync(descriptor.Connector, descriptor.TestCase);

        Assert.True(
            result.Passed,
            $"Test '{descriptor.TestCase.Name}' failed:{Environment.NewLine}{string.Join(Environment.NewLine, result.Failures.Select(failure => $"- {failure}"))}{Environment.NewLine}Body:{Environment.NewLine}{result.Invocation.BodyText}");
    }
}