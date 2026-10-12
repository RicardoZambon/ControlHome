// Negative fixture: proves the Application rule rejects this web-stack namespace (see ReferenceRulesTests).
namespace ControlHome.Architecture.Tests.Fixtures.WebStack.Builder;

public sealed class UsesApplicationBuilder
{
    public Microsoft.AspNetCore.Builder.IApplicationBuilder? Builder { get; set; }
}
