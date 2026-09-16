using GmapPlanner.Core.Services.Publish;
using Xunit;

namespace GmapPlanner.Core.Tests;

public class MyMapsSessionTests
{
    [Theory]
    [InlineData("https://accounts.google.com/v3/signin/identifier", true)]
    [InlineData("https://accounts.google.com/ServiceLogin", true)]
    [InlineData("https://www.google.com/maps/d/edit?mid=abc&hl=en", false)]
    [InlineData("https://www.google.com/maps/d/u/0/", false)]
    public void IsSignInRedirect_flags_only_google_account_pages(string url, bool expected) =>
        Assert.Equal(expected, MyMapsSession.IsSignInRedirect(url));
}
