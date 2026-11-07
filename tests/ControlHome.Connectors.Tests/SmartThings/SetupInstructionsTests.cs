using ControlHome.Connectors.SmartThings.Flow;

namespace ControlHome.Connectors.Tests.SmartThings;

/// <summary>The wizard's data-driven instructions stay in step with <c>docs/connectors/smartthings.md</c>.</summary>
public sealed class SetupInstructionsTests
{
    [Fact]
    public void The_instructions_are_complete_in_both_languages()
    {
        var instructions = SetupInstructions.Default;

        instructions.DocsUrl.Scheme.ShouldBe("https");
        instructions.StepsEn.Count.ShouldBe(instructions.StepsPtBr.Count);
        instructions.Description.En.ShouldNotContain("{");
        instructions.Description.PtBr.ShouldNotContain("{");
        instructions.Description.En.ShouldContain("1) ");
        instructions.Title.PtBr.ShouldNotBe(instructions.Title.En);
    }

    [Fact]
    public void The_docs_page_describes_the_same_app_as_the_wizard()
    {
        var instructions = SetupInstructions.Default;
        var docs = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "connectors", "smartthings.md"));

        // The wizard links to this section (GitHub's anchor for the heading).
        instructions.DocsUrl.Fragment.ShouldBe("#create-your-own-smartthings-app");
        docs.ShouldContain("## Create your own SmartThings app");
        docs.ShouldContain(instructions.CliCommand);
        docs.ShouldContain(instructions.AppType);
        docs.ShouldContain(instructions.DefaultRedirectUri);
        foreach (var scope in instructions.Scopes)
        {
            docs.ShouldContain($"`{scope}`");
        }

        docs.ShouldContain("## Live validation checklist");
        docs.ShouldContain("Experimental: not live-validated");
    }

    private static string RepositoryRoot()
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
