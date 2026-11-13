using System.Globalization;
using ExplorerPlus.Core.Shell;

namespace ExplorerPlus.Core.Tests;

public class SizeFormatTests
{
    [Theory]
    [InlineData(0L, "0 KB")]
    [InlineData(1L, "1 KB")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1025L, "2 KB")]
    [InlineData(999L * 1024, "999 KB")]
    [InlineData(1000L * 1024, "0.98 MB")]
    [InlineData(13_055_308L, "12.45 MB")]
    [InlineData(4_992_871_219L, "4.65 GB")]
    [InlineData(1024L * 1024 * 1024 - 1, "1.00 GB")]
    [InlineData(3L * 1024 * 1024 * 1024 * 1024, "3.00 TB")]
    public void Sizes_use_the_largest_unit_under_1000(long bytes, string expected)
    {
        var old = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { Assert.Equal(expected, SizeFormat.Format(bytes)); }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void Small_sizes_can_be_given_in_bytes()
    {
        var old = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal("512 bytes", SizeFormat.FormatWithBytes(512));
            Assert.Equal("2 KB", SizeFormat.FormatWithBytes(2048));
        }
        finally { CultureInfo.CurrentCulture = old; }
    }
}
