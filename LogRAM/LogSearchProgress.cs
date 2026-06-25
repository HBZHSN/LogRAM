namespace LogRAM;

public sealed record LogSearchProgress(long BytesRead, long TotalBytes, long MatchCount);
