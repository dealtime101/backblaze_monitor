using BackblazeMonitor.Core;
using Xunit;

namespace BackblazeMonitor.Core.Tests;

public class VacationTests
{
    // ---------- State, from the output of the remote "status" ----------
    [Fact]
    public void Active_hold_reads_the_time_left() =>
        Assert.Equal(new VacationState(true, "47h02m", false), Vacation.ParseState("hold: ACTIF (47h02m restantes) threads=20 -> restore 8"));

    [Fact]
    public void No_hold() =>
        Assert.Equal(new VacationState(false, "none", false), Vacation.ParseState("hold: AUCUN (mutex + pause-Plex normaux)"));

    [Fact]
    public void Expired_hold() =>
        Assert.Equal(new VacationState(false, "expired", false), Vacation.ParseState("hold: expire, purge au prochain tick (-) threads=20 -> restore 8"));

    [Fact]
    public void Anything_else_is_unreadable_never_no_hold() =>
        Assert.Equal(VacationState.Unreadable, Vacation.ParseState("Permission denied (publickey)."));

    [Fact]
    public void A_failed_call_is_unreachable_whatever_it_printed()
    {
        Assert.Equal(VacationState.Unreachable, Vacation.FromStatusCall(false, "hold: ACTIF (1h restantes)"));
        Assert.Equal("47h02m", Vacation.FromStatusCall(true, "hold: ACTIF (47h02m restantes)").Text);
    }

    // ---------- Menu ----------
    [Fact]
    public void Durations_are_the_ones_the_remote_accepts_and_their_labels() =>
        Assert.Equal(
            "1 h | 4 h | 8 h | 12 h | 1 d (24 h) | 2 d (48 h) | 3 d (72 h) | Until I stop it (14 d)",
            string.Join(" | ", Vacation.Hours.Select(Vacation.Label)));

    [Fact]
    public void Hours_list_matches_the_remote() =>
        Assert.Equal("1,4,8,12,24,48,72,336", string.Join(",", Vacation.Hours));

    [Fact]
    public void Stop_is_greyed_only_when_the_remote_is_known_to_hold_nothing()
    {
        Assert.True(Vacation.StopEnabled(null));
        Assert.True(Vacation.StopEnabled(new VacationState(true, "1h", false)));
        Assert.True(Vacation.StopEnabled(VacationState.Unreadable));
        Assert.True(Vacation.StopEnabled(VacationState.Unreachable));
        Assert.False(Vacation.StopEnabled(Vacation.ParseState("hold: AUCUN")));
        // expired but not purged yet: the raised threads are still applied, Stop forces the purge
        Assert.True(Vacation.StopEnabled(Vacation.ParseState("hold: expire")));
    }

    [Fact]
    public void Menu_text_and_messages()
    {
        Assert.Equal("Vacation mode: ?", Vacation.MenuText(null));
        Assert.Equal("Vacation mode: none", Vacation.MenuText(Vacation.ParseState("hold: AUCUN")));
        Assert.Equal("vacation 2 d (48 h)", Vacation.ResultMessage(48, true));
        Assert.Equal("vacation stopped", Vacation.ResultMessage(0, true));
        Assert.Equal("vacation: failed", Vacation.ResultMessage(4, false));
    }

    [Fact]
    public void Tray_text_carries_the_vacation_only_when_given()
    {
        Assert.Equal("Backblaze: Running\n5 MB/s", Formatting.GetTrayText("Running", "5 MB/s", null));
        Assert.Equal("Backblaze: Running\n5 MB/s\nVacation: 47h02m", Formatting.GetTrayText("Running", "5 MB/s", "47h02m"));
        Assert.Equal(63, Formatting.GetTrayText("Running", "5 MB/s", new string('x', 80)).Length);
    }

    // BAC466.15: the cut at 63 characters must not eat the vacation line for the REAL texts (states x rates up to
    // 999.9 Mbps x durations up to 335h59m); a new, longer state will turn this guard red
    [Fact]
    public void Tray_cut_never_eats_the_vacation_line_for_real_texts()
    {
        var states = new[]
        {
            "Stopped", "Running", "Starting...", "Stopping...", "Pausing...", "Resuming...", "Paused",
            ServiceText.PausedByWitness, "Service not found", "Status unavailable", "Loading...",
        };
        var rates = new[] { "0 kbps", "999 kbps", "999.9 Mbps", "125.0 MB/s", "0 KB/s" };
        var notWhole = from st in states
                       from sp in rates
                       from v in new[] { "1h00m", "335h59m" }
                       where !Formatting.GetTrayText(st, sp, v).EndsWith("Vacation: " + v, StringComparison.Ordinal)
                       select $"{st}|{sp}|{v}";
        Assert.Empty(notWhole);
    }

    // ---------- What goes to ssh ----------
    [Theory]
    [InlineData(0, "off")]
    [InlineData(1, "on 1")]
    [InlineData(48, "on 48")]
    [InlineData(336, "on 336")]
    [InlineData(999, null)]
    [InlineData(5, null)]
    [InlineData(-1, null)]
    public void Only_listed_durations_make_a_command(int hours, string? expected) =>
        Assert.Equal(expected, Vacation.Command(hours));

    [Fact]
    public void Ssh_arguments_are_bounded_and_the_key_with_a_space_stays_one_argument()
    {
        var a = Vacation.SshArguments("pc.example", @"C:\Users\First Last\.ssh\k", "on 24");
        Assert.Equal(
            new[]
            {
                "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", "-o", "IdentitiesOnly=yes", "-o", "IdentityAgent=none",
                "-i", @"C:\Users\First Last\.ssh\k", "-n", "pc.example", "on 24",
            },
            a);
    }

    [Theory]
    [InlineData("homepage.example", @"C:\k", true)]
    [InlineData("user@10.0.0.2", @"C:\Users\Zoé\.ssh\k", true)]
    [InlineData("host", @"C:\Users\First Last\.ssh\k", true)] // a profile name with a space is common
    [InlineData("", @"C:\k", false)]
    [InlineData("host", "", false)]
    [InlineData(null, null, false)]
    [InlineData("-oProxyCommand=calc", @"C:\k", false)] // would become an ssh option
    [InlineData("two words", @"C:\k", false)]
    [InlineData("host", "C:\\k\nx", false)]
    public void Only_a_plain_host_and_a_key_enable_the_feature(string? host, string? key, bool expected) =>
        Assert.Equal(expected, Vacation.IsConfigured(host, key));

    // ---------- settings.txt ----------
    [Fact]
    public void Settings_read_and_keep_the_two_keys_and_a_plain_file_stays_plain()
    {
        var s = Settings.Parse("Units=bits\r\nVacationHost= h.example \r\nVacationKey=C:\\Users\\Zoé\\.ssh\\k\r\n");
        Assert.True(s.VacationEnabled);
        Assert.Equal("h.example", s.VacationHost);
        Assert.Equal(@"C:\Users\Zoé\.ssh\k", s.VacationKey);
        var back = Settings.Parse(s.ToFileText());
        Assert.Equal(s.VacationHost, back.VacationHost);
        Assert.Equal(s.VacationKey, back.VacationKey);

        var plain = new Settings();
        Assert.False(plain.VacationEnabled);
        Assert.DoesNotContain(plain.ToLines(), l => l.StartsWith("Vacation", StringComparison.Ordinal));
    }

    [Fact]
    public void A_key_path_with_an_accent_survives_a_save()
    {
        var f = Path.Combine(Path.GetTempPath(), "bbm-settings-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var s = new Settings { VacationHost = "h.example", VacationKey = @"C:\Users\Zoé\.ssh\k" };
            Assert.True(s.Save(f));
            Assert.Equal(@"C:\Users\Zoé\.ssh\k", Settings.Load(f).VacationKey);
        }
        finally
        {
            File.Delete(f);
        }
    }
}
