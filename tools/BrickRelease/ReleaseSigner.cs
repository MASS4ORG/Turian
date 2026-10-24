using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mass4.BrickRelease;

static partial class ReleaseSigner
{
    static readonly JsonSerializerOptions ReleaseJson = new(ReleaseArchive.Json)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Signs a qualified archive's release metadata with the CI key. The build provenance, when given, is written
    /// beside the signatures, which do not cover it.
    /// </summary>
    internal static async Task SignAsync(string root, string archive, string qualificationFile, string output,
        string publicKey, string privateKey, string temporaryRoot, CancellationToken cancellationToken,
        ReleaseBuild? build = null)
    {
        var qualification = ReleaseArchive.Validate(archive, qualificationFile);
        ValidateIdentity(root, publicKey);
        var directory = Path.Combine(temporaryRoot, "brick-signing-" + Guid.NewGuid().ToString("N"));
        var temporary = OperatingSystem.IsWindows()
            ? Directory.CreateDirectory(directory)
            : Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var key = Path.Combine(temporary.FullName, "key");
            await WriteKeyAsync(key, privateKey, cancellationToken);
            var derived = await ToolProcess.RunAsync("ssh-keygen", ["-y", "-P", "", "-f", key], cancellationToken);
            if (!Blob(derived).AsSpan().SequenceEqual(Blob(publicKey)))
            {
                throw new InvalidDataException("CI private key does not match the approved public key.");
            }

            var signatures = await SignMessagesAsync(qualification, publicKey, key, temporary.FullName,
                cancellationToken);
            ReleaseArchive.Validate(archive, qualificationFile);
            var metadata = new
            {
                qualification.PackageId,
                qualification.Version,
                qualification.Integrity,
                qualification.SourceRevision,
                signedBy = Fingerprint(publicKey),
                publisherKey = publicKey.Trim(),
                signature = signatures[0],
                publisherSignature = signatures[1],
                isPrivate = false,
                build,
            };
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(metadata, ReleaseJson) + "\n",
                cancellationToken);
        }
        finally
        {
            temporary.Delete(recursive: true);
        }
    }

    internal static void ValidateIdentity(string root, string publicKey)
    {
        var master = File.ReadAllText(Path.Combine(root, "Turian/Editor/Core/Packages/registry-mass4.pub"));
        if (Blob(publicKey).AsSpan().SequenceEqual(Blob(master)))
        {
            throw new InvalidDataException("The CI release identity must differ from the owner master key.");
        }

        var revoked = JsonSerializer.Deserialize<string[]>(
            File.ReadAllText(Path.Combine(root, "signing/revoked-keys.json")))
                      ?? throw new InvalidDataException("Missing revocation list.");
        if (revoked.Any(static value => !FingerprintPattern().IsMatch(value ?? "")))
        {
            throw new InvalidDataException("Invalid revoked fingerprint.");
        }

        if (revoked.Contains(Fingerprint(publicKey), StringComparer.Ordinal))
        {
            throw new InvalidDataException("CI identity is revoked.");
        }
    }

    internal static string Fingerprint(string publicKey) =>
        "SHA256:" + Convert.ToBase64String(SHA256.HashData(Blob(publicKey))).TrimEnd('=');

    static byte[] Blob(string publicKey)
    {
        var fields = publicKey.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2 || fields[0] != "ssh-ed25519")
        {
            throw new InvalidDataException("CI signing requires an Ed25519 OpenSSH public key.");
        }

        return Convert.FromBase64String(fields[1]);
    }

    [GeneratedRegex(@"\ASHA256:[A-Za-z0-9+/]{43}\z")]
    private static partial Regex FingerprintPattern();

    static async Task WriteKeyAsync(string path, string privateKey, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using var file = new FileStream(path, options);
        await file.WriteAsync(Encoding.UTF8.GetBytes(privateKey.TrimEnd() + "\n"), cancellationToken);
    }

    static async Task<string[]> SignMessagesAsync(Qualification qualification, string publicKey, string key,
        string temporary, CancellationToken cancellationToken)
    {
        var message = $"{qualification.PackageId}@{qualification.Version}\n{qualification.Integrity}\n";
        var allowed = Path.Combine(temporary, "allowed-signers");
        await File.WriteAllTextAsync(allowed, "mass4 " + publicKey.Trim() + "\n", cancellationToken);
        var signatures = new List<string>();
        foreach (var signingNamespace in new[] { "bricks@mass4.org", "brick-publisher@mass4.org" })
        {
            var payload = Path.Combine(temporary, $"message-{signatures.Count}");
            await File.WriteAllTextAsync(payload, message, new UTF8Encoding(false), cancellationToken);
            _ = await ToolProcess.RunAsync("ssh-keygen", ["-Y", "sign", "-f", key, "-n", signingNamespace, payload],
                cancellationToken);
            _ = await ToolProcess.RunAsync("ssh-keygen",
                ["-Y", "verify", "-f", allowed, "-I", "mass4", "-n", signingNamespace, "-s", payload + ".sig"],
                cancellationToken, message);
            var armored = await File.ReadAllLinesAsync(payload + ".sig", cancellationToken);
            signatures.Add(string.Concat(armored.Where(static line => !line.StartsWith("-----", StringComparison.Ordinal))
                .Select(static line => line.Trim())));
        }

        return [.. signatures];
    }
}
