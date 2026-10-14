// Reference dumper: decodes .osu files with osu!lazer's legacy decoder + osu! ruleset converter and
// writes each hit object's timing and combo info to "<file>.expected.csv" next to the input.
// These files are the ground truth for Overlay.Core's parser tests.
//   LazerOracle <file.osu> [more.osu ...]
using System.Globalization;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Types;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Objects;

var inv = CultureInfo.InvariantCulture;
var ruleset = new OsuRuleset();

foreach (var path in args)
{
    Beatmap beatmap;
    using (var stream = File.OpenRead(path))
    using (var reader = new LineBufferedReader(stream))
        beatmap = Decoder.GetDecoder<Beatmap>(reader).Decode(reader);

    beatmap.BeatmapInfo.Ruleset = ruleset.RulesetInfo;
    var playable = new FlatWorkingBeatmap(beatmap).GetPlayableBeatmap(ruleset.RulesetInfo, Array.Empty<Mod>());

    var outPath = Path.ChangeExtension(path, ".expected.csv");
    using var w = new StreamWriter(outPath);
    w.WriteLine("index,kind,start_ms,end_ms,slides,new_combo,combo_colour_index,combo_number");
    int i = 0;
    foreach (var h in playable.HitObjects)
    {
        string kind = h switch { Slider => "slider", Spinner => "spinner", _ => "circle" };
        int slides = h is Slider s ? s.RepeatCount + 1 : 0;
        var combo = (IHasComboInformation)h;
        w.WriteLine(string.Join(",", i++, kind,
            h.StartTime.ToString("R", inv), h.GetEndTime().ToString("R", inv), slides,
            combo.NewCombo ? 1 : 0, combo.ComboIndexWithOffsets, combo.IndexInCurrentCombo + 1));
    }
    Console.WriteLine($"{Path.GetFileName(path)}: {i} objects -> {Path.GetFileName(outPath)}");
}
