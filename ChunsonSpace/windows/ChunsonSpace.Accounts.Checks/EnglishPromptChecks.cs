using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ChunsonSpace.Accounts;
using ChunsonSpace.Fingerprint;

internal static class EnglishPromptChecks
{
    public static int Run(string directory)
    {
        Directory.CreateDirectory(directory); var checks = new List<object>();
        void Check(string name, bool passed) { checks.Add(new { name, passed }); if (!passed) throw new InvalidOperationException(name); }
        using var resource = typeof(EnglishPromptChecks).Assembly.GetManifestResourceStream("EnglishPromptProvenance")!;
        var golden = JsonNode.Parse(resource)!;
        var prompts = new Dictionary<string,string> { ["static"] = VisualTestCatalog.Prompt(VisualTestKind.Static, ""), ["animation"] = VisualTestCatalog.Prompt(VisualTestKind.Animation, ""), ["revision/initial"] = VisualTestCatalog.Prompt(VisualTestKind.Revision, ""), ["ciallo"] = CialloTest.Prompt, ["instructions/helpful"] = BuiltInTestInstructions.Helpful, ["instructions/fingerprint"] = BuiltInTestInstructions.Fingerprint };
        foreach (var variant in VisualTestCatalog.Variants) prompts["variation/" + variant.Id] = VisualTestCatalog.Prompt(VisualTestKind.Variation, variant.Id);
        prompts["revision/review"] = VisualTestCatalog.ReviewPrompt(prompts["static"]);
        prompts["revision/revise"] = VisualTestCatalog.RevisionPrompt(prompts["static"], "<svg fixture/>", "fixture critique");
        Check("all fixed catalog entries covered", prompts.Count == golden["prompts"]!.AsObject().Count);
        foreach (var (name,text) in prompts)
        {
            Check(name + " exact approved provenance", text == golden["prompts"]![name]!["text"]!.ToString() && Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).Equals(golden["prompts"]![name]!["sha256"]!.ToString(), StringComparison.OrdinalIgnoreCase));
            Check(name + " English instructions / fixed Ciallo", text.All(c => c <= 127));
        }
        Check("catalog versions separated", VisualTestCatalog.Version == golden["visualVersion"]!.ToString() && FingerprintEngine.ChallengeVersion == golden["fingerprintVersion"]!.ToString());
        Check("no added answer language or translation instructions", prompts.Values.All(p => !p.Contains("Simplified Chinese") && !p.Contains("in English") && !p.Contains("translate", StringComparison.OrdinalIgnoreCase)));
        Check("original Ciallo token and separately versioned raw policy", CialloTest.Prompt == "ciallo" && CialloTest.Version == golden["cialloVersion"]!.ToString() && BuiltInTestInstructions.Helpful == "You are a helpful assistant.");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var valid = true;
        for (var n = 292; n <= 332; n++) for (var a=0;a<5;a++) for (var b=0;b<5;b++) for(var c=0;c<5;c++) for(var d=0;d<4;d++)
        {
            var generated = FingerprintEngine.Default.GenerateChallenges(new Choices(n-292,a,b,c,d));
            valid &= generated.Count == 3 && generated.Select(x => x.ExpectedCount).Distinct().Count() == 3 && generated.All(x => x.ExpectedCount is >=292 and <=332 && x.Prompt.All(ch => ch <=127) && x.Id.StartsWith(FingerprintEngine.ChallengeVersion + "-", StringComparison.Ordinal));
            digest.AppendData(Encoding.UTF8.GetBytes(generated[0].ExpectedCount + "\n" + generated[0].Prompt + "\n"));
        }
        Check("all 20500 integer prompt combinations English / original mechanics / versioned IDs", valid);
        Check("all 20500 combinations exact approved provenance", Convert.ToHexString(digest.GetHashAndReset()).Equals(golden["fingerprintExhaustiveSha256"]!.ToString(), StringComparison.OrdinalIgnoreCase));
        File.WriteAllText(Path.Combine(directory,"checks.json"), System.Text.Json.JsonSerializer.Serialize(checks, new System.Text.Json.JsonSerializerOptions { WriteIndented=true }));
        return checks.Count;
    }
    private sealed class Choices(int n, int a, int b, int c, int d) : IFingerprintRandom
    {
        private int i;
        public int NextIndex(int length) { var at = i++; return at < 3 ? at == 0 ? n : 0 : new[] { a,b,c,d }[(at-3)%4]; }
        public Guid NextGuid() => Guid.Empty;
    }
}
