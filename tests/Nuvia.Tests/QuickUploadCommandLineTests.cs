using System;
using System.Collections.Generic;
using Nuvia.App.QuickUpload;
using Xunit;

namespace Nuvia.Tests;

/// <summary>
/// Pure command-line parsing tests for the shell "Upload to Nuvia" entry point. No filesystem access and
/// no network: only the mapping from <c>--upload …</c>/<c>--to …</c> tokens to a
/// <see cref="QuickUploadRequest"/>. The path values here are made-up and carry nothing sensitive.
/// </summary>
public sealed class QuickUploadCommandLineTests
{
    private static QuickUploadRequest? Parse(params string[] args)
        => QuickUploadCommandLine.Parse(args);

    [Fact]
    public void Parse_NoArgs_ReturnsNull()
    {
        Assert.Null(Parse());
        Assert.Null(QuickUploadCommandLine.Parse(Array.Empty<string>()));
    }

    [Fact]
    public void Parse_WithoutUploadFlag_ReturnsNull()
    {
        Assert.Null(Parse("--other", "value"));
    }

    [Fact]
    public void Parse_UploadWithPath_DefaultsToAsk()
    {
        var request = Parse("--upload", @"C:\docs\report.pdf");

        Assert.NotNull(request);
        Assert.Equal(@"C:\docs\report.pdf", request!.Path);
        Assert.Equal(QuickUploadDestination.Ask, request.To);
    }

    [Theory]
    [InlineData("saved", QuickUploadDestination.Saved)]
    [InlineData("group", QuickUploadDestination.Group)]
    [InlineData("ask", QuickUploadDestination.Ask)]
    [InlineData("SAVED", QuickUploadDestination.Saved)]
    [InlineData("nonsense", QuickUploadDestination.Ask)]
    public void Parse_ToValue_MapsToDestination(string token, QuickUploadDestination expected)
    {
        var request = Parse("--upload", @"C:\a\file.bin", "--to", token);

        Assert.NotNull(request);
        Assert.Equal(expected, request!.To);
    }

    [Fact]
    public void Parse_InlineEqualsForms_AreAccepted()
    {
        var request = Parse("--upload=C:\\a\\file.bin", "--to=group");

        Assert.NotNull(request);
        Assert.Equal(@"C:\a\file.bin", request!.Path);
        Assert.Equal(QuickUploadDestination.Group, request.To);
    }

    [Fact]
    public void Parse_FlagsAreCaseInsensitive()
    {
        var request = Parse("--UPLOAD", @"C:\a\file.bin", "--TO", "saved");

        Assert.NotNull(request);
        Assert.Equal(QuickUploadDestination.Saved, request!.To);
    }

    [Fact]
    public void Parse_UploadWithNoValue_ReturnsNull()
    {
        Assert.Null(Parse("--upload"));
    }

    [Fact]
    public void Parse_UploadFollowedByAnotherFlag_ReturnsNull()
    {
        // The next token is itself a flag, so it is not consumed as the path — no usable path means null.
        Assert.Null(Parse("--upload", "--to", "saved"));
    }

    [Fact]
    public void Parse_TrimsSurroundingWhitespaceInPath()
    {
        var request = Parse("--upload", "  C:\\a\\file.bin  ");

        Assert.NotNull(request);
        Assert.Equal(@"C:\a\file.bin", request!.Path);
    }

    [Fact]
    public void ContainsUploadFlag_TrueEvenWithoutPath()
    {
        Assert.True(QuickUploadCommandLine.ContainsUploadFlag(new[] { "--upload" }));
        Assert.True(QuickUploadCommandLine.ContainsUploadFlag(new[] { "--upload=C:\\a\\file.bin" }));
    }

    [Fact]
    public void ContainsUploadFlag_FalseWhenAbsent()
    {
        Assert.False(QuickUploadCommandLine.ContainsUploadFlag(Array.Empty<string>()));
        Assert.False(QuickUploadCommandLine.ContainsUploadFlag(new[] { "--to", "saved" }));
    }

    [Fact]
    public void Parse_MalformedUpload_ButContainsFlag_LetsCallerDistinguish()
    {
        // This is exactly the case App.OnStartup treats as "malformed --upload": flag present, path absent.
        IReadOnlyList<string> args = new[] { "--upload" };
        Assert.Null(QuickUploadCommandLine.Parse(args));
        Assert.True(QuickUploadCommandLine.ContainsUploadFlag(args));
    }
}
