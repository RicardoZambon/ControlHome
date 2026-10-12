// Negative fixture: proves the Application rule rejects this web-stack namespace (see ReferenceRulesTests).
namespace ControlHome.Architecture.Tests.Fixtures.WebStack.Mvc;

public sealed class UsesProblemDetails
{
    public Microsoft.AspNetCore.Mvc.ProblemDetails Create() => new();
}
