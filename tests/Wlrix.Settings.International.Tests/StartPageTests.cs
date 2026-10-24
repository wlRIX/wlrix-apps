using Wlrix.Settings.International.Views;
using Xunit;

namespace Wlrix.Settings.International.Tests;

/// <summary><c>--page</c>, which Keyboard Settings' Layout button uses to open the Keyboard dialog.</summary>
public class StartPageTests
{
    [Theory]
    [InlineData(new[] { "--page", "keyboard" }, StartPage.Keyboard)]
    [InlineData(new[] { "--page=timezone" }, StartPage.TimeZone)]
    [InlineData(new[] { "--page", "Country" }, StartPage.Country)]
    [InlineData(new[] { "--page", "language" }, StartPage.Language)]
    [InlineData(new[] { "--page", "nonsense" }, StartPage.None)]
    [InlineData(new[] { "--page" }, StartPage.None)]
    [InlineData(new string[0], StartPage.None)]
    public void ThePageIsReadFromTheArguments(string[] args, StartPage page) =>
        Assert.Equal(page, Program.ParsePage(args));
}
