using Wlrix.Packages.AppStream;
using Wlrix.SoftwareManager.ViewModels;
using Xunit;

namespace Wlrix.Packages.Tests;

/// <summary>What AppStream changes about a row, and above all what it must not.</summary>
public class ProductRowTests
{
    private static readonly AppStreamProduct Gimp =
        new("GNU Image Manipulation Program", "Image editor", ["Graphics"], "gimp", []);

    private static PackageRowViewModel Row(string name, string version = "3.0.4", AppStreamProduct? product = null,
        string category = "") =>
        new(name, version, "New", 100, "extra", canInstall: true, canRemove: true,
            product: product, categoryText: category);

    [Fact]
    public void NoProduct_LooksExactlyAsItAlwaysHas()
    {
        var row = Row("zlib");

        Assert.Equal("zlib, 3.0.4", row.DisplayName);
        Assert.Equal("zlib, 3.0.4", row.FullName);
        Assert.Null(row.ToolTipText);
        Assert.Equal(string.Empty, row.CategoryText);
        Assert.Null(row.Icon);
    }

    [Fact]
    public void NoProductAndNoVersion_ShowsTheBareName()
    {
        var row = Row("/tmp/file.pkg.tar.zst", version: string.Empty);

        Assert.Equal("/tmp/file.pkg.tar.zst", row.DisplayName);
        Assert.Equal("/tmp/file.pkg.tar.zst", row.FullName);
    }

    [Fact]
    public void Product_NamesTheRowAndTheFieldKeepsThePackageName()
    {
        var row = Row("gimp", product: Gimp, category: "Graphics");

        Assert.Equal("gimp", row.Name);
        Assert.Equal("GNU Image Manipulation Program, 3.0.4", row.DisplayName);
        Assert.Equal("GNU Image Manipulation Program (gimp), 3.0.4", row.FullName);
        Assert.Equal("Image editor\ngimp", row.ToolTipText);
        Assert.Equal("Graphics", row.CategoryText);
    }

    [Fact]
    public void Product_SameNameAsThePackageIsNotRepeated()
    {
        var row = Row("firefox", product: Gimp with { Name = "firefox" });

        Assert.Equal("firefox, 3.0.4", row.FullName);
    }

    [Fact]
    public void Product_WithNoNameKeepsThePackageNameAndNoTooltip()
    {
        // A package of several applications lends an icon and a category but no name.
        var row = Row("libreoffice-fresh", product: Gimp with { Name = null, Summary = null }, category: "Office");

        Assert.Equal("libreoffice-fresh, 3.0.4", row.DisplayName);
        Assert.Equal("libreoffice-fresh, 3.0.4", row.FullName);
        Assert.Null(row.ToolTipText);
        Assert.Equal("Office", row.CategoryText);
    }

    [Fact]
    public void Marks_GoByThePackageNameWhateverTheRowShows()
    {
        // The transaction is built from these names. A product name here would ask pacman for a
        // package called "GNU Image Manipulation Program".
        var inventory = new InventoryViewModel();
        inventory.Replace([Row("gimp", product: Gimp), Row("zlib")]);

        inventory.Rows[0].MarkedForInstall = true;
        inventory.Rows[1].MarkedForRemoval = true;

        Assert.Equal(["gimp"], inventory.MarkedForInstall);
        Assert.Equal(["zlib"], inventory.MarkedForRemoval);
    }

    [Fact]
    public void Marks_SurviveTheSamePackageComingBackWithAProduct()
    {
        // Installing the catalog package mid-session re-reads it, so a row can gain a product.
        var inventory = new InventoryViewModel();
        inventory.Replace([Row("gimp")]);
        inventory.Rows[0].MarkedForInstall = true;

        inventory.Replace([Row("gimp", product: Gimp)]);

        Assert.True(inventory.Rows[0].MarkedForInstall);
        Assert.Equal(["gimp"], inventory.MarkedForInstall);
    }

    [Fact]
    public void SelectedField_ShowsTheFullName()
    {
        var inventory = new InventoryViewModel();
        inventory.Replace([Row("gimp", product: Gimp)]);

        inventory.Selected = inventory.Rows[0];

        Assert.Equal("GNU Image Manipulation Program (gimp), 3.0.4", inventory.SelectedName);
    }
}
