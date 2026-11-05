using PlainCommander.Core.Model;

namespace PlainCommander.Presentation;

/// <summary>
/// Supplies a UI-specific image for an entry. Returns a cached image right away, or null and calls
/// <c>onLoaded</c> later on the UI thread. When it returns non-null it never calls back.
/// </summary>
public interface IIconProvider
{
    object? GetIcon(FileEntry entry, Action<object?> onLoaded);
}
