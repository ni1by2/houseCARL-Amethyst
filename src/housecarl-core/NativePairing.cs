using Mutagen.Bethesda.Pex;

namespace HousecarlCore;

/// <summary>Lists native Papyrus functions declared by one compiled script class.</summary>
/// <param name="ClassName">PEX object name, including any Papyrus namespace.</param>
/// <param name="NativeFunctions">Named state functions and property accessors marked native.</param>
public sealed record NativeClassDecl(string ClassName, IReadOnlyList<string> NativeFunctions);

/// <summary>Extracts the Papyrus declaration half of an SKSE native-function pairing.</summary>
/// <remarks>This does not prove that a DLL registers the declared functions at runtime.</remarks>
public static class NativePairing
{
    /// <summary>Raw PEX function flag bit that marks a native implementation.</summary>
    const uint NativeFlagBit = 0x2;

    /// <summary>Extracts classes that declare at least one native function or accessor.</summary>
    /// <param name="pex">Already parsed PEX model owned by the caller.</param>
    /// <returns>Native declarations in PEX object order.</returns>
    public static IReadOnlyList<NativeClassDecl> ExtractNativeClasses(PexFile pex)
    {
        var result = new List<NativeClassDecl>();
        foreach (var obj in pex.Objects)
        {
            var natives = new List<string>();
            foreach (var st in obj.States)
                foreach (var f in st.Functions.Cast<PexObjectNamedFunction>())
                    if (IsNative(f.Function)) natives.Add(f.FunctionName ?? "(unnamed)");
            // Property handlers are functions too and must participate in pairing.
            foreach (var p in obj.Properties)
            {
                if (p.ReadHandler is { } get && IsNative(get)) natives.Add($"{p.Name}.Get");
                if (p.WriteHandler is { } set && IsNative(set)) natives.Add($"{p.Name}.Set");
            }
            if (natives.Count > 0)
                result.Add(new NativeClassDecl(obj.Name ?? "(unnamed object)", natives));
        }
        return result;
    }

    /// <summary>Tests the raw native flag on one PEX function.</summary>
    /// <param name="f">Function to inspect.</param>
    /// <returns>True when the native bit is set.</returns>
    static bool IsNative(PexObjectFunction f) => ((uint)f.Flags & NativeFlagBit) != 0;
}
