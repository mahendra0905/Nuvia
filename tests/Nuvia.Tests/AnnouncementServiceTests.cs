using System;
using Nuvia.App.Services;
using Xunit;

namespace Nuvia.Tests;

/// <summary>
/// Unit tests for the pure, network-free parts of the announcement reader: the channel-post → notification
/// mapping and the URL extractor. No network, no Telegram client, no WPF — safe to run on any platform. The
/// live no-join channel read and the WPF notification UI are Windows/manual-only and are not covered here.
/// </summary>
public sealed class AnnouncementServiceTests
{
    private static readonly DateTimeOffset When =
        new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n  \r\n")]
    public void ToNotification_ReturnsNull_ForBlankText(string? text)
    {
        Assert.Null(AnnouncementService.ToNotification(42, text, When));
    }

    [Fact]
    public void ToNotification_FirstNonBlankLineBecomesTitle_RestBecomesMessage()
    {
        var item = AnnouncementService.ToNotification(7, "New release!\nGrab it today.\nThanks.", When);

        Assert.NotNull(item);
        Assert.Equal("New release!", item!.Title);
        Assert.Equal("Grab it today.\nThanks.", item.Message);
    }

    [Fact]
    public void ToNotification_SkipsLeadingBlankLines_WhenPickingTitle()
    {
        var item = AnnouncementService.ToNotification(7, "\n\n  Heads up\nBody line", When);

        Assert.NotNull(item);
        Assert.Equal("Heads up", item!.Title);
        Assert.Equal("Body line", item.Message);
    }

    [Fact]
    public void ToNotification_SingleLine_HasEmptyMessage()
    {
        var item = AnnouncementService.ToNotification(7, "Just a title", When);

        Assert.NotNull(item);
        Assert.Equal("Just a title", item!.Title);
        Assert.Equal(string.Empty, item.Message);
    }

    [Fact]
    public void ToNotification_SetsChannelIdAndAnnouncementKindAndTimestamp()
    {
        var item = AnnouncementService.ToNotification(123, "Title\nBody", When);

        Assert.NotNull(item);
        Assert.Equal("chan:123", item!.Id);
        Assert.Equal(NotificationKind.Announcement, item.Kind);
        Assert.Equal(When, item.Timestamp);
    }

    [Fact]
    public void ToNotification_AddsOpenAction_WhenTextHasUrl()
    {
        var item = AnnouncementService.ToNotification(1, "See this\nDetails at https://nuvia.app/news please.", When);

        Assert.NotNull(item);
        Assert.Equal("https://nuvia.app/news", item!.ActionUrl);
        Assert.Equal("Open", item.ActionLabel);
    }

    [Fact]
    public void ToNotification_NoAction_WhenTextHasNoUrl()
    {
        var item = AnnouncementService.ToNotification(1, "Title\nNo links here.", When);

        Assert.NotNull(item);
        Assert.Null(item!.ActionUrl);
        Assert.Null(item.ActionLabel);
    }

    [Fact]
    public void ToNotification_ClampsLongTitleAndMessage()
    {
        var title = new string('a', 300);
        var body = new string('b', 800);

        var item = AnnouncementService.ToNotification(1, title + "\n" + body, When);

        Assert.NotNull(item);
        Assert.True(item!.Title.Length <= 141);          // 140 + ellipsis
        Assert.EndsWith("…", item.Title);
        Assert.True(item.Message.Length <= 401);          // 400 + ellipsis
        Assert.EndsWith("…", item.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no url at all")]
    [InlineData("ftp://example.com/file")]      // only http/https are recognised
    public void ExtractFirstUrl_ReturnsNull_WhenNoHttpUrl(string? text)
    {
        Assert.Null(AnnouncementService.ExtractFirstUrl(text));
    }

    [Fact]
    public void ExtractFirstUrl_ReturnsFirstMatch()
    {
        var url = AnnouncementService.ExtractFirstUrl("go to http://a.test and later https://b.test");

        Assert.Equal("http://a.test", url);
    }

    [Theory]
    [InlineData("Visit https://nuvia.app.", "https://nuvia.app")]
    [InlineData("See (https://nuvia.app/x)", "https://nuvia.app/x")]
    [InlineData("Link: https://nuvia.app/path!", "https://nuvia.app/path")]
    public void ExtractFirstUrl_TrimsTrailingPunctuation(string text, string expected)
    {
        Assert.Equal(expected, AnnouncementService.ExtractFirstUrl(text));
    }
}
