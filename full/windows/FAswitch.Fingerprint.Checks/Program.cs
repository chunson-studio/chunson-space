using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FAswitch.Fingerprint;

var engine = FingerprintEngine.Default;
int count = 0;
void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); count++; Console.WriteLine("PASS " + label); }
string output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(AppContext.BaseDirectory, "fingerprint-fixture");
Directory.CreateDirectory(output);
Check(engine.ModelIds.Count == 16 && engine.ModelIds.Contains("gpt-6-astra") && !engine.ModelIds.Contains("gpt-6.1-sol"), "embedded closed bank contains exactly the original sixteen labels");
Check(engine.BankHash == "6A678E6C73EB015C1C507D61CDD1313B41916D55186BB65920D1BF119EC5009E" && engine.BankBuiltAt == "2026-09-23T06:17:08.081468+00:00", "bank provenance hash and built date match copied source");
var parserTexts = new[] { "", "拒答", "1 2 letters 3 4 5", "1 2 abc 3 4", "1 2 𐐀 3 4 5", "1 2 🐶 3 4", "３ ４ 5 ٢ 6", "0,356,355,-1,2.3", "v4 response 80 numbers: 1,2,3,4", new string('9', 400) + " " + new string('0', 300) + "1 2" };
var parserCases = parserTexts.Select(text => new { text, numbers = engine.ParseNumbers(text) }).ToArray();
Check(engine.ParseNumbers("1 2 abc 3 4").SequenceEqual([1, 2]), "equal digit runs keep the first run");
Check(engine.ParseNumbers("1 2 𐐀 3 4 5").SequenceEqual([3, 4, 5]), "supplementary Unicode letters split runs like JavaScript Unicode property escapes");
Check(engine.ParseNumbers("３ ４ 5 ٢ 6").SequenceEqual([5, 6]), "only ASCII decimal digit matches are parsed");
Check(engine.ParseNumbers(new string('9', 400) + " " + new string('0', 300) + "1 2").SequenceEqual([1, 2]), "oversized digit tokens are skipped without overflow and leading zeroes are retained");
string Sequence(int length, int pattern) => string.Join(' ', Enumerable.Range(0, length).Select(index => pattern switch
{
    0 => (index * 19 + 7) % 89 + 1,
    1 => (index * 101 + 31) % 355 + 1,
    2 => index < length / 2 ? 301 + index % 55 : 1 + index % 53,
    _ => 1 + (index * 23 + 113) % 355
}));
var a = new FingerprintOutput(Sequence(300, 0), 300); var b = new FingerprintOutput(Sequence(310, 1), 310); var c = new FingerprintOutput(Sequence(332, 2), 332);
var cases = new List<(string Name, FingerprintOutput[] Outputs)>
{
    ("refusal", [new("无法生成这段序列。", 300)]), ("truncated", [new(Sequence(164, 0), 300)]),
    ("boundary-accepted", [new(Sequence(165, 0), 300)]), ("zero-outputs", []),
    ("default-count-reject-79", [new(Sequence(79, 0))]), ("default-count-accept-80", [new(Sequence(80, 1))]),
    ("one", [a]), ("two", [a, b]), ("three", [a, b, c]),
    ("mixed-refusal-truncated-one", [new("拒绝回答", 300), a, new(Sequence(100, 1), 310)]),
    ("four-calibration-cap", [a, b, c, new(Sequence(297, 3), 297)]),
    ("reverse-same-histogram", [new(string.Join(' ', engine.ParseNumbers(c.Text).Reverse()), 332)]),
    ("ordered-forward", [c])
};
for (int seed = 0; seed < 12; seed++)
{
    var rng = new Random(seed); var numbers = Enumerable.Range(0, 292 + seed).Select(_ => rng.Next(1, 356));
    cases.Add(("synthetic-" + seed, [new(string.Join(seed % 2 == 0 ? "," : "\n", numbers), 292 + seed)]));
}
var analyses = cases.Select(item => new { name = item.Name, outputs = item.Outputs, analysis = engine.Analyze(item.Outputs) }).ToArray();
foreach (var item in analyses)
{
    if (item.analysis.UsedOutputs == 0) Check(item.analysis.Status == FingerprintAnalysisStatus.NoUsableOutputs && item.analysis.Results.Count == 0 && item.analysis.Calibration is null, item.name + " zero-valid result is typed without invented probabilities");
    else
    {
        Check(item.analysis.Status == FingerprintAnalysisStatus.Valid && item.analysis.Results.Count == 16 && Math.Abs(item.analysis.Results.Sum(result => result.Probability) - 1) < 1e-12, item.name + " finite normalized closed-set probabilities");
        Check(item.analysis.Calibration!.Queries == Math.Min(item.analysis.UsedOutputs, 3), item.name + " uses calibration for accepted answers only");
        Check(item.analysis.FamilyProbabilities.All(family => Math.Abs(item.analysis.Results.Where(result => result.Family == family.Family).Sum(result => result.ConditionalProbability) - 1) < 1e-12), item.name + " conditional family probability is normalized");
    }
}
Check(engine.Analyze([a]).Calibration!.Beta == 7.277579699216447 && engine.Analyze([a, b]).Calibration!.Beta == 11.99999999999999 && engine.Analyze([a, b, c]).Calibration!.CvAccuracy == 1, "original one/two/three answer temperatures and reported CV metadata retained");
var forward = engine.Analyze([c]); var backward = engine.Analyze([new(string.Join(' ', engine.ParseNumbers(c.Text).Reverse()), 332)]);
Check(forward.Results.Any(result => Math.Abs(result.Score - backward.Results.Single(other => other.ModelId == result.ModelId).Score) > 1e-6), "ordered quarter blocks influence scores even with equal marginal counts");
var challengeCases = new List<object>();
for (uint seed = 0; seed < 16; seed++)
{
    var tape = Enumerable.Range(0, 64).Select(index => unchecked((uint)index * 2654435761u + seed * 131u)).ToArray();
    tape[0] = uint.MaxValue;
    var challenges = engine.GenerateChallenges(new TapeRandom(tape));
    Check(challenges.Count == 3 && challenges.Select(challenge => challenge.ExpectedCount).Distinct().Count() == 3 && challenges.All(challenge => challenge.ExpectedCount is >= 292 and <= 332), "challenge tape " + seed + " distinct original lengths");
    challengeCases.Add(new { tape, challenges });
}
for (int iteration = 0; iteration < 8; iteration++)
{
    var generated = engine.GenerateChallenges();
    Check(generated.All(challenge => challenge.Prompt.Contains("禁止调用或借助任何工具", StringComparison.Ordinal) && Guid.TryParse(challenge.Id[(challenge.Id.LastIndexOf("probe-", StringComparison.Ordinal) + 8)..], out _)), "native crypto challenge IDs and direct-only prompts");
}
using var bankStream = typeof(FingerprintEngine).Assembly.GetManifestResourceStream("FAswitch.Fingerprint.Resources.unified_bank.json")!;
using var bankBytes = new MemoryStream(); bankStream.CopyTo(bankBytes);
void Reject(Action<JsonObject> mutate, string label)
{
    var root = JsonNode.Parse(bankBytes.ToArray())!.AsObject(); mutate(root);
    try { _ = new FingerprintEngine(Encoding.UTF8.GetBytes(root.ToJsonString())); throw new InvalidOperationException("invalid bank accepted: " + label); }
    catch (FingerprintBankException) { Check(true, label); }
}
Reject(root => root["models"]![0]!["counts"]!.AsArray().RemoveAt(0), "wrong count dimension rejected");
Reject(root => root["robust"]!["hellinger"]!["feature_scale"]![0] = 0, "zero feature scale rejected before division");
Reject(root => root["robust"]!["ordered_blocks"]!["centroids"]![0]!.AsArray().RemoveAt(0), "wrong ordered feature dimension rejected");
Reject(root => root["robust"]!["ordered_blocks"]!["environment_centroids"]![0]!.AsArray().RemoveAt(0), "environment model dimension rejected");
Reject(root => root["robust"]!["model_order"]![0] = "other", "centroid model ordering mismatch rejected");
Reject(root => root["calibration"]!["2"]!["beta"] = -1, "invalid softmax calibration rejected");
Reject(root => root["robust"]!["ordered_blocks"]!["weight"] = 0.5, "ordered fusion weight cannot silently change from quarter");
Reject(root => root["models"]![0]!["counts"]![0] = -1, "negative fingerprint counts rejected");
var overflowJson = Encoding.UTF8.GetString(bankBytes.ToArray()).Replace("7.277579699216447", "1e999", StringComparison.Ordinal);
try { _ = new FingerprintEngine(Encoding.UTF8.GetBytes(overflowJson)); throw new InvalidOperationException("non-finite accepted"); }
catch (FingerprintBankException) { Check(true, "non-finite numeric bank token rejected"); }
var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
File.WriteAllText(Path.Combine(output, "compare-cases.json"), JsonSerializer.Serialize(new { bankHash = engine.BankHash, parserCases, analyses, challengeCases }, options));
File.WriteAllBytes(Path.Combine(output, "unified_bank.json"), bankBytes.ToArray());
Console.WriteLine($"{count} Fingerprint isolated checks passed; no model or account requests.");

sealed class TapeRandom(uint[] tape) : IFingerprintRandom
{
    private int position, guid;
    public int NextIndex(int length)
    {
        ulong limit = (1UL << 32) / (uint)length * (uint)length; uint next;
        do { next = tape[position++ % tape.Length]; } while (next >= limit);
        return (int)(next % (uint)length);
    }
    public Guid NextGuid() => Guid.Parse($"00000000-0000-4000-8000-{++guid:D12}");
}
