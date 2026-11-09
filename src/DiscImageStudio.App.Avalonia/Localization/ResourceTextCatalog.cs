using System.Globalization;
using System.Resources;

namespace DiscImageStudio.App.Avalonia.Localization;

public interface ITextCatalog
{
    /// <summary>The language the text is in.</summary>
    CultureInfo Culture { get; }

    string this[string resourceId] { get; }
}

/// <summary>
/// The application text, read from <c>Localization/Strings.resx</c> (English, the neutral
/// language) and its satellite translations such as <c>Strings.pt-BR.resx</c>. The language is
/// fixed for the catalog's life: the app picks it once at startup.
/// </summary>
public sealed class ResourceTextCatalog : ITextCatalog
{
    private static readonly ResourceManager Resources = new(
        "DiscImageStudio.App.Avalonia.Localization.Strings",
        typeof(ResourceTextCatalog).Assembly);

    public ResourceTextCatalog(CultureInfo culture)
    {
        Culture = culture ?? throw new ArgumentNullException(nameof(culture));
    }

    /// <summary>The neutral English text, independent of the machine's language.</summary>
    public static ResourceTextCatalog English { get; } = new(CultureInfo.GetCultureInfo(AppLanguage.English));

    public CultureInfo Culture { get; }

    /// <summary>Every resource ID, from the neutral English resources.</summary>
    public static IReadOnlyList<string> Keys
    {
        get
        {
            ResourceSet set = Resources.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)
                ?? throw new MissingManifestResourceException("The neutral application text is missing.");
            return [.. set.Cast<System.Collections.DictionaryEntry>().Select(entry => (string)entry.Key).Order(StringComparer.Ordinal)];
        }
    }

    public string this[string resourceId]
        => Resources.GetString(resourceId, Culture)
           ?? throw new KeyNotFoundException($"Unknown text resource ID '{resourceId}'.");
}
