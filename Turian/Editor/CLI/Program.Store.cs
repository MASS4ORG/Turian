using System.Text;
using System.Threading;
using MASS4.Bricks;

namespace Turian.Editor.CLI;

public static partial class Program
{
    static Argument<string> StoreArgument() =>
        new("store") { Description = "The asset store, such as store.mass4.org or http://store.mass4.local:5080" };

    static Command BrickLoginCommand()
    {
        var storeArg = StoreArgument();
        var tokenOption = new Option<string?>("--token")
        {
            Description = "An access token created on the store's /manage page, saved as it is; for CI, where no one types a password",
        };
        var emailOption = new Option<string?>("--email") { Description = "Your store account's email; asked for when omitted" };
        var prefixOption = new Option<string[]>("--prefix")
        {
            Description = "A brick name prefix your account owns, such as org.mass4, repeat for several; the store's claimed prefixes when omitted",
        };
        var daysOption = new Option<int>("--days") { Description = "How many days the token lasts", DefaultValueFactory = _ => 30 };
        var readOnlyOption = new Option<bool>("--read-only") { Description = "Only download private bricks; the token cannot publish or change releases" };

        var command = new Command("login", "Sign in to an asset store and save an access token in ~/.gaya/credentials.json, for publishing and private bricks")
        {
            storeArg, tokenOption, emailOption, prefixOption, daysOption, readOnlyOption,
        };
        command.SetAction(async (result, cancellationToken) => await RunBrickAsync(async () =>
        {
            var store = new StoreClient(result.GetValue(storeArg)!);
            var host = store.Origin.Host;
            var token = result.GetValue(tokenOption);
            if (string.IsNullOrWhiteSpace(token))
            {
                var email = result.GetValue(emailOption) ?? Ask($"Email for {host}: ");
                var password = ReadSecret($"Password for {email}: ");
                var prefixes = result.GetValue(prefixOption) is { Length: > 0 } given
                    ? given
                    : await ClaimedPrefixesAsync(store, cancellationToken).ConfigureAwait(false);
                var issued = await store.LoginAsync(email, password, prefixes, $"Turian CLI on {Environment.MachineName}",
                    !result.GetValue(readOnlyOption), result.GetValue(daysOption), cancellationToken).ConfigureAwait(false);
                token = issued.Secret;
                Console.WriteLine($"Signed in to {host} for {string.Join(", ", prefixes)}; the token expires {issued.ExpiresUtc:yyyy-MM-dd}");
            }

            RegistryClient.SaveToken(host, token);
            Console.WriteLine($"Saved the token for {host} in {RegistryClient.CredentialsPath}");
        }).ConfigureAwait(false));
        return command;
    }

    /// <summary>The name prefixes the store's index records owners for, which a sign-in without --prefix asks for.</summary>
    static async Task<string[]> ClaimedPrefixesAsync(StoreClient store, CancellationToken cancellationToken)
    {
        var registry = await RegistryDiscovery.DiscoverAsync(store.Origin.ToString().TrimEnd('/'), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return registry.Scopes.Count > 0
            ? [.. registry.Scopes]
            : throw new PackageException($"{store.Origin.Host} does not list its prefixes yet; give the ones your account owns with --prefix, such as --prefix com.acme.");
    }

    /// <summary>A line from the user, prompted on standard error so standard output stays clean.</summary>
    static string Ask(string prompt)
    {
        Console.Error.Write(prompt);
        var answer = Console.ReadLine();
        return string.IsNullOrWhiteSpace(answer) ? throw new PackageException("Nothing was entered.") : answer.Trim();
    }

    /// <summary>A secret typed without echo; read as a plain line when input is redirected, as in a script.</summary>
    static string ReadSecret(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            return Ask(prompt);
        }

        Console.Error.Write(prompt);
        var secret = new StringBuilder();
        for (var key = Console.ReadKey(intercept: true); key.Key != ConsoleKey.Enter; key = Console.ReadKey(intercept: true))
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                secret.Length = Math.Max(0, secret.Length - 1);
            }
            else if (!char.IsControl(key.KeyChar))
            {
                _ = secret.Append(key.KeyChar);
            }
        }

        Console.Error.WriteLine();
        return secret.Length > 0 ? secret.ToString() : throw new PackageException("Nothing was entered.");
    }

    static Command BrickReleasesCommand()
    {
        var storeArg = StoreArgument();
        var command = new Command("releases", "List the releases your access token may manage on an asset store") { storeArg };
        command.SetAction(async (result, cancellationToken) => await RunBrickAsync(async () =>
        {
            var releases = await new StoreClient(result.GetValue(storeArg)!).ReleasesAsync(cancellationToken).ConfigureAwait(false);
            foreach (var release in releases)
            {
                Console.WriteLine($"{release.PackageId} {release.Version}{ReleaseState(release)}");
            }

            if (releases.Count == 0)
            {
                Console.WriteLine("none");
            }
        }).ConfigureAwait(false));
        return command;
    }

    static Command BrickDeprecateCommand()
    {
        var storeArg = StoreArgument();
        var idArg = new Argument<string>("id") { Description = "Brick id" };
        var versionArg = new Argument<string>("version") { Description = "Version to deprecate" };
        var reasonOption = new Option<string>("--reason")
        {
            Description = "What consumers should do instead; an empty reason clears the deprecation",
            Required = true,
        };

        var command = new Command("deprecate", "Mark a released version deprecated on an asset store, saying what to use instead; nothing is removed")
        {
            storeArg, idArg, versionArg, reasonOption,
        };
        command.SetAction(async (result, cancellationToken) => await RunBrickAsync(async () =>
        {
            var reason = result.GetValue(reasonOption)!;
            await new StoreClient(result.GetValue(storeArg)!).SetStateAsync(result.GetValue(idArg)!, result.GetValue(versionArg)!,
                new StoreReleaseState(DeprecationReason: reason), cancellationToken).ConfigureAwait(false);
            Console.WriteLine(reason.Length == 0 ? "Deprecation cleared" : "Deprecated");
        }).ConfigureAwait(false));
        return command;
    }

    static Command BrickWhoamiCommand()
    {
        var storeArg = StoreArgument();
        var command = new Command("whoami", "Show which account the saved access token acts for on an asset store, and the prefixes it may publish") { storeArg };
        command.SetAction(async (result, cancellationToken) => await RunBrickAsync(async () =>
        {
            var store = new StoreClient(result.GetValue(storeArg)!);
            var account = await store.AccountAsync(cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"{account.Email} on {store.Origin.Host}");
            Console.WriteLine(account.Prefixes.Count == 0 ? "prefixes: none" : $"prefixes: {string.Join(", ", account.Prefixes)}");
        }).ConfigureAwait(false));
        return command;
    }

    static Command BrickUploadCommand()
    {
        var storeArg = StoreArgument();
        var fileArg = new Argument<FileInfo>("file") { Description = "The packed .brick that was signed" };
        var releaseOption = new Option<FileInfo>("--release")
        {
            Description = "The signed release JSON made with it on the signing workstation",
            Required = true,
        };

        var command = new Command("upload", "Upload a brick signed elsewhere, with its signed release JSON, to an asset store; no key is needed here")
        {
            storeArg, fileArg, releaseOption,
        };
        command.SetAction(async (result, cancellationToken) => await RunBrickAsync(async () =>
        {
            var store = new StoreClient(result.GetValue(storeArg)!);
            var release = await store.PublishSignedAsync(result.GetValue(fileArg)!.FullName, result.GetValue(releaseOption)!.FullName,
                ["gaya", ProjectPackages.HostName], cancellationToken).ConfigureAwait(false);
            ReportPublished(store, release);
        }).ConfigureAwait(false));
        return command;
    }

    /// <summary>Tells whether a release the store received is offered to consumers yet.</summary>
    static void ReportPublished(StoreClient store, StoreRelease release)
    {
        Console.WriteLine($"Published {release.PackageId} {release.Version} to {store.Origin.Host}{ReleaseState(release)}");
        if (!release.Approved)
        {
            Console.WriteLine("It waits for the store's approval and is not offered to consumers until then.");
        }
    }

    /// <summary>Uploads a brick to a store and reports whether consumers can see it yet.</summary>
    static async Task PublishToStoreAsync(string path, string address, string key, string? publisherKey, bool isPrivate,
        PackagePrecast? precast, bool twin, CancellationToken cancellationToken)
    {
        var store = new StoreClient(address);
        foreach (var release in await BrickStorePublishing.PublishAsync(path, store, key, publisherKey, isPrivate,
                     precast: precast, twin: twin, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            ReportPublished(store, release);
        }
    }

    /// <summary>Yanks a version on a store, or puts it back.</summary>
    static Task YankOnStoreAsync(string address, string id, string version, bool undo, CancellationToken cancellationToken) =>
        new StoreClient(address).SetStateAsync(id, version, new StoreReleaseState(Yanked: !undo), cancellationToken);

    static string ReleaseState(StoreRelease release)
    {
        string[] states =
        [
            .. release.Approved ? Array.Empty<string>() : ["awaiting approval"],
            .. release.Yanked ? ["yanked"] : Array.Empty<string>(),
            .. release.Quarantined ? ["quarantined"] : Array.Empty<string>(),
            .. release.IsPrivate ? ["private"] : Array.Empty<string>(),
            .. release.DeprecationReason is { Length: > 0 } reason ? [$"deprecated: {reason}"] : Array.Empty<string>(),
        ];
        return states.Length == 0 ? string.Empty : $"  ({string.Join(", ", states)})";
    }
}
