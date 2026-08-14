using System.Reflection;
using S1Atlas.Core.Indexing;
using S1Atlas.Indexing.Workflow;
using Xunit;

namespace S1Atlas.Indexing.Tests.Workflow;

public sealed class ApiIndexingWorkflowTests
{
    [Fact]
    public void Installed_api_workflow_exposes_the_narrow_v1_contract()
    {
        var workflowType = typeof(IndexingWorkflow).Assembly.GetType(
            "S1Atlas.Indexing.Workflow.ApiIndexingWorkflow",
            throwOnError: false);

        Assert.NotNull(workflowType);
        var method = workflowType.GetMethod(
            "RunInstalledAsync",
            BindingFlags.Instance | BindingFlags.Public,
            binder: null,
            types:
            [
                typeof(CodebaseKind),
                typeof(string),
                typeof(string),
                typeof(bool),
                typeof(CancellationToken)
            ],
            modifiers: null);
        Assert.NotNull(method);
        Assert.Equal(typeof(Task<IndexingWorkflowResult>), method.ReturnType);
    }
}
