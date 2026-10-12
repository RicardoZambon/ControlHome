using System.Xml.Linq;
using NetArchTest.Rules;

namespace ControlHome.Architecture.Tests;

/// <summary>Enforces the reference graph: Domain (no deps) ← Application ← Infrastructure ← Api.</summary>
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

    [Fact]
    public void Domain_has_no_dependency_on_outer_layers_or_frameworks()
    {
        AssertNoTypeDependencies(Domain, Application, Infrastructure, Api, "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore");
    }

    [Fact]
    public void Application_has_no_dependency_on_Infrastructure_Api_or_AspNetCore()
    {
        AssertNoTypeDependencies(Application, Infrastructure, Api, "Microsoft.AspNetCore");
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
    }

    [Theory]
    [InlineData(Application, new[] { Infrastructure, Api })]
    [InlineData(Infrastructure, new[] { Api })]
    public void Project_does_not_reference_outer_layers(string projectName, string[] forbidden)
    {
        var referenced = ItemIncludes(LoadProject(projectName), "ProjectReference")
            .Select(include => Path.GetFileNameWithoutExtension(include))
            .ToArray();

        referenced.ShouldNotContain(r => forbidden.Contains(r), $"{projectName} references [{string.Join(", ", referenced)}]");
    }

    private static void AssertNoTypeDependencies(string assemblyName, params string[] forbidden)
    {
        var assemblyPath = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.dll");
        File.Exists(assemblyPath).ShouldBeTrue($"Assembly not found: {assemblyPath}");

        // Guard against a vacuous pass (wrong or empty file): the root-namespace marker type must be visible.
        var markers = Types.FromFile(assemblyPath).ShouldNot().HaveName("AssemblyMarker").GetResult().FailingTypeNames ?? [];
        markers.ShouldContain($"{assemblyName}.AssemblyMarker");

        var result = Types.FromFile(assemblyPath).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();

        var offenders = result.FailingTypeNames ?? [];
        result.IsSuccessful.ShouldBeTrue(
            $"{assemblyName} must not depend on [{string.Join(", ", forbidden)}]; offending types: {string.Join(", ", offenders)}");
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
