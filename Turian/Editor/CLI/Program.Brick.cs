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

        return new Command("brick", "Manage a project's bricks (packages): add, remove, list, restore, update, embed; author, verify and pack your own")
        {
            BrickNewCommand(),
            BrickAddCommand(projectOption),
            BrickRemoveCommand(projectOption),
            BrickListCommand(projectOption),
            BrickRestoreCommand(projectOption),
            BrickUpdateCommand(projectOption),
            BrickEmbedCommand(projectOption),
            BrickCopyCommand(projectOption),
            BrickDiffCommand(projectOption),
            BrickRebaseCommand(projectOption),
            BrickStubCommand(),
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

        var command = new Command("new", "Create a brick folder with a manifest, an assembly definition and a script")
        {
            idArg, pathOption, nameOption, licenseOption,
        };
        command.SetAction(result => RunBrick(() =>
        {
            var root = BrickService.New(result.GetValue(pathOption)!.FullName, result.GetValue(idArg)!,
                result.GetValue(nameOption), result.GetValue(licenseOption)!);
            Console.WriteLine($"Created {root}");
        }));
        return command;
    }

    static Command BrickAddCommand(Option<DirectoryInfo> projectOption)
    {
        var idArg = new Argument<string>("id") { Description = "Brick id" };
        var sourceArg = new Argument<string?>("source")
        {
            Description = "builtin:<id>, file:<folder or .brick>, git+<url>[#ref] or a version range; builtin:<id> when omitted",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var command = new Command("add", "Install a brick into the project") { idArg, sourceArg, projectOption };
        command.SetAction(result => RunBrick(() =>
        {
            var id = result.GetValue(idArg)!;
            var resolution = BrickService.Add(result.GetValue(projectOption)!.FullName, id, result.GetValue(sourceArg));
            var brick = resolution.Packages.First(p => p.Id == id);
            Console.WriteLine($"Installed {brick.Id} {brick.Version} ({brick.Source})");
        }));
        return command;
    }

    static Command BrickRemoveCommand(Option<DirectoryInfo> projectOption)
    {
        var idArg = new Argument<string>("id") { Description = "Brick id" };

        var command = new Command("remove", "Remove a brick from the project's manifest") { idArg, projectOption };
        command.SetAction(result => RunBrick(() =>
        {
            var id = result.GetValue(idArg)!;
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

    static Command BrickListCommand(Option<DirectoryInfo> projectOption)
    {
        var command = new Command("list", "List the bricks the project resolves to") { projectOption };
        command.SetAction(result => RunBrick(() =>
        {
            foreach (var brick in BrickService.List(result.GetValue(projectOption)!.FullName))
            {
                var direct = brick.Depth == 1 ? "*" : " ";
                Console.WriteLine($"{direct} {brick.Id} {brick.Version} [{brick.Origin}] {brick.Source}");
            }
        }));
        return command;
    }

    static Command BrickRestoreCommand(Option<DirectoryInfo> projectOption)
    {
        var lockedOption = new Option<bool>("--locked")
        {
            Description = "Install exactly what the lock file records and fail when the manifest has drifted (CI)",
        };

        var command = new Command("restore", "Fetch every brick the project needs into the store") { projectOption, lockedOption };
        command.SetAction(result => RunBrick(() =>
        {
            var resolution = BrickService.Restore(result.GetValue(projectOption)!.FullName,
                result.GetValue(lockedOption) || Environment.GetEnvironmentVariable("CI") == "true");
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
