namespace MASS4.Bricks;

/// <summary>Reports every brick a resolution could not find in one message, with the registries it asked.</summary>
public sealed partial class PackageResolver
{
    sealed partial class Session
    {
        /// <summary>Id → why the registries scoped to it could not give it.</summary>
        readonly Dictionary<string, string> unresolved = new(StringComparer.Ordinal);

        /// <summary>Registry name → why its index could not be read.</summary>
        readonly Dictionary<string, string> unreadable = new(StringComparer.Ordinal);

        /// <summary>
        /// Throws one <see cref="PackageException"/> naming every brick that is still missing: those no registry could
        /// give and those no configured registry serves, with the state of each registry and what to do next.
        /// </summary>
        /// <returns>A task that completes when nothing is missing.</returns>
        /// <exception cref="PackageException">A brick is missing.</exception>
        public async Task ThrowIfUnresolvedAsync()
        {
            var problems = unresolved.Values.ToList();
            problems.AddRange(Constraints
                .Where(c => !Resolved.ContainsKey(c.Id) && !Twins.ContainsKey(c.Id) && !unresolved.ContainsKey(c.Id))
                .GroupBy(static c => c.Id)
                .Select(static g => $"{g.First().RequiredBy} requires {g.Key} {g.First().Range}, which nothing provides and no configured registry serves."));
            if (problems.Count == 0)
            {
                return;
            }

            // Only now, as a report: a resolution that succeeds offline from the lock never reads an index.
            foreach (var registry in registries)
            {
                _ = await IndexAsync(registry).ConfigureAwait(false);
            }

            var lines = problems.Count == 1
                ? [problems[0]]
                : new List<string> { $"{problems.Count} bricks could not be resolved:" }.Concat(problems.Select(static p => "- " + p)).ToList();
            lines.Add(RegistriesLine());

            // A brick a registry has but refuses (an untrusted key, no fitting version) says itself what to do.
            if (Constraints.Select(static c => c.Id).Distinct().Any(id => !Resolved.ContainsKey(id) && !Twins.ContainsKey(id) && NoRegistryHas(id)))
            {
                lines.Add($"Add a registry that has them to \"scopedRegistries\" in {ProjectManifest.DirectoryName}/{ProjectManifest.FileName}, "
                          + "or declare where each comes from: a folder, a .brick file or a git URL.");
            }

            throw new PackageException(string.Join(Environment.NewLine, lines)) { Problems = problems };
        }

        /// <summary>Whether no registry scoped to the brick lists it in an index that could be read.</summary>
        bool NoRegistryHas(string id) =>
            !registries.Any(r => r.Serves(id) && indexes.GetValueOrDefault(r.Name)?.Bricks.ContainsKey(id) == true);

        /// <summary>The registries the resolution asked, each with how many bricks it lists or why it could not be read.</summary>
        string RegistriesLine()
        {
            if (registries.Count == 0)
            {
                return "No registry is configured.";
            }

            var states = registries.Select(registry => unreadable.TryGetValue(registry.Name, out var reason)
                ? $"{registry.Name} could not be read ({reason.TrimEnd('.')})"
                : $"{registry.Name} lists {indexes.GetValueOrDefault(registry.Name)?.Bricks.Count ?? 0} brick(s)");
            return $"Registries: {string.Join("; ", states)}.";
        }
    }
}
