namespace LogRAM;

public sealed record LogLine(long LineNumber, string Text, long StartOffset, long NextOffset);
