using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LogRAM;

public sealed class LogFileDocument : IDisposable
{
    public static readonly long MaxFileSize = CalculateMaxFileSize();

    private const double AvailableMemoryUsageRatio = 0.8;

    private const int SampleSize = 64 * 1024;
    private const int ChunkBits = 26;
    private const int ChunkSize = 1 << ChunkBits;
    private const int ChunkMask = ChunkSize - 1;
    private const int SearchBatchSize = 1024;
    private const long ProgressBytes = 64L * 1024L * 1024L;
    private const long ParallelPlainSearchThreshold = 256L * 1024L * 1024L;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Vector<byte> VectorLowercaseA = new((byte)'a');
    private static readonly Vector<byte> VectorLowercaseZ = new((byte)'z');
    private static readonly Vector<byte> VectorAsciiCaseBit = new(0x20);

    private readonly byte[][] _chunks;
    private readonly List<long> _lineStarts;
    private readonly Encoding _encoding;

    private LogFileDocument(
        string filePath,
        long fileSize,
        LogTextEncoding encodingKind,
        Encoding encoding,
        byte[][] chunks,
        List<long> lineStarts)
    {
        FilePath = filePath;
        FileSize = fileSize;
        EncodingKind = encodingKind;
        _encoding = encoding;
        _chunks = chunks;
        _lineStarts = lineStarts;
    }

    public string FilePath { get; }

    public long FileSize { get; }

    public LogTextEncoding EncodingKind { get; }

    public long LineCount => _lineStarts.Count;

    public static LogFileDocument Open(string filePath, LogTextEncoding? encodingOverride)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("File does not exist.", filePath);
        }

        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Length > MaxFileSize)
        {
            throw new InvalidOperationException("The log file exceeds the available memory limit.");
        }

        var encodingKind = encodingOverride ?? DetectEncoding(filePath, fileInfo.Length);
        var encoding = GetEncoding(encodingKind);
        var chunks = LoadFileIntoMemory(filePath, fileInfo.Length, out var lineStarts);

        return new LogFileDocument(filePath, fileInfo.Length, encodingKind, encoding, chunks, lineStarts);
    }

    private static long CalculateMaxFileSize()
    {
        return (long)(GetAvailablePhysicalMemory() * AvailableMemoryUsageRatio);
    }

    public static long GetAvailablePhysicalMemory()
    {
        const long fallback = 2L * 1024L * 1024L * 1024L;

        try
        {
            var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status) && status.ullAvailPhys > 0)
            {
                return (long)status.ullAvailPhys;
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return available > 0 ? available : fallback;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    public LogPage ReadLines(long startOffset, long startLineNumber, int maxLines)
    {
        return ReadLinesFromLine(GetLineNumberForOffset(startOffset), maxLines);
    }

    public LogPage ReadLinesFromLine(long startLineNumber, int maxLines)
    {
        if (maxLines <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLines));
        }

        if (FileSize == 0 || _lineStarts.Count == 0)
        {
            return new LogPage(Array.Empty<LogLine>(), 0, 0);
        }

        var startIndex = (int)Math.Clamp(startLineNumber - 1, 0, _lineStarts.Count - 1);
        var count = Math.Min(maxLines, _lineStarts.Count - startIndex);
        var lines = new List<LogLine>(count);

        for (var i = 0; i < count; i++)
        {
            var lineIndex = startIndex + i;
            var start = _lineStarts[lineIndex];
            var next = GetLineEndOffset(lineIndex);
            lines.Add(new LogLine(lineIndex + 1L, DecodeLine(start, next), start, next));
        }

        var nextIndex = startIndex + count;
        var nextOffset = nextIndex < _lineStarts.Count ? _lineStarts[nextIndex] : FileSize;
        return new LogPage(lines, _lineStarts[startIndex], nextOffset);
    }

    internal string GetLineText(long lineNumber)
    {
        if (_lineStarts.Count == 0)
        {
            return string.Empty;
        }

        var lineIndex = (int)Math.Clamp(lineNumber - 1, 0, _lineStarts.Count - 1);
        return DecodeLine(_lineStarts[lineIndex], GetLineEndOffset(lineIndex));
    }

    public long GetOffsetForLine(long lineNumber)
    {
        if (_lineStarts.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Clamp(lineNumber - 1, 0, _lineStarts.Count - 1);
        return _lineStarts[index];
    }

    public long GetLineNumberForOffset(long offset)
    {
        return GetLineIndexForOffset(offset) + 1L;
    }

    private int GetLineIndexForOffset(long offset)
    {
        if (_lineStarts.Count == 0)
        {
            return 0;
        }

        var clampedOffset = Math.Clamp(offset, 0, FileSize);
        var index = _lineStarts.BinarySearch(clampedOffset);
        if (index >= 0)
        {
            return index;
        }

        return Math.Max(0, ~index - 1);
    }

    public Task<LogSearchSummary> SearchAsync(
        string pattern,
        bool useRegex,
        bool caseSensitive,
        Action<IReadOnlyList<LogSearchResult>> onBatch,
        IProgress<LogSearchProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            throw new ArgumentException("Search pattern cannot be empty.", nameof(pattern));
        }

        Regex? regex = null;
        byte[]? patternBytes = null;
        var plainIgnoreCaseNeedsDecode = false;

        if (useRegex)
        {
            var options = RegexOptions.CultureInvariant;
            if (!caseSensitive)
            {
                options |= RegexOptions.IgnoreCase;
            }

            regex = new Regex(pattern, options);
        }
        else
        {
            patternBytes = _encoding.GetBytes(pattern);
            plainIgnoreCaseNeedsDecode = !caseSensitive && ContainsNonAscii(patternBytes);
        }

        return Task.Run(() =>
        {
            if (FileSize == 0)
            {
                progress?.Report(new LogSearchProgress(0, FileSize, 0));
                return new LogSearchSummary(0);
            }

            if (regex is null && !plainIgnoreCaseNeedsDecode && patternBytes is not null)
            {
                return SearchPlainBytes(patternBytes, caseSensitive, onBatch, progress, cancellationToken);
            }

            var batch = new List<LogSearchResult>(SearchBatchSize);
            var matchCount = 0L;
            var lastProgressAt = 0L;

            for (var lineIndex = 0; lineIndex < _lineStarts.Count; lineIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var start = _lineStarts[lineIndex];
                var next = GetLineEndOffset(lineIndex);
                var match = IsSearchMatch(start, next, pattern, regex, patternBytes, caseSensitive, plainIgnoreCaseNeedsDecode);

                if (match)
                {
                    matchCount++;
                    batch.Add(new LogSearchResult(lineIndex + 1L, start, this));

                    if (batch.Count >= SearchBatchSize)
                    {
                        onBatch(batch.ToArray());
                        batch.Clear();
                    }
                }

                if (next - lastProgressAt >= ProgressBytes || next >= FileSize)
                {
                    progress?.Report(new LogSearchProgress(next, FileSize, matchCount));
                    lastProgressAt = next;
                }
            }

            if (batch.Count > 0)
            {
                onBatch(batch.ToArray());
            }

            progress?.Report(new LogSearchProgress(FileSize, FileSize, matchCount));
            return new LogSearchSummary(matchCount);
        }, cancellationToken);
    }

    private LogSearchSummary SearchPlainBytes(
        byte[] pattern,
        bool caseSensitive,
        Action<IReadOnlyList<LogSearchResult>> onBatch,
        IProgress<LogSearchProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (pattern.Length == 0 || pattern.Length > FileSize)
        {
            progress?.Report(new LogSearchProgress(FileSize, FileSize, 0));
            return new LogSearchSummary(0);
        }

        if (!caseSensitive && !ContainsAsciiLetter(pattern))
        {
            caseSensitive = true;
        }

        var batch = new List<LogSearchResult>(SearchBatchSize);
        var matchCount = 0L;
        var currentLineIndex = 0;
        var lastMatchedLineIndex = -1;
        var normalizedPattern = caseSensitive ? pattern : NormalizeAsciiPattern(pattern);
        var ignoreCaseAnchors = caseSensitive ? Array.Empty<AsciiSearchAnchor>() : CreateAsciiSearchAnchors(normalizedPattern);

        if (ShouldSearchPlainBytesInParallel())
        {
            return SearchPlainBytesParallel(
                normalizedPattern,
                caseSensitive,
                ignoreCaseAnchors,
                onBatch,
                progress,
                cancellationToken);
        }

        for (var chunkIndex = 0; chunkIndex < _chunks.Length; chunkIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = _chunks[chunkIndex];
            var chunkBaseOffset = (long)chunkIndex << ChunkBits;
            currentLineIndex = Math.Max(currentLineIndex, GetLineIndexForOffset(chunkBaseOffset));
            var maxContainedStart = chunk.Length - pattern.Length;

            if (caseSensitive)
            {
                SearchChunkCaseSensitive(
                    chunk,
                    chunkBaseOffset,
                    normalizedPattern,
                    maxContainedStart,
                    ref currentLineIndex,
                    ref lastMatchedLineIndex,
                    batch,
                    onBatch,
                    ref matchCount,
                    cancellationToken);
            }
            else
            {
                SearchChunkAsciiIgnoreCase(
                    chunk,
                    chunkBaseOffset,
                    normalizedPattern,
                    ignoreCaseAnchors,
                    maxContainedStart,
                    ref currentLineIndex,
                    ref lastMatchedLineIndex,
                    batch,
                    onBatch,
                    ref matchCount,
                    cancellationToken);
            }

            var tailStart = Math.Max(0, chunk.Length - pattern.Length + 1);
            for (var localOffset = tailStart; localOffset < chunk.Length; localOffset++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var absoluteOffset = chunkBaseOffset + localOffset;
                if (absoluteOffset + pattern.Length > FileSize)
                {
                    break;
                }

                if (!BytesMatchAt(absoluteOffset, normalizedPattern, caseSensitive))
                {
                    continue;
                }

                AddPlainSearchMatch(
                    absoluteOffset,
                    ref currentLineIndex,
                    ref lastMatchedLineIndex,
                    batch,
                    onBatch,
                    ref matchCount);
            }

            progress?.Report(new LogSearchProgress(Math.Min(chunkBaseOffset + chunk.Length, FileSize), FileSize, matchCount));
        }

        if (batch.Count > 0)
        {
            onBatch(batch.ToArray());
        }

        progress?.Report(new LogSearchProgress(FileSize, FileSize, matchCount));
        return new LogSearchSummary(matchCount);
    }

    private bool ShouldSearchPlainBytesInParallel()
    {
        return FileSize >= ParallelPlainSearchThreshold && _chunks.Length > 1 && Environment.ProcessorCount > 1;
    }

    private LogSearchSummary SearchPlainBytesParallel(
        byte[] normalizedPattern,
        bool caseSensitive,
        AsciiSearchAnchor[] ignoreCaseAnchors,
        Action<IReadOnlyList<LogSearchResult>> onBatch,
        IProgress<LogSearchProgress>? progress,
        CancellationToken cancellationToken)
    {
        var chunkResults = new List<LogSearchResult>?[_chunks.Length];
        var scannedBytes = 0L;
        var reportedMatches = 0L;
        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, _chunks.Length)
        };

        Parallel.For(0, _chunks.Length, parallelOptions, chunkIndex =>
        {
            var results = SearchPlainBytesInChunk(chunkIndex, normalizedPattern, caseSensitive, ignoreCaseAnchors, cancellationToken);
            chunkResults[chunkIndex] = results;

            var bytes = Interlocked.Add(ref scannedBytes, _chunks[chunkIndex].Length);
            var matches = Interlocked.Add(ref reportedMatches, results?.Count ?? 0);
            progress?.Report(new LogSearchProgress(bytes, FileSize, matches));
        });

        var batch = new List<LogSearchResult>(SearchBatchSize);
        var matchCount = 0L;
        var lastEmittedLineNumber = 0L;

        for (var chunkIndex = 0; chunkIndex < chunkResults.Length; chunkIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var results = chunkResults[chunkIndex];
            if (results is null)
            {
                continue;
            }

            foreach (var result in results)
            {
                if (result.LineNumber == lastEmittedLineNumber)
                {
                    continue;
                }

                lastEmittedLineNumber = result.LineNumber;
                matchCount++;
                batch.Add(result);

                if (batch.Count >= SearchBatchSize)
                {
                    onBatch(batch.ToArray());
                    batch.Clear();
                }
            }
        }

        if (batch.Count > 0)
        {
            onBatch(batch.ToArray());
        }

        progress?.Report(new LogSearchProgress(FileSize, FileSize, matchCount));
        return new LogSearchSummary(matchCount);
    }

    private List<LogSearchResult>? SearchPlainBytesInChunk(
        int chunkIndex,
        byte[] normalizedPattern,
        bool caseSensitive,
        AsciiSearchAnchor[] ignoreCaseAnchors,
        CancellationToken cancellationToken)
    {
        var chunk = _chunks[chunkIndex];
        var chunkBaseOffset = (long)chunkIndex << ChunkBits;
        var currentLineIndex = GetLineIndexForOffset(chunkBaseOffset);
        var lastMatchedLineIndex = -1;
        var results = new List<LogSearchResult>();
        var maxContainedStart = chunk.Length - normalizedPattern.Length;

        if (caseSensitive)
        {
            SearchChunkCaseSensitiveToList(
                chunk,
                chunkBaseOffset,
                normalizedPattern,
                maxContainedStart,
                ref currentLineIndex,
                ref lastMatchedLineIndex,
                results,
                cancellationToken);
        }
        else
        {
            SearchChunkAsciiIgnoreCaseToList(
                chunk,
                chunkBaseOffset,
                normalizedPattern,
                ignoreCaseAnchors[0],
                maxContainedStart,
                ref currentLineIndex,
                ref lastMatchedLineIndex,
                results,
                cancellationToken);
        }

        var tailStart = Math.Max(0, chunk.Length - normalizedPattern.Length + 1);
        for (var localOffset = tailStart; localOffset < chunk.Length; localOffset++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var absoluteOffset = chunkBaseOffset + localOffset;
            if (absoluteOffset + normalizedPattern.Length > FileSize)
            {
                break;
            }

            if (!BytesMatchAt(absoluteOffset, normalizedPattern, caseSensitive))
            {
                continue;
            }

            AddPlainSearchMatchToList(absoluteOffset, ref currentLineIndex, ref lastMatchedLineIndex, results);
        }

        return results.Count == 0 ? null : results;
    }

    private void SearchChunkCaseSensitive(
        byte[] chunk,
        long chunkBaseOffset,
        byte[] pattern,
        int maxContainedStart,
        ref int currentLineIndex,
        ref int lastMatchedLineIndex,
        List<LogSearchResult> batch,
        Action<IReadOnlyList<LogSearchResult>> onBatch,
        ref long matchCount,
        CancellationToken cancellationToken)
    {
        var searchOffset = 0;

        while (searchOffset <= maxContainedStart)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativeIndex = chunk.AsSpan(searchOffset).IndexOf(pattern);
            if (relativeIndex < 0)
            {
                break;
            }

            var localOffset = searchOffset + relativeIndex;
            var absoluteOffset = chunkBaseOffset + localOffset;
            AddPlainSearchMatch(
                absoluteOffset,
                ref currentLineIndex,
                ref lastMatchedLineIndex,
                batch,
                onBatch,
                ref matchCount);

            searchOffset = GetNextSearchOffset(chunkBaseOffset, chunk.Length, localOffset, currentLineIndex);
        }
    }

    private void SearchChunkAsciiIgnoreCase(
        byte[] chunk,
        long chunkBaseOffset,
        byte[] normalizedPattern,
        AsciiSearchAnchor[] anchors,
        int maxContainedStart,
        ref int currentLineIndex,
        ref int lastMatchedLineIndex,
        List<LogSearchResult> batch,
        Action<IReadOnlyList<LogSearchResult>> onBatch,
        ref long matchCount,
        CancellationToken cancellationToken)
    {
        var anchor = anchors[0];
        var maxAnchorOffset = maxContainedStart + anchor.PatternOffset;
        var searchOffset = anchor.PatternOffset;

        while (searchOffset <= maxAnchorOffset)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativeIndex = IndexOfAsciiIgnoreCase(
                chunk.AsSpan(searchOffset, maxAnchorOffset - searchOffset + 1),
                anchor.NormalizedValue);
            if (relativeIndex < 0)
            {
                break;
            }

            var anchorOffset = searchOffset + relativeIndex;
            var localOffset = anchorOffset - anchor.PatternOffset;
            if (AsciiBytesMatchAt(chunk.AsSpan(localOffset, normalizedPattern.Length), normalizedPattern))
            {
                var absoluteOffset = chunkBaseOffset + localOffset;
                AddPlainSearchMatch(
                    absoluteOffset,
                    ref currentLineIndex,
                    ref lastMatchedLineIndex,
                    batch,
                    onBatch,
                    ref matchCount);

                searchOffset = GetNextSearchOffset(chunkBaseOffset, chunk.Length, localOffset, currentLineIndex) + anchor.PatternOffset;
            }
            else
            {
                searchOffset = anchorOffset + 1;
            }
        }
    }

    private void SearchChunkCaseSensitiveToList(
        byte[] chunk,
        long chunkBaseOffset,
        byte[] pattern,
        int maxContainedStart,
        ref int currentLineIndex,
        ref int lastMatchedLineIndex,
        List<LogSearchResult> results,
        CancellationToken cancellationToken)
    {
        var searchOffset = 0;

        while (searchOffset <= maxContainedStart)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativeIndex = chunk.AsSpan(searchOffset).IndexOf(pattern);
            if (relativeIndex < 0)
            {
                break;
            }

            var localOffset = searchOffset + relativeIndex;
            AddPlainSearchMatchToList(chunkBaseOffset + localOffset, ref currentLineIndex, ref lastMatchedLineIndex, results);
            searchOffset = GetNextSearchOffset(chunkBaseOffset, chunk.Length, localOffset, currentLineIndex);
        }
    }

    private void SearchChunkAsciiIgnoreCaseToList(
        byte[] chunk,
        long chunkBaseOffset,
        byte[] normalizedPattern,
        AsciiSearchAnchor anchor,
        int maxContainedStart,
        ref int currentLineIndex,
        ref int lastMatchedLineIndex,
        List<LogSearchResult> results,
        CancellationToken cancellationToken)
    {
        var maxAnchorOffset = maxContainedStart + anchor.PatternOffset;
        var searchOffset = anchor.PatternOffset;

        while (searchOffset <= maxAnchorOffset)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativeIndex = IndexOfAsciiIgnoreCase(
                chunk.AsSpan(searchOffset, maxAnchorOffset - searchOffset + 1),
                anchor.NormalizedValue);
            if (relativeIndex < 0)
            {
                break;
            }

            var anchorOffset = searchOffset + relativeIndex;
            var localOffset = anchorOffset - anchor.PatternOffset;
            if (AsciiBytesMatchAt(chunk.AsSpan(localOffset, normalizedPattern.Length), normalizedPattern))
            {
                AddPlainSearchMatchToList(chunkBaseOffset + localOffset, ref currentLineIndex, ref lastMatchedLineIndex, results);
                searchOffset = GetNextSearchOffset(chunkBaseOffset, chunk.Length, localOffset, currentLineIndex) + anchor.PatternOffset;
            }
            else
            {
                searchOffset = anchorOffset + 1;
            }
        }
    }

    private void AddPlainSearchMatch(
        long absoluteOffset,
        ref int currentLineIndex,
        ref int lastMatchedLineIndex,
        List<LogSearchResult> batch,
        Action<IReadOnlyList<LogSearchResult>> onBatch,
        ref long matchCount)
    {
        while (currentLineIndex + 1 < _lineStarts.Count && _lineStarts[currentLineIndex + 1] <= absoluteOffset)
        {
            currentLineIndex++;
        }

        if (currentLineIndex == lastMatchedLineIndex)
        {
            return;
        }

        lastMatchedLineIndex = currentLineIndex;
        matchCount++;
        batch.Add(new LogSearchResult(currentLineIndex + 1L, _lineStarts[currentLineIndex], this));

        if (batch.Count >= SearchBatchSize)
        {
            onBatch(batch.ToArray());
            batch.Clear();
        }
    }

    private void AddPlainSearchMatchToList(
        long absoluteOffset,
        ref int currentLineIndex,
        ref int lastMatchedLineIndex,
        List<LogSearchResult> results)
    {
        while (currentLineIndex + 1 < _lineStarts.Count && _lineStarts[currentLineIndex + 1] <= absoluteOffset)
        {
            currentLineIndex++;
        }

        if (currentLineIndex == lastMatchedLineIndex)
        {
            return;
        }

        lastMatchedLineIndex = currentLineIndex;
        results.Add(new LogSearchResult(currentLineIndex + 1L, _lineStarts[currentLineIndex], this));
    }

    private int GetNextSearchOffset(long chunkBaseOffset, int chunkLength, int localOffset, int currentLineIndex)
    {
        var nextOffset = localOffset + 1L;
        if (currentLineIndex + 1 < _lineStarts.Count)
        {
            var nextLineLocalOffset = _lineStarts[currentLineIndex + 1] - chunkBaseOffset;
            if (nextLineLocalOffset > localOffset)
            {
                nextOffset = Math.Max(nextOffset, nextLineLocalOffset);
            }
        }

        return (int)Math.Min(nextOffset, chunkLength);
    }

    private static byte[][] LoadFileIntoMemory(string filePath, long fileSize, out List<long> lineStarts)
    {
        lineStarts = new List<long>(EstimateLineIndexCapacity(fileSize)) { 0 };

        if (fileSize == 0)
        {
            lineStarts.Clear();
            return Array.Empty<byte[]>();
        }

        var chunkCount = checked((int)((fileSize + ChunkSize - 1) / ChunkSize));
        var chunks = new byte[chunkCount][];
        var loaded = 0L;

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1024 * 1024, FileOptions.SequentialScan);

        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            var chunkLength = (int)Math.Min(ChunkSize, fileSize - loaded);
            var chunk = new byte[chunkLength];
            ReadExactly(stream, chunk);
            chunks[chunkIndex] = chunk;

            var searchStart = 0;
            while (searchStart < chunk.Length)
            {
                var index = Array.IndexOf(chunk, (byte)'\n', searchStart);
                if (index < 0)
                {
                    break;
                }

                var nextOffset = loaded + index + 1;
                if (nextOffset < fileSize)
                {
                    lineStarts.Add(nextOffset);
                }

                searchStart = index + 1;
            }

            loaded += chunk.Length;
        }

        lineStarts.TrimExcess();
        return chunks;
    }

    private static int EstimateLineIndexCapacity(long fileSize)
    {
        const long estimatedAverageLineBytes = 160;
        var estimated = fileSize / estimatedAverageLineBytes;
        if (estimated <= 0)
        {
            return 1;
        }

        return estimated > int.MaxValue - 1 ? int.MaxValue : (int)estimated + 1;
    }

    private static void ReadExactly(FileStream stream, byte[] buffer)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = stream.Read(buffer, totalRead, buffer.Length - totalRead);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of file while loading log into memory.");
            }

            totalRead += read;
        }
    }

    private static LogTextEncoding DetectEncoding(string filePath, long fileSize)
    {
        if (fileSize == 0)
        {
            return LogTextEncoding.Utf8;
        }

        var length = (int)Math.Min(fileSize, SampleSize);
        var sample = new byte[length];

        using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var read = stream.Read(sample, 0, sample.Length);
            if (read != sample.Length)
            {
                Array.Resize(ref sample, read);
            }
        }

        if (sample.Length >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF)
        {
            return LogTextEncoding.Utf8;
        }

        return LooksLikeUtf8(sample) ? LogTextEncoding.Utf8 : LogTextEncoding.Gbk;
    }

    private static bool LooksLikeUtf8(byte[] sample)
    {
        try
        {
            var decoder = StrictUtf8.GetDecoder();
            var chars = new char[StrictUtf8.GetMaxCharCount(sample.Length)];
            decoder.Convert(sample, 0, sample.Length, chars, 0, chars.Length, flush: false, out _, out _, out _);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static Encoding GetEncoding(LogTextEncoding encoding)
    {
        return encoding switch
        {
            LogTextEncoding.Utf8 => Encoding.UTF8,
            LogTextEncoding.Gbk => Encoding.GetEncoding(936),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding))
        };
    }

    private long GetLineEndOffset(int lineIndex)
    {
        return lineIndex + 1 < _lineStarts.Count ? _lineStarts[lineIndex + 1] : FileSize;
    }

    private byte GetByte(long offset)
    {
        return _chunks[(int)(offset >> ChunkBits)][(int)(offset & ChunkMask)];
    }

    private string DecodeLine(long start, long next)
    {
        var contentStart = start;
        var contentEnd = next;

        if (contentEnd > contentStart && GetByte(contentEnd - 1) == (byte)'\n')
        {
            contentEnd--;
        }

        if (contentEnd > contentStart && GetByte(contentEnd - 1) == (byte)'\r')
        {
            contentEnd--;
        }

        if (contentStart == 0 && contentEnd - contentStart >= 3 && GetByte(0) == 0xEF && GetByte(1) == 0xBB && GetByte(2) == 0xBF)
        {
            contentStart = 3;
        }

        var length = checked((int)(contentEnd - contentStart));
        if (length <= 0)
        {
            return string.Empty;
        }

        var chunkIndex = (int)(contentStart >> ChunkBits);
        var chunkOffset = (int)(contentStart & ChunkMask);
        var chunk = _chunks[chunkIndex];

        if (chunkOffset + length <= chunk.Length)
        {
            return _encoding.GetString(chunk, chunkOffset, length);
        }

        var bytes = new byte[length];
        CopyRange(contentStart, bytes);
        return _encoding.GetString(bytes);
    }

    private void CopyRange(long start, byte[] destination)
    {
        var copied = 0;
        var position = start;

        while (copied < destination.Length)
        {
            var chunkIndex = (int)(position >> ChunkBits);
            var chunkOffset = (int)(position & ChunkMask);
            var chunk = _chunks[chunkIndex];
            var count = Math.Min(destination.Length - copied, chunk.Length - chunkOffset);

            Array.Copy(chunk, chunkOffset, destination, copied, count);
            copied += count;
            position += count;
        }
    }

    private bool IsSearchMatch(
        long start,
        long next,
        string pattern,
        Regex? regex,
        byte[]? patternBytes,
        bool caseSensitive,
        bool plainIgnoreCaseNeedsDecode)
    {
        if (regex is not null)
        {
            return regex.IsMatch(DecodeLine(start, next));
        }

        if (plainIgnoreCaseNeedsDecode)
        {
            return DecodeLine(start, next).Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }

        var bytes = patternBytes ?? Array.Empty<byte>();
        return caseSensitive ? ContainsBytes(start, next, bytes) : ContainsBytesAsciiIgnoreCase(start, next, bytes);
    }

    private static byte[] NormalizeAsciiPattern(byte[] pattern)
    {
        var normalized = new byte[pattern.Length];
        for (var i = 0; i < pattern.Length; i++)
        {
            normalized[i] = ToAsciiUpper(pattern[i]);
        }

        return normalized;
    }

    private static AsciiSearchAnchor[] CreateAsciiSearchAnchors(byte[] normalizedPattern)
    {
        var anchorCount = Math.Min(3, normalizedPattern.Length);
        var anchors = new AsciiSearchAnchor[anchorCount];
        var firstIndex = FindBestAsciiSearchAnchor(normalizedPattern, -1, -1);
        anchors[0] = new AsciiSearchAnchor(firstIndex, normalizedPattern[firstIndex]);

        if (anchorCount > 1)
        {
            var secondIndex = FindBestAsciiSearchAnchor(normalizedPattern, firstIndex, -1);
            anchors[1] = new AsciiSearchAnchor(secondIndex, normalizedPattern[secondIndex]);
        }

        if (anchorCount > 2)
        {
            var thirdIndex = FindBestAsciiSearchAnchor(normalizedPattern, anchors[0].PatternOffset, anchors[1].PatternOffset);
            anchors[2] = new AsciiSearchAnchor(thirdIndex, normalizedPattern[thirdIndex]);
        }

        return anchors;
    }

    private static int FindBestAsciiSearchAnchor(byte[] normalizedPattern, int excludedIndex1, int excludedIndex2)
    {
        var bestIndex = -1;
        var bestScore = int.MinValue;
        var bestDistance = int.MinValue;

        for (var i = 0; i < normalizedPattern.Length; i++)
        {
            if (i == excludedIndex1 || i == excludedIndex2)
            {
                continue;
            }

            var score = GetAsciiSearchAnchorScore(normalizedPattern[i]);
            var distance = GetAsciiSearchAnchorDistance(i, excludedIndex1, excludedIndex2, normalizedPattern.Length);
            if (score > bestScore || (score == bestScore && distance > bestDistance))
            {
                bestIndex = i;
                bestScore = score;
                bestDistance = distance;
            }
        }

        return bestIndex;
    }

    private static int GetAsciiSearchAnchorDistance(int index, int excludedIndex1, int excludedIndex2, int patternLength)
    {
        var distance = Math.Max(index, patternLength - 1 - index);

        if (excludedIndex1 >= 0)
        {
            distance = Math.Min(distance, Math.Abs(index - excludedIndex1));
        }

        if (excludedIndex2 >= 0)
        {
            distance = Math.Min(distance, Math.Abs(index - excludedIndex2));
        }

        return distance;
    }

    private static int GetAsciiSearchAnchorScore(byte value)
    {
        if (value is (byte)' ' or (byte)'\t')
        {
            return 0;
        }

        if (IsAsciiUpperLetter(value))
        {
            return 1;
        }

        if (value is (byte)':' or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'/' or (byte)'\\')
        {
            return 2;
        }

        if (value is >= (byte)'0' and <= (byte)'9')
        {
            return 3;
        }

        return 4;
    }

    private static int IndexOfAsciiIgnoreCase(ReadOnlySpan<byte> source, byte normalizedValue)
    {
        return IsAsciiUpperLetter(normalizedValue)
            ? source.IndexOfAny(normalizedValue, ToAsciiLower(normalizedValue))
            : source.IndexOf(normalizedValue);
    }

    private static bool AsciiBytesMatchAt(ReadOnlySpan<byte> source, byte[] normalizedPattern)
    {
        var index = 0;
        var vectorLength = Vector<byte>.Count;
        if (Vector.IsHardwareAccelerated && normalizedPattern.Length >= vectorLength)
        {
            for (; index <= normalizedPattern.Length - vectorLength; index += vectorLength)
            {
                var sourceVector = ToAsciiUpper(new Vector<byte>(source.Slice(index, vectorLength)));
                var patternVector = new Vector<byte>(normalizedPattern, index);
                if (!Vector.EqualsAll(sourceVector, patternVector))
                {
                    return false;
                }
            }
        }

        for (; index < normalizedPattern.Length; index++)
        {
            if (ToAsciiUpper(source[index]) != normalizedPattern[index])
            {
                return false;
            }
        }

        return true;
    }

    private bool BytesMatchAt(long offset, byte[] normalizedPattern, bool caseSensitive)
    {
        var remaining = normalizedPattern.Length;
        var patternIndex = 0;
        var position = offset;

        while (remaining > 0)
        {
            var chunkIndex = (int)(position >> ChunkBits);
            var chunkOffset = (int)(position & ChunkMask);
            var chunk = _chunks[chunkIndex];
            var count = Math.Min(remaining, chunk.Length - chunkOffset);

            for (var i = 0; i < count; i++)
            {
                var source = chunk[chunkOffset + i];
                var normalizedSource = caseSensitive ? source : ToAsciiUpper(source);
                if (normalizedSource != normalizedPattern[patternIndex + i])
                {
                    return false;
                }
            }

            remaining -= count;
            patternIndex += count;
            position += count;
        }

        return true;
    }

    private bool ContainsBytes(long start, long next, byte[] pattern)
    {
        if (pattern.Length == 0)
        {
            return true;
        }

        if (next - start < pattern.Length)
        {
            return false;
        }

        var lastStart = next - pattern.Length;
        for (var offset = start; offset <= lastStart; offset++)
        {
            var matched = true;
            for (var i = 0; i < pattern.Length; i++)
            {
                if (GetByte(offset + i) != pattern[i])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private bool ContainsBytesAsciiIgnoreCase(long start, long next, byte[] pattern)
    {
        if (pattern.Length == 0)
        {
            return true;
        }

        if (next - start < pattern.Length)
        {
            return false;
        }

        var lastStart = next - pattern.Length;
        for (var offset = start; offset <= lastStart; offset++)
        {
            var matched = true;
            for (var i = 0; i < pattern.Length; i++)
            {
                if (ToAsciiUpper(GetByte(offset + i)) != ToAsciiUpper(pattern[i]))
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsNonAscii(byte[] bytes)
    {
        foreach (var value in bytes)
        {
            if (value >= 128)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAsciiLetter(byte[] bytes)
    {
        foreach (var value in bytes)
        {
            if ((value >= (byte)'A' && value <= (byte)'Z') || (value >= (byte)'a' && value <= (byte)'z'))
            {
                return true;
            }
        }

        return false;
    }

    private static byte ToAsciiUpper(byte value)
    {
        return value is >= (byte)'a' and <= (byte)'z' ? (byte)(value - 32) : value;
    }

    private static Vector<byte> ToAsciiUpper(Vector<byte> value)
    {
        var lowercaseMask = Vector.BitwiseAnd(
            Vector.GreaterThanOrEqual(value, VectorLowercaseA),
            Vector.LessThanOrEqual(value, VectorLowercaseZ));
        return value - Vector.BitwiseAnd(lowercaseMask, VectorAsciiCaseBit);
    }

    private static byte ToAsciiLower(byte normalizedUpperValue)
    {
        return (byte)(normalizedUpperValue + 32);
    }

    private static bool IsAsciiUpperLetter(byte value)
    {
        return value is >= (byte)'A' and <= (byte)'Z';
    }

    private readonly struct AsciiSearchAnchor
    {
        public AsciiSearchAnchor(int patternOffset, byte normalizedValue)
        {
            PatternOffset = patternOffset;
            NormalizedValue = normalizedValue;
        }

        public int PatternOffset { get; }

        public byte NormalizedValue { get; }
    }

    public void Dispose()
    {
    }
}
