using System.Xml.Linq;

namespace Bijecta.CliAuth.UnitTests;

public sealed class PackageBoundaryTests
{
    [Theory]
    [InlineData("Bijecta.CliAuth.Core")]
    [InlineData("Bijecta.CliAuth.Extensions")]
    [InlineData("Bijecta.CliAuth.Storage.File")]
    [InlineData("Bijecta.CliAuth.Storage.MacOS")]
    [InlineData("Bijecta.CliAuth.Storage.Linux")]
    [InlineData("Bijecta.CliAuth.Storage.Windows")]
    public void PackageReferencesRespectCoreAndPlatformBoundaries(string package)
    {
        var project = XDocument.Load(Path.Combine(
            RepositoryRoot(), "src", package, $"{package}.csproj"));
        var references = project.Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(
                reference.Attribute("Include")!.Value))
            .ToArray();

        if (package == "Bijecta.CliAuth.Core")
        {
            Assert.Empty(references);
        }
        else
        {
            Assert.Equal(["Bijecta.CliAuth.Core"], references);
        }

        Assert.Empty(project.Descendants("FrameworkReference"));
        Assert.DoesNotContain(project.Descendants("PackageReference"), reference =>
            reference.Attribute("Include")!.Value == "System.CommandLine");
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Bijecta.CliAuth.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Cannot locate the repository root.");
    }
}
