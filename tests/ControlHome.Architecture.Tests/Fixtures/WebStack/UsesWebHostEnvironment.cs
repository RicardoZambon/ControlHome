// Negative fixture: proves the Application rule rejects this web-stack namespace (see ReferenceRulesTests).
namespace ControlHome.Architecture.Tests.Fixtures.WebStack.Hosting;

public sealed class UsesWebHostEnvironment
{
    public Microsoft.AspNetCore.Hosting.IWebHostEnvironment? Environment { get; set; }
}
