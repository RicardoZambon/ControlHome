// Negative fixture: proves the Application rule rejects this web-stack namespace (see ReferenceRulesTests).
namespace ControlHome.Architecture.Tests.Fixtures.WebStack.Authentication;

public sealed class UsesAuthenticationProperties
{
    public Microsoft.AspNetCore.Authentication.AuthenticationProperties Create() => new();
}
