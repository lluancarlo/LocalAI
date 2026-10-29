using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LocalAI.Core.Assistants;

namespace LocalAI.App.Views;

/// <summary>
/// The assistants' pictures (<see cref="AssistantAvatars"/>) from the app's Assets, decoded once at a small size:
/// they are only shown in small circles.
/// </summary>
public static class AvatarImages
{
    private const int DecodeWidth = 128;
    private static readonly Dictionary<string, Bitmap> Cache = [];

    /// <summary>The picture with this name; the default one for an empty or unknown name.</summary>
    public static Bitmap Get(string? avatar)
    {
        var name = avatar is AssistantAvatars.Female or AssistantAvatars.Male ? avatar : AssistantAvatars.Default;
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var bitmap)) return bitmap;
            using var stream = AssetLoader.Open(new Uri($"avares://LocalAI/Assets/{name}.png"));
            return Cache[name] = Bitmap.DecodeToWidth(stream, DecodeWidth);
        }
    }
}
