using Gaya.Packages;

namespace Turian.Editor.CLI;

public static partial class Program
{
    static Command BrickCommand()
    {
        var projectOption = new Option<DirectoryInfo>("--project")
        {
            Description = "Project folder",
            DefaultValueFactory = _ => new DirectoryInfo("./"),
        };

        var studioOption = new Option<bool>("--studio")
        {
            Description = "Manage the studio's own bricks (~/.gaya/studio/Bricks) instead of a project's",
        };

        return new Command("brick", "Manage a project's or the studio's bricks (packages): add, remove, list, restore, update, embed; author, verify and pack your own")
        {
            BrickNewCommand(),
            BrickAddCommand(projectOption, studioOption),
            BrickRemoveCommand(projectOption, studioOption),
            BrickListCommand(projectOption, studioOption),
            BrickRestoreCommand(projectOption, studioOption),
            BrickUpdateCommand(projectOption),
            BrickEmbedCommand(projectOption),
            BrickCopyCommand(projectOption),
            BrickDiffCommand(projectOption),
            BrickRebaseCommand(projectOption),
            BrickStubCommand(),
            BrickPublishCommand(),
            BrickYankCommand(),
            BrickSearchCommand(projectOption),
            BrickRegistryCommand(projectOption),
            BrickVerifyCommand(),
            BrickPackCommand(),
        };
    }

    static Command BrickNewCommand()
    {
        var idArg = new Argument<string>("id") { Description = "Brick id, lowercase reverse-DNS such as user.you.inventory" };
        var pathOption = new Option<DirectoryInfo>("--path")
        {
            Description = "Folder the brick folder is created in",
            DefaultValueFactory = _ => new DirectoryInfo("./"),
        };
        var nameOption = new Option<string?>("--display-name") { Description = "Name shown to users" };
        var licenseOption = new Option<string>("--license")
        {
            Description = "SPDX license expression",
            DefaultValueFactory = _ => "MIT",
        };

        var kindOption = new Option<string>("--kind")
        {
            Description = "code (an assembly definition and a script), or a content-only studio brick: theme, icon-theme, font-pack",
            DefaultValueFactory = _ => "code",
        };
        kindOption.AcceptOnlyFromAmong("code", "theme", "icon-theme", "font-pack");

        var command = new Command("new", "Create a brick folder: code with a manifest, an assembly definition and a script, or a content-only theme, icon theme or font pack")
        {
            idArg, pathOption, nameOption, licenseOption, kindOption,
        };
        command.SetAction(result => RunBrick(() =>
        {
            var parent = result.GetValue(pathOption)!.FullName;
            var id = result.GetValue(idArg)!;
            var root = result.GetValue(kindOption) switch
            {
                "theme" => ContentBricks.New(parent, id, ContentBrickKind.Theme, ProjectPackages.GayaVersion,
                    result.GetValue(nameOption), result.GetValue(licenseOption)!),
                "icon-theme" => ContentBricks.New(parent, id, ContentBrickKind.IconTheme, ProjectPackages.GayaVersion,
                    result.GetValue(nameOption), result.GetValue(licenseOption)!),
                "font-pack" => ContentBricks.New(parent, id, ContentBrickKind.FontPack, ProjectPackages.GayaVersion,
                    result.GetValue(nameOption), result.GetValue(licenseOption)!),
                _ => BrickService.New(parent, id, result.GetValue(nameOption), result.GetValue(licenseOption)!),
            };
            Console.WriteLine($"Created {root}");
        }));
        return command;
    }

    static Command BrickAddCommand(Option<DirectoryInfo> projectOption, Option<bool> studioOption)
    {
        var idArg = new Argument<string>("id") { Description = "Brick id" };
        var sourceArg = new Argument<string?>("source")
        {
            Description = "builtin:<id>, file:<folder or .brick>, git+<url>[#ref] or a version range; builtin:<id> when omitted",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var command = new Command("add", "Install a brick into the project, or into the studio with --studio")
        {
            idArg, sourceArg, projectOption, studioOption,
        };
        command.SetAction(result => RunBrick(() =>
        {
            var id = result.GetValue(idArg)!;
            if (result.GetValue(studioOption))
            {
                var studio = TurianStudioBricks.Workspace();
                studio.Add(id, result.GetValue(sourceArg));
                var added = studio.Resolve().First(p => p.Id == id);
                Console.WriteLine($"Installed {added.Id} {added.Version} ({added.Source}) into the studio");
                return;
            }

            var resolution = BrickService.Add(result.GetValue(projectOption)!.FullName, id, result.GetValue(sourceArg));
            var brick = resolution.Packages.First(p => p.Id == id);
            Console.WriteLine($"Installed {brick.Id} {brick.Version} ({brick.Source})");
        }));
        return command;
    }

    static Command BrickRemoveCommand(Option<DirectoryInfo> projectOption, Option<bool> studioOption)
    {
        var idArg = new Argument<string>("id") { Description = "Brick id" };

        var command = new Command("remove", "Remove a brick from the project's manifest, or the studio's with --studio")
        {
            idArg, projectOption, studioOption,
        };
        command.SetAction(result => RunBrick(() =>
        {
            var id = result.GetValue(idArg)!;
            if (result.GetValue(studioOption))
            {
                var studio = TurianStudioBricks.Workspace();
                if (!studio.Remove(id)) throw new PackageException($"The studio's manifest does not declare {id}.");
                studio.Restore();
                Console.WriteLine($"Removed {id} from the studio");
                return;
            }

            var project = result.GetValue(projectOption)!.FullName;
            if (!BrickService.Remove(project, id))
                throw new PackageException($"The project's manifest does not declare {id}.");

            _ = BrickService.Restore(project);
            Console.WriteLine($"Removed {id}");
            if (Directory.Exists(Path.Combine(project, ProjectManifest.DirectoryName, id)))
                Console.WriteLine($"Its embedded copy in {ProjectManifest.DirectoryName}/{id} remains; delete that folder to uninstall it.");
        }));
        return command;
    }

    static Command BrickListCommand(Option<DirectoryInfo> projectOption, Option<bool> studioOption)
    {
        var command = new Command("list", "List the bricks the project, or the studio with --studio, resolves to")
        {
            projectOption, studioOption,
        };
        command.SetAction(result => RunBrick(() =>
        {
            var bricks = result.GetValue(studioOption)
                ? TurianStudioBricks.Workspace().Resolve()
                : BrickService.List(result.GetValue(projectOption)!.FullName);
            foreach (var brick in bricks)
            {
                var direct = brick.Depth == 1 ? "*" : " ";
                Console.WriteLine($"{direct} {brick.Id} {brick.Version} [{brick.Origin}] {brick.Source}");
            }
        }));
        return command;
    }

    static Command BrickRestoreCommand(Option<DirectoryInfo> projectOption, Option<bool> studioOption)
    {
        var lockedOption = new Option<bool>("--locked")
        {
            Description = "Install exactly what the lock file records and fail when the manifest has drifted (CI)",
        };

        var command = new Command("restore", "Fetch every brick the project, or the studio with --studio, needs into the store")
        {
            projectOption, lockedOption, studioOption,
        };
        command.SetAction(result => RunBrick(() =>
        {
            var locked = result.GetValue(lockedOption) || Environment.GetEnvironmentVariable("CI") == "true";
            if (result.GetValue(studioOption))
            {
                var studio = TurianStudioBricks.Workspace().ResolveAll(locked);
                Console.WriteLine($"Restored {studio.Packages.Count} studio brick(s)");
                return;
            }

            var resolution = BrickService.Restore(result.GetValue(projectOption)!.FullName, locked);
            Console.WriteLine($"Restored {resolution.Packages.Count} brick(s)");
        }));
        return command;
    }

    static Command BrickUpdateCommand(Option<DirectoryInfo> projectOption)
    {
        var idsArg = new Argument<string[]>("ids")
        {
            Description = "Bricks to update; all of them when omitted",
            Arity = ArgumentArity.ZeroOrMore,
        };

        var command = new Command("update", "Move git bricks to their newest commit and rewrite the lock") { idsArg, projectOption };
        command.SetAction(result => RunBrick(() =>
        {
            var resolution = BrickService.Update(result.GetValue(projectOption)!.FullName, result.GetValue(idsArg));
            Console.WriteLine($"Updated; {resolution.Packages.Count} brick(s) resolved");
        }));
        return command;
    }

    static Command BrickEmbedCommand(Option<DirectoryInfo> projectOption)
    {
        var idArg = new Argument<string>("id") { Description = "Brick id" };

        var command = new Command("embed", "Copy an installed brick into the project as a writable fork") { idArg, projectOption };
        command.SetAction(result => RunBrick(() =>
            Console.WriteLine($"Embedded at {BrickService.Embed(result.GetValue(projectOption)!.FullName, result.GetValue(idArg)!)}")));
        return command;
    }

    static Command BrickCopyCommand(Option<DirectoryInfo> projectOption)
    {
        var idArg = new Argument<string>("id") { Description = "Brick id" };
        var assetsArg = new Argument<string[]>("assets")
        {
            Description = "Assets to copy, as paths inside the brick; all of its assets when omitted",
            Arity = ArgumentArity.ZeroOrMore,
        };
        var toOption = new Option<string>("--to")
        {
            Description = "Folder under Assets the copies go into; the brick's last id segment when omitted",
        };
        var remapOption = new Option<bool>("--remap")
        {
            Description = "Point the project's own files that use the originals at the copies",
        };

        var command = new Command("copy", "Copy a brick's assets into the project under new ids, detached from the brick")
        {
            idArg, assetsArg, toOption, remapOption, projectOption,
        };
        command.SetAction(result => RunBrick(() =>
        {
            var id = result.GetValue(idArg)!;
            var project = result.GetValue(projectOption)!.FullName;
            var assets = result.GetValue(assetsArg) is { Length: > 0 } chosen
                ? chosen
                : [.. BrickAssetCopy.Assets(BrickService.List(project).FirstOrDefault(p => p.Id == id)
                                            ?? throw new PackageException($"The project does not install {id}."))];
            foreach (var copy in BrickService.CopyAssets(project, id, assets, result.GetValue(toOption) ?? id.Split('.')[^1],
                         result.GetValue(remapOption)))
                Console.WriteLine($"{copy.Source} -> {copy.Target} ({copy.NewId})");
        }));
        return command;
    }

    static Command VariantCommand()
    {
        var baseArg = new Argument<string>("base")
        {
            Description = "The data asset to vary: a path in the project, or <brick id>:<path inside the brick>",
        };
        var nameArg = new Argument<string>("name") { Description = "The variant's file name without extension" };
        var folderOption = new Option<string>("--folder")
        {
            Description = "Folder, relative to the project, the variant is written into",
            DefaultValueFactory = _ => "Assets",
        };
        var projectOption = new Option<DirectoryInfo>("--project")
        {
            Description = "Project folder",
            DefaultValueFactory = _ => new DirectoryInfo("./"),
        };

        var command = new Command("variant", "Create a variant of a data asset: the same values with the ones you override, without forking the original")
        {
            baseArg, nameArg, folderOption, projectOption,
        };
        command.SetAction(result =>
        {
            try
            {
                var project = result.GetValue(projectOption)!.FullName;
                var spec = result.GetValue(baseArg)!;
                var colon = spec.IndexOf(':', StringComparison.Ordinal);
                var baseFile = colon > 0 && PackageId.IsValid(spec[..colon])
                    ? Path.Combine((BrickService.List(project).FirstOrDefault(p => p.Id == spec[..colon])
                                    ?? throw new PackageException($"The project does not install {spec[..colon]}.")).RootPath, spec[(colon + 1)..])
                    : Path.GetFullPath(spec, project);

                var (path, id) = DataAssetVariantFactory.Create(project, baseFile, result.GetValue(folderOption)!, result.GetValue(nameArg)!);
                Console.WriteLine($"{path} ({id}); put the values to change under {DataAssetVariants.Property}.Overrides");
                return 0;
            }
            catch (Exception ex) when (ex is PackageException or IOException or InvalidOperationException)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        });
        return command;
    }

    static Command BrickDiffCommand(Option<DirectoryInfo> projectOption)
    {
        var idArg = new Argument<string>("id") { Description = "Id of an embedded brick" };

        var command = new Command("diff", "Show what an embedded fork changed since it was copied") { idArg, projectOption };
        command.SetAction(result => RunBrick(() =>
        {
            var differences = BrickService.Diff(result.GetValue(projectOption)!.FullName, result.GetValue(idArg)!);
            foreach (var difference in differences)
                Console.WriteLine($"{difference.Change switch { ForkChange.Added => "A", ForkChange.Modified => "M", _ => "D" }} {difference.Path}");
            if (differences.Count == 0) Console.WriteLine("The fork matches its original");
        }));
        return command;
    }

    static Command BrickRebaseCommand(Option<DirectoryInfo> projectOption)
    {
        var idArg = new Argument<string>("id") { Description = "Id of an embedded brick" };
        var toOption = new Option<string?>("--to")
        {
            Description = "Source of the new release (git+<url>#<tag>, file:<.brick>); the declared source, fetched again, when omitted",
        };

        var command = new Command("rebase", "Merge a new release of an embedded fork's original into the fork") { idArg, toOption, projectOption };
        command.SetAction(result =>
        {
            try
            {
                var merged = BrickService.Rebase(result.GetValue(projectOption)!.FullName, result.GetValue(idArg)!, result.GetValue(toOption));
                foreach (var path in merged.Updated) Console.WriteLine($"updated  {path}");
                foreach (var path in merged.Added) Console.WriteLine($"added    {path}");
                foreach (var path in merged.Removed) Console.WriteLine($"removed  {path}");
                foreach (var path in merged.Merged) Console.WriteLine($"merged   {path}");
                foreach (var path in merged.Conflicts) Console.Error.WriteLine($"conflict {path}");
                if (merged.HasConflicts) Console.Error.WriteLine("Resolve the conflicts in the fork; the markers are <<<<<<< fork / ======= / >>>>>>> upstream.");
                return merged.HasConflicts ? 1 : 0;
            }
            catch (PackageException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        });
        return command;
    }

    static Command BrickStubCommand()
    {
        var pathArg = new Argument<DirectoryInfo>("path") { Description = "The real brick's folder" };
        var outOption = new Option<DirectoryInfo>("--out") { Description = "Folder to write the stub into", Required = true };

        var command = new Command("stub", "Write a stub of a brick: the same asset and type ids with placeholder content") { pathArg, outOption };
        command.SetAction(result => RunBrick(() =>
            Console.WriteLine($"Wrote stub {BrickStub.Write(result.GetValue(pathArg)!.FullName, result.GetValue(outOption)!.FullName)} to {result.GetValue(outOption)!.FullName}")));
        return command;
    }

    static Command BrickPublishCommand()
    {
        var pathArg = new Argument<DirectoryInfo>("path") { Description = "Brick folder", DefaultValueFactory = _ => new DirectoryInfo("./") };
        var registryOption = new Option<DirectoryInfo>("--registry") { Description = "The registry's root folder (what you upload to its host)", Required = true };
        var keyOption = new Option<FileInfo>("--key") { Description = "The registry's private key file (an OpenSSH ed25519 key)", Required = true };
        var publisherOption = new Option<FileInfo?>("--publisher-key") { Description = "Your own private key, which owns your names in the registry" };
        var claimOption = new Option<string?>("--claim") { Description = "A name prefix to claim with the publisher key, such as com.acme (user.<name> is claimed on first publish)" };
        var precastOption = new Option<bool>("--precast") { Description = "Compile the brick's assemblies into Precast~ first" };

        var command = new Command("publish", "Pack a brick and add it, signed, to a static registry folder") { pathArg, registryOption, keyOption, publisherOption, claimOption, precastOption };
        command.SetAction(async (result, _) => await RunBrickAsync(async () =>
        {
            var path = result.GetValue(pathArg)!.FullName;
            var precast = result.GetValue(precastOption) ? await BrickService.PrecastAsync(path, Log.Logger).ConfigureAwait(false) : null;
            var published = BrickService.Publish(path, result.GetValue(registryOption)!.FullName, result.GetValue(keyOption)!.FullName,
                result.GetValue(publisherOption)?.FullName, result.GetValue(claimOption), precast);
            Console.WriteLine($"Published {published.Id} {published.Version} as {published.Url}");
        }).ConfigureAwait(false));
        return command;
    }

    static Command BrickYankCommand()
    {
        var registryArg = new Argument<DirectoryInfo>("registry") { Description = "The registry's root folder" };
        var idArg = new Argument<string>("id") { Description = "Brick id" };
        var versionArg = new Argument<string>("version") { Description = "Version to withdraw" };

        var command = new Command("yank", "Withdraw a version from a registry: projects that locked it keep it, nobody new gets it") { registryArg, idArg, versionArg };
        command.SetAction(result => RunBrick(() =>
        {
            RegistryPublisher.Yank(result.GetValue(registryArg)!.FullName, result.GetValue(idArg)!, result.GetValue(versionArg)!);
            Console.WriteLine("Yanked");
        }));
        return command;
    }

    static Command BrickSearchCommand(Option<DirectoryInfo> projectOption)
    {
        var queryArg = new Argument<string>("query") { Description = "Text the brick id contains; everything when omitted", Arity = ArgumentArity.ZeroOrOne };

        var command = new Command("search", "Find bricks in the project's registries and the public one") { queryArg, projectOption };
        command.SetAction(async (result, _) => await RunBrickAsync(async () =>
        {
            foreach (var (registry, id, latest) in await BrickService.SearchAsync(result.GetValue(projectOption)!.FullName, result.GetValue(queryArg) ?? string.Empty).ConfigureAwait(false))
                Console.WriteLine($"{id} {latest}  [{registry}]");
        }).ConfigureAwait(false));
        return command;
    }

    static Command BrickRegistryCommand(Option<DirectoryInfo> projectOption)
    {
        var nameArg = new Argument<string>("name") { Description = "A name for the registry" };
        var urlArg = new Argument<string>("url") { Description = "The registry's https://…/v1 address, or the v1 folder of a copy" };
        var scopeOption = new Option<string[]>("--scope") { Description = "Name prefix served from this registry; repeat for several", Required = true, AllowMultipleArgumentsPerToken = true };
        var keyOption = new Option<string[]>("--key") { Description = "A trusted key: an OpenSSH public key line, or a .pub file; repeat for several", AllowMultipleArgumentsPerToken = true };
        var unsignedOption = new Option<bool>("--allow-unsigned") { Description = "Accept bricks without a signature (only for a registry you run)" };

        var add = new Command("add", "Take bricks whose names match --scope from a registry") { nameArg, urlArg, scopeOption, keyOption, unsignedOption, projectOption };
        add.SetAction(result => RunBrick(() =>
        {
            var keys = (result.GetValue(keyOption) ?? []).Select(static key => File.Exists(key) ? File.ReadAllText(key).Trim() : key).ToList();
            BrickService.AddRegistry(result.GetValue(projectOption)!.FullName, new ScopedRegistry
            {
                Name = result.GetValue(nameArg)!,
                Url = result.GetValue(urlArg)!,
                Scopes = [.. result.GetValue(scopeOption)!],
                Keys = keys,
                AllowUnsigned = result.GetValue(unsignedOption),
            });
            Console.WriteLine($"Added registry {result.GetValue(nameArg)}");
        }));

        var removeName = new Argument<string>("name") { Description = "The registry's name" };
        var remove = new Command("remove", "Stop taking bricks from a registry") { removeName, projectOption };
        remove.SetAction(result => RunBrick(() =>
        {
            if (!BrickService.RemoveRegistry(result.GetValue(projectOption)!.FullName, result.GetValue(removeName)!))
                throw new PackageException($"The project does not declare a registry named {result.GetValue(removeName)}.");
        }));

        var list = new Command("list", "List the registries bricks come from") { projectOption };
        list.SetAction(result => RunBrick(() =>
        {
            foreach (var registry in BrickService.Registries(result.GetValue(projectOption)!.FullName))
                Console.WriteLine($"{registry.Name}  {registry.Url}  scopes: {string.Join(", ", registry.Scopes)}  keys: {registry.Keys.Count}{(registry.AllowUnsigned ? "  (unsigned allowed)" : string.Empty)}");
        }));

        return new Command("registry", "Registries the project takes bricks from") { add, remove, list };
    }

    static Command BrickVerifyCommand()
    {
        var pathArg = new Argument<DirectoryInfo>("path")
        {
            Description = "Brick folder",
            DefaultValueFactory = _ => new DirectoryInfo("./"),
        };

        var againstOption = new Option<string?>("--against")
        {
            Description = "Also check that the brick exposes every asset id and type id of this brick (a folder or .brick file), as a stub must",
        };

        var command = new Command("verify", "Check a brick's manifest and that every asset ships a unique .meta") { pathArg, againstOption };
        command.SetAction(result =>
        {
            var path = result.GetValue(pathArg)!.FullName;
            var issues = result.GetValue(againstOption) is { } against ? BrickVerifier.VerifyAgainst(path, against) : BrickVerifier.Verify(path);
            foreach (var issue in issues) Console.Error.WriteLine(issue);
            if (issues.Count == 0) Console.WriteLine("Brick is sound");
            return issues.Count == 0 ? 0 : 1;
        });
        return command;
    }

    static Command BrickPackCommand()
    {
        var pathArg = new Argument<DirectoryInfo>("path")
        {
            Description = "Brick folder",
            DefaultValueFactory = _ => new DirectoryInfo("./"),
        };
        var outOption = new Option<DirectoryInfo>("--out")
        {
            Description = "Folder the .brick and its .sha256 are written to",
            DefaultValueFactory = _ => new DirectoryInfo("./.bricks"),
        };

        var precastOption = new Option<bool>("--precast")
        {
            Description = "Compile the brick's assemblies into Precast~ first, so consumers load them instead of compiling the code",
        };

        var command = new Command("pack", "Verify a brick and pack it into a .brick file") { pathArg, outOption, precastOption };
        command.SetAction(async (result, _) => await RunBrickAsync(async () =>
        {
            var path = result.GetValue(pathArg)!.FullName;
            var precast = result.GetValue(precastOption) ? await BrickService.PrecastAsync(path, Log.Logger).ConfigureAwait(false) : null;
            var packed = BrickService.Pack(path, result.GetValue(outOption)!.FullName, precast);
            Console.WriteLine($"{packed.Path}\n{packed.Integrity}");
        }).ConfigureAwait(false));
        return command;
    }

    static async Task<int> RunBrickAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return 0;
        }
        catch (PackageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>Runs a brick command, reporting a package problem as an error line and exit code 1.</summary>
    static int RunBrick(Action action)
    {
        try
        {
            action();
            return 0;
        }
        catch (PackageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
