using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Wlrix.Packages.AppStream;
using Wlrix.Packages.Processes;
using Xunit;

namespace Wlrix.Packages.Tests;

public sealed class AppStreamCatalogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("wlrix-appstream-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static AppStreamComponent Component(string id, string kind, string package, string? name = null,
        int priority = 0) =>
        new(id, kind, [package], name ?? id, $"{id} summary", [$"{id}-category"], null, [], priority);

    private AppStreamIndex Load(params string[] roots) =>
        new AppStreamCatalog(NullLogger<AppStreamCatalog>.Instance, roots, "en-US").Load();

    private void WriteGzip(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionMode.Compress);
        gzip.Write(Encoding.UTF8.GetBytes(content));
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Index_APackageWithNoComponentHasNoProduct()
    {
        Assert.Null(AppStreamIndex.Build([Component("a", "desktop-application", "a")]).Find("other"));
        Assert.Null(AppStreamIndex.Empty.Find("anything"));
    }

    [Fact]
    public void Index_OneApplicationNamesItsPackageWhateverElseItCarries()
    {
        var index = AppStreamIndex.Build([
            Component("codeblocks-contrib", "addon", "codeblocks"),
            Component("codeblocks.desktop", "desktop-application", "codeblocks", "Code::Blocks IDE"),
        ]);

        Assert.Equal("Code::Blocks IDE", index.Find("codeblocks")?.Name);
    }

    [Fact]
    public void Index_SeveralApplicationsLeaveTheNameAndLendTheOneNamedAfterThePackage()
    {
        var index = AppStreamIndex.Build([
            Component("calibrate_lens_gui.desktop", "desktop-application", "hugin"),
            Component("hugin.desktop", "desktop-application", "hugin"),
        ]);

        var product = index.Find("hugin");
        Assert.NotNull(product);
        Assert.Null(product.Name);
        Assert.Null(product.Summary);
        Assert.Equal(["hugin.desktop-category"], product.Categories);
    }

    [Fact]
    public void Index_SeveralApplicationsNoneNamedAfterThePackageLendTheFirstById()
    {
        var index = AppStreamIndex.Build([
            Component("z.desktop", "desktop-application", "suite"),
            Component("a.desktop", "desktop-application", "suite"),
        ]);

        Assert.Equal(["a.desktop-category"], index.Find("suite")?.Categories);
    }

    [Fact]
    public void Index_OneOtherComponentNamesItsPackageAndSeveralSayNothing()
    {
        var index = AppStreamIndex.Build([
            Component("org.fcitx.mozc", "addon", "fcitx5-mozc", "Mozc"),
            Component("font.a", "font", "gsfonts"),
            Component("font.b", "font", "gsfonts"),
        ]);

        Assert.Equal("Mozc", index.Find("fcitx5-mozc")?.Name);
        Assert.Null(index.Find("gsfonts"));
    }

    [Fact]
    public void Index_TheSameComponentInTwoCatalogsIsOneAndHigherPriorityWins()
    {
        var index = AppStreamIndex.Build([
            Component("a", "desktop-application", "a", "Low"),
            Component("a", "desktop-application", "a", "High", priority: 10),
            Component("a", "desktop-application", "a", "Also low"),
        ]);

        Assert.Equal("High", index.Find("a")?.Name);
    }

    [Theory]
    [InlineData("gimp", "Graphics", "Graphics")]
    [InlineData("rasters", "2DGraphics;RasterGraphics;Graphics", "Graphics")]
    [InlineData("player", "Audio;Player", "AudioVideo")]
    [InlineData("video", "Video;AudioVideo", "AudioVideo")]
    [InlineData("none", "Qt;KDE", null)]
    public void Product_MainCategoryIsTheFirstRecognized(string _, string categories, string? expected)
    {
        var product = new AppStreamProduct("x", "x", categories.Split(';'), null, []);

        Assert.Equal(expected, product.MainCategory);
    }

    [Fact]
    public void IconCandidates_PrefersTheSmallestBigEnoughThenLargestDown()
    {
        var files = AppStreamCatalog.IconCandidates("/r", "o", [
            new CachedIcon("i", 128, 128, 1),
            new CachedIcon("i", 16, 16, 1),
            new CachedIcon("i", 48, 48, 1),
            new CachedIcon("i", 24, 24, 1),
        ], 32);

        Assert.Equal(
            ["/r/icons/o/48x48/i", "/r/icons/o/128x128/i", "/r/icons/o/24x24/i", "/r/icons/o/16x16/i"],
            files);
    }

    [Fact]
    public void IconCandidates_NamesTheScaleDirectoryAndCountsTheScale()
    {
        var files = AppStreamCatalog.IconCandidates("/r", "o", [
            new CachedIcon("i", 64, 64, 1),
            new CachedIcon("i", 24, 24, 2),
        ], 32);

        Assert.Equal(["/r/icons/o/24x24@2/i", "/r/icons/o/64x64/i"], files);
    }

    [Fact]
    public void IconCandidates_AnIconWithNoSizeCouldBeInAnyOfTheUsualPlaces()
    {
        var files = AppStreamCatalog.IconCandidates("/r", "o", [new CachedIcon("i", 0, 0, 1)], 32);

        Assert.Equal(["/r/icons/o/64x64/i", "/r/icons/o/128x128/i", "/r/icons/o/i"], files);
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../evil")]
    public void IconCandidates_ABadOriginHasNoIcons(string origin)
    {
        Assert.Empty(AppStreamCatalog.IconCandidates("/r", origin, [new CachedIcon("i", 64, 64, 1)], 32));
    }

    [Fact]
    public void Load_ReadsXmlAndYamlFromTheirDirectoriesCompressedOrNot()
    {
        WriteGzip("xml/one.xml.gz", """
            <components origin="o"><component type="desktop-application">
              <id>a</id><name>Product A</name><pkgname>a</pkgname>
              <icon type="cached" width="48" height="48">a.jxl</icon>
            </component></components>
            """);
        Write("xml/two.xml", """
            <components origin="o"><component type="desktop-application">
              <id>b</id><name>Product B</name><pkgname>b</pkgname>
            </component></components>
            """);
        WriteGzip("yaml/three.yml.gz", """
            ---
            File: DEP-11
            Origin: y
            ---
            Type: desktop-application
            ID: c
            Package: c
            Name:
              C: Product C
            """);

        var index = Load(_root);

        Assert.Equal("Product A", index.Find("a")?.Name);
        Assert.Equal("Product B", index.Find("b")?.Name);
        Assert.Equal("Product C", index.Find("c")?.Name);
        Assert.Equal([Path.Combine(_root, "icons/o/48x48/a.jxl")], index.Find("a")?.IconFiles);
    }

    [Fact]
    public void Load_ABrokenCatalogCostsOnlyItself()
    {
        Write("xml/a-truncated.xml", "<components origin=\"o\"><component><id>x");
        Write("xml/b-not-a-catalog.xml", "<html/>");
        File.WriteAllBytes(Path.Combine(_root, "xml/c-not-gzip.xml.gz"), [1, 2, 3, 4]);
        Write("xml/d-unrelated.txt", "not read");
        File.CreateSymbolicLink(Path.Combine(_root, "xml/e-dangling.xml"), Path.Combine(_root, "gone"));
        Write("xml/z-good.xml", "<components origin=\"o\"><component><id>g</id><name>Good</name><pkgname>g</pkgname></component></components>");

        var index = Load(_root);

        Assert.Equal(1, index.Count);
        Assert.Equal("Good", index.Find("g")?.Name);
    }

    [Fact]
    public void Load_NoCatalogsAtAllIsAnEmptyIndex()
    {
        Assert.Equal(0, Load(Path.Combine(_root, "not-there")).Count);
    }

    private sealed class FakeRunner(Func<IReadOnlyList<string>, ProcessResult> run) : IProcessRunner
    {
        public int Runs { get; private set; }

        public Task<ProcessResult> RunAsync(string program, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("djxl", program);
            Runs++;
            return Task.FromResult(run(arguments));
        }

        public IAsyncEnumerable<OutputLine> StreamAsync(string program, IReadOnlyList<string> arguments,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static ProcessResult WritesOutput(IReadOnlyList<string> arguments)
    {
        File.WriteAllText(arguments[1], "png");
        return new ProcessResult(0, string.Empty, string.Empty);
    }

    [Fact]
    public async Task Jxl_ConvertsOnceAndKeepsTheResult()
    {
        Write("a.jxl", "jxl");
        var runner = new FakeRunner(WritesOutput);
        var converter = new JxlIconConverter(runner, Path.Combine(_root, "cache"));

        var first = await converter.ToPngAsync(Path.Combine(_root, "a.jxl"));
        var second = await converter.ToPngAsync(Path.Combine(_root, "a.jxl"));

        Assert.NotNull(first);
        Assert.EndsWith(".png", first);
        Assert.Equal("png", File.ReadAllText(first));
        Assert.Equal(first, second);
        Assert.Equal(1, runner.Runs);
    }

    [Fact]
    public async Task Jxl_ConvertsAgainWhenTheSourceIsNewer()
    {
        Write("a.jxl", "jxl");
        var source = Path.Combine(_root, "a.jxl");
        var runner = new FakeRunner(WritesOutput);
        var converter = new JxlIconConverter(runner, Path.Combine(_root, "cache"));

        await converter.ToPngAsync(source);
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddHours(1));
        await converter.ToPngAsync(source);

        Assert.Equal(2, runner.Runs);
    }

    [Fact]
    public async Task Jxl_AFailureLeavesNothingBehind()
    {
        Write("a.jxl", "jxl");
        var cache = Path.Combine(_root, "cache");
        var converter = new JxlIconConverter(new FakeRunner(arguments =>
        {
            File.WriteAllText(arguments[1], "half a png");
            return new ProcessResult(1, string.Empty, "broken");
        }), cache);

        Assert.Null(await converter.ToPngAsync(Path.Combine(_root, "a.jxl")));
        Assert.Empty(Directory.GetFiles(cache));
    }

    [Fact]
    public async Task Jxl_NoDjxlMeansNoIcon()
    {
        Write("a.jxl", "jxl");
        var converter = new JxlIconConverter(
            new FakeRunner(_ => throw new PackageProcessException("djxl is not installed")),
            Path.Combine(_root, "cache"));

        Assert.Null(await converter.ToPngAsync(Path.Combine(_root, "a.jxl")));
    }
}
