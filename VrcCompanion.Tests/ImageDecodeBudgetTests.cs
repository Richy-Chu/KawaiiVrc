using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// Every <c>AsyncImage</c> names the width it wants decoded. A width far above
/// the box it draws in buys nothing on screen and costs the decoded bytes twice:
/// on the card where it is held and in the loader's fixed-size cache, where one
/// oversized picture evicts several correctly sized ones.
/// </summary>
public class ImageDecodeBudgetTests
{
    /// <summary>
    /// 200% display scaling is the widest the app can draw: a 52 DIP box is 104
    /// physical pixels, so twice the box is the last useful decode width.
    /// </summary>
    private const int MaximumScale = 2;

    /// <summary>The crisp floor the app already uses for tiny avatars.</summary>
    private const int MinimumWidth = 96;

    [Fact]
    public void AnImageIsNotDecodedAtMoreThanTwiceTheWidthOfItsBox()
    {
        var violations = new List<string>();
        var checkedImages = 0;

        foreach (var path in XamlFiles())
        {
            XDocument document;
            try
            {
                document = XDocument.Load(path, LoadOptions.SetLineInfo);
            }
            catch (Exception ex)
            {
                violations.Add($"{Relative(path)}: could not be read ({ex.Message})");
                continue;
            }

            foreach (var image in document.Descendants().Where(IsAsyncImage))
            {
                if (NumericAttribute(image, "DecodeWidth") is not { } decode)
                {
                    continue;
                }

                if (EnclosingWidth(image) is not { } box)
                {
                    // A full-width picture has no drawn width to compare with.
                    continue;
                }

                checkedImages++;
                if (decode > Math.Max(MinimumWidth, box * MaximumScale))
                {
                    violations.Add(
                        $"{Relative(path)}:{Line(image)} decodes {decode} px into a {box} px box");
                }
            }
        }

        // A scan that matched nothing would pass whatever the markup said.
        Assert.True(checkedImages > 40, $"only {checkedImages} images were checked");
        Assert.True(
            violations.Count == 0,
            "Decode width above twice the drawn width:" + Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    private static bool IsAsyncImage(XElement element)
        => string.Equals(element.Name.LocalName, "AsyncImage", StringComparison.Ordinal);

    /// <summary>The nearest ancestor that names a numeric Width, or null.</summary>
    private static int? EnclosingWidth(XElement image)
    {
        for (var ancestor = image.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (NumericAttribute(ancestor, "Width") is { } width)
            {
                return width;
            }
        }

        return null;
    }

    private static int? NumericAttribute(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value;
        return int.TryParse(value, out var number) && number > 0 ? number : null;
    }

    private static int Line(XElement element)
        => element is IXmlLineInfo line && line.HasLineInfo() ? line.LineNumber : 0;

    private static IEnumerable<string> XamlFiles()
    {
        var root = FindProductionSourceRoot();
        return Directory
            .EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar)
                .Any(part => string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(path => path, StringComparer.Ordinal);
    }

    private static string Relative(string path) => Path.GetRelativePath(FindProductionSourceRoot(), path);

    private static string FindProductionSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "VrcCompanion", "Services", "UserDataPaths.cs");
            if (File.Exists(candidate))
            {
                return Path.Combine(directory.FullName, "VrcCompanion");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find the VrcCompanion source tree above " + AppContext.BaseDirectory);
    }
}
