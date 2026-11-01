using System.Globalization;
using System.Text.RegularExpressions;
using BackblazeMonitor.Core;
using Xunit;

namespace BackblazeMonitor.Core.Tests;

/// <summary>Progress PER DISK, on XML shaped like the real reports.</summary>
public class VolumeTests
{
    private const string GE = "{11111111-1111-1111-1111-111111111111}";
    private const string GF = "{22222222-2222-2222-2222-222222222222}";
    private const string GX = "{33333333-3333-3333-3333-333333333333}";

    private const string Info = "<myidentity bzlogin=\"secret@exemple.com\" /><bzvolume bzVolumeGuid=\"" + GE + "\" mountPointPath=\"E:\\\" /><bzvolume mountPointPath=\"f:\\\" bzVolumeGuid=\"" + GF + "\" />";
    private const string Tot = "<totals><bzvolume bzVolumeGuid=\"" + GF + "\" pervol_sel_for_backup_numbytes=\"4000\" /><bzvolume bzVolumeGuid=\"" + GE + "\" pervol_sel_for_backup_numbytes=\"1000\" /><bzvolume bzVolumeGuid=\"" + GX + "\" pervol_sel_for_backup_numbytes=\"9\" /></totals>";
    private const string Rem = "<remaining><bzvolume bzVolumeGuid=\"" + GE + "\" pervol_remaining_files_numbytes=\"250\" /><bzvolume bzVolumeGuid=\"" + GF + "\" pervol_remaining_files_numbytes=\"0\" /><bzvolume bzVolumeGuid=\"" + GX + "\" pervol_remaining_files_numbytes=\"9\" /></remaining>";

    private static readonly IReadOnlyList<VolumeInfo> V = BackupReports.ReadVolumes(Tot, Rem, Info);
    private static readonly DateTime NowDt = new(2026, 10, 2, 12, 0, 0);
    private static readonly long T0 = new DateTimeOffset(NowDt).ToUnixTimeSeconds();

    [Fact]
    public void Disks_letters_sorted_GUID_without_a_letter_dropped() => Assert.Equal("E,F", string.Join(',', V.Select(v => v.Drive)));

    [Fact]
    public void Disks_numbers_re_read() => Assert.Equal("E=1000/250 F=4000/0", string.Join(' ', V.Select(v => $"{v.Drive}={v.Selected}/{v.Remaining}")));

    [Fact]
    public void Disks_lowercase_letter_and_attributes_in_the_other_order() => Assert.Equal("F", V[1].Drive);

    [Fact]
    public void Disks_remaining_missing_means_disk_dropped_never_zero()
    {
        var rem = Regex.Replace(Rem, @"<bzvolume bzVolumeGuid=""\{11111111[^>]*>", "");
        Assert.Single(BackupReports.ReadVolumes(Tot, rem, Info));
    }

    [Fact]
    public void Disks_unreadable_number_means_disk_dropped() =>
        Assert.Single(BackupReports.ReadVolumes(Tot.Replace("numbytes=\"1000\"", "numbytes=\"12x\""), Rem, Info));

    [Fact]
    public void Disks_missing_report_means_no_disk() => Assert.Empty(BackupReports.ReadVolumes(null, null, null));

    [Fact]
    public void Disks_the_email_from_bzinfo_does_not_leak()
    {
        var text = VolumeSamples.GetText(V, Array.Empty<VolumeSample>(), NowDt);
        Assert.DoesNotMatch("secret|@", text);
    }

    [Fact]
    public void Attr_is_case_insensitive_and_missing_is_null()
    {
        Assert.Equal("x", BackupReports.GetAttr("<a FOO=\"x\" />", "foo"));
        Assert.Null(BackupReports.GetAttr("<a foo=\"x\" />", "bar"));
        Assert.Equal("", BackupReports.GetAttr("<a foo=\"\" />", "foo"));
    }

    // ---------- Samples ----------
    private static readonly IReadOnlyList<VolumeSample> S1 = VolumeSamples.Add(Array.Empty<VolumeSample>(), V, T0);

    [Fact]
    public void Samples_one_per_disk_bytes_finished() => Assert.Equal("E=750 F=4000", string.Join(' ', S1.Select(s => $"{s.D}={s.C}")));

    [Fact]
    public void Samples_not_twice_within_the_hour() => Assert.Equal(2, VolumeSamples.Add(S1, V, T0 + 3599).Count);

    [Fact]
    public void Samples_one_more_on_the_hour() => Assert.Equal(4, VolumeSamples.Add(S1, V, T0 + 3600).Count);

    [Fact]
    public void Samples_over_8_days_pruned() =>
        Assert.Empty(VolumeSamples.Add(S1, Array.Empty<VolumeInfo>(), T0 + BzConstants.VolKeepSec + 1));

    [Fact]
    public void Samples_exactly_8_days_kept() =>
        Assert.Equal(2, VolumeSamples.Add(S1, Array.Empty<VolumeInfo>(), T0 + BzConstants.VolKeepSec).Count);

    // ---------- Pace ----------
    private static VolumeSample Smp(long t, string d, long c) => new(t, d, c);

    [Fact]
    public void Pace_a_single_sample_is_unknown() => Assert.Null(VolumeSamples.GetRate(new[] { Smp(0, "E", 0) }, "E", 100));

    [Fact]
    public void Pace_under_an_hour_is_unknown() => Assert.Null(VolumeSamples.GetRate(new[] { Smp(0, "E", 0), Smp(3599, "E", 999) }, "E", 4000));

    [Fact]
    public void Pace_exactly_one_hour() => Assert.Equal(2.0, VolumeSamples.GetRate(new[] { Smp(0, "E", 0), Smp(3600, "E", 7200) }, "E", 4000));

    [Fact]
    public void Pace_a_drop_shrinking_selection_does_not_count()
    {
        const long h = 3600;
        var s = new[] { Smp(0, "E", 0), Smp(h, "E", 3600), Smp(2 * h, "E", 100), Smp(3 * h, "E", 3700) };
        Assert.Equal(7200.0 / 10800, VolumeSamples.GetRate(s, "E", 11000));
    }

    [Fact]
    public void Pace_other_disks_do_not_mix_in()
    {
        const long h = 3600;
        var s = new[] { Smp(0, "E", 0), Smp(0, "F", 0), Smp(h, "F", 9999), Smp(h, "E", 3600) };
        Assert.Equal(1.0, VolumeSamples.GetRate(s, "E", 4000));
    }

    [Fact]
    public void Pace_only_the_last_7_days_count()
    {
        const long now7 = 8 * 86400;
        var s = new[] { Smp(0, "E", 0), Smp(86400, "E", 1000000), Smp(now7 - 7 * 86400, "E", 1000000), Smp(now7, "E", 1000000 + 86400) };
        Assert.Equal(86400.0 / (7 * 86400), VolumeSamples.GetRate(s, "E", now7));
    }

    // ---------- Text ----------
    private const long G = 1024L * 1024 * 1024;
    private static readonly long Sec = new DateTimeOffset(NowDt).ToUnixTimeSeconds();
    private static readonly VolumeInfo[] Vg = { new("E", 1000 * G, 250 * G), new("F", 4000 * G, 0) };

    private static string[] Lines(IReadOnlyList<VolumeInfo> vols, IReadOnlyList<VolumeSample> samples) =>
        VolumeSamples.GetText(vols, samples, NowDt).Split("\r\n");

    private static string Squash(string s) => Regex.Replace(s, @"\s+", " ");

    [Fact]
    public void Text_header_and_rows()
    {
        var sm = new[] { Smp(Sec - 2 * 86400, "E", 650 * G), Smp(Sec, "E", 750 * G) };
        var txt = Lines(Vg, sm);
        Assert.Equal("Drive    Sent          Left    Rate 7 d         ETA", txt[0]);
        Assert.Equal("E: 75.0 % 250.00 GB 50.00 GB/d done ~5 d", Squash(txt[1]));
        Assert.Equal("F: 100.0 % 0 KB — up to date", Squash(txt[2]));
        Assert.Equal("", txt[3]);
        Assert.Equal("Sent = sent, or attached to a copy that is already there (0 bytes).", txt[4]);
        Assert.Equal("The ETA applies to what is selected now, at the pace of the last 7 days.", txt[5]);
    }

    [Fact]
    public void Text_columns_are_aligned()
    {
        var sm = new[] { Smp(Sec - 2 * 86400, "E", 650 * G), Smp(Sec, "E", 750 * G) };
        Assert.Equal("E:      75.0 %   250.00 GB    50.00 GB/d   done ~5 d", Lines(Vg, sm)[1]);
    }

    [Fact]
    public void Text_no_sample_pace_and_finish_unknown() =>
        Assert.Equal("E: 75.0 % 250.00 GB — —", Squash(Lines(Vg, Array.Empty<VolumeSample>())[1]));

    [Fact]
    public void Text_remaining_but_zero_pace_means_finish_unknown() =>
        Assert.Equal("E: 75.0 % 250.00 GB 0 KB/d —", Squash(Lines(Vg, new[] { Smp(Sec - 86400, "E", 750 * G), Smp(Sec, "E", 750 * G) })[1]));

    [Fact]
    public void Text_no_disk() =>
        Assert.Equal("Backblaze report unreadable: no disk", VolumeSamples.GetText(Array.Empty<VolumeInfo>(), Array.Empty<VolumeSample>(), NowDt));

    [Fact]
    public void Text_nothing_selected() =>
        Assert.Equal("V:       nothing selected", Lines(new[] { new VolumeInfo("V", 0, 0) }, Array.Empty<VolumeSample>())[1]);

    // ---------- File ----------
    [Fact]
    public void File_missing_write_re_read_damaged_lines_and_failure()
    {
        var dir = Directory.CreateTempSubdirectory("bbm-vol").FullName;
        try
        {
            var path = Path.Combine(dir, "volumes.txt");
            Assert.Empty(VolumeSamples.Read(path));
            Assert.True(VolumeSamples.Save(path, S1));
            var relu = VolumeSamples.Read(path);
            Assert.Equal(string.Join(';', S1.Select(s => $"{s.T} {s.D} {s.C}")), string.Join(';', relu.Select(s => $"{s.T} {s.D} {s.C}")));
            Assert.Equal($"{T0}\tE\t750", File.ReadAllLines(path)[0]);
            File.AppendAllText(path, "abime\r\n12\tzz\t5\r\n");
            Assert.Equal(2, VolumeSamples.Read(path).Count);
            Assert.False(VolumeSamples.Save(Path.Combine(dir, "absent", "volumes.txt"), S1));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // BAC466.35: a failed write is retried on later ticks, not only when the report changes again
    [Fact]
    public void A_failed_volumes_write_stays_pending_until_it_succeeds()
    {
        var dir = Directory.CreateTempSubdirectory("bbm-vol3").FullName;
        try
        {
            var later = Path.Combine(dir, "later");
            var path = Path.Combine(later, "volumes.txt");
            var store = new VolumeSampleStore(path);
            Assert.False(store.Record(V, T0)); // folder missing: not written
            Assert.True(store.Pending);
            store.RetryPending();
            Assert.True(store.Pending); // still failing
            Directory.CreateDirectory(later);
            store.RetryPending();
            Assert.False(store.Pending);
            var again = new VolumeSampleStore(path);
            again.Load();
            Assert.Equal(store.Samples.Count, again.Samples.Count);
            Assert.NotEmpty(again.Samples);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Store_records_at_most_one_sample_per_disk_per_hour_and_saves()
    {
        var dir = Directory.CreateTempSubdirectory("bbm-vol2").FullName;
        try
        {
            var path = Path.Combine(dir, "volumes.txt");
            var store = new VolumeSampleStore(path);
            store.Load();
            Assert.Empty(store.Samples);
            Assert.False(store.Record(Array.Empty<VolumeInfo>(), T0));
            Assert.True(store.Record(V, T0));
            Assert.True(store.Record(V, T0 + 10));
            Assert.Equal(2, store.Samples.Count);
            var again = new VolumeSampleStore(path);
            again.Load();
            Assert.Equal(2, again.Samples.Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void An_unreadable_volumes_file_is_not_overwritten_and_is_merged_once_readable()
    {
        var dir = Directory.CreateTempSubdirectory("bbm-vol-locked").FullName;
        try
        {
            var path = Path.Combine(dir, "volumes.txt");
            Directory.CreateDirectory(path); // exists, cannot be read as a file
            Assert.Null(VolumeSamples.TryRead(path));
            var store = new VolumeSampleStore(path);
            store.Load();
            Assert.False(store.Record(new[] { new VolumeInfo("E", 1000, 100) }, T0 + 5000));
            Assert.True(store.Pending);
            Assert.True(Directory.Exists(path)); // nothing written over it

            Directory.Delete(path);
            Assert.True(VolumeSamples.Save(path, S1));
            store.RetryPending();
            Assert.False(store.Pending);
            var back = VolumeSamples.Read(path);
            Assert.Equal(S1.Count + 1, back.Count); // the old history is still there, plus the new sample
            Assert.Contains(back, s => s.T == T0 + 5000);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_write_that_fails_leaves_the_previous_file_whole_and_no_temp_file()
    {
        var dir = Directory.CreateTempSubdirectory("bbm-atomic").FullName;
        try
        {
            var path = Path.Combine(dir, "volumes.txt");
            Assert.True(VolumeSamples.Save(path, S1));
            var before = File.ReadAllText(path);
            Directory.CreateDirectory(path + ".tmp"); // the temp name is taken: the write cannot happen
            Assert.False(VolumeSamples.Save(path, S1.Take(1)));
            Assert.Equal(before, File.ReadAllText(path));
            Directory.Delete(path + ".tmp");
            Assert.True(VolumeSamples.Save(path, S1.Take(1)));
            Assert.Single(File.ReadAllLines(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---------- Remaining tracker ----------
    [Fact]
    public void Remaining_tracker_caches_by_timestamp_and_retries_a_failure()
    {
        var t = new RemainingTracker();
        var stamp = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(RemainingRefresh.Missing, t.Refresh(null, () => null));
        Assert.Null(t.Bytes);
        Assert.Equal(RemainingRefresh.Unreadable, t.Refresh(stamp, () => "<truncated"));
        Assert.Null(t.Bytes);
        // the timestamp was not remembered: read again
        var calls = 0;
        Assert.Equal(RemainingRefresh.Updated, t.Refresh(stamp, () => { calls++; return "<r remainingnumfilesforbackup=\"7\" remainingnumbytesforbackup=\"42\" />"; }));
        Assert.Equal(1, calls);
        Assert.Equal(42L, t.Bytes);
        Assert.Equal(7L, t.Files);
        Assert.Equal(RemainingRefresh.Unchanged, t.Refresh(stamp, () => { calls++; return null; }));
        Assert.Equal(1, calls);
        Assert.Equal(42L, t.Bytes);
        // a rewrite that cannot be read: unknown, never the stale figure as 0
        Assert.Equal(RemainingRefresh.Unreadable, t.Refresh(stamp.AddHours(1), () => ""));
        Assert.Null(t.Bytes);
        Assert.Null(t.Files);
        // the report disappears: the file count goes with the bytes
        t.Refresh(stamp.AddHours(2), () => "<r remainingnumfilesforbackup=\"7\" remainingnumbytesforbackup=\"42\" />");
        Assert.Equal(7L, t.Files);
        Assert.Equal(RemainingRefresh.Missing, t.Refresh(null, () => null));
        Assert.Null(t.Files);
    }
}
