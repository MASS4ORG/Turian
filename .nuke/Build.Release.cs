using System.Diagnostics;

namespace Turian.NUKE;

/// <summary>
/// This is the main build file for the project.
/// This partial is responsible for tagging releases and publishing them to GitLab/GitHub,
/// including uploading the packed artifacts produced by <see cref="Pack"/>.
/// </summary>
sealed partial class Build
{
    [Parameter("GitLab group/project path, e.g. turian/Turian (default: turian/Turian)")]
    readonly string gitlabProjectPath = "turian/Turian";

    [Parameter("GitHub owner/repo path, e.g. turian/Turian (default: turian/Turian)")]
    readonly string githubRepository = "turian/Turian";

    [Parameter("Branch the release commit is pushed to (default: main)")]
    readonly string releaseBranch = "main";

    [Parameter("GitLab personal/project access token with api + write_repository scope. Omit to skip pushing/releasing to GitLab.")]
    [Secret]
    readonly string gitlabToken;

    [Parameter("GitHub token (repo scope, or the Actions-provided GITHUB_TOKEN) with contents:write. Omit to skip pushing/releasing to GitHub.")]
    [Secret]
    readonly string githubToken;

    /// <summary>
    /// Commits the changelog, tags the release and pushes the commit+tag to every host a token
    /// was supplied for. Pushing to both hosts keeps a standalone GitHub mirror in sync without
    /// relying on platform-native mirroring; a host that already has the tag is skipped so this
    /// is safe to run redundantly from both GitLab and GitHub schedules.
    /// </summary>
    public Target Tag => td =>
        td
            .DependsOn(UpdateChangelog)
            .OnlyWhenDynamic(() => HasNewCommits)
            .Executes(() =>
            {
                RunGit("set release bot name", "config", "user.name", "Turian Bot");
                RunGit("set release bot email", "config", "user.email", "massa+turian@brunomassa.com");
                RunGit("commit changelog", "-c", "commit.gpgsign=false", "commit", "-am", $"chore(release): {Version}");
                RunGit("tag release", "-c", "tag.gpgsign=false", "tag", TagName);

                PushToRemote("GitLab", gitlabToken, gitlabProjectPath, "gitlab.com", "oauth2");
                PushToRemote("GitHub", githubToken, githubRepository, "github.com", "x-access-token");
            });

    void PushToRemote(string name, string token, string repositoryPath, string host, string credentialUser)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            Log.Information("No token supplied for {Name}; skipping push", name);
            return;
        }

        var url = $"https://{credentialUser}:{token}@{host}/{repositoryPath}.git";

        var existingTag = RunGit("check remote tag", "ls-remote", "--tags", url, TagName);
        if (!string.IsNullOrWhiteSpace(existingTag))
        {
            Log.Information("{Name} already has tag {TagName}; skipping push", name, TagName);
            return;
        }

        RunGit("push release commit", "push", url, $"HEAD:{releaseBranch}");
        RunGit("push release tag", "push", url, TagName);
        Log.Information("Pushed {TagName} to {Name}", TagName, name);
    }

    string RunGit(string operation, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = RootDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start git for {operation}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var standardOutput = output.GetAwaiter().GetResult();
        var standardError = error.GetAwaiter().GetResult();

        if (process.ExitCode == 0) return standardOutput;

        var message = standardError;
        if (!string.IsNullOrEmpty(githubToken)) message = message.Replace(githubToken, "[redacted]", StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(gitlabToken)) message = message.Replace(gitlabToken, "[redacted]", StringComparison.Ordinal);
        throw new InvalidOperationException($"Git {operation} failed (exit {process.ExitCode}): {message.Trim()}");
    }

    /// <summary>
    /// Creates the GitLab release for the current tag: every archive in <see cref="ArtifactsDirectory"/>
    /// is uploaded to the project's generic package registry and linked from the release.
    /// </summary>
    public Target GitLabRelease => td =>
        td
            .Requires(() => gitlabToken)
            .Executes(async () =>
            {
                using var http = new HttpClient { BaseAddress = new Uri("https://gitlab.com/api/v4/") };
                http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", gitlabToken);

                var encodedProject = Uri.EscapeDataString(gitlabProjectPath);
                var links = new List<object>();

                foreach (var archive in ArtifactsDirectory.GlobFiles("*.zip", "*.deb", "*.exe"))
                {
                    var packageUrl = $"projects/{encodedProject}/packages/generic/turian/{Version}/{archive.Name}";
                    using var content = new ByteArrayContent(await File.ReadAllBytesAsync(archive));
                    var response = await http.PutAsync(packageUrl, content);
                    response.EnsureSuccessStatusCode();

                    links.Add(new
                    {
                        name = archive.Name,
                        url = $"https://gitlab.com/api/v4/{packageUrl}",
                        direct_asset_path = $"/{archive.Name}",
                        link_type = "package",
                    });
                    Log.Information("Uploaded {Archive} to GitLab generic package registry", archive.Name);
                }

                var payload = JsonSerializer.Serialize(new
                {
                    tag_name = TagName,
                    name = TagName,
                    description = ReadChangelogSection(TagName),
                    assets = new { links },
                });

                var releaseResponse = await http.PostAsync(
                    $"projects/{encodedProject}/releases",
                    new StringContent(payload, Encoding.UTF8, "application/json"));
                releaseResponse.EnsureSuccessStatusCode();
                Log.Information("Created GitLab release {TagName}", TagName);
            });

    /// <summary>
    /// Creates the GitHub release for the current tag and uploads every archive in
    /// <see cref="ArtifactsDirectory"/> as a release asset.
    /// </summary>
    public Target GitHubRelease => td =>
        td
            .Requires(() => githubToken)
            .Executes(async () =>
            {
                using var http = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
                http.DefaultRequestHeaders.Authorization = new("Bearer", githubToken);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Turian-NUKE-Build");
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

                var payload = JsonSerializer.Serialize(new
                {
                    tag_name = TagName,
                    name = TagName,
                    body = ReadChangelogSection(TagName),
                    draft = false,
                    prerelease = false,
                });

                var releaseResponse = await http.PostAsync(
                    $"repos/{githubRepository}/releases",
                    new StringContent(payload, Encoding.UTF8, "application/json"));
                releaseResponse.EnsureSuccessStatusCode();

                using var releaseDocument = JsonDocument.Parse(await releaseResponse.Content.ReadAsStringAsync());
                var releaseId = releaseDocument.RootElement.GetProperty("id").GetInt64();
                Log.Information("Created GitHub release {TagName}", TagName);

                using var uploadHttp = new HttpClient { BaseAddress = new Uri("https://uploads.github.com/") };
                uploadHttp.DefaultRequestHeaders.Authorization = new("Bearer", githubToken);
                uploadHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Turian-NUKE-Build");
                uploadHttp.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

                foreach (var archive in ArtifactsDirectory.GlobFiles("*.zip", "*.deb", "*.exe"))
                {
                    using var content = new ByteArrayContent(await File.ReadAllBytesAsync(archive));
                    content.Headers.ContentType = new(archive.HasExtension("deb")
                        ? "application/vnd.debian.binary-package"
                        : archive.HasExtension("exe") ? "application/vnd.microsoft.portable-executable" : "application/zip");
                    var uploadResponse = await uploadHttp.PostAsync(
                        $"repos/{githubRepository}/releases/{releaseId}/assets?name={Uri.EscapeDataString(archive.Name)}",
                        content);
                    uploadResponse.EnsureSuccessStatusCode();
                    Log.Information("Uploaded {Archive} to GitHub release {TagName}", archive.Name, TagName);
                }
            });
}
