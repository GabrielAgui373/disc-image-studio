using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using DiscImageStudio.App.Avalonia.Hosting;
using DiscImageStudio.App.Avalonia.Localization;
using DiscImageStudio.Application.Settings;
using Xunit;

namespace DiscImageStudio.PortableArchitectureTests;

/// <summary>The interface languages: English (neutral) and Brazilian Portuguese.</summary>
public sealed partial class LocalizationTests
{
    private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo(AppLanguage.PortugueseBrazil);

    [Fact]
    public void Portuguese_translates_every_key_with_the_same_placeholders()
    {
        Dictionary<string, string> portuguese = PortugueseOnly();
        ResourceTextCatalog english = ResourceTextCatalog.English;
        IReadOnlyList<string> keys = ResourceTextCatalog.Keys;
        Assert.NotEmpty(keys);
        Assert.Empty(keys.Except(portuguese.Keys));
        Assert.Empty(portuguese.Keys.Except(keys));
        Assert.All(keys, key =>
        {
            Assert.Equal(Placeholders(english[key]), Placeholders(portuguese[key]));
            object[] arguments = [.. Enumerable.Repeat<object>(1.5, 8)];
            _ = string.Format(Portuguese, portuguese[key], arguments);
        });
    }

    [Fact]
    public void Every_literal_text_key_in_the_app_exists()
    {
        string app = Path.Combine(RepositoryRoot(), "src", "DiscImageStudio.App.Avalonia");
        HashSet<string> keys = [.. ResourceTextCatalog.Keys];
        string[] missing = [.. Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => TextKey().Matches(File.ReadAllText(path)).Select(match => match.Groups[1].Value))
            .Where(key => !key.EndsWith('.') && !keys.Contains(key))
            .Distinct()];
        Assert.Empty(missing);
    }

    [Fact]
    public void The_catalog_reads_the_language_it_was_made_for()
    {
        Assert.Equal("Burn", ResourceTextCatalog.English["navigation.burn"]);
        Assert.Equal("Gravar", new ResourceTextCatalog(Portuguese)["navigation.burn"]);
        Assert.Throws<KeyNotFoundException>(() => ResourceTextCatalog.English["no.such.key"]);
    }

    [Theory]
    [InlineData(null, "pt-BR", "pt-BR")]
    [InlineData("system", "pt-PT", "pt-BR")]
    [InlineData("system", "fr-FR", "en")]
    [InlineData("system", "en-US", "en")]
    [InlineData("en", "pt-BR", "en")]
    [InlineData("pt-BR", "de-DE", "pt-BR")]
    [InlineData("klingon", "pt-BR", "pt-BR")]
    public void The_system_choice_follows_the_system_language(string? saved, string system, string expected)
        => Assert.Equal(expected, AppLanguage.Resolve(saved, CultureInfo.GetCultureInfo(system)));

    [Fact]
    public void Startup_sets_the_interface_language_but_keeps_the_formatting_culture()
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            global::DiscImageStudio.App.Avalonia.App app = new();
            CultureInfo language = StartupPreferences.Apply(app, new UserSettings(), CultureInfo.GetCultureInfo("pt-BR"));
            Assert.Equal("pt-BR", language.Name);
            Assert.Equal("pt-BR", CultureInfo.CurrentUICulture.Name);
            Assert.Equal("en", CultureInfo.CurrentCulture.Name);

            language = StartupPreferences.Apply(app, new UserSettings { Language = "en" }, CultureInfo.GetCultureInfo("pt-BR"));
            Assert.Equal("en", language.Name);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
            CultureInfo.DefaultThreadCurrentCulture = previousCulture;
            CultureInfo.DefaultThreadCurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public void Older_settings_without_a_language_follow_the_system()
    {
        Assert.Equal(UserSettings.SystemLanguage, new UserSettings().Normalize().Language);
        Assert.Equal(UserSettings.SystemLanguage, new UserSettings { Language = "  " }.Normalize().Language);
        UserSettings? old = System.Text.Json.JsonSerializer.Deserialize<UserSettings>("{\"schemaVersion\":1,\"culture\":\"en\",\"theme\":\"Dark\"}", new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        Assert.Equal(UserSettings.SystemLanguage, old!.Language);
    }

    private static Dictionary<string, string> PortugueseOnly()
    {
        ResourceManager resources = new("DiscImageStudio.App.Avalonia.Localization.Strings", typeof(ResourceTextCatalog).Assembly);
        ResourceSet set = resources.GetResourceSet(Portuguese, createIfNotExists: true, tryParents: false)
            ?? throw new MissingManifestResourceException("The pt-BR satellite assembly is missing.");
        return set.Cast<DictionaryEntry>().ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.Ordinal);
    }

    private static string[] Placeholders(string text)
        => [.. Placeholder().Matches(text).Select(match => match.Groups[1].Value).Distinct().Order(StringComparer.Ordinal)];

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DiscImageStudio.Linux.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }

    [GeneratedRegex(@"\{(\d+)(?:[:,][^}]*)?\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"[tT]ext\[""([A-Za-z0-9_.]+)""\]")]
    private static partial Regex TextKey();
}
