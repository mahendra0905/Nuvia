using System.Linq;
using System.Text;
using Nuvia.App.Services;
using Xunit;

namespace Nuvia.Tests;

/// <summary>
/// Tests for <see cref="NuviaMarkers"/> — the identification tags Nuvia writes into Telegram so files
/// and the managed group survive a reinstall. Pure string logic: no network, no credentials.
/// </summary>
public sealed class NuviaMarkersTests
{
    [Fact]
    public void CaptionMarker_IsBuiltOnlyFromZeroWidthFormatCharacters()
    {
        var marker = NuviaMarkers.SavedMessageCaptionMarker;

        Assert.NotEmpty(marker);
        // Every code point must be one of the two invisible format characters we chose, so the caption
        // renders as truly empty in Telegram.
        Assert.All(marker, ch => Assert.True(ch == (char)0x2060 || ch == (char)0x200D,
            $"Unexpected caption-marker code point U+{(int)ch:X4}; only U+2060 and U+200D are allowed."));
    }

    [Fact]
    public void BuildUploadCaption_IsRecognisedAsNuvia()
    {
        var caption = NuviaMarkers.BuildUploadCaption();
        Assert.True(NuviaMarkers.CaptionIndicatesNuvia(caption));
    }

    [Fact]
    public void CaptionIndicatesNuvia_IsFalseForEmptyNullAndPlainText()
    {
        Assert.False(NuviaMarkers.CaptionIndicatesNuvia(null));
        Assert.False(NuviaMarkers.CaptionIndicatesNuvia(string.Empty));
        Assert.False(NuviaMarkers.CaptionIndicatesNuvia("just a normal caption"));
    }

    [Fact]
    public void CaptionIndicatesNuvia_FindsTheMarkerEvenWhenSurroundedByOtherText()
    {
        var caption = "prefix" + NuviaMarkers.SavedMessageCaptionMarker + "suffix";
        Assert.True(NuviaMarkers.CaptionIndicatesNuvia(caption));
    }

    [Fact]
    public void GroupAboutText_CarriesTheMarkerAndFitsTelegramLimit()
    {
        Assert.Contains(NuviaMarkers.GroupAboutMarker, NuviaMarkers.GroupAboutText);
        Assert.True(NuviaMarkers.AboutIndicatesNuviaGroup(NuviaMarkers.GroupAboutText));
        // Telegram caps a channel's about at 255 characters; stay well under it.
        Assert.True(NuviaMarkers.GroupAboutText.Length <= 255);
    }

    [Fact]
    public void AboutIndicatesNuviaGroup_IsFalseForUnrelatedDescriptions()
    {
        Assert.False(NuviaMarkers.AboutIndicatesNuviaGroup(null));
        Assert.False(NuviaMarkers.AboutIndicatesNuviaGroup(string.Empty));
        Assert.False(NuviaMarkers.AboutIndicatesNuviaGroup("A perfectly ordinary group description."));
    }

    // ------------------------------------------------------ folder path marker

    [Fact]
    public void BuildUploadCaption_WithNoFolder_IsByteIdenticalToTheBareMarker()
    {
        // A root-level upload must send exactly what every pre-folders build sent, so existing files keep
        // being recognised byte-for-byte and nothing visible changes on Telegram.
        Assert.Equal(NuviaMarkers.SavedMessageCaptionMarker, NuviaMarkers.BuildUploadCaption());
        Assert.Equal(NuviaMarkers.SavedMessageCaptionMarker, NuviaMarkers.BuildUploadCaption(null));
        Assert.Equal(NuviaMarkers.SavedMessageCaptionMarker, NuviaMarkers.BuildUploadCaption(string.Empty));

        // The bare marker carries no folder, so a reinstall drops the file at the location root.
        Assert.Null(NuviaMarkers.TryReadFolderPath(NuviaMarkers.SavedMessageCaptionMarker));
    }

    [Theory]
    [InlineData("Work")]
    [InlineData("Work/Invoices")]
    [InlineData("Work/Invoices/2026/Q1")]
    [InlineData("Проекты/Счета")]
    [InlineData("日本語/請求書")]
    [InlineData("a b/c d e")] // internal spaces are fine; only surrounding ones are rejected
    public void BuildUploadCaption_RoundTripsAFolderPath(string path)
    {
        var caption = NuviaMarkers.BuildUploadCaption(path);
        Assert.Equal(path, NuviaMarkers.TryReadFolderPath(caption));
    }

    [Fact]
    public void FolderCaption_IsStillRecognisedAsNuvia_AndRendersInvisible()
    {
        var caption = NuviaMarkers.BuildUploadCaption("Work/Invoices");

        Assert.True(NuviaMarkers.CaptionIndicatesNuvia(caption));
        // Marker (U+2060/U+200D) plus payload (those two plus U+200B/U+200C) — every code point zero-width.
        Assert.All(caption, ch => Assert.True(
            ch is (char)0x2060 or (char)0x200D or (char)0x200B or (char)0x200C,
            $"Unexpected caption code point U+{(int)ch:X4}; the folder caption must be all zero-width."));
    }

    [Fact]
    public void TryReadFolderPath_ReturnsNull_ForEveryMalformedCaption()
    {
        var marker = NuviaMarkers.SavedMessageCaptionMarker;

        Assert.Null(NuviaMarkers.TryReadFolderPath(null));
        Assert.Null(NuviaMarkers.TryReadFolderPath(string.Empty));
        Assert.Null(NuviaMarkers.TryReadFolderPath("just a normal caption")); // no marker at all
        Assert.Null(NuviaMarkers.TryReadFolderPath(marker + "abc"));           // length not a multiple of 4
        Assert.Null(NuviaMarkers.TryReadFolderPath(marker + "wxyz"));          // symbols outside the alphabet
        Assert.Null(NuviaMarkers.TryReadFolderPath(marker + EncodePayload(new byte[] { 0xFF }))); // bad UTF-8
        // A payload that decodes cleanly but names an illegal segment ("." is a reserved relative token).
        Assert.Null(NuviaMarkers.TryReadFolderPath(marker + EncodePayload(Encoding.UTF8.GetBytes("."))));
    }

    [Fact]
    public void BuildUploadCaption_RefusesAPathThatIsTooLongToEncode()
    {
        // Four 60-char segments (243 UTF-8 bytes) are a valid, in-depth path but blow the caption budget.
        var tooLong = string.Join("/", Enumerable.Repeat(new string('a', FolderNames.MaxNameLength), 4));
        Assert.False(FolderNames.IsEncodablePath(tooLong));
        Assert.Throws<FolderPathTooLongException>(() => NuviaMarkers.BuildUploadCaption(tooLong));
    }

    // The production encoder is private; this mirror lets a test craft deliberately malformed payloads.
    private static readonly char[] PayloadAlphabet = { (char)0x2060, (char)0x200D, (char)0x200B, (char)0x200C };

    private static string EncodePayload(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 4);
        foreach (var b in bytes)
        {
            sb.Append(PayloadAlphabet[(b >> 6) & 0b11]);
            sb.Append(PayloadAlphabet[(b >> 4) & 0b11]);
            sb.Append(PayloadAlphabet[(b >> 2) & 0b11]);
            sb.Append(PayloadAlphabet[b & 0b11]);
        }
        return sb.ToString();
    }
}
