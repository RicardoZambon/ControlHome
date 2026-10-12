// Negative fixture: proves the Application rule rejects this web-stack namespace (see ReferenceRulesTests).
namespace ControlHome.Architecture.Tests.Fixtures.WebStack.Http;

public sealed class UsesHttpContext
{
    public string? Path(Microsoft.AspNetCore.Http.HttpContext context) => context?.Request.Path.Value;
}
