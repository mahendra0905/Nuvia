using Nuvia.App.QuickUpload;
using Xunit;

namespace Nuvia.Tests;

/// <summary>
/// Tests for the only thing that crosses the process boundary: the quick-upload pipe payload. They pin
/// the round-trip through <see cref="QuickUploadRequest.Serialize"/> / <see cref="QuickUploadRequest.TryParse"/>,
/// the stable string tokens (so a reordered enum can't silently change the wire meaning), and the safe
/// fallback to <see cref="QuickUploadRequest.Activate"/> for anything malformed — a garbled or hostile
/// pipe write must never crash the primary instance. The payload carries only a path and a hint, never a
/// secret.
/// </summary>
public sealed class QuickUploadRequestTests
{
    [Theory]
    [InlineData(QuickUploadDestination.Ask)]
    [InlineData(QuickUploadDestination.Saved)]
    [InlineData(QuickUploadDestination.Group)]
    public void SerializeThenParse_RoundTrips(QuickUploadDestination to)
    {
        var original = new QuickUploadRequest(@"C:\a\file.bin", to);

        var ok = QuickUploadRequest.TryParse(original.Serialize(), out var parsed);

        Assert.True(ok);
        Assert.Equal(original.Path, parsed.Path);
        Assert.Equal(original.To, parsed.To);
    }

    [Theory]
    [InlineData(QuickUploadDestination.Saved, "saved")]
    [InlineData(QuickUploadDestination.Group, "group")]
    [InlineData(QuickUploadDestination.Ask, "ask")]
    public void Serialize_UsesStableStringTokens_NotEnumNumbers(QuickUploadDestination to, string token)
    {
        var json = new QuickUploadRequest(@"C:\a\file.bin", to).Serialize();

        Assert.Contains($"\"{token}\"", json);
    }

    [Fact]
    public void TryParse_Null_ReturnsFalseAndActivate()
    {
        var ok = QuickUploadRequest.TryParse(null, out var parsed);

        Assert.False(ok);
        Assert.Same(QuickUploadRequest.Activate, parsed);
    }

    [Fact]
    public void TryParse_Empty_ReturnsFalseAndActivate()
    {
        Assert.False(QuickUploadRequest.TryParse("   ", out var parsed));
        Assert.Same(QuickUploadRequest.Activate, parsed);
    }

    [Fact]
    public void TryParse_MalformedJson_ReturnsFalseAndActivate()
    {
        Assert.False(QuickUploadRequest.TryParse("{ not json ", out var parsed));
        Assert.Same(QuickUploadRequest.Activate, parsed);
    }

    [Fact]
    public void TryParse_UnknownToken_DefaultsToAsk()
    {
        var ok = QuickUploadRequest.TryParse("{\"v\":1,\"path\":\"C:\\\\a\\\\f.bin\",\"to\":\"weird\"}", out var parsed);

        Assert.True(ok);
        Assert.Equal(QuickUploadDestination.Ask, parsed.To);
    }

    [Fact]
    public void TryParse_MissingPath_YieldsActivateOnlyRequest()
    {
        var ok = QuickUploadRequest.TryParse("{\"v\":1,\"to\":\"saved\"}", out var parsed);

        Assert.True(ok);
        Assert.True(parsed.IsActivateOnly);
        Assert.Equal(QuickUploadDestination.Saved, parsed.To);
    }

    [Fact]
    public void IsActivateOnly_TrueWhenPathBlank()
    {
        Assert.True(new QuickUploadRequest("", QuickUploadDestination.Ask).IsActivateOnly);
        Assert.True(new QuickUploadRequest("   ", QuickUploadDestination.Saved).IsActivateOnly);
        Assert.True(QuickUploadRequest.Activate.IsActivateOnly);
    }

    [Fact]
    public void IsActivateOnly_FalseWhenPathPresent()
    {
        Assert.False(new QuickUploadRequest(@"C:\a\file.bin", QuickUploadDestination.Ask).IsActivateOnly);
    }
}
