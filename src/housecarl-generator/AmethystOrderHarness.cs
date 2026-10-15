namespace HousecarlGenerator;

/// <summary>Inspects an explicitly selected saved Amethyst order without scanning deployed Data.</summary>
internal static class AmethystOrderHarness
{
    /// <summary>Reads one schema-v1 connection through the current filegraph adapter, then builds the resolver.</summary>
    public static int Run(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: amethyst-order <absolute connection.json>; reads a saved snapshot with unverified freshness");
            return 2;
        }
        try
        {
            var snapshot = AmethystSavedSnapshot.Capture(args[0]);
            Console.WriteLine($"WARNING: {AmethystSnapshotProvenance.Warning}");
            Console.WriteLine($"profile: {snapshot.Provenance.Profile}; saved generation: {snapshot.Provenance.Generation}");
            var paths = snapshot.OrderedPluginPaths;
            if (paths.Count == 0)
            {
                Console.Error.WriteLine("error: no active plugin paths resolved from the selected saved profile");
                return 1;
            }
            foreach (var path in paths) Console.WriteLine(path);
            using var resolver = LoadOrderResolver.Build(paths);
            Console.WriteLine($"plugins={resolver.PluginCount}; records={resolver.RecordCount}; conflicts={resolver.ConflictCount}");
            foreach (var failure in resolver.LoadFailures) Console.Error.WriteLine(failure);
            return resolver.LoadFailures.Count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }
}
