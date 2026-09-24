namespace Turian.Editor.CLI;

public static partial class Program
{
    static Command OapCommand(Argument<FileSystemInfo> projectArg) =>
        new("oap", "Open Asset Package tools: pack, unpack, list, inspect, validate")
        {
            OapPackCommand(projectArg),
            OapUnpackCommand(),
            OapListCommand(),
            OapInspectCommand(),
            OapValidateCommand()
        };

    static Command OapPackCommand(Argument<FileSystemInfo> projectArg)
    {
        var outputArg = new Argument<string>("Output") { Description = "Destination .oap file path" };
        var nameOption = new Option<string?>("--name") { Description = "Package name written into the manifest" };
        var storeOption = new Option<bool>("--store") { Description = "Disable compression; store every asset verbatim" };
        var keyOption = new Option<string?>("--key")
        {
            Description = "Encrypt every asset with ChaCha20 using this passphrase (never stored in the package)"
        };
        var targetOption = new Option<string?>("--target")
        {
            Description = "For per-target assets (textures), pack this build target's artifact, e.g. 'pc'"
        };
        var requiresOption = new Option<string[]>("--requires")
        {
            Description = "Name of another package this one requires; repeat for several",
            AllowMultipleArgumentsPerToken = true
        };

        var cmd = new Command("pack", "Pack a project's imported assets into a .oap package")
        {
            projectArg, outputArg, nameOption, storeOption, keyOption, targetOption, requiresOption
        };

        cmd.SetAction(async (result, _) =>
        {
            var settings = CreateSettings(result.GetValue(projectArg)!);
            var output = Path.GetFullPath(result.GetValue(outputArg)!);

            await PrepareProjectAsync(settings, reimport: true, loadUserCode: false).ConfigureAwait(false);

            var options = new OapPackOptions
            {
                Name = result.GetValue(nameOption),
                Compression = result.GetValue(storeOption)
                    ? OapCompressChoice.Fixed(OapCompression.Store)
                    : OapCompressChoice.Auto,
                Passphrase = result.GetValue(keyOption),
                Target = result.GetValue(targetOption)
            };

            foreach (var required in result.GetValue(requiresOption) ?? [])
            {
                options.Requires.Add(required);
            }

            var res = new OapArchiveBuilder(Log.Logger).BuildPackage(settings.ProjectAbsoluteDir, output, options);
            Log.Logger.LogInformation("Packed {Count} asset(s) into {Path}", res.EntryCount, res.OapFilePath);
            return 0;
        });

        return cmd;
    }

    static Command OapUnpackCommand()
    {
        var packageArg = new Argument<string>("Package") { Description = ".oap file to extract" };
        var outDirArg = new Argument<string>("OutputDir") { Description = "Directory to extract into" };
        var keyOption = new Option<string?>("--key") { Description = "Decryption passphrase for an encrypted package" };

        var cmd = new Command("unpack", "Extract every asset from a .oap package, recreating virtual paths")
        {
            packageArg, outDirArg, keyOption
        };

        cmd.SetAction(result =>
        {
            var reader = OapReader.OpenFile(Path.GetFullPath(result.GetValue(packageArg)!));
            var passphrase = result.GetValue(keyOption);
            if (!string.IsNullOrEmpty(passphrase))
            {
                reader.SetKey(OapCrypto.DeriveKey(passphrase));
            }

            var outDir = Path.GetFullPath(result.GetValue(outDirArg)!);
            for (var i = 0; i < reader.Count; i++)
            {
                var entry = reader.EntryAt(i);
                var relative = SafeRelativePath(reader.VirtualPath(entry), entry.AssetId);
                var destination = Path.GetFullPath(Path.Combine(outDir, relative));
                if (!destination.StartsWith(outDir, StringComparison.Ordinal))
                {
                    Log.Logger.LogWarning("Skipping entry with an unsafe virtual path: {Path}", reader.VirtualPath(entry));
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, reader.ReadAsset(entry, verify: true));
            }

            Log.Logger.LogInformation("Unpacked {Count} asset(s) into {Path}", reader.Count, outDir);
            return 0;
        });

        return cmd;
    }

    static Command OapListCommand()
    {
        var packageArg = new Argument<string>("Package") { Description = ".oap file to list" };
        var cmd = new Command("list", "Print one line per asset: id, type, codecs, sizes and virtual path")
        {
            packageArg
        };

        cmd.SetAction(result =>
        {
            var reader = OapReader.OpenFile(Path.GetFullPath(result.GetValue(packageArg)!));
            Log.Logger.LogInformation("{Count} asset(s):", reader.Count);
            for (var i = 0; i < reader.Count; i++)
            {
                var entry = reader.EntryAt(i);
                Log.Logger.LogInformation(
                    "  {Id}  type={Type,-8} {Compression,-8} {Encryption,-9} {Uncompressed,10} -> {Stored,-10}  {Path}",
                    entry.AssetId.ToString("D"),
                    (OapAssetType)entry.AssetType,
                    entry.Compression,
                    entry.Encryption,
                    entry.UncompressedSize,
                    entry.StoredSize,
                    reader.VirtualPath(entry));
            }

            return 0;
        });

        return cmd;
    }

    static Command OapInspectCommand()
    {
        var packageArg = new Argument<string>("Package") { Description = ".oap file to inspect" };
        var cmd = new Command("inspect", "Print the header summary and manifest") { packageArg };

        cmd.SetAction(result =>
        {
            var path = Path.GetFullPath(result.GetValue(packageArg)!);
            var reader = OapReader.OpenFile(path);
            var header = reader.Header;

            Log.Logger.LogInformation("OAP package : {Path}", path);
            Log.Logger.LogInformation("  format    : {Major}.{Minor}", OapFormat.FormatMajor, OapFormat.FormatMinor);
            Log.Logger.LogInformation("  assets    : {Count}", header.EntryCount);
            Log.Logger.LogInformation("  flags     : {Flags}", header.Flags);
            Log.Logger.LogInformation("  index     : offset {Offset}, {Size} bytes", header.IndexOffset, header.IndexSize);
            Log.Logger.LogInformation(
                "  strings   : offset {Offset}, {Size} bytes",
                header.StringTableOffset,
                header.StringTableSize);

            var manifest = reader.Manifest;
            Log.Logger.LogInformation(
                "  manifest  : {Manifest}",
                manifest is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes.Span) : "(none)");
            return 0;
        });

        return cmd;
    }

    static Command OapValidateCommand()
    {
        var packageArg = new Argument<string>("Package") { Description = ".oap file to validate" };
        var keyOption = new Option<string?>("--key") { Description = "Decryption passphrase for an encrypted package" };
        var cmd = new Command("validate", "Verify the header and every asset CRC-32; exit 1 on any failure")
        {
            packageArg, keyOption
        };

        cmd.SetAction(result =>
        {
            var path = Path.GetFullPath(result.GetValue(packageArg)!);
            OapReader reader;
            try
            {
                reader = OapReader.OpenFile(path);
            }
            catch (Exception ex) when (ex is OapException or IOException)
            {
                Log.Logger.LogError("INVALID: {Path}: {Message}", path, ex.Message);
                return 1;
            }

            var passphrase = result.GetValue(keyOption);
            if (!string.IsNullOrEmpty(passphrase))
            {
                reader.SetKey(OapCrypto.DeriveKey(passphrase));
            }

            var failures = 0;
            for (var i = 0; i < reader.Count; i++)
            {
                var entry = reader.EntryAt(i);
                try
                {
                    _ = reader.ReadAsset(entry, verify: true);
                }
                catch (OapException ex)
                {
                    Log.Logger.LogError("  CORRUPT: {Path} ({Message})", reader.VirtualPath(entry), ex.Message);
                    failures++;
                }
            }

            if (failures == 0)
            {
                Log.Logger.LogInformation("OK: {Path} — {Count} asset(s) verified", path, reader.Count);
                return 0;
            }

            Log.Logger.LogError("FAILED: {Path} — {Failures}/{Count} asset(s) corrupt", path, failures, reader.Count);
            return 1;
        });

        return cmd;
    }

}
