using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace HousecarlMcp;

/// <summary>One new-patch transaction in staging; profile and deployment remain manager-owned.</summary>
internal sealed class AmethystStagingWrite : IDisposable
{
    /// <summary>The current MCP call's output permission; direct read callers cannot allocate patches.</summary>
    static readonly AsyncLocal<AmethystStagingWrite?> Active = new();
    /// <summary>The enclosing call, restored when this scope ends.</summary>
    readonly AmethystStagingWrite? _previous;
    /// <summary>Saved inputs whose stability is checked before reporting a staged patch.</summary>
    readonly AmethystSavedSnapshot _snapshot;
    /// <summary>New plugin paths registered before any output bytes are written.</summary>
    readonly List<string> _plugins = [];
    /// <summary>Whether the caller is inside the guarded new-patch lane.</summary>
    internal static bool IsActive => Active.Value is not null;

    /// <summary>Bind output permission to one captured manager generation.</summary>
    internal AmethystStagingWrite(AmethystSavedSnapshot snapshot)
    { _snapshot = snapshot; _previous = Active.Value; Active.Value = this; }

    /// <summary>Register only a new direct staging folder; rollback cannot delete existing mods.</summary>
    internal static string Register(string folder, string plugin)
    {
        if (Active.Value is not { } call) return "";
        if (Path.GetDirectoryName(Path.GetFullPath(folder)) != call._snapshot.ModsDir ||
            Path.GetFileName(plugin) != plugin || !plugin.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Amethyst new patches must be direct staging mods containing a new .esp");
        var path = Path.Combine(folder, plugin);
        var meta = Path.Combine(folder, "meta.ini");
        if (File.Exists(path) || File.Exists(meta))
            throw new InvalidOperationException("Amethyst patch output appeared during allocation; retry with a fresh patch name");
        call._snapshot.RegisterArtifact(path, null);
        call._snapshot.RegisterArtifact(meta, null);
        call._plugins.Add(path);
        return $"amethystProfile={call._snapshot.ProfileDir}\r\n" +
            $"amethystGeneration={call._snapshot.Provenance.Generation}\r\n" +
            "pendingDeployment=true\r\n";
    }

    /// <summary>Persist output hashes and describe staging success without claiming game-visible success.</summary>
    internal void Complete(CallToolResult result)
    {
        var outputs = new JsonArray();
        var verified = new Dictionary<string, LinuxFileIdentity>();
        foreach (var path in _plugins.Where(File.Exists))
        {
            var before = LinuxFileIdentity.Of(path);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            if (LinuxFileIdentity.Of(path) != before)
                throw new InvalidOperationException("Amethyst staged patch changed while verifying its output; retry");
            var meta = Path.Combine(Path.GetDirectoryName(path)!, "meta.ini");
            var text = File.ReadAllText(meta).Replace("pendingDeployment=true\r\n", $"pendingDeployment=true\r\npatchSha256={hash}\r\n", StringComparison.Ordinal);
            AtomicFile.WriteAllBytes(meta, System.Text.Encoding.UTF8.GetBytes(text));
            verified[path] = before;
            verified[meta] = LinuxFileIdentity.Of(meta);
            outputs.Add(new JsonObject { ["path"] = path, ["sha256"] = hash });
        }
        _snapshot.CheckUnchanged();
        foreach (var (path, stamp) in verified)
            if (LinuxFileIdentity.Of(path) != stamp)
                throw new InvalidOperationException("Amethyst staged output changed before the response; retry");
        if (outputs.Count == 0) return;
        var metadata = new JsonObject
        {
            ["state"] = "pending_deployment", ["profile"] = _snapshot.ProfileDir,
            ["outputs"] = outputs,
            ["next_step"] = "Refresh Amethyst, place and enable the patch, rebuild and deploy. Staging success does not confirm what is loaded in-game."
        };
        foreach (var block in result.Content.OfType<TextContentBlock>())
        {
            JsonObject? json = null;
            try { json = JsonNode.Parse(block.Text) as JsonObject; }
            catch (JsonException) { /* Text replies retain their normal format. */ }
            if (json is not null)
            { json["amethyst_staging"] = metadata.DeepClone(); block.Text = json.ToJsonString(); }
            else block.Text += "\n\nPatch written to Amethyst staging; pending deployment. " + metadata["next_step"]!.GetValue<string>();
        }
        var structured = result.StructuredContent is { ValueKind: JsonValueKind.Object } value
            ? JsonNode.Parse(value.GetRawText())!.AsObject() : new JsonObject();
        structured["amethyst_staging"] = metadata.DeepClone();
        result.StructuredContent = JsonSerializer.SerializeToElement(structured);
    }

    /// <summary>Remove only empty output directories after registered artifacts have been rolled back.</summary>
    internal void RemoveEmptyFolders()
    {
        foreach (var folder in _plugins.Select(p => Path.GetDirectoryName(p)!).Distinct())
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
    }

    /// <summary>Restore the enclosing call's permission; files are committed or rolled back explicitly.</summary>
    public void Dispose() => Active.Value = _previous;
}
