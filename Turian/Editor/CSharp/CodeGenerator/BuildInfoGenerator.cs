namespace Turian.CSharp.CodeGenerator;

/// <summary>
/// Generates <c>Gaya.Plugin.Turian.BuildInfo</c>: the version this build was compiled at, the
/// compilation date, and the contributor list parsed from <c>CONTRIBUTORS.md</c>. The consuming
/// project supplies the version through the <c>GitDescribe</c> MSBuild property (exposed to the
/// generator via <c>CompilerVisibleProperty</c>) and adds <c>CONTRIBUTORS.md</c> as an additional
/// file. Consumed by the About dialog.
/// </summary>
/// <seealso href="AboutDialogChrome.cs"/>
[Generator]
public class BuildInfoGenerator : IIncrementalGenerator
{
    static string ParseContributors(string? markdownContent)
    {
        if (string.IsNullOrWhiteSpace(markdownContent))
        {
            return "(contributors list not available)";
        }

        var regex = ContributorRegex();
        var matches = regex.Matches(markdownContent);

        var contributors = new List<string>();

        foreach (var match in matches.Cast<Match>())
        {
            var name = match.Groups["name"].Value;
            var email = match.Groups["email"].Value;

            contributors.Add($"{name} ({email})");
        }

        contributors.Sort();
        return string.Join("\n", contributors);
    }

    static readonly Regex contributorRegex = new(
        "\\*\\s+(?:\\[(?<name>[^\\]]+)\\]\\((?<email>[^)]+)\\)|(?<name2>[^[\\n]+))",
        RegexOptions.Multiline | RegexOptions.Compiled);

    static Regex ContributorRegex() => contributorRegex;

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var contributorsFile = context.AdditionalTextsProvider
            .Where(file => Path.GetFileName(file.Path) == "CONTRIBUTORS.md");

        var version = context.AnalyzerConfigOptionsProvider
            .Select((options, _) => options.GlobalOptions.TryGetValue("build_property.GitDescribe", out var value)
                && !string.IsNullOrWhiteSpace(value)
                ? value
                : "dev");

        var buildInfo = contributorsFile
            .Select((file, _) => ParseContributors(file.GetText()?.ToString()))
            .Combine(version)
            .Combine(context.CompilationProvider.Select((_, _) => System.DateTime.UtcNow.ToString("yyyy-MM-dd")));

        context.RegisterSourceOutput(buildInfo, (spc, data) =>
        {
            var ((contributorsList, versionString), compilationDate) = data;
            var source = $@"
namespace Gaya.Plugin.Turian;

public static partial class BuildInfo
{{
    static BuildInfo()
    {{
        SVersion = {ToLiteral(versionString)};
        SContributors = {ToLiteral(contributorsList)};
        SCompilationDate = {ToLiteral(compilationDate)};
    }}
}}";
            spc.AddSource("BuildInfo.g.cs", SourceText.From(source, Encoding.UTF8));
        });
    }

    static string ToLiteral(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
}
