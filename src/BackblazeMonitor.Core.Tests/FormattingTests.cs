using System.Globalization;
using BackblazeMonitor.Core;
using Xunit;

namespace BackblazeMonitor.Core.Tests;

public class FormattingTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0);
    private const double GB = 1024.0 * 1024 * 1024;
    private const double TB = GB * 1024;

    // ---------- Reading the report ----------
    private const string Xml = "<contents><remaining remainingnumfilesforbackup=\"189379\" remainingnumbytesforbackup=\"96575689374142\" /></contents>";

    [Fact]
    public void Remaining_bytes_and_files_are_read()
    {
        var r = BackupReports.ReadRemaining(Xml);
        Assert.NotNull(r);
        Assert.Equal(96575689374142L, r!.Bytes);
        Assert.Equal(189379L, r.Files);
    }

    [Fact]
    public void Truncated_report_is_unknown_not_zero() =>
        Assert.Null(BackupReports.ReadRemaining("<remaining remainingnumfilesforbackup=\"1\""));

    [Theory]
    [InlineData("<r remainingnumfilesforbackup=\"189379\" remainingnumbytesforbackup=\"9657")]  // cut in the middle of the byte count
    [InlineData("<r remainingnumfilesforbackup=\"189379\" remainingnumbytesforbackup=\"")]      // cut right after the opening quote
    [InlineData("<r remainingnumbytesforbackup=\"")]
    public void A_report_cut_in_the_middle_of_the_byte_count_is_unknown_not_a_small_number(string cut) =>
        Assert.Null(BackupReports.ReadRemaining(cut));

    [Fact]
    public void A_report_cut_in_the_file_count_keeps_the_bytes_and_drops_the_files()
    {
        var r = BackupReports.ReadRemaining("<r remainingnumbytesforbackup=\"5\" remainingnumfilesforbackup=\"18");
        Assert.Equal(5L, r!.Bytes);
        Assert.Null(r.Files);
    }

    [Fact]
    public void Missing_report_is_unknown() => Assert.Null(BackupReports.ReadRemaining(null));

    [Fact]
    public void Remaining_zero_is_read_as_zero() =>
        Assert.Equal(0L, BackupReports.ReadRemaining("<remaining remainingnumfilesforbackup=\"0\" remainingnumbytesforbackup=\"0\" />")!.Bytes);

    [Fact]
    public void Remaining_without_file_count_has_null_files()
    {
        var r = BackupReports.ReadRemaining("<r remainingnumbytesforbackup=\"5\" />");
        Assert.Equal(5L, r!.Bytes);
        Assert.Null(r.Files);
    }

    // ---------- Finish time ----------
    [Fact]
    public void Finish_today() => Assert.Equal("done 13:00", Formatting.FormatEta(3600, Now));

    [Fact]
    public void Finish_tomorrow() => Assert.Equal("done tomorrow 01:00", Formatting.FormatEta(13 * 3600, Now));

    [Fact]
    public void Finish_in_days() => Assert.Equal("done ~10 d", Formatting.FormatEta(10 * 86400, Now));

    [Fact]
    public void Finish_in_years() => Assert.Equal("done ~12.2 yr", Formatting.FormatEta(12.2 * 365.25 * 86400, Now));

    [Fact]
    public void Finish_capped() => Assert.Equal("done > 100 yr", Formatting.FormatEta(1e12, Now));

    // ---------- Displayed line ----------
    [Fact]
    public void Remaining_unknown() =>
        Assert.Equal("Remaining: unknown (Backblaze report unreadable)", Formatting.GetRemainingText(null, null, 0, Now));

    [Fact]
    public void Remaining_up_to_date() => Assert.Equal("Backup up to date", Formatting.GetRemainingText(0, 0, 1000, Now));

    [Fact]
    public void Remaining_no_rate_no_finish_time() =>
        Assert.Equal("Remaining 1.00 TB - 5 files", Formatting.GetRemainingText((long)TB, 5, 0, Now));

    [Fact]
    public void Remaining_with_rate() =>
        Assert.Equal("Remaining 1.00 GB - 12 files - done 14:00", Formatting.GetRemainingText((long)GB, 12, GB / 7200, Now));

    [Fact]
    public void Remaining_files_use_thousands_separator() =>
        Assert.Equal("Remaining 4.8 MB - 189,379 files", Formatting.GetRemainingText(5_000_000, 189379, 0, Now));

    // ---------- Notification area ----------
    [Theory]
    [InlineData("12.3 Mbps")]
    [InlineData("5 MB/s")]
    public void Tray_tooltip_state_and_rate(string rate) =>
        Assert.Equal("Backblaze: Running\n" + rate, Formatting.GetTrayText("Running", rate));

    [Fact]
    public void Tray_tooltip_capped_at_the_NotifyIcon_limit()
    {
        Assert.Equal(BzConstants.TrayTextMax, Formatting.GetTrayText(new string('x', 200), "1.0 Mbps").Length);
        Assert.Equal(BzConstants.TrayTextMax, Formatting.CapTrayText(new string('y', 500)).Length); // what the tray controller applies to any text
        Assert.Equal("short", Formatting.CapTrayText("short"));
    }

    // ---------- Rates and sizes ----------
    [Theory]
    [InlineData(2e6, true, "2.0 Mbps")]
    [InlineData(4.89e6, true, "4.9 Mbps")]
    [InlineData(262e3, true, "262 kbps")]
    [InlineData(0, true, "0 kbps")]
    [InlineData(999, true, "0 kbps")]
    [InlineData(80000, true, "80 kbps")]
    [InlineData(1045000, true, "1.0 Mbps")]
    [InlineData(2e6, false, "244 KB/s")]
    [InlineData(800000, false, "98 KB/s")]
    [InlineData(0, false, "0 KB/s")]
    [InlineData(9e6, false, "1.1 MB/s")]
    public void Rate(double bits, bool asBits, string want) => Assert.Equal(want, Formatting.FormatRate(bits, asBits));

    [Theory]
    [InlineData(5e6, "4.8 MB")]
    [InlineData(0.0, "0 KB")]
    [InlineData(1023.0, "0 KB")]
    [InlineData(2048.0, "2 KB")]
    [InlineData(5242880.0, "5.0 MB")]
    [InlineData(1073741824.0, "1.00 GB")]
    [InlineData(1099511627776.0, "1.00 TB")]
    [InlineData(1073741824.0 * 999.99, "999.99 GB")]
    public void Size(double bytes, string want) => Assert.Equal(want, Formatting.FormatSize(bytes));

    [Fact]
    public void Widest_remaining_lines_still_use_invariant_separators()
    {
        Assert.Equal("Remaining 87.84 TB - 189,379 files - done ~12 d",
            Formatting.GetRemainingText(96575689374142, 189379, 96575689374142 / (12.4 * 86400), Now));
        Assert.Equal("999.99 GB over 24 h  -  average 999.9 Mbps",
            Formatting.FormatSize(999.99 * GB) + " over 24 h  -  average " + Formatting.FormatRate(999.9e6, true));
    }

    [Fact]
    public void Formatting_ignores_the_machine_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-CA");
            Assert.Equal("Remaining 4.8 MB - 189,379 files - done 13:00",
                Formatting.GetRemainingText(5_000_000, 189379, 5_000_000 / 3600.0, Now));
            Assert.Equal("12.3 Mbps", Formatting.FormatRate(12.3e6, true));
            Assert.Equal("Thu 24, 23:10", Formatting.FormatWhen(new DateTime(2026, 9, 24, 23, 10, 0), Now));
            Assert.Equal("Backblaze: x\ny", Formatting.GetTrayText("x", "y"));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void When_is_time_today_and_english_day_otherwise()
    {
        Assert.Equal("11:58", Formatting.FormatWhen(new DateTime(2026, 9, 25, 11, 58, 0), Now));
        Assert.Equal("Thu 24, 23:10", Formatting.FormatWhen(new DateTime(2026, 9, 24, 23, 10, 0), Now));
    }
}
