using System.Globalization;
using Overlay.Core.Beatmap;
using Xunit.Abstractions;

namespace Overlay.Tests;

public class BeatmapParserTests(ITestOutputHelper output)
{
    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);

    /// <summary>
    /// Compares every object with osu!lazer's decoder output (TestData/*.expected.csv, made by tools/LazerOracle).
    /// </summary>
    [Theory]
    [InlineData("cloudburst")] // single BPM, heavy SV, 28 repeat sliders, spinners
    [InlineData("stakillaz")]  // SV changes, repeat sliders, combo colour skips
    [InlineData("phoenix")]    // 4 BPM sections, 930 SV lines, repeat sliders
    public void Matches_lazer_reference(string map)
    {
        var beatmap = OsuFileParser.ParseFile(Data(map + ".osu"));
        var expected = File.ReadLines(Data(map + ".expected.csv")).Skip(1).Select(l => l.Split(',')).ToList();

        Assert.Equal(expected.Count, beatmap.HitObjects.Count);
        double maxEndDiff = 0;
        for (int i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var h = beatmap.HitObjects[i];
            string where = $"object {i} at {h.StartTime} ms";

            Assert.True(e[1] == h.Kind.ToString().ToLowerInvariant(), $"{where}: kind {h.Kind}, expected {e[1]}");
            Assert.Equal(double.Parse(e[2], CultureInfo.InvariantCulture), h.StartTime, 6);
            double endDiff = Math.Abs(double.Parse(e[3], CultureInfo.InvariantCulture) - h.EndTime);
            maxEndDiff = Math.Max(maxEndDiff, endDiff);
            Assert.True(endDiff < 1, $"{where}: end {h.EndTime}, expected {e[3]}");
            Assert.True(int.Parse(e[4]) == h.Slides, $"{where}: slides {h.Slides}, expected {e[4]}");
            if (h.Kind != HitObjectKind.Spinner)
                Assert.True((e[5] == "1") == h.NewCombo, $"{where}: new combo {h.NewCombo}, expected {e[5]}");
            Assert.True(int.Parse(e[6]) == h.ComboColourIndex, $"{where}: colour index {h.ComboColourIndex}, expected {e[6]}");
            Assert.True(int.Parse(e[7]) == h.ComboNumber, $"{where}: combo number {h.ComboNumber}, expected {e[7]}");
        }
        output.WriteLine($"{map}: {expected.Count} objects, max end-time difference {maxEndDiff:G3} ms");
    }

    [Fact]
    public void Test_maps_cover_the_required_features()
    {
        var cloudburst = OsuFileParser.ParseFile(Data("cloudburst.osu"));
        var phoenix = OsuFileParser.ParseFile(Data("phoenix.osu"));
        var stakillaz = OsuFileParser.ParseFile(Data("stakillaz.osu"));
        var all = new[] { cloudburst, phoenix, stakillaz };

        Assert.All(all, b => Assert.Equal(0, b.Mode));
        Assert.All(all, b => Assert.Contains(b.HitObjects, h => h.Kind == HitObjectKind.Slider && h.Slides > 1));
        Assert.All(all, b => Assert.True(b.TimingPoints.Count(t => !t.Uninherited) > 100, "expected many SV changes"));
        Assert.True(phoenix.Timing.Sections.Select(s => s.BeatLength).Distinct().Count() >= 3, "expected BPM changes");
        Assert.Equal("Commencement of Cessation", phoenix.Version);
        Assert.Equal(1.7, phoenix.SliderMultiplier);
    }

    private const string Header = "osu file format v14\n[General]\nMode: 0\n[Difficulty]\nSliderMultiplier:1\n";

    [Fact]
    public void Slider_duration_uses_beat_length_sv_and_slides()
    {
        // 500 ms/beat, SV 2x (green -50). Velocity = 100 * 1 * 2 / 500 = 0.4 px/ms; 3 slides * 100 px / 0.4 = 750 ms.
        var map = OsuFileParser.Parse(Header +
            "[TimingPoints]\n0,500,4,2,0,100,1,0\n1000,-50,4,2,0,100,0,0\n" +
            "[HitObjects]\n0,0,1000,2,0,L|100:0,3,100\n");
        var s = Assert.Single(map.HitObjects);
        Assert.Equal(HitObjectKind.Slider, s.Kind);
        Assert.Equal(3, s.Slides);
        Assert.Equal(1750, s.EndTime, 9);
    }

    [Fact]
    public void Green_line_overrides_red_line_at_same_time_regardless_of_order()
    {
        var map = OsuFileParser.Parse(Header +
            "[TimingPoints]\n0,500,4,2,0,100,1,0\n2000,-50,4,2,0,100,0,0\n2000,250,4,2,0,100,1,0\n3000,300,4,2,0,100,1,0\n");
        Assert.Equal(2, map.Timing.SliderVelocityAt(2000));
        Assert.Equal(250, map.Timing.BeatLengthAt(2500));
        Assert.Equal(1, map.Timing.SliderVelocityAt(3000)); // a red line resets SV
    }

    [Fact]
    public void Timing_lookups_before_first_line_and_clamping()
    {
        var map = OsuFileParser.Parse(Header +
            "[TimingPoints]\n100,400,3,2,0,100,1,0\n200,-5,4,2,0,100,0,0\n300,-100000,4,2,0,100,0,0\n");
        Assert.Equal(400, map.Timing.BeatLengthAt(0));     // before the first red line: use the first
        Assert.Equal(3, map.Timing.SectionAt(0).Meter);
        Assert.Equal(1, map.Timing.SliderVelocityAt(50));  // before any line: SV 1
        Assert.Equal(10, map.Timing.SliderVelocityAt(250), 9);  // -5 clamps to -10 -> 10x
        Assert.Equal(0.01, map.Timing.SliderVelocityAt(350), 9); // clamps to -10000 -> 0.01x
    }

    [Fact]
    public void Combo_numbering_with_spinners_and_colour_skips()
    {
        var map = OsuFileParser.Parse(Header +
            "[TimingPoints]\n0,500,4,2,0,100,1,0\n" +
            "[HitObjects]\n" +
            "0,0,0,1,0\n" +            // first object: new combo, colour 1, #1
            "0,0,100,1,0\n" +          // #2
            "0,0,200,12,0,300\n" +     // spinner with NC: keeps colour, #3, forces NC on next
            "0,0,400,1,0\n" +          // forced NC: colour 2, #1
            "0,0,500,37,0\n" +         // NC + skip 2 (32|4|1): colour 2 + 1 + 2 = 5, #1
            "0,0,600,1,0\n");          // #2
        var h = map.HitObjects;
        Assert.Equal(new[] { 1, 1, 1, 2, 5, 5 }, h.Select(x => x.ComboColourIndex));
        Assert.Equal(new[] { 1, 2, 3, 1, 1, 2 }, h.Select(x => x.ComboNumber));
        Assert.Equal(300, h[2].EndTime);
    }

    [Fact]
    public void Default_combo_colours_when_map_has_none()
    {
        var map = OsuFileParser.Parse(Header);
        Assert.Equal(OsuFileParser.DefaultComboColours, map.ComboColours);
        Assert.False(map.HasOwnComboColours); // so the skin's colours apply

        var coloured = OsuFileParser.Parse(Header + "[Colours]\nCombo2 : 1,2,3\nCombo1 : 4,5,6\n");
        Assert.Equal(new[] { new Rgb(4, 5, 6), new Rgb(1, 2, 3) }, coloured.ComboColours);
        Assert.True(coloured.HasOwnComboColours);
    }

    [Fact]
    public void Discovery_lists_only_standard_difficulties()
    {
        var dir = Directory.CreateTempSubdirectory("overlay-test-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.osu"), "osu file format v14\n[General]\nMode: 0\n[Editor]\nBeatDivisor: 6\nTimelineZoom: 3.600004\n[Metadata]\nVersion:Hard\n[Difficulty]\nCircleSize:18\n[HitObjects]\n");
            File.WriteAllText(Path.Combine(dir, "b.osu"), "osu file format v14\n[General]\nMode: 3\n[Metadata]\nVersion:4K\n[HitObjects]\n");
            File.WriteAllText(Path.Combine(dir, "c.osu"), "osu file format v14\n[General]\nMode: 0\n[Metadata]\nVersion:Easy\n");
            File.WriteAllText(Path.Combine(dir, "notes.txt"), "Mode: 0");

            var found = DifficultyDiscovery.FindStandardDifficulties(dir);
            Assert.Equal(new[] { "Easy", "Hard" }, found.Select(d => d.Version));
            Assert.Equal(new int?[] { null, 6 }, found.Select(d => d.BeatDivisor)); // the editor's saved snap
            Assert.Equal(new double?[] { null, 3.600004 }, found.Select(d => d.TimelineZoom)); // and timeline zoom
            Assert.Equal(new double?[] { null, 18 }, found.Select(d => d.CircleSize));

            // Default: the largest file, unless one was used before.
            Assert.Equal("Hard", DifficultyDiscovery.PickDefault(found, lastUsedPath: null)!.Version);
            Assert.Equal("Easy", DifficultyDiscovery.PickDefault(found, Path.Combine(dir, "C.OSU"))!.Version);
            Assert.Equal("Hard", DifficultyDiscovery.PickDefault(found, Path.Combine(dir, "gone.osu"))!.Version);
            Assert.Null(DifficultyDiscovery.PickDefault(Array.Empty<DifficultyInfo>(), null));
        }
        finally { Directory.Delete(dir, true); }
    }
}
