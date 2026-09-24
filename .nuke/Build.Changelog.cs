namespace Turian.NUKE;

/// <summary>
/// This is the main build file for the project.
/// This partial is responsible for turning Conventional Commits into a Keep a Changelog entry.
/// </summary>
sealed partial class Build
{
    static AbsolutePath ChangelogFile => RootDirectory / "CHANGELOG.md";

    const string UnreleasedHeader = "## [Unreleased]";

    static readonly (string Prefix, string Section)[] changelogSections =
    [
        ("feat", "Added"),
        ("fix", "Fixed"),
        ("perf", "Changed"),
        ("refactor", "Changed"),
        ("revert", "Changed"),
        ("security", "Security"),
        ("remove", "Removed"),
        ("deprecate", "Deprecated"),
    ];

    /// <summary>
    /// Rewrites CHANGELOG.md: the commits since the last tag are grouped into a new
    /// "## [Version] - date" section (Keep a Changelog style), and a fresh empty
    /// "## [Unreleased]" section is left above it for subsequent commits.
    /// </summary>
    public Target UpdateChangelog => td =>
        td
            .DependsOn(CheckNewCommits)
            .OnlyWhenDynamic(() => HasNewCommits)
            .Executes(() =>
            {
                var grouped = CollectChangelogEntries();
                var section = BuildChangelogSection(Version, grouped);
                WriteChangelogSection(section);
                Log.Information("Updated {ChangelogFile} with version {Version}", ChangelogFile, Version);
            });

    /// <summary>
    /// Commits since the last tag (or the full history if there is no tag yet),
    /// grouped by Conventional Commits type into Keep a Changelog sections.
    /// Non-user-facing types (chore, docs, test, ci, build, style) are omitted.
    /// </summary>
    Dictionary<string, List<string>> CollectChangelogEntries()
    {
        var range = HasAnyTags ? $"{CurrentTag}..HEAD" : "HEAD";
        var subjects = Git($"log {range} --no-merges --pretty=format:%s")
            .Select(output => output.Text)
            .Where(subject => !string.IsNullOrWhiteSpace(subject));

        var grouped = new Dictionary<string, List<string>>();
        foreach (var subject in subjects)
        {
            var match = Regex.Match(subject, @"^(?<type>\w+)(?:\([\w\-.]+\))?!?:\s*(?<message>.+)$");
            if (!match.Success)
            {
                continue;
            }

            var type = match.Groups["type"].Value;
            var section = changelogSections
                .FirstOrDefault(entry => string.Equals(entry.Prefix, type, StringComparison.OrdinalIgnoreCase))
                .Section;
            if (section is null)
            {
                continue;
            }

            if (!grouped.TryGetValue(section, out var entries))
            {
                entries = [];
                grouped[section] = entries;
            }
            entries.Add(match.Groups["message"].Value.Trim());
        }

        return grouped;
    }

    static string BuildChangelogSection(string version, Dictionary<string, List<string>> grouped)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"## [{version}] - {DateTime.UtcNow:yyyy-MM-dd}");

        var orderedSections = changelogSections.Select(entry => entry.Section).Distinct();
        foreach (var section in orderedSections)
        {
            if (!grouped.TryGetValue(section, out var entries) || entries.Count == 0)
            {
                continue;
            }

            builder.AppendLine();
            builder.AppendLine(CultureInfo.InvariantCulture, $"### {section}");
            foreach (var entry in entries)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- {entry}");
            }
        }

        return builder.ToString().TrimEnd();
    }

    static void WriteChangelogSection(string section)
    {
        var content = ChangelogFile.FileExists() ? ChangelogFile.ReadAllText() : $"# Changelog{Environment.NewLine}";
        var replacement = $"{UnreleasedHeader}{Environment.NewLine}{Environment.NewLine}{section}";

        // Replace the whole old "## [Unreleased]" section (header + body up to the next "## "
        // heading or end of file), not just the header line, so stale Unreleased content doesn't
        // survive as an orphaned block below the newly inserted version section.
        var unreleasedSection = new Regex(
            $@"{Regex.Escape(UnreleasedHeader)}.*?(?=\n##\s|\z)",
            RegexOptions.Singleline);

        content = unreleasedSection.IsMatch(content)
            ? unreleasedSection.Replace(content, replacement.Replace("$", "$$", StringComparison.Ordinal), 1)
            : $"{content.TrimEnd()}{Environment.NewLine}{Environment.NewLine}{replacement}{Environment.NewLine}";

        ChangelogFile.WriteAllText(content);
    }

    /// <summary>
    /// The changelog section body for a given tag, used as release notes for GitLab/GitHub releases.
    /// Falls back to an empty string when the tag has no dedicated section (e.g. re-running a release).
    /// </summary>
    static string ReadChangelogSection(string tagName)
    {
        var version = tagName.TrimStart('v');
        if (!ChangelogFile.FileExists())
        {
            return string.Empty;
        }

        var content = ChangelogFile.ReadAllText();
        var match = Regex.Match(
            content,
            $@"##\s*\[{Regex.Escape(version)}\][^\n]*\n(?<body>.*?)(?=\n##\s*\[|\z)",
            RegexOptions.Singleline);
        return match.Success ? match.Groups["body"].Value.Trim() : string.Empty;
    }
}
