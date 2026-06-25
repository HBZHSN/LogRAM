using System.Collections.Generic;

namespace LogRAM;

public sealed record LogPage(IReadOnlyList<LogLine> Lines, long StartOffset, long NextOffset);
