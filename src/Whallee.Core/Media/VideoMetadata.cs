namespace Whallee.Core.Media;

/// <summary>What the metadata probe returns. Duration is required; everything else may be absent.</summary>
public sealed record VideoMetadata(
    string Title,
    TimeSpan Duration,
    string? ThumbnailUrl,
    string? License)
{
    /// <summary>
    /// Whether YouTube reports a Creative Commons licence.
    /// </summary>
    /// <remarks>
    /// SURFACED, NEVER ENFORCED - the spec's ruling. Downloading is contrary to YouTube's terms
    /// except for CC-licensed material, so the confirm dialog states what this says and leaves the
    /// decision to the user. Do not turn this into a gate.
    /// </remarks>
    public bool IsCreativeCommons =>
        License is not null &&
        (License.Contains("creative commons", StringComparison.OrdinalIgnoreCase) ||
         License.StartsWith("cc ", StringComparison.OrdinalIgnoreCase));
}
