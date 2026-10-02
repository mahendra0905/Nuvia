using System;
using Nuvia.App.Services;
using Xunit;

namespace Nuvia.Tests;

/// <summary>
/// Unit tests for the pure, network-free parts of the update checker: version parsing/comparison and the
/// JSON parsers. No network, no credentials, no WPF — safe to run on any platform. The live HTTP path and
/// the WPF notification UI are Windows/manual-only and are not covered here.
/// </summary>
public sealed class UpdateServiceTests
{
    [Theory]
    [InlineData("1.2.0", 1, 2, 0)]
    [InlineData("v1.2.0", 1, 2, 0)]
    [InlineData("V2.0.1", 2, 0, 1)]
    [InlineData("1.2.0-beta", 1, 2, 0)]
    [InlineData("v3.4.5+build.7", 3, 4, 5)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("  v1.0.0  ", 1, 0, 0)]
    public void TryParseVersion_ParsesCommonTagShapes(string raw, int major, int minor, int build)
    {
        var ok = UpdateService.TryParseVersion(raw, out var version);

        Assert.True(ok);
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(build, Math.Max(0, version.Build));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("release-2024")]
    public void TryParseVersion_RejectsNonVersions(string? raw)
    {
        Assert.False(UpdateService.TryParseVersion(raw, out _));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]
    [InlineData("1.0.0", "1.1.0", true)]
    [InlineData("1.0.0", "2.0.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.2.0", "1.1.9", false)]
    [InlineData("2.0.0", "1.9.9", false)]
    public void IsNewer_ComparesStrictly(string current, string candidate, bool expected)
    {
        Assert.True(UpdateService.TryParseVersion(current, out var cur));
        Assert.True(UpdateService.TryParseVersion(candidate, out var cand));

        Assert.Equal(expected, UpdateService.IsNewer(cur, cand));
    }

    [Fact]
    public void IsNewer_TreatsTwoAndThreePartAsEqual()
    {
        Assert.True(UpdateService.TryParseVersion("1.0", out var twoPart));
        Assert.True(UpdateService.TryParseVersion("1.0.0", out var threePart));

        Assert.False(UpdateService.IsNewer(twoPart, threePart));
        Assert.False(UpdateService.IsNewer(threePart, twoPart));
    }

    [Fact]
    public void ParseGitHubLatest_ReadsExpectedFields()
    {
        const string json = """
        {
            "tag_name": "v1.4.2",
            "html_url": "https://github.com/acme/nuvia/releases/tag/v1.4.2",
            "body": "Fixes and improvements.",
            "draft": false,
            "prerelease": false
        }
        """;

        var release = UpdateService.ParseGitHubLatest(json);

        Assert.NotNull(release);
        Assert.Equal("v1.4.2", release!.TagName);
        Assert.Equal("https://github.com/acme/nuvia/releases/tag/v1.4.2", release.HtmlUrl);
        Assert.Equal("Fixes and improvements.", release.Body);
        Assert.False(release.Draft);
        Assert.False(release.Prerelease);
    }

    [Fact]
    public void ParseGitHubLatest_FlagsDraftAndPrerelease()
    {
        const string json = """{ "tag_name": "v2.0.0", "draft": true, "prerelease": true }""";

        var release = UpdateService.ParseGitHubLatest(json);

        Assert.NotNull(release);
        Assert.True(release!.Draft);
        Assert.True(release.Prerelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    public void ParseGitHubLatest_ReturnsNullOnBadInput(string? json)
    {
        Assert.Null(UpdateService.ParseGitHubLatest(json));
    }

    [Fact]
    public void ParseWebsiteManifest_ReadsLatestAndAnnouncements()
    {
        const string json = """
        {
            "latest": { "version": "1.5.0", "downloadUrl": "https://nuvia.example/download", "notes": "New." },
            "announcements": [
                { "id": "a1", "title": "Maintenance", "message": "Brief downtime.", "url": "https://nuvia.example/status", "date": "2026-09-30T10:00:00Z" },
                { "id": "a2", "title": "Welcome" }
            ]
        }
        """;

        var manifest = UpdateService.ParseWebsiteManifest(json);

        Assert.NotNull(manifest);
        Assert.Equal("1.5.0", manifest!.LatestVersion);
        Assert.Equal("https://nuvia.example/download", manifest.DownloadUrl);
        Assert.Equal(2, manifest.Announcements.Count);
        Assert.Equal("a1", manifest.Announcements[0].Id);
        Assert.Equal("Maintenance", manifest.Announcements[0].Title);
        Assert.NotNull(manifest.Announcements[0].Date);
    }

    [Fact]
    public void ParseWebsiteManifest_ToleratesMissingSections()
    {
        var manifest = UpdateService.ParseWebsiteManifest("{}");

        Assert.NotNull(manifest);
        Assert.Null(manifest!.LatestVersion);
        Assert.Empty(manifest.Announcements);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    public void ParseWebsiteManifest_ReturnsNullOnBadInput(string? json)
    {
        Assert.Null(UpdateService.ParseWebsiteManifest(json));
    }

    [Fact]
    public void FormatRelative_UsesFriendlyBuckets()
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("just now", NotificationItem.FormatRelative(now.AddSeconds(-5), now));
        Assert.Equal("just now", NotificationItem.FormatRelative(now.AddMinutes(5), now)); // future clamps
        Assert.Equal("1 minute ago", NotificationItem.FormatRelative(now.AddMinutes(-1), now));
        Assert.Equal("5 minutes ago", NotificationItem.FormatRelative(now.AddMinutes(-5), now));
        Assert.Equal("1 hour ago", NotificationItem.FormatRelative(now.AddHours(-1), now));
        Assert.Equal("3 hours ago", NotificationItem.FormatRelative(now.AddHours(-3), now));
        Assert.Equal("2 days ago", NotificationItem.FormatRelative(now.AddDays(-2), now));
    }

    [Fact]
    public void NotificationItem_HasActionRequiresUrlAndLabel()
    {
        Assert.True(new NotificationItem { ActionUrl = "https://x", ActionLabel = "Open" }.HasAction);
        Assert.False(new NotificationItem { ActionUrl = "https://x" }.HasAction);
        Assert.False(new NotificationItem { ActionLabel = "Open" }.HasAction);
        Assert.False(new NotificationItem().HasAction);
    }
}
