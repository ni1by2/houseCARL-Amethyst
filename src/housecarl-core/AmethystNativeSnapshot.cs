using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HousecarlCore;

/// <summary>Ask Amethyst's native reader for winners using a private backup of the pinned SQLite transaction.</summary>
internal static class AmethystNativeSnapshot
{
    /// <summary>Read all native winner pages; producer initialization and cache writes affect only the disposable copy.</summary>
    /// <param name="database">Existing read-only catalog connection inside the capture transaction.</param>
    /// <param name="profile">Canonical identity stored in the captured profile intent.</param>
    /// <param name="generation">Expected saved profile generation.</param>
    /// <param name="inventory">Expected catalog inventory generation.</param>
    /// <param name="accept">Validate and retain one native winner against the captured candidates.</param>
    internal static bool Read(SqliteConnection database, string profile, long generation, ulong inventory, string deployMode, Action<JsonElement> accept)
    {
        var extension = Environment.GetEnvironmentVariable("AMETHYST_FILEGRAPH_EXTENSION")
            ?? Path.Combine(AppContext.BaseDirectory, "amethyst_filegraph.abi3.so");
        if (!Path.IsPathFullyQualified(extension) || !File.Exists(extension))
            throw new InvalidOperationException("Amethyst's native filegraph component is missing; install the complete Linux preview package");
        var root = Path.Combine(Path.GetTempPath(), "hc-amethyst-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(root, "filegraph.sqlite3"), Pooling = false }.ToString()))
            {
                copy.Open();
                database.BackupDatabase(copy);
            }
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("AMETHYST_FILEGRAPH_PYTHON") ?? "python3")
            { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "-I", Path.Combine(AppContext.BaseDirectory, "amethyst_snapshot.py"), root, profile, extension, deployMode })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("cannot start Amethyst's snapshot reader");
            var errors = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                using var header = JsonDocument.Parse(Frame(process.StandardOutput.BaseStream, deadline.Token));
                var metadata = header.RootElement;
                if (metadata.GetProperty("api").GetInt32() != 13 || metadata.GetProperty("schema").GetInt32() != 9 ||
                    metadata.GetProperty("generation").GetInt64() != generation || metadata.GetProperty("inventory_generation").GetUInt64() != inventory)
                    throw new InvalidOperationException("Amethyst's native snapshot disagrees with the captured catalog generation");
                long cursor = 0;
                while (true)
                {
                    process.StandardInput.WriteLine(cursor);
                    process.StandardInput.Flush();
                    using var page = AmethystIntentPayload.Read(Frame(process.StandardOutput.BaseStream, deadline.Token));
                    if (page.RootElement.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException("Amethyst's native winner page has an unsupported shape");
                    if (page.RootElement.GetArrayLength() == 0) break;
                    foreach (var winner in page.RootElement.EnumerateArray())
                    {
                        var id = winner.GetProperty("candidate_id").GetInt64();
                        if (id <= cursor) throw new InvalidOperationException("Amethyst's native winner cursor did not advance");
                        accept(winner);
                        cursor = id;
                    }
                }
                process.StandardInput.WriteLine("deployment");
                process.StandardInput.Flush();
                using var deployment = JsonDocument.Parse(Frame(process.StandardOutput.BaseStream, deadline.Token));
                if (deployment.RootElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidOperationException("Amethyst's native committed-deployment check has an unsupported shape");
                process.StandardInput.Close();
                process.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult();
                if (process.ExitCode != 0) throw new InvalidOperationException("Amethyst's native snapshot reader failed: " + errors.GetAwaiter().GetResult().Trim());
                return deployment.RootElement.GetBoolean();
            }
            catch (Exception ex) when (ex is EndOfStreamException or OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.WaitForExit();
                var detail = errors.GetAwaiter().GetResult().Trim();
                throw new InvalidOperationException("cannot read Amethyst's native snapshot: " + (detail.Length > 0 ? detail : ex.Message), ex);
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
            }
        }
        catch (Win32Exception ex)
        { throw new InvalidOperationException("Amethyst snapshot reads require Python 3.12 or newer; install Python or set AMETHYST_FILEGRAPH_PYTHON", ex); }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Read one bounded page or metadata frame; an incomplete frame refuses instead of publishing partial winners.</summary>
    static byte[] Frame(Stream stream, CancellationToken cancellation)
    {
        var header = new byte[4];
        stream.ReadExactlyAsync(header, cancellation).AsTask().GetAwaiter().GetResult();
        var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (size == 0 || size > 64 * 1024 * 1024) throw new InvalidOperationException("Amethyst's native snapshot page exceeds the supported frame size");
        var payload = new byte[checked((int)size)];
        stream.ReadExactlyAsync(payload, cancellation).AsTask().GetAwaiter().GetResult();
        return payload;
    }
}
