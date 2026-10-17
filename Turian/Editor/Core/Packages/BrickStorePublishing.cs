using Gaya.Packages;

namespace Turian.Editor.Core;

/// <summary>Packs a brick and uploads it to an asset store, signed with the registry's and the publisher's keys.</summary>
public static class BrickStorePublishing
{
    /// <summary>
    /// Packs the brick (after <see cref="BrickService.Pack"/>'s checks) and uploads it. The publisher key defaults to the
    /// registry key, which is how a registry owner publishes its own names.
    /// </summary>
    /// <param name="packageRoot">The brick folder.</param>
    /// <param name="store">The store to upload to.</param>
    /// <param name="registryKeyFile">The private key the store trusts to sign releases.</param>
    /// <param name="publisherKeyFile">The private key owning the brick's name prefix; the registry key when null.</param>
    /// <param name="isPrivate">Whether only readers with access to the publisher's bricks can download it.</param>
    /// <param name="sourceRevision">The source revision to record; the CI commit when null and one is known.</param>
    /// <param name="precast">What the brick's <c>Precast~</c> payload was built with.</param>
    /// <param name="cancellationToken">Cancels the upload.</param>
    /// <returns>The release the store holds.</returns>
    /// <exception cref="PackageException">The brick is invalid, or the store refused it.</exception>
    public static async Task<StoreRelease> PublishAsync(string packageRoot, StoreClient store, string registryKeyFile,
        string? publisherKeyFile = null, bool isPrivate = false, string? sourceRevision = null,
        PackagePrecast? precast = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var scratch = Path.Combine(Path.GetTempPath(), $"turian-publish-{Guid.NewGuid():N}");
        try
        {
            var packed = BrickService.Pack(packageRoot, scratch, precast);
            return await store.PublishAsync(packed.Path, new SshKeygenSigner(registryKeyFile),
                new SshKeygenSigner(publisherKeyFile ?? registryKeyFile), isPrivate, sourceRevision ?? CiRevision(),
                ["gaya", ProjectPackages.HostName], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    /// <summary>The commit a GitHub or GitLab pipeline builds, or null outside CI.</summary>
    static string? CiRevision() =>
        new[] { "GITHUB_SHA", "CI_COMMIT_SHA" }.Select(Environment.GetEnvironmentVariable)
            .FirstOrDefault(static v => !string.IsNullOrEmpty(v));
}
