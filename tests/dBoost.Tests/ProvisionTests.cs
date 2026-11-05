using System.Security.Cryptography;
using System.Text;
using DBoost.Core;

namespace DBoost.Tests;

public class ProvisionTests
{
    private static (byte[] Data, string Hash) Payload(string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        return (data, Convert.ToHexStringLower(SHA256.HashData(data)));
    }

    [Fact]
    public void Unpacks_once_with_licence_and_reuses_it_after()
    {
        string dir = Directory.CreateTempSubdirectory("dboost-prov-").FullName;
        try
        {
            var (data, hash) = Payload("pretend this is ffmpeg");
            int opened = 0;
            string exe = FfmpegLocator.Provision(hash, () => { opened++; return new MemoryStream(data); }, dir,
                () => new MemoryStream(Encoding.UTF8.GetBytes("LGPL")));

            Assert.Equal(data, File.ReadAllBytes(exe));
            Assert.Equal("LGPL", File.ReadAllText(Path.Combine(Path.GetDirectoryName(exe)!, "LICENSE.txt")));
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, ".complete")));

            string again = FfmpegLocator.Provision(hash, () => { opened++; return new MemoryStream(data); }, dir);
            Assert.Equal(exe, again);
            Assert.Equal(1, opened);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_corrupt_payload_is_rejected_and_not_left_behind()
    {
        string dir = Directory.CreateTempSubdirectory("dboost-prov-").FullName;
        try
        {
            var (_, hash) = Payload("the real thing");
            var ex = Assert.Throws<InvalidOperationException>(() =>
                FfmpegLocator.Provision(hash, () => new MemoryStream(Encoding.UTF8.GetBytes("something else")), dir));
            Assert.Contains("checksum", ex.Message);
            Assert.Empty(Directory.GetFiles(dir, "ffmpeg.exe*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_new_version_replaces_the_old_folder()
    {
        string dir = Directory.CreateTempSubdirectory("dboost-prov-").FullName;
        try
        {
            var (a, ha) = Payload("version one");
            var (b, hb) = Payload("version two");
            string first = FfmpegLocator.Provision(ha, () => new MemoryStream(a), dir);
            string second = FfmpegLocator.Provision(hb, () => new MemoryStream(b), dir);
            Assert.NotEqual(Path.GetDirectoryName(first), Path.GetDirectoryName(second));
            Assert.False(File.Exists(first));
            Assert.Single(Directory.GetDirectories(Path.Combine(dir, "ffmpeg")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Folders_are_searched_for_media_only()
    {
        string dir = Directory.CreateTempSubdirectory("dboost-expand-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "sub"));
            File.WriteAllText(Path.Combine(dir, "a.mp3"), "");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "");
            File.WriteAllText(Path.Combine(dir, "sub", "b.MKV"), "");
            string loose = Path.Combine(dir, "picked.txt");
            File.WriteAllText(loose, "");

            var found = MediaExtensions.Expand([dir, loose]).Select(Path.GetFileName).ToList();
            Assert.Equal(["a.mp3", "b.MKV", "picked.txt"], found); // a file you pick yourself is always tried
        }
        finally { Directory.Delete(dir, true); }
    }
}
