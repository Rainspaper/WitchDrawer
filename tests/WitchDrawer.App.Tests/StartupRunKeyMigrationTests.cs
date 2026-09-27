using WitchDrawer.Native.Windows;

namespace WitchDrawer.App.Tests;

public class StartupRunKeyMigrationTests
{
    private sealed class Capture
    {
        public string? ReadValue { get; init; }
        public string? WrittenValue { get; set; }
    }

    [Fact]
    public void EnsureSilentArgument_AppendsSilentToLegacyValue()
    {
        var capture = new Capture
        {
            ReadValue = @"""C:\Program Files\WitchDrawer\WitchDrawer.exe"""
        };

        var result = StartupRunKeyMigration.EnsureSilentArgument(
            @"C:\Program Files\WitchDrawer\WitchDrawer.exe",
            "--silent",
            () => capture.ReadValue,
            value => capture.WrittenValue = value);

        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(
            @"""C:\Program Files\WitchDrawer\WitchDrawer.exe"" --silent",
            capture.WrittenValue);
    }

    [Fact]
    public void EnsureSilentArgument_SkipsWhenAlreadyHasSilent()
    {
        var capture = new Capture
        {
            ReadValue = @"""C:\Program Files\WitchDrawer\WitchDrawer.exe"" --silent"
        };

        var result = StartupRunKeyMigration.EnsureSilentArgument(
            @"C:\Program Files\WitchDrawer\WitchDrawer.exe",
            "--silent",
            () => capture.ReadValue,
            _ => throw new InvalidOperationException("should not write"));

        Assert.Equal(0, result.UpdatedCount);
        Assert.Null(capture.WrittenValue);
    }

    [Fact]
    public void EnsureSilentArgument_SkipsWhenExecutableDoesNotMatch()
    {
        var capture = new Capture
        {
            ReadValue = @"""C:\Other\NotWitchDrawer.exe"""
        };

        var result = StartupRunKeyMigration.EnsureSilentArgument(
            @"C:\Program Files\WitchDrawer\WitchDrawer.exe",
            "--silent",
            () => capture.ReadValue,
            _ => throw new InvalidOperationException("should not write"));

        Assert.Equal(0, result.UpdatedCount);
        Assert.Null(capture.WrittenValue);
    }

    [Fact]
    public void EnsureSilentArgument_SkipsWhenValueMissing()
    {
        var result = StartupRunKeyMigration.EnsureSilentArgument(
            @"C:\Program Files\WitchDrawer\WitchDrawer.exe",
            "--silent",
            () => null,
            _ => throw new InvalidOperationException("should not write"));

        Assert.Equal(0, result.UpdatedCount);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void EnsureSilentArgument_ReturnsEmptyForNullExecutablePath()
    {
        var result = StartupRunKeyMigration.EnsureSilentArgument(
            null,
            "--silent",
            () => "irrelevant",
            _ => throw new InvalidOperationException("should not write"));

        Assert.Equal(0, result.UpdatedCount);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData(@"""C:\a\b.exe""", true, @"C:\a\b.exe")]
    [InlineData(@"C:\a\b.exe", true, @"C:\a\b.exe")]
    [InlineData(@"""C:\a b\b.exe"" --silent", true, @"C:\a b\b.exe")]
    [InlineData(@"""""", false, null)]
    [InlineData(@"""unclosed", false, null)]
    public void TryGetExecutablePath_ParsesCommandLine(
        string commandLine,
        bool expected,
        string? expectedPath)
    {
        var ok = StartupRunKeyMigration.TryGetExecutablePath(
            commandLine,
            out var executablePath);

        Assert.Equal(expected, ok);
        if (expected)
        {
            Assert.Equal(expectedPath, executablePath);
        }
    }
}