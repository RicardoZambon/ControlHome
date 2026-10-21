using System.Xml.Linq;
using Mono.Cecil;
using NetArchTest.Rules;

namespace ControlHome.Architecture.Tests;

/// <summary>
/// Enforces the reference graph: Domain (no deps) ← Application ← Infrastructure ← Api; Connectors.Abstractions → Domain;
/// Connectors.&lt;Vendor&gt; (Simulated, Mqtt, DahuaHttp) → Connectors.Abstractions only.
/// </summary>
/// <remarks>
/// Assemblies are inspected from disk (Mono.Cecil via <see cref="Types.FromFile"/>) instead of being loaded into the test
/// process, so the rules never execute project code and are unaffected by OS code-integrity policies on dev machines.
/// </remarks>
public sealed class ReferenceRulesTests
{
    private const string Domain = "ControlHome.Domain";
    private const string Application = "ControlHome.Application";
    private const string Infrastructure = "ControlHome.Infrastructure";
    private const string Api = "ControlHome.Api";
    private const string ConnectorsPrefix = "ControlHome.Connectors.";
    private const string ConnectorsAbstractions = "ControlHome.Connectors.Abstractions";
    private const string SimulatedConnector = "ControlHome.Connectors.Simulated";
    private const string MqttConnector = "ControlHome.Connectors.Mqtt";
    private const string DahuaHttpConnector = "ControlHome.Connectors.DahuaHttp";
    private const string FixturesNamespace = "ControlHome.Architecture.Tests.Fixtures";

    /// <summary>What no connector assembly (the SDK included) may depend on: the application layers, EF Core, the web stack.</summary>
    private static readonly string[] _connectorForbidden =
        [Application, Infrastructure, Api, "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore"];

    /// <summary>
    /// The ASP.NET Core web stack Application must never touch. Application MAY use ASP.NET Core Identity's model
    /// (namespace Microsoft.AspNetCore.Identity: IdentityUser, UserManager — from the Microsoft.Extensions.Identity.*
    /// packages, not the shared framework).
    /// </summary>
    private static readonly string[] _webStack =
    [
        "Microsoft.AspNetCore.Http",
        "Microsoft.AspNetCore.Mvc",
        "Microsoft.AspNetCore.Builder",
        "Microsoft.AspNetCore.Routing",
        "Microsoft.AspNetCore.Hosting",
        "Microsoft.AspNetCore.Authentication",
    ];

    private static readonly string[] _applicationForbidden = [Infrastructure, Api, .. _webStack];

    [Fact]
    public void Domain_has_no_dependency_on_outer_layers_or_frameworks()
    {
        AssertNoTypeDependencies(
            Domain,
            Application,
            Infrastructure,
            Api,
            "ControlHome.Connectors",
            "Microsoft.EntityFrameworkCore",
            "Microsoft.AspNetCore",
            "Microsoft.Extensions");
    }

    [Fact]
    public void Connectors_Abstractions_depends_on_Domain_only()
    {
        AssertNoTypeDependencies(ConnectorsAbstractions, [.. _connectorForbidden, SimulatedConnector, MqttConnector, DahuaHttpConnector]);
    }

    [Theory]
    [InlineData(SimulatedConnector)]
    [InlineData(MqttConnector)]
    [InlineData(DahuaHttpConnector)]
    public void Connector_assemblies_never_depend_on_Application_Infrastructure_or_Api(string connector)
    {
        AssertNoTypeDependencies(connector, _connectorForbidden);
    }

    [Fact]
    public void Connector_rule_rejects_a_connector_that_uses_the_application_layer()
    {
        // Guard against a vacuous pass: the Api depends on Application, so the same rule must fail for it.
        var result = Types.FromFile(AssemblyPath(Api)).ShouldNot().HaveDependencyOnAny(_connectorForbidden).GetResult();

        result.IsSuccessful.ShouldBeFalse();
    }

    [Fact]
    public void Connectors_Abstractions_project_references_only_Domain()
    {
        var project = LoadProject(ConnectorsAbstractions);

        ProjectReferenceNames(project).ShouldBe([Domain]);
        ItemIncludes(project, "FrameworkReference").ShouldBeEmpty();
        ItemIncludes(project, "PackageReference").ShouldNotContain(
            p => p.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) || p.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_connector_project_references_only_Connectors_Abstractions()
    {
        var connectors = Directory.GetDirectories(Path.Combine(FindRepositoryRoot(), "src"), ConnectorsPrefix + "*")
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => name != ConnectorsAbstractions)
            .ToList();

        connectors.ShouldContain(SimulatedConnector);
        connectors.ShouldContain(MqttConnector);
        connectors.ShouldContain(DahuaHttpConnector);
        foreach (var connector in connectors)
        {
            var project = LoadProject(connector);
            ProjectReferenceNames(project).ShouldBe([ConnectorsAbstractions], connector);
            ItemIncludes(project, "FrameworkReference").ShouldBeEmpty(connector);
            ItemIncludes(project, "PackageReference").ShouldNotContain(
                p => p.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) || p.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal),
                connector);
        }
    }

    [Fact]
    public void Application_has_no_dependency_on_Infrastructure_Api_or_the_web_stack()
    {
        AssertNoTypeDependencies(Application, _applicationForbidden);
    }

    [Theory]
    [InlineData("Http", "UsesHttpContext")]
    [InlineData("Mvc", "UsesProblemDetails")]
    [InlineData("Builder", "UsesApplicationBuilder")]
    [InlineData("Routing", "UsesLinkGenerator")]
    [InlineData("Hosting", "UsesWebHostEnvironment")]
    [InlineData("Authentication", "UsesAuthenticationProperties")]
    public void Application_rule_rejects_web_stack_types(string area, string fixtureType)
    {
        var fixtureNamespace = $"{FixturesNamespace}.WebStack.{area}";

        var result = TestAssemblyTypes().That().ResideInNamespace(fixtureNamespace)
            .ShouldNot().HaveDependencyOnAny(_applicationForbidden).GetResult();

        result.IsSuccessful.ShouldBeFalse($"the Application rule must reject {fixtureNamespace}");
        (result.FailingTypeNames ?? []).ShouldContain($"{fixtureNamespace}.{fixtureType}");
    }

    [Fact]
    public void Application_rule_allows_the_identity_model()
    {
        var fixtureNamespace = $"{FixturesNamespace}.IdentityModel";

        // Guard against a vacuous pass: the fixture really does depend on Microsoft.AspNetCore.Identity ...
        TestAssemblyTypes().That().ResideInNamespace(fixtureNamespace)
            .ShouldNot().HaveDependencyOn("Microsoft.AspNetCore.Identity").GetResult().IsSuccessful.ShouldBeFalse();

        // ... and the Application rule accepts it.
        var result = TestAssemblyTypes().That().ResideInNamespace(fixtureNamespace)
            .ShouldNot().HaveDependencyOnAny(_applicationForbidden).GetResult();
        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Application_assembly_references_no_AspNetCore_assembly()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(AssemblyPath(Application));

        var references = assembly.MainModule.AssemblyReferences.Select(r => r.Name).ToArray();

        // IdentityUser/UserManager come from Microsoft.Extensions.Identity.*, so no Microsoft.AspNetCore.* assembly is needed.
        references.ShouldContain("Microsoft.Extensions.Identity.Stores");
        references.ShouldNotContain(r => r.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal), string.Join(", ", references));
    }

    [Fact]
    public void Application_project_has_no_AspNetCore_framework_or_package_reference()
    {
        var project = LoadProject(Application);

        ItemIncludes(project, "FrameworkReference").ShouldBeEmpty();
        ItemIncludes(project, "PackageReference").ShouldNotContain(p => p.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal));
    }

    [Fact]
    public void Infrastructure_has_no_dependency_on_Api()
    {
        AssertNoTypeDependencies(Infrastructure, Api);
    }

    [Fact]
    public void Domain_project_has_no_project_or_package_references()
    {
        var project = LoadProject(Domain);

        ItemIncludes(project, "ProjectReference").ShouldBeEmpty();
        ItemIncludes(project, "PackageReference").ShouldBeEmpty();
        ItemIncludes(project, "FrameworkReference").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(Application, new[] { Infrastructure, Api, SimulatedConnector, MqttConnector, DahuaHttpConnector })]
    [InlineData(Infrastructure, new[] { Api, SimulatedConnector, MqttConnector, DahuaHttpConnector })]
    public void Project_does_not_reference_outer_layers(string projectName, string[] forbidden)
    {
        var referenced = ProjectReferenceNames(LoadProject(projectName));

        referenced.ShouldNotContain(r => forbidden.Contains(r), $"{projectName} references [{string.Join(", ", referenced)}]");
    }

    private static string[] ProjectReferenceNames(XDocument project) =>
        ItemIncludes(project, "ProjectReference").Select(include => Path.GetFileNameWithoutExtension(include)).ToArray();

    private static void AssertNoTypeDependencies(string assemblyName, params string[] forbidden)
    {
        var assemblyPath = AssemblyPath(assemblyName);

        // Guard against a vacuous pass (wrong or empty file): the root-namespace marker type must be visible.
        var markers = Types.FromFile(assemblyPath).ShouldNot().HaveName("AssemblyMarker").GetResult().FailingTypeNames ?? [];
        markers.ShouldContain($"{assemblyName}.AssemblyMarker");

        var result = Types.FromFile(assemblyPath).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();

        var offenders = result.FailingTypeNames ?? [];
        result.IsSuccessful.ShouldBeTrue(
            $"{assemblyName} must not depend on [{string.Join(", ", forbidden)}]; offending types: {string.Join(", ", offenders)}");
    }

    private static Types TestAssemblyTypes() => Types.FromFile(AssemblyPath("ControlHome.Architecture.Tests"));

    private static string AssemblyPath(string assemblyName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");
        File.Exists(path).ShouldBeTrue($"Assembly not found: {path}");
        return path;
    }

    private static XDocument LoadProject(string projectName)
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", projectName, $"{projectName}.csproj");
        File.Exists(path).ShouldBeTrue($"Project file not found: {path}");
        return XDocument.Load(path);
    }

    private static string[] ItemIncludes(XDocument project, string itemName) =>
        project.Descendants(itemName)
            .Select(e => (string?)e.Attribute("Include"))
            .OfType<string>()
            .Select(include => include.Replace('\\', '/'))
            .ToArray();

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ControlHome.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"ControlHome.slnx not found above {AppContext.BaseDirectory}.");
    }
}
