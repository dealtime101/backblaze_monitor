using BackblazeMonitor.Core;
using Xunit;

namespace BackblazeMonitor.Core.Tests;

public sealed class FileRotationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bbm-rot").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void A_file_over_the_limit_moves_to_old_replacing_the_previous_old_one()
    {
        var path = Path.Combine(_dir, "error.log");
        File.WriteAllText(path + ".old", "ancient");
        File.WriteAllText(path, new string('x', 101));
        Assert.True(FileRotation.RotateIfLarger(path, 100));
        Assert.False(File.Exists(path));
        Assert.Equal(101, File.ReadAllText(path + ".old").Length);
    }

    [Fact]
    public void A_small_or_missing_file_is_left_alone()
    {
        var path = Path.Combine(_dir, "error.log");
        Assert.False(FileRotation.RotateIfLarger(path, 100));
        File.WriteAllText(path, new string('x', 100));
        Assert.False(FileRotation.RotateIfLarger(path, 100)); // exactly at the limit: kept
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".old"));
    }
}
