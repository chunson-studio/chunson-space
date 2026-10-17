namespace FAswitch.Fingerprint;

public sealed record FingerprintChallenge(string Id, int ExpectedCount, string Prompt);
public sealed record FingerprintOutput(string Text, int ExpectedCount = 0);
public enum FingerprintAnalysisStatus { Valid, NoUsableOutputs }
public sealed record FingerprintCandidate(string ModelId, string DisplayName, double Probability, double ProfileSimilarity,
    double Score, string Family, string FamilyName, double ConditionalProbability);
public sealed record FingerprintDiagnostic(int Index, int ParsedNumbers, int MinimumNumbers, bool Accepted);
public sealed record FingerprintFamilyProbability(string Family, string DisplayName, double Probability);
public sealed record FingerprintCalibration(int Queries, double Beta, double CvAccuracy);
public sealed record FingerprintAnalysis(FingerprintAnalysisStatus Status, IReadOnlyList<FingerprintCandidate> Results,
    IReadOnlyList<FingerprintDiagnostic> Diagnostics, int UsedOutputs, IReadOnlyList<FingerprintFamilyProbability> FamilyProbabilities,
    string? Prediction, string? PredictionName, double? Probability, string? FamilyPrediction, string? FamilyPredictionName,
    double? FamilyProbability, FingerprintCalibration? Calibration, string Message);
public sealed class FingerprintBankException(string message, Exception? inner = null) : Exception(message, inner);
public interface IFingerprintRandom
{
    int NextIndex(int length);
    Guid NextGuid();
}
