// Negative fixture: proves the Application rule rejects this web-stack namespace (see ReferenceRulesTests).
namespace ControlHome.Architecture.Tests.Fixtures.WebStack.Routing;

public sealed class UsesLinkGenerator
{
    public Microsoft.AspNetCore.Routing.LinkGenerator? Links { get; set; }
}
