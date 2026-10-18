using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChunsonSpace.Fingerprint;

public sealed class FingerprintEngine
{
    public const string ChallengeVersion = Challenges.Version;
    public const int ValueMin = 1, ValueMax = 355, Dimension = 355;
    public const double Alpha = 0.5, OrderedBlockWeight = 0.25;
    private const string NoUsableMessage = "没有可用回答：请粘贴完整数字序列；拒答或严重截断的回答不会计入。";
    private static readonly Lazy<FingerprintEngine> Instance = new(LoadDefault);
    private static readonly Regex Digits = new("[0-9]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly Bank bank;
    public static FingerprintEngine Default => Instance.Value;
    public IReadOnlyList<string> ModelIds { get; }
    public string BankBuiltAt { get; }
    public string BankHash { get; }
    public FingerprintEngine(ReadOnlySpan<byte> bankJson)
    {
        BankHash = Convert.ToHexString(SHA256.HashData(bankJson));
        bank = Bank.Read(bankJson);
        BankBuiltAt = bank.BuiltAt;
        ModelIds = Array.AsReadOnly(bank.Models.Select(model => model.Id).ToArray());
    }
    public IReadOnlyList<FingerprintChallenge> GenerateChallenges(IFingerprintRandom? random = null) => Challenges.Generate(random);
    public IReadOnlyList<int> ParseNumbers(string? text)
    {
        text ??= "";
        var best = new List<int>(); var current = new List<int>(); int previousEnd = 0;
        foreach (Match match in Digits.Matches(text))
        {
            if (current.Count > 0 && ContainsLetter(text.AsSpan(previousEnd, match.Index - previousEnd)))
            {
                if (current.Count > best.Count) best = current;
                current = [];
            }
            int value = 0;
            foreach (char digit in match.ValueSpan) { value = Math.Min(356, value * 10 + digit - '0'); }
            if (value is >= ValueMin and <= ValueMax) current.Add(value);
            previousEnd = match.Index + match.Length;
        }
        if (current.Count > best.Count) best = current;
        return best.AsReadOnly();
    }
    public FingerprintAnalysis Analyze(IEnumerable<FingerprintOutput> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        var diagnostics = new List<FingerprintDiagnostic>();
        var valid = new List<(double[] Counts, double[] Scores)>(); int index = 0;
        foreach (var output in outputs)
        {
            ArgumentNullException.ThrowIfNull(output);
            var numbers = ParseNumbers(output.Text);
            int minimum = output.ExpectedCount != 0 ? Math.Max(80, (int)Math.Ceiling(output.ExpectedCount * 0.55)) : 80;
            bool accepted = numbers.Count >= minimum;
            diagnostics.Add(new(index++, numbers.Count, minimum, accepted));
            if (accepted)
            {
                var counts = Counts(numbers);
                valid.Add((counts, Score(numbers, counts)));
            }
        }
        if (valid.Count == 0) return new(FingerprintAnalysisStatus.NoUsableOutputs, [], diagnostics.AsReadOnly(), 0, [],
            null, null, null, null, null, null, null, NoUsableMessage);
        var scores = new double[bank.Models.Length];
        for (int model = 0; model < scores.Length; model++)
        {
            double sum = 0; foreach (var item in valid) sum += item.Scores[model]; scores[model] = sum / valid.Count;
        }
        var calibration = bank.Calibrations[Math.Min(valid.Count, 3) - 1];
        var probabilities = Softmax(scores.Select(score => calibration.Beta * score).ToArray());
        var pooled = new double[Dimension];
        for (int position = 0; position < Dimension; position++) foreach (var item in valid) pooled[position] += item.Counts[position];
        var entries = bank.Models.Select((model, position) => new FingerprintCandidate(model.Id, model.Name, probabilities[position],
            Similarity(pooled, model.Counts), scores[position], model.Family, bank.FamilyNames[model.Family], 0)).OrderByDescending(item => item.Probability).ToArray();
        var families = new List<FingerprintFamilyProbability>();
        foreach (var family in bank.FamilyOrder)
        {
            double sum = 0; foreach (var entry in entries) if (entry.Family == family) sum += entry.Probability;
            families.Add(new(family, bank.FamilyNames[family], sum));
        }
        var familyProbabilities = families.ToDictionary(item => item.Family, item => item.Probability);
        for (int position = 0; position < entries.Length; position++)
            entries[position] = entries[position] with { ConditionalProbability = entries[position].Probability / familyProbabilities[entries[position].Family] };
        var winning = families[0]; foreach (var family in families) if (family.Probability > winning.Probability) winning = family;
        if (entries.Any(item => !double.IsFinite(item.Probability) || !double.IsFinite(item.ProfileSimilarity) || !double.IsFinite(item.Score) || !double.IsFinite(item.ConditionalProbability)))
            throw new FingerprintBankException("指纹参考库数值不稳定，未输出概率");
        return new(FingerprintAnalysisStatus.Valid, Array.AsReadOnly(entries), diagnostics.AsReadOnly(), valid.Count, families.AsReadOnly(),
            entries[0].ModelId, entries[0].DisplayName, entries[0].Probability, winning.Family, winning.DisplayName, winning.Probability,
            new(Math.Min(valid.Count, 3), calibration.Beta, calibration.CvAccuracy),
            "结果是当前参考库内、均匀先验下的闭集概率；未收录模型仍会归入最相似候选，不代表真实服务端型号认证。");
    }
    private double[] Score(IReadOnlyList<int> numbers, double[] counts)
    {
        var feature = new double[Dimension]; double total = Sum(counts) + Alpha * Dimension;
        for (int position = 0; position < feature.Length; position++) feature[position] = Math.Sqrt((counts[position] + Alpha) / total);
        var marginalUnit = Normalize(SubtractBasis(FeatureStandardize(feature, bank.Hellinger), bank.Hellinger.Basis));
        var marginal = Standardize(bank.Hellinger.Centroids.Select(centroid => Dot(marginalUnit, centroid)).ToArray());
        var orderedFeature = FeatureStandardize(OrderedFeature(numbers), bank.Ordered);
        var unit = Normalize(orderedFeature);
        var environmentScores = bank.Environments.Select(centroids => centroids.Select(centroid => Dot(unit, centroid)).ToArray()).ToArray();
        var template = new double[bank.Models.Length];
        for (int model = 0; model < template.Length; model++) template[model] = environmentScores.Max(environment => environment[model]);
        template = Standardize(template);
        var projected = Normalize(SubtractBasis(orderedFeature, bank.Ordered.Basis));
        var nuisance = Standardize(bank.Ordered.Centroids.Select(centroid => Dot(projected, centroid)).ToArray());
        var ordered = Standardize(template.Select((value, model) => 0.5 * value + 0.5 * nuisance[model]).ToArray());
        return marginal.Select((value, model) => (1 - OrderedBlockWeight) * value + OrderedBlockWeight * ordered[model]).ToArray();
    }
    private static double[] Counts(IReadOnlyList<int> numbers) { var counts = new double[Dimension]; foreach (int number in numbers) counts[number - ValueMin]++; return counts; }
    private static double[] OrderedFeature(IReadOnlyList<int> numbers)
    {
        var pieces = new List<double>(74); int start = 0, basic = numbers.Count / 4, remainder = numbers.Count % 4;
        for (int block = 0; block < 4; block++)
        {
            int size = basic + (block < remainder ? 1 : 0); var bins = Enumerable.Repeat(0.5, 16).ToArray();
            for (int position = start; position < start + size; position++) bins[Math.Min(15, (int)Math.Floor(((numbers[position] - 1) / 355d) * 16))]++;
            double total = Sum(bins); foreach (double value in bins) pieces.Add(Math.Sqrt(value / total)); start += size;
        }
        var digits = Enumerable.Repeat(0.5, 10).ToArray(); foreach (int number in numbers) digits[number % 10]++;
        double digitTotal = Sum(digits); foreach (double value in digits) pieces.Add(Math.Sqrt(value / digitTotal));
        return pieces.ToArray();
    }
    private static double[] FeatureStandardize(double[] feature, Artifact artifact) => feature.Select((value, position) => (value - artifact.Mean[position]) / artifact.Scale[position]).ToArray();
    private static double Sum(double[] values) { double sum = 0; foreach (double value in values) sum += value; return sum; }
    private static double Dot(double[] left, double[] right) { double value = 0; for (int position = 0; position < left.Length; position++) value += left[position] * right[position]; return value; }
    private static double[] Normalize(double[] values) { double scale = Math.Max(Math.Sqrt(Dot(values, values)), 1e-12); return values.Select(value => value / scale).ToArray(); }
    private static double[] SubtractBasis(double[] values, double[][] basis)
    {
        var result = (double[])values.Clone();
        foreach (var vector in basis) { double projection = Dot(result, vector); for (int position = 0; position < result.Length; position++) result[position] -= projection * vector[position]; }
        return result;
    }
    private static double[] Standardize(double[] values)
    {
        double center = Sum(values) / values.Length, variance = 0;
        foreach (double value in values) { double delta = value - center; variance += delta * delta; }
        double scale = Math.Max(Math.Sqrt(variance / values.Length), 1e-12);
        return values.Select(value => (value - center) / scale).ToArray();
    }
    private static double[] Softmax(double[] values) { double max = values.Max(); var weights = values.Select(value => Math.Exp(value - max)).ToArray(); double total = Sum(weights); return weights.Select(value => value / total).ToArray(); }
    private static double Similarity(double[] left, double[] right)
    {
        double leftTotal = Sum(left), rightTotal = Sum(right) + Alpha * Dimension;
        var p = left.Select(value => value / leftTotal).ToArray(); var q = right.Select(value => (value + Alpha) / rightTotal).ToArray();
        var midpoint = p.Select((value, position) => (value + q[position]) / 2).ToArray();
        double Divergence(double[] values) { double sum = 0; for (int position = 0; position < values.Length; position++) if (values[position] != 0) sum += values[position] * Math.Log(values[position] / midpoint[position]); return sum; }
        double js = (Divergence(p) + Divergence(q)) / 2; return 1 - Math.Sqrt(js / Math.Log(2));
    }
    private static bool ContainsLetter(ReadOnlySpan<char> text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter) return true;
        return false;
    }
    private static FingerprintEngine LoadDefault()
    {
        using var stream = typeof(FingerprintEngine).Assembly.GetManifestResourceStream("ChunsonSpace.Fingerprint.Resources.unified_bank.json")
            ?? throw new FingerprintBankException("内置指纹参考库缺失");
        using var content = new MemoryStream(); stream.CopyTo(content); return new(content.ToArray());
    }
    private sealed record Model(string Id, string Name, string Family, double[] Counts);
    private sealed record Artifact(double[] Mean, double[] Scale, double[][] Basis, double[][] Centroids);
    private sealed record Calibration(double Beta, double CvAccuracy);
    private sealed record Bank(string BuiltAt, Model[] Models, Artifact Hellinger, Artifact Ordered, double[][][] Environments,
        Calibration[] Calibrations, string[] FamilyOrder, Dictionary<string, string> FamilyNames)
    {
        public static Bank Read(ReadOnlySpan<byte> content)
        {
            try
            {
                using var document = JsonDocument.Parse(content.ToArray()); var root = document.RootElement;
                if (root.GetProperty("schema").GetString() != "robust-number-fingerprint-bank") throw new FormatException("schema");
                var method = root.GetProperty("method"); var range = Vector(method.GetProperty("range"), 2, "range");
                if (range[0] != ValueMin || range[1] != ValueMax || Number(method.GetProperty("alpha")) != Alpha || Number(method.GetProperty("ordered_block_weight")) != OrderedBlockWeight) throw new FormatException("method");
                string builtAt = root.GetProperty("built_at").GetString() ?? throw new FormatException("built_at");
                if (!DateTimeOffset.TryParse(builtAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new FormatException("built_at");
                var models = new List<Model>(); var ids = new HashSet<string>(StringComparer.Ordinal); var familyOrder = new List<string>(); var names = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var model in root.GetProperty("models").EnumerateArray())
                {
                    string id = Text(model, "id"), name = Text(model, "display_name");
                    if (!ids.Add(id)) throw new FormatException("duplicate model");
                    string family = OptionalText(model, "family") ?? "models";
                    if (!names.ContainsKey(family)) { familyOrder.Add(family); names.Add(family, OptionalText(model, "family_name") ?? family); }
                    var counts = Vector(model.GetProperty("counts"), Dimension, "counts");
                    if (counts.Any(value => value < 0) || Sum(counts) <= 0 || !double.IsFinite(Sum(counts))) throw new FormatException("counts");
                    models.Add(new(id, name, family, counts));
                }
                if (models.Count < 2) throw new FormatException("models");
                var robust = root.GetProperty("robust");
                if (!robust.GetProperty("robust_ready").GetBoolean() || !robust.GetProperty("model_order").EnumerateArray().Select(value => value.GetString()).SequenceEqual(models.Select(model => model.Id))) throw new FormatException("model order");
                var hellinger = ReadArtifact(robust.GetProperty("hellinger"), Dimension, models.Count);
                var orderedElement = robust.GetProperty("ordered_blocks");
                if (Number(orderedElement.GetProperty("weight")) != OrderedBlockWeight) throw new FormatException("ordered weight");
                var ordered = ReadArtifact(orderedElement, 74, models.Count);
                var environments = orderedElement.GetProperty("environment_centroids").EnumerateArray().Select(environment => Matrix(environment, models.Count, 74, "environment centroids")).ToArray();
                if (environments.Length == 0) throw new FormatException("environments");
                var calibrations = new Calibration[3];
                for (int index = 0; index < 3; index++)
                {
                    var calibration = root.GetProperty("calibration").GetProperty((index + 1).ToString(CultureInfo.InvariantCulture));
                    double beta = Number(calibration.GetProperty("beta")), accuracy = Number(calibration.GetProperty("cv_accuracy"));
                    if (beta <= 0 || accuracy is < 0 or > 1) throw new FormatException("calibration");
                    calibrations[index] = new(beta, accuracy);
                }
                return new(builtAt, models.ToArray(), hellinger, ordered, environments, calibrations, familyOrder.ToArray(), names);
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
            { throw new FingerprintBankException("指纹参考库的版本、维度、顺序或有限数校验失败", error); }
        }
        private static string Text(JsonElement parent, string property) => OptionalText(parent, property) ?? throw new FormatException(property);
        private static string? OptionalText(JsonElement parent, string property) => parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(value.GetString()) ? value.GetString() : null;
        private static double Number(JsonElement value) { double number = value.GetDouble(); if (!double.IsFinite(number)) throw new FormatException("non-finite"); return number; }
        private static double[] Vector(JsonElement value, int length, string name) { var result = value.EnumerateArray().Select(Number).ToArray(); if (result.Length != length) throw new FormatException(name); return result; }
        private static double[][] Matrix(JsonElement value, int rows, int columns, string name) { var result = value.EnumerateArray().Select(row => Vector(row, columns, name)).ToArray(); if (result.Length != rows) throw new FormatException(name); return result; }
        private static Artifact ReadArtifact(JsonElement artifact, int dimension, int models)
        {
            var mean = Vector(artifact.GetProperty("feature_mean"), dimension, "feature mean"); var scale = Vector(artifact.GetProperty("feature_scale"), dimension, "feature scale");
            if (scale.Any(value => value <= 0)) throw new FormatException("feature scale");
            var basis = artifact.GetProperty("nuisance_basis").EnumerateArray().Select(row => Vector(row, dimension, "basis")).ToArray();
            if (artifact.GetProperty("nuisance_rank").GetInt32() != basis.Length) throw new FormatException("basis rank");
            return new(mean, scale, basis, Matrix(artifact.GetProperty("centroids"), models, dimension, "centroids"));
        }
    }
}
