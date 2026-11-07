using System.Reflection;
using System.Xml.Linq;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Application.Projects;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.Generation;
using DiscImageStudio.ImageIO;
using DiscImageStudio.Iso;
using DiscImageStudio.Rendering;
using DvdImageSolver.Encoding;
using Xunit;

namespace DiscImageStudio.PortableArchitectureTests;

public sealed class DependencyBoundaryTests
{
    private static readonly string[] WindowsOnlyAssemblies =
    [
        "DiscImageStudio.App",
        "DiscImageStudio.Burning",
        "DiscImageStudio.Imaging",
        "Microsoft.WindowsDesktop.App",
        "PresentationCore",
        "PresentationFramework",
        "System.Windows.Forms",
        "WindowsBase",
    ];

    public static TheoryData<Assembly> PortableAssemblies =>
    [
        typeof(OpticalDiscFamily).Assembly,
        typeof(ProjectWorkspace).Assembly,
        typeof(RasterCodec).Assembly,
        typeof(Iso9660ImageWriter).Assembly,
        typeof(CdDiscModule).Assembly,
        typeof(DvdDiscModule).Assembly,
        typeof(DvdEccBlockEncoder).Assembly,
        typeof(BluRayDiscModule).Assembly,
        typeof(DiscGenerationService).Assembly,
        typeof(ImageComposer).Assembly,
    ];

    [Theory]
    [MemberData(nameof(PortableAssemblies))]
    public void Portable_assemblies_do_not_reference_desktop_ui_frameworks(Assembly assembly)
    {
        string[] references = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, IsWindowsOnlyAssembly);
        Assert.DoesNotContain(references, name => name.StartsWith("Avalonia", StringComparison.Ordinal));
    }

    [Fact]
    public void Avalonia_shell_does_not_reference_windows_only_product_assemblies()
    {
        Assembly assembly = typeof(global::DiscImageStudio.App.Avalonia.App).Assembly;
        string[] references = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, IsWindowsOnlyAssembly);
    }

    [Fact]
    public void Avalonia_project_targets_portable_dotnet_without_windows_desktop()
    {
        string projectPath = RepositoryPath(
            "src",
            "DiscImageStudio.App.Avalonia",
            "DiscImageStudio.App.Avalonia.csproj");
        XDocument project = XDocument.Load(projectPath);

        string[] targetFrameworks = Values(project, "TargetFramework")
            .Concat(Values(project, "TargetFrameworks"))
            .SelectMany(value => value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToArray();

        Assert.Equal(["net10.0"], targetFrameworks);
        Assert.DoesNotContain(Values(project, "UseWPF"), IsEnabled);
        Assert.DoesNotContain(Values(project, "UseWindowsForms"), IsEnabled);
        Assert.DoesNotContain(
            Includes(project, "FrameworkReference"),
            include => include.Contains("WindowsDesktop", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            Includes(project, "ProjectReference"),
            include => IsWindowsOnlyProjectReference(include));
    }

    [Fact]
    public void Linux_solution_contains_only_the_expected_portable_projects()
    {
        XDocument solution = XDocument.Load(RepositoryPath("DiscImageStudio.Linux.slnx"));
        string[] actual = solution
            .Descendants()
            .Where(element => element.Name.LocalName == "Project")
            .Select(element => element.Attribute("Path")?.Value.Replace('\\', '/'))
            .Where(path => path is not null)
            .Cast<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();

        string[] expected =
        [
            "src/DiscImageStudio.App.Avalonia/DiscImageStudio.App.Avalonia.csproj",
            "src/DiscImageStudio.Application/DiscImageStudio.Application.csproj",
            "src/DiscImageStudio.BluRay/DiscImageStudio.BluRay.csproj",
            "src/DiscImageStudio.Cd/DiscImageStudio.Cd.csproj",
            "src/DiscImageStudio.Cli/DiscImageStudio.Cli.csproj",
            "src/DiscImageStudio.Core/DiscImageStudio.Core.csproj",
            "src/DiscImageStudio.Dvd/DiscImageStudio.Dvd.csproj",
            "src/DiscImageStudio.Dvd.Engine/DiscImageStudio.Dvd.Engine.csproj",
            "src/DiscImageStudio.Generation/DiscImageStudio.Generation.csproj",
            "src/DiscImageStudio.ImageIO/DiscImageStudio.ImageIO.csproj",
            "src/DiscImageStudio.Iso/DiscImageStudio.Iso.csproj",
            "src/DiscImageStudio.Rendering/DiscImageStudio.Rendering.csproj",
            "src/DvdImageSolver/DvdImageSolver.csproj",
            "tests/DiscImageStudio.PortableArchitectureTests/DiscImageStudio.PortableArchitectureTests.csproj",
        ];

        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
        Assert.DoesNotContain(actual, path => path.Contains("DiscImageStudio.App/", StringComparison.Ordinal));
        Assert.DoesNotContain(actual, path => path.Contains("DiscImageStudio.Burning", StringComparison.Ordinal));
        Assert.DoesNotContain(actual, path => path.Contains("DiscImageStudio.Imaging", StringComparison.Ordinal));
    }

    [Fact]
    public void Portable_project_files_do_not_depend_on_ui_projects_or_avalonia()
    {
        XDocument solution = XDocument.Load(RepositoryPath("DiscImageStudio.Linux.slnx"));
        string[] portableProjects = solution
            .Descendants()
            .Where(element => element.Name.LocalName == "Project")
            .Select(element => element.Attribute("Path")?.Value.Replace('\\', '/'))
            .Where(path => path is not null
                && path.StartsWith("src/", StringComparison.Ordinal)
                && !path.Contains("DiscImageStudio.App.Avalonia", StringComparison.Ordinal))
            .Cast<string>()
            .ToArray();

        foreach (string relativePath in portableProjects)
        {
            XDocument project = XDocument.Load(RepositoryPath(relativePath.Split('/')));
            Assert.DoesNotContain(
                Includes(project, "PackageReference"),
                include => include.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(
                Includes(project, "ProjectReference"),
                IsWindowsOnlyProjectReference);
        }
    }

    [Fact]
    public void Dvd_engine_has_one_portable_source_owner_and_thin_consumers()
    {
        string enginePath = RepositoryPath(
            "src",
            "DiscImageStudio.Dvd.Engine",
            "DiscImageStudio.Dvd.Engine.csproj");
        XDocument engine = XDocument.Load(enginePath);
        Assert.Equal(["net10.0"], Values(engine, "TargetFramework"));
        Assert.DoesNotContain(Values(engine, "OutputType"), value => value == "Exe");
        Assert.DoesNotContain(Includes(engine, "FrameworkReference"),
            include => include.Contains("WindowsDesktop", StringComparison.OrdinalIgnoreCase));

        XDocument module = XDocument.Load(RepositoryPath(
            "src",
            "DiscImageStudio.Dvd",
            "DiscImageStudio.Dvd.csproj"));
        string[] moduleReferences = Includes(module, "ProjectReference")
            .Select(include => include.Replace('\\', '/'))
            .ToArray();
        Assert.Contains(moduleReferences,
            include => include.EndsWith("DiscImageStudio.Dvd.Engine.csproj", StringComparison.Ordinal));
        Assert.DoesNotContain(moduleReferences,
            include => include.EndsWith("DvdImageSolver.csproj", StringComparison.Ordinal));
        Assert.DoesNotContain(Includes(module, "Compile"),
            include => include.Contains("DvdImageSolver", StringComparison.Ordinal));

        XDocument host = XDocument.Load(RepositoryPath(
            "src",
            "DvdImageSolver",
            "DvdImageSolver.csproj"));
        Assert.Equal(["net10.0"], Values(host, "TargetFramework"));
        Assert.Contains(
            Includes(host, "ProjectReference").Select(include => include.Replace('\\', '/')),
            include => include.EndsWith("DiscImageStudio.Dvd.csproj", StringComparison.Ordinal));
        Assert.Equal(
            ["Program.cs"],
            Directory.EnumerateFiles(
                    RepositoryPath("src", "DvdImageSolver"),
                    "*.cs",
                    SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal));

        Assert.Equal(
            "DiscImageStudio.Dvd.Engine",
            typeof(DvdEccBlockEncoder).Assembly.GetName().Name);
        Assert.False(Directory.Exists(RepositoryPath("src", "DvdImageSolver", "Encoding")));
        Assert.False(Directory.Exists(RepositoryPath("src", "DvdImageSolver", "Solver")));
    }

    private static bool IsWindowsOnlyAssembly(string name)
        => WindowsOnlyAssemblies.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static bool IsWindowsOnlyProjectReference(string include)
    {
        string projectName = Path.GetFileNameWithoutExtension(include.Replace('\\', '/'));
        return WindowsOnlyAssemblies.Contains(projectName, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsEnabled(string value)
        => bool.TryParse(value, out bool enabled) && enabled;

    private static IEnumerable<string> Values(XDocument document, string elementName)
        => document
            .Descendants()
            .Where(element => element.Name.LocalName == elementName)
            .Select(element => element.Value.Trim())
            .Where(value => value.Length > 0);

    private static IEnumerable<string> Includes(XDocument document, string elementName)
        => document
            .Descendants()
            .Where(element => element.Name.LocalName == elementName)
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .Where(value => value.Length > 0);

    private static string RepositoryPath(params string[] parts)
        => Path.Combine([FindRepositoryRoot(), .. parts]);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DiscImageStudio.Linux.slnx"))
                && File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Disc Image Studio repository root.");
    }
}
