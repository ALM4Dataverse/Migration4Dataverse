using System.Reflection;
using Migration4Dataverse.Engine.PowerShell;

namespace Migration4Dataverse.Engine.Tests.PowerShell;

public sealed class DataverseRequestCostTests
{
    private static readonly MethodInfo ExecuteMethod = typeof(FakeOrganizationService)
        .GetMethod(nameof(FakeOrganizationService.Execute))!;

    [Fact]
    public void BatchRequestCostExpandsNestedAndBulkMessages()
    {
        var request = new Request("ExecuteTransaction")
        {
            Requests =
            [
                new Request("Create"),
                new Request("CreateMultiple")
                {
                    Targets = new object[] { new(), new(), new() }
                },
                new Request("ExecuteMultiple")
                {
                    Requests = [new Request("Update"), new Request("UpsertMultiple")
                    {
                        Targets = new object[] { new(), new() }
                    }]
                }
            ]
        };

        Assert.Equal(7, DataverseRequestCostCalculator.Calculate(ExecuteMethod, [request]));
    }

    [Fact]
    public void EmptyBatchCountsAsOneServiceRequest()
    {
        Assert.Equal(
            1,
            DataverseRequestCostCalculator.Calculate(
                ExecuteMethod,
                [new Request("ExecuteMultiple") { Requests = [] }]));
        Assert.Equal(
            1,
            DataverseRequestCostCalculator.Calculate(
                ExecuteMethod,
                [new Request("CreateMultiple") { Targets = Array.Empty<object>() }]));
    }

    [Fact]
    public void RetrieveMultipleCountsAsOneServiceRequest()
    {
        Assert.Equal(
            1,
            DataverseRequestCostCalculator.Calculate(
                ExecuteMethod,
                [new Request("RetrieveMultiple")]));
    }

    [Fact]
    public void RecognizedBulkMessageWithoutTargetsFailsExplicitly()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            DataverseRequestCostCalculator.Calculate(
                ExecuteMethod,
                [new Request("DeleteMultiple")]));

        Assert.Contains("Targets", exception.Message, StringComparison.Ordinal);
    }

    private sealed class FakeOrganizationService
    {
        public object Execute(object request) => request;
    }

    private sealed class Request(string requestName)
    {
        public string RequestName { get; } = requestName;

        public IEnumerable<object>? Requests { get; init; }

        public IEnumerable<object>? Targets { get; init; }
    }
}
