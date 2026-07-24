using Mutagen.Bethesda.Plugins;

namespace HousecarlCore;

/// <summary>Identifies one of the two files associated with a spoken dialogue response.</summary>
public enum VoiceFile
{
    /// <summary>The FUZ audio container. Without it, the response is silent.</summary>
    Fuz,

    /// <summary>The LIP synchronization file. Without it, audio may play without mouth movement.</summary>
    Lip
}

/// <summary>
/// Derives canonical Data-relative voice paths from a dialogue INFO and its resolved context.
/// This pure transform performs no record resolution or filesystem access.
/// </summary>
/// <remarks>
/// Skyrim does not store an audio filename on the INFO response. It derives the name from the defining
/// plugin, voice type, truncated quest and topic EditorIDs, local INFO ID, and response number. Callers
/// must resolve the speaker's voice-type EditorID before using this helper.
/// </remarks>
public static class VoicePath
{
    /// <summary>Maximum number of quest EditorID characters retained in a voice filename.</summary>
    public const int QuestEdidMax = 10;

    /// <summary>Maximum number of topic EditorID characters retained in a voice filename.</summary>
    public const int TopicEdidMax = 15;

    /// <summary>Builds the canonical Bethesda path for one dialogue response file.</summary>
    /// <param name="info">
    /// INFO identity. Its ModKey supplies the defining-plugin directory and its local ID supplies the filename.
    /// </param>
    /// <param name="voiceType">
    /// Resolved speaker VoiceType EditorID used as the voice directory.
    /// This method does not validate it as a path segment.
    /// </param>
    /// <param name="questEdid">
    /// Parent quest EditorID, or null/empty when absent. At most <see cref="QuestEdidMax"/> characters are retained.
    /// </param>
    /// <param name="topicEdid">
    /// Parent topic EditorID, or null/empty when absent. At most <see cref="TopicEdidMax"/> characters are retained.
    /// </param>
    /// <param name="responseNumber">The response's stored ResponseNumber, not its position in a response list.</param>
    /// <param name="kind">Whether to return the audio-container or lip-sync path.</param>
    /// <returns>
    /// A backslash-separated Data-relative path whose INFO ID is rendered as eight uppercase hexadecimal digits.
    /// </returns>
    /// <remarks>
    /// Null or empty EditorIDs deliberately leave empty filename segments, producing the double-underscore shape used
    /// by real game data. An enum value other than <see cref="VoiceFile.Fuz"/> follows the LIP branch.
    /// </remarks>
    public static string For(FormKey info, string voiceType, string? questEdid, string? topicEdid,
                             int responseNumber, VoiceFile kind)
    {
        // The defining plugin and local FormID keep the filename independent of load-order position.
        var plugin = info.ModKey.FileName.ToString();
        var id = "00" + info.ID.ToString("X6");
        var q = Trunc(questEdid, QuestEdidMax);
        var t = Trunc(topicEdid, TopicEdidMax);
        var ext = kind == VoiceFile.Fuz ? "fuz" : "lip";
        return $@"Sound\Voice\{plugin}\{voiceType}\{q}_{t}_{id}_{responseNumber}.{ext}";
    }

    /// <summary>Truncates an optional EditorID without changing an absent segment into a value.</summary>
    /// <param name="s">EditorID text, or null/empty for an absent filename segment.</param>
    /// <param name="max">Maximum characters to retain. Callers provide a non-negative format constant.</param>
    /// <returns>
    /// Empty for null/empty input; otherwise the input or its first <paramref name="max"/> characters.
    /// </returns>
    static string Trunc(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max));
}
