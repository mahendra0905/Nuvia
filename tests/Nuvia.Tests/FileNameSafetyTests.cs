using Nuvia.App.Services;
using Xunit;

namespace Nuvia.Tests;

/// <summary>
/// Tests for <see cref="FileNameSafety"/>. A remote filename is untrusted input, so these tests
/// pin down that directory traversal, path syntax, control characters, Windows-reserved device
/// names and absurd lengths can never survive into a local path component. No network, no
/// credentials, no filesystem writes.
/// </summary>
public sealed class FileNameSafetyTests
{
    [Fact]
    public void PlainName_IsUnchanged()
    {
        Assert.Equal("report.pdf", FileNameSafety.ToSafeFileName("report.pdf"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrBlank_FallsBackToDefault(string? input)
    {
        Assert.Equal("download", FileNameSafety.ToSafeFileName(input));
    }

    [Fact]
    public void EmptyResult_UsesProvidedFallback()
    {
        Assert.Equal("myfile", FileNameSafety.ToSafeFileName("   ", fallback: "myfile"));
    }

    [Theory]
    [InlineData("..\\..\\Windows\\System32\\evil.dll", "evil.dll")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\Users\\me\\secret.txt", "secret.txt")]
    [InlineData("folder/sub/file.txt", "file.txt")]
    public void DirectoryComponents_AreStripped(string input, string expected)
    {
        var result = FileNameSafety.ToSafeFileName(input);
        Assert.Equal(expected, result);
        Assert.DoesNotContain('/', result);
        Assert.DoesNotContain('\\', result);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void RelativeTokens_NeverSurvive(string input)
    {
        // A bare "." or ".." must collapse to the fallback, never a traversal token.
        Assert.Equal("download", FileNameSafety.ToSafeFileName(input));
    }

    [Fact]
    public void InvalidWindowsCharacters_AreRemoved()
    {
        var result = FileNameSafety.ToSafeFileName("a:b*c?d\"e<f>g|h.txt");
        foreach (var bad in new[] { ':', '*', '?', '"', '<', '>', '|' })
            Assert.DoesNotContain(bad, result);
        Assert.Equal("abcdefgh.txt", result);
    }

    [Fact]
    public void ControlCharacters_AreRemoved()
    {
        var result = FileNameSafety.ToSafeFileName("na\u0000me\u0007.txt");
        Assert.Equal("name.txt", result);
    }

    [Fact]
    public void TrailingDotsAndSpaces_AreTrimmed()
    {
        // Windows silently drops these, which could let two names collide on disk.
        Assert.Equal("file", FileNameSafety.ToSafeFileName("file. . ."));
        Assert.Equal("file.txt", FileNameSafety.ToSafeFileName("  file.txt  "));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("NUL.txt")]
    [InlineData("COM1")]
    [InlineData("LPT9.log")]
    public void ReservedDeviceNames_ArePrefixed(string input)
    {
        var result = FileNameSafety.ToSafeFileName(input);
        Assert.StartsWith("_", result);
    }

    [Fact]
    public void OverlongName_IsCappedButKeepsExtension()
    {
        var stem = new string('a', 500);
        var result = FileNameSafety.ToSafeFileName(stem + ".txt");
        Assert.True(result.Length <= 100, $"length was {result.Length}");
        Assert.EndsWith(".txt", result);
    }

    [Fact]
    public void IsSafeFileName_TrueForAlreadySafeName()
    {
        Assert.True(FileNameSafety.IsSafeFileName("report.pdf"));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("folder\\file.txt")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("bad:name")]
    public void IsSafeFileName_FalseForUnsafeName(string? input)
    {
        Assert.False(FileNameSafety.IsSafeFileName(input));
    }

    [Fact]
    public void SanitisedOutput_IsAlwaysReportedSafe()
    {
        // Whatever comes out of ToSafeFileName must itself pass IsSafeFileName.
        foreach (var messy in new[]
                 {
                     "..\\..\\x", "a:b*c?.txt", "CON", "  ", ".", new string('z', 300) + ".dat",
                 })
        {
            var safe = FileNameSafety.ToSafeFileName(messy);
            Assert.True(FileNameSafety.IsSafeFileName(safe),
                $"ToSafeFileName({messy!}) produced '{safe}', which IsSafeFileName rejected.");
        }
    }
}
