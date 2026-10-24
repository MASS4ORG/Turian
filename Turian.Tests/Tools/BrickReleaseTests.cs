using Mass4.BrickRelease;
using ReleaseProgram = Mass4.BrickRelease.Program;

namespace Turian.Tests;

/// <summary>Checks release qualification, SSHSIG interoperability and private-key cleanup with disposable identities.</summary>
[Collection("SerialTests")]
public sealed class BrickReleaseTests : IAsyncLifetime
{
    readonly string root = Directory.CreateTempSubdirectory("brick-release-tests-").FullName;
    string Archive => Path.Combine(root, "org.mass4.fixture-1.0.0--source.brick");
    string QualificationFile => Path.Combine(root, "qualification.json");
    string Output => Path.Combine(root, "release.json");
    string Key => Path.Combine(root, "fixture-key");
    string PublicKey => File.ReadAllText(Key + ".pub");

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(root, "signing"));
        File.WriteAllText(Path.Combine(root, "signing/revoked-keys.json"), "[]");
        await GenerateKeyAsync(Key);
        var master = Path.Combine(root, "master");
        await GenerateKeyAsync(master);
        var publicFile = Path.Combine(root, "Turian/Editor/Core/Packages/registry-mass4.pub");
        Directory.CreateDirectory(Path.GetDirectoryName(publicFile)!);
        File.Copy(master + ".pub", publicFile);
        WriteArchive();
        ReleaseArchive.Qualify(Archive, "v1.0.0", new string('a', 40), QualificationFile);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Directory.Delete(root, recursive: true);
        return ValueTask.CompletedTask;
    }

    /// <summary>Both signatures verify with the production C# verifier against the qualified metadata message.</summary>
    [Fact]
    public async Task SignaturesInteroperateWithRegistryVerifierAndPreserveArchive()
    {
        var bytes = File.ReadAllBytes(Archive);
        await SignAsync();
        using var metadata = JsonDocument.Parse(File.ReadAllText(Output));
        var release = metadata.RootElement;
        var integrity = release.GetProperty("integrity").GetString()!;
        var message = RegistrySignatures.Message("org.mass4.fixture", "1.0.0", integrity);
        foreach (var (field, signingNamespace) in new[]
                 {
                     ("signature", RegistrySignatures.RegistryNamespace),
                     ("publisherSignature", RegistrySignatures.PublisherNamespace),
                 })
        {
            var signature = release.GetProperty(field).GetString()!;
            Assert.True(SshSignature.Verify(PublicKey, signingNamespace, message, signature));
            Assert.False(SshSignature.Verify(PublicKey, signingNamespace, bytes, signature));
            Assert.False(SshSignature.Verify(PublicKey, "gaya-store", message, signature));
        }

        Assert.Equal(SshSignature.Fingerprint(PublicKey), release.GetProperty("signedBy").GetString());
        Assert.Equal(new string('a', 40), release.GetProperty("sourceRevision").GetString());
        Assert.False(release.TryGetProperty("build", out _));
        Assert.Equal(bytes, File.ReadAllBytes(Archive));
        Assert.DoesNotContain(File.ReadAllText(Key).Trim(), File.ReadAllText(Output));
        AssertClean();
    }

    /// <summary>Changing tested payload or manifest identity invalidates qualification before signing.</summary>
    [Theory]
    [InlineData("org.mass4.other", "1.0.0", "original")]
    [InlineData("org.mass4.fixture", "2.0.0", "original")]
    [InlineData("org.mass4.fixture", "1.0.0", "changed")]
    public async Task ChangedArchiveIsRejected(string id, string version, string content)
    {
        WriteArchive(id, version, content);
        await Assert.ThrowsAsync<InvalidDataException>(() => SignAsync());
        Assert.False(File.Exists(Output));
        AssertClean();
    }

    /// <summary>Master, revoked and unapproved identities cannot sign a routine release.</summary>
    [Theory]
    [InlineData("master")]
    [InlineData("revoked")]
    [InlineData("mismatch")]
    public async Task UnauthorizedIdentitiesAreRejected(string reason)
    {
        var publicKey = PublicKey;
        if (reason == "master")
        {
            publicKey = File.ReadAllText(Path.Combine(root, "master.pub"));
        }
        else if (reason == "revoked")
        {
            File.WriteAllText(Path.Combine(root, "signing/revoked-keys.json"),
                JsonSerializer.Serialize(new[] { ReleaseSigner.Fingerprint(publicKey) }));
        }
        else
        {
            await GenerateKeyAsync(Path.Combine(root, "other"));
            publicKey = File.ReadAllText(Path.Combine(root, "other.pub"));
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => SignAsync(publicKey));
        Assert.False(File.Exists(Output));
        AssertClean();
    }

    /// <summary>Encrypted keys fail without prompting and leave no temporary private-key copy.</summary>
    [Fact]
    public async Task EncryptedKeyIsRejectedWithoutPrompt()
    {
        var encrypted = Path.Combine(root, "encrypted");
        await GenerateKeyAsync(encrypted, "test-only-passphrase");
        await Assert.ThrowsAsync<InvalidOperationException>(() => SignAsync(File.ReadAllText(encrypted + ".pub"),
            File.ReadAllText(encrypted)));
        AssertClean();
    }

    /// <summary>Cancellation removes private material before returning to the caller.</summary>
    [Fact]
    public async Task CancellationCleansSigningDirectory()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReleaseSigner.SignAsync(root, Archive,
            QualificationFile, Output, PublicKey, File.ReadAllText(Key), root, new CancellationToken(true)));
        AssertClean();
    }

    /// <summary>Revocation configuration is required and malformed configuration fails closed.</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("{invalid")]
    [InlineData("[null]")]
    [InlineData("[\"invalid fingerprint\"]")]
    public void InvalidRevocationConfigurationIsRejected(string json)
    {
        File.WriteAllText(Path.Combine(root, "signing/revoked-keys.json"), json);
        Assert.ThrowsAny<Exception>(() => ReleaseSigner.ValidateIdentity(root, PublicKey));
    }

    /// <summary>Missing revocation state and non-Ed25519 public keys cannot bypass the signing guard.</summary>
    [Fact]
    public void MissingRevocationsAndWrongKeyAlgorithmAreRejected()
    {
        File.Delete(Path.Combine(root, "signing/revoked-keys.json"));
        Assert.Throws<FileNotFoundException>(() => ReleaseSigner.ValidateIdentity(root, PublicKey));
        Assert.Throws<InvalidDataException>(() => ReleaseSigner.Fingerprint("ssh-rsa AAAA"));
    }

    /// <summary>Cancelling a running tool terminates that child process and drains its redirected streams.</summary>
    [Fact]
    public async Task ProcessCancellationStopsOnlyTheStartedChild()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ToolProcess.RunAsync("sleep", ["30"], cancellation.Token));
    }

    /// <summary>Qualifying a release requires its exact tag and a full source commit.</summary>
    [Theory]
    [InlineData("v2.0.0", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("v1.0.0", "main")]
    public void InvalidTagOrRevisionIsRejected(string tag, string revision) =>
        Assert.Throws<InvalidDataException>(() => ReleaseArchive.Qualify(Archive, tag, revision, Output));

    /// <summary>Only valid archive identities with one root manifest qualify.</summary>
    [Theory]
    [InlineData("invalid id", "1.0.0")]
    [InlineData("org.mass4.fixture", "invalid")]
    public void InvalidManifestIsRejected(string id, string version)
    {
        WriteArchive(id, version);
        Assert.Throws<InvalidDataException>(() => ReleaseArchive.Read(Archive));
    }

    /// <summary>Duplicate manifests cannot change which identity a consumer or signer observes.</summary>
    [Fact]
    public void DuplicateManifestsAreRejected()
    {
        using (var packed = ZipFile.Open(Archive, ZipArchiveMode.Update))
        {
            using var writer = new StreamWriter(packed.CreateEntry("package.json").Open());
            writer.Write("{}");
        }

        Assert.Throws<InvalidDataException>(() => ReleaseArchive.Read(Archive));
    }

    /// <summary>Workflow outputs bind the archive filename and qualification without multiline injection.</summary>
    [Fact]
    public void GithubOutputsMatchQualifiedArchive()
    {
        var outputs = Path.Combine(root, "github-output");
        var qualification = ReleaseArchive.Qualify(Archive, "v1.0.0", new string('a', 40), QualificationFile, outputs);
        var lines = File.ReadAllLines(outputs);
        Assert.Equal(qualification, JsonSerializer.Deserialize<Qualification>(lines[0]["qualification=".Length..],
            ReleaseArchive.Json));
        Assert.Equal("archive=" + Path.GetFileName(Archive), lines[1]);
        var renamed = Path.Combine(root, "other.brick");
        File.Copy(Archive, renamed);
        Assert.Throws<InvalidDataException>(() => ReleaseArchive.Qualify(renamed, "v1.0.0", new string('a', 40),
            QualificationFile, outputs));
    }

    /// <summary>
    /// A bake twin is qualified beside its source under prefixed outputs, under the source's id and the variant its
    /// manifest declares, and a manifest naming the brick by its v2 <c>id</c> is read like one using <c>name</c>.
    /// </summary>
    [Fact]
    public void TwinsQualifyUnderPrefixedOutputs()
    {
        const string bakeOf = "\"bakeOf\": { \"brick\": \"user.mateo.rules\", \"version\": \"1.0.0\", \"integrity\": \"sha256-x\" }";
        var twin = Path.Combine(root, "user.mateo.rules-1.0.0--turian3.3.brick");
        WriteManifest(twin, $$"""{ "id": "user.mateo.rules", "version": "1.0.0", "variant": "turian3.3", {{bakeOf}} }""");

        var outputs = Path.Combine(root, "github-output");
        var qualification = ReleaseArchive.Qualify(twin, "v1.0.0", new string('a', 40), QualificationFile, outputs, "twin-");

        Assert.Equal("user.mateo.rules", qualification.PackageId);
        Assert.Equal("twin-archive=" + Path.GetFileName(twin), File.ReadAllLines(outputs)[1]);
        Assert.Throws<InvalidDataException>(() => ReleaseArchive.Qualify(twin, "v1.0.0", new string('a', 40),
            QualificationFile, outputs, "twin\narchive=evil-"));

        // A bake without a declared variant, or a name the manifest does not back, cannot be released.
        var legacy = Path.Combine(root, "user.mateo.rules.bake-1.0.0--source.brick");
        WriteManifest(legacy, $$"""{ "id": "user.mateo.rules.bake", "version": "1.0.0", {{bakeOf}} }""");
        Assert.Throws<InvalidDataException>(() => ReleaseArchive.Qualify(legacy, "v1.0.0", new string('a', 40),
            QualificationFile, outputs, "twin-"));
        var misnamed = Path.Combine(root, "user.mateo.rules-1.0.0--source.brick");
        File.Copy(twin, misnamed);
        Assert.Throws<InvalidDataException>(() => ReleaseArchive.Qualify(misnamed, "v1.0.0", new string('a', 40),
            QualificationFile, outputs, "twin-"));
    }

    /// <summary>
    /// The signer writes build provenance beside the signatures when given both a digest-pinned image and an exact
    /// Turian version; a plain tag, a partial build or a floating version is refused before any key is read.
    /// </summary>
    [Fact]
    public async Task SigningRecordsTheBuild()
    {
        var image = "ghcr.io/mass4org/turian-cli@sha256:" + new string('c', 64);
        await ReleaseSigner.SignAsync(root, Archive, QualificationFile, Output, PublicKey, File.ReadAllText(Key), root,
            TestContext.Current.CancellationToken, ReleaseArchive.ValidateBuild(image, "3.4.0"));

        using var metadata = JsonDocument.Parse(File.ReadAllText(Output));
        var build = metadata.RootElement.GetProperty("build");
        Assert.Equal((image, "3.4.0"), (build.GetProperty("image").GetString(), build.GetProperty("turianVersion").GetString()));
        Assert.Equal(2, build.EnumerateObject().Count());

        foreach (var (reference, version) in new[] { ("ghcr.io/mass4org/turian-cli:3.4.0", "3.4.0"), (image, "3.x"), (image, "latest") })
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseProgram.ExecuteAsync(
                ["sign", Archive, QualificationFile, Output, "--build", reference, version], root, TestContext.Current.CancellationToken));
        }

        await Assert.ThrowsAsync<ArgumentException>(() => ReleaseProgram.ExecuteAsync(
            ["sign", Archive, QualificationFile, Output, "--build", image], root, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => ReleaseProgram.ExecuteAsync(
            ["sign", Archive, QualificationFile, Output, "--image", image, "3.4.0"], root, TestContext.Current.CancellationToken));
        AssertClean();
    }

    /// <summary>The command dispatcher qualifies and validates without shell argument interpretation.</summary>
    [Fact]
    public async Task CommandDispatchQualifiesAndValidates()
    {
        await ReleaseProgram.ExecuteAsync(["qualify", Archive, "v1.0.0", new string('b', 40), QualificationFile],
            root, TestContext.Current.CancellationToken);
        await ReleaseProgram.ExecuteAsync(["validate", Archive, QualificationFile], root, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ArgumentException>(() => ReleaseProgram.ExecuteAsync([], root, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => ReleaseProgram.ExecuteAsync(["unknown"], root,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => ReleaseProgram.ExecuteAsync(["validate"], root,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => ReleaseProgram.ExecuteAsync(
            ["qualify", Archive, "v1.0.0", new string('b', 40), QualificationFile, "--unknown"], root,
            TestContext.Current.CancellationToken));
    }

    /// <summary>Consumer verification clears its store before locked restore and respects Studio-only scope.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsumerRestoreStartsColdAndHonorsScope(bool studio)
    {
        WriteArchive(scopes: studio ? ["studio"] : null);
        var image = "ghcr.io/mass4org/turian-cli@sha256:" + new string('a', 64);
        var calls = new List<string[]>();
        string? work = null;
        var restored = false;
        await ConsumerSmoke.RunAsync(Archive, image, TestContext.Current.CancellationToken, (arguments, _) =>
        {
            work = arguments.Single(static argument => argument.EndsWith(":/test", StringComparison.Ordinal))[..^6];
            var command = arguments[(Array.IndexOf(arguments, image) + 1)..];
            calls.Add(command);
            Assert.True(Directory.Exists(Path.Combine(work, "home")));
            if (command is ["brick", "add", ..])
            {
                Directory.CreateDirectory(Path.Combine(work, "store"));
                File.WriteAllText(Path.Combine(work, "store/warm-cache"), "cached");
            }

            if (command is ["brick", "restore", "--locked", ..])
            {
                Assert.False(Directory.Exists(Path.Combine(work, "store")));
                restored = true;
            }

            return Task.FromResult("");
        });
        Assert.True(restored);
        Assert.Equal(!studio, calls.Any(static command => command[0] == "compile"));
        Assert.Equal(studio, calls.Any(static command => command.Contains("--studio")));
        Assert.False(Directory.Exists(work));
        await Assert.ThrowsAsync<InvalidDataException>(() => ConsumerSmoke.RunAsync(Archive,
            "ghcr.io/mass4org/turian-cli:latest", TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The GitLab brick template defaults to one official image pinned by digest, never a moving tag.
    /// </summary>
    [Fact]
    public void GitLabTemplatePinsTheOfficialImage()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Turian.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.SkipWhen(directory is null, "Test binaries are outside the repository.");
        var template = File.ReadAllText(Path.Combine(directory.FullName, "ci/brick.gitlab-ci.yml"));

        Assert.Single(Regex.Matches(template, @"ghcr\.io/mass4org/turian-cli[@:]"));
        Assert.Matches(@"TURIAN_CLI_IMAGE: ghcr\.io/mass4org/turian-cli@sha256:[0-9a-f]{64}\n", template);
    }

    /// <summary>
    /// Inside the CLI image the consumer runs the local turian-cli, isolated by its own home, store and configuration,
    /// with the archive and project at their real paths.
    /// </summary>
    [Fact]
    public async Task LocalConsumerIsolatesTheCli()
    {
        WriteArchive();
        var calls = new List<string[]>();
        var restored = false;
        await ConsumerSmoke.RunLocalAsync(Archive, TestContext.Current.CancellationToken, (arguments, environment, _) =>
        {
            var work = Path.GetDirectoryName(environment["HOME"])!;
            Assert.True(Directory.Exists(environment["HOME"]));
            Assert.Equal(Path.Combine(work, "store"), environment["GAYA_BRICKS"]);
            Assert.Equal(Path.Combine(work, "config"), environment["GAYA_CONFIG_HOME"]);
            calls.Add(arguments);
            if (arguments is ["brick", "add", ..])
            {
                Assert.Equal($"file:{Path.GetFullPath(Archive)}", arguments[3]);
                Directory.CreateDirectory(environment["GAYA_BRICKS"]);
            }

            if (arguments is ["brick", "restore", "--locked", ..])
            {
                Assert.False(Directory.Exists(environment["GAYA_BRICKS"]));
                restored = true;
            }

            return Task.FromResult("");
        });

        Assert.True(restored);
        Assert.Equal(["new", "brick", "compile", "brick", "brick"], calls.Select(static call => call[0]));
        Assert.StartsWith(Path.GetTempPath(), calls[0][1], StringComparison.Ordinal);
    }

    /// <summary>The executable consumes environment credentials without leaving them in child-process environments.</summary>
    [Fact]
    public async Task CommandSigningConsumesEnvironmentKeyAndReportsErrors()
    {
        var values = new Dictionary<string, string?>
        {
            ["BRICK_RELEASE_ROOT"] = root,
            ["RUNNER_TEMP"] = root,
            ["MASS4_BRICKS_CI_PUBLIC_KEY"] = PublicKey,
            ["MASS4_BRICKS_CI_SIGNING_KEY"] = File.ReadAllText(Key),
            ["GITHUB_OUTPUT"] = Path.Combine(root, "github-output"),
        };
        var saved = values.ToDictionary(static entry => entry.Key,
            static entry => Environment.GetEnvironmentVariable(entry.Key));
        try
        {
            foreach (var (name, value) in values)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            Assert.Equal(0, await ReleaseProgram.Main(["sign", Archive, QualificationFile, Output]));
            Assert.Null(Environment.GetEnvironmentVariable("MASS4_BRICKS_CI_SIGNING_KEY"));
            Assert.Equal(0, await ReleaseProgram.Main(["validate", Archive, QualificationFile]));
            Assert.Equal(1, await ReleaseProgram.Main([]));
            Assert.Equal(1, await ReleaseProgram.Main(["sign", Archive, QualificationFile, Output]));
            File.WriteAllText(QualificationFile, "{}");
            Assert.Equal(1, await ReleaseProgram.Main(["validate", Archive, QualificationFile]));
            await ReleaseProgram.ExecuteAsync(["qualify", Archive, "v1.0.0", new string('a', 40),
                QualificationFile, "--github-output"], root, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(values["GITHUB_OUTPUT"]));
            await Assert.ThrowsAsync<InvalidDataException>(() => ReleaseProgram.ExecuteAsync(
                ["consumer", Archive, "latest"], root, TestContext.Current.CancellationToken));
            AssertClean();
        }
        finally
        {
            foreach (var (name, value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    static async Task GenerateKeyAsync(string path, string passphrase = "") =>
        _ = await ToolProcess.RunAsync("ssh-keygen", ["-q", "-t", "ed25519", "-N", passphrase, "-f", path],
            TestContext.Current.CancellationToken);

    Task SignAsync(string? publicKey = null, string? privateKey = null) =>
        ReleaseSigner.SignAsync(root, Archive, QualificationFile, Output, publicKey ?? PublicKey,
            privateKey ?? File.ReadAllText(Key), root, TestContext.Current.CancellationToken);

    void AssertClean() => Assert.Empty(Directory.GetDirectories(root, "brick-signing-*"));

    static void WriteManifest(string archive, string json)
    {
        using var packed = ZipFile.Open(archive, ZipArchiveMode.Create);
        using var writer = new StreamWriter(packed.CreateEntry("package.json").Open());
        writer.Write(json);
    }

    void WriteArchive(string id = "org.mass4.fixture", string version = "1.0.0", string content = "original",
        string[]? scopes = null)
    {
        File.Delete(Archive);
        using var packed = ZipFile.Open(Archive, ZipArchiveMode.Create);
        using (var manifest = new StreamWriter(packed.CreateEntry("package.json").Open()))
        {
            manifest.Write(JsonSerializer.Serialize(new { name = id, version, scopes }));
        }

        using var readme = new StreamWriter(packed.CreateEntry("README.md").Open());
        readme.Write(content);
    }
}
