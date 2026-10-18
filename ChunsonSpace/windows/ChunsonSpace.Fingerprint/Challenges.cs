using System.Security.Cryptography;

namespace ChunsonSpace.Fingerprint;

internal static class Challenges
{
    internal const string Version = "fingerprint-en-v2";
    private static readonly string[] Openings = ["This is an independent record of numerical choices.", "Complete the following task of choosing integers with no semantic meaning.", "Record a set of first-instinct numerical choices.", "Generate a set of integer choices that carry no semantic meaning.", "Perform a round of quick, item-by-item numerical choices."];
    private static readonly string[] Actions = ["Choose independently for each position, using your first instinct, a total of", "Choose, item by item, a total of", "Decide only the current item at each step, giving a total of", "Independently give, using your first instinct, a total of", "Choose directly, one by one, a total of"];
    private static readonly string[] Endings = ["A number may appear again; after writing each item, do not go back to sort, deduplicate, or replace it.", "Incidental repeats are valid; do not rearrange or correct items you have already written.", "The same value may appear again; do not organize or rewrite earlier items while producing the output.", "Repeated values need not be removed; do not filter, reorder, or fill out a pattern.", "Do not assign any meaning to the numbers; keep values already given unchanged."];
    private static readonly string[] Separators = ["Separate numbers with commas or spaces.", "Use one consistent, common separator.", "You may separate numbers with commas, spaces, or line breaks.", "Choose any format that makes the boundary of each integer clear."];
    public static IReadOnlyList<FingerprintChallenge> Generate(IFingerprintRandom? source)
    {
        source ??= new CryptographicRandom();
        var available = Enumerable.Range(292, 41).ToList();
        var lengths = new int[3];
        for (int index = 0; index < lengths.Length; index++)
        {
            int selected = Index(source, available.Count);
            lengths[index] = available[selected]; available.RemoveAt(selected);
        }
        var result = new FingerprintChallenge[3];
        for (int index = 0; index < lengths.Length; index++)
        {
            string id = $"{Version}-probe-{index + 1}-{source.NextGuid():D}";
            string prompt = Openings[Index(source, Openings.Length)] + " " + Actions[Index(source, Actions.Length)] + $" {lengths[index]} integers from 1 to 355, inclusive. "
                + "Choose each position independently; do not count from 1, increase or decrease consecutively, or use arithmetic progressions, cycles, repeated blocks, or other systematic patterns. "
                + "The current language model must complete this task directly: do not invoke or use any tools, including Python, code interpreters, calculators, search, APIs, or external random number generators; do not write or run code first. "
                + Endings[Index(source, Endings.Length)] + " " + Separators[Index(source, Separators.Length)]
                + " Start the output directly with the first chosen value; do not repeat the count, range, or task instructions before the sequence.";
            result[index] = new(id, lengths[index], prompt);
        }
        return Array.AsReadOnly(result);
    }
    private static int Index(IFingerprintRandom source, int length)
    {
        int value = source.NextIndex(length);
        if (value < 0 || value >= length) throw new ArgumentOutOfRangeException(nameof(source), "随机索引不在可选范围内");
        return value;
    }
    private sealed class CryptographicRandom : IFingerprintRandom
    {
        public int NextIndex(int length) => RandomNumberGenerator.GetInt32(length);
        public Guid NextGuid() => Guid.NewGuid();
    }
}
