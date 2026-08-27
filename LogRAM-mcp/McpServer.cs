using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LogRAM;

namespace LogRAM.Mcp;

internal sealed class McpServer : IDisposable
{
    private const string LatestProtocolVersion = "2025-11-25";
    private const int MaxTerms = 32;
    private const int MaxTermLength = 8192;
    private readonly Dictionary<string, LogFileDocument> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly JsonElement Tools = JsonDocument.Parse(
        """
        {
          "tools": [
            {
              "name": "search_log",
              "title": "Search a large log",
              "description": "Searches a log using LogRAM's in-memory engine. The file is loaded automatically on first use and remains cached for later calls. query is ANDed with include_any/include_all/exclude. Use small context and max_results first, then read_log for focused follow-up.",
              "inputSchema": {
                "type": "object",
                "properties": {
                  "path": { "type": "string", "description": "Absolute or working-directory-relative log path." },
                  "query": { "type": "string", "description": "Plain text query, or a regex when regex=true." },
                  "include_any": { "type": "array", "items": { "type": "string" }, "maxItems": 32, "description": "At least one term must occur (OR)." },
                  "include_all": { "type": "array", "items": { "type": "string" }, "maxItems": 32, "description": "Every term must occur (AND)." },
                  "exclude": { "type": "array", "items": { "type": "string" }, "maxItems": 32, "description": "No term may occur (NOT)." },
                  "regex": { "type": "boolean", "default": false, "description": "Treat query as a regular expression." },
                  "case_sensitive": { "type": "boolean", "default": false },
                  "before": { "type": "integer", "minimum": 0, "maximum": 200, "default": 0, "description": "Context lines before each match." },
                  "after": { "type": "integer", "minimum": 0, "maximum": 200, "default": 0, "description": "Context lines after each match." },
                  "max_results": { "type": "integer", "minimum": 1, "maximum": 1000, "default": 100, "description": "Maximum matches returned; match_count still reports the total." },
                  "start_line": { "type": "integer", "minimum": 1, "default": 1 },
                  "end_line": { "type": "integer", "minimum": 1 },
                  "encoding": { "type": "string", "enum": ["utf8", "gbk"], "default": "utf8" },
                  "refresh": { "type": "boolean", "default": false, "description": "Read content appended since this path was cached." }
                },
                "required": ["path"],
                "additionalProperties": false
              },
              "annotations": { "readOnlyHint": true, "destructiveHint": false, "idempotentHint": true, "openWorldHint": false }
            },
            {
              "name": "read_log",
              "title": "Read log lines",
              "description": "Reads a bounded line range from a cached log, loading it on first use. Use after search_log to inspect a precise region without another search.",
              "inputSchema": {
                "type": "object",
                "properties": {
                  "path": { "type": "string" },
                  "start_line": { "type": "integer", "minimum": 1 },
                  "line_count": { "type": "integer", "minimum": 1, "maximum": 2000, "default": 100 },
                  "encoding": { "type": "string", "enum": ["utf8", "gbk"], "default": "utf8" },
                  "refresh": { "type": "boolean", "default": false }
                },
                "required": ["path", "start_line"],
                "additionalProperties": false
              },
              "annotations": { "readOnlyHint": true, "destructiveHint": false, "idempotentHint": true, "openWorldHint": false }
            },
            {
              "name": "list_open_logs",
              "title": "List cached logs",
              "description": "Lists logs currently retained in this MCP process and their memory usage.",
              "inputSchema": { "type": "object", "additionalProperties": false },
              "annotations": { "readOnlyHint": true, "destructiveHint": false, "idempotentHint": true, "openWorldHint": false }
            },
            {
              "name": "close_log",
              "title": "Release cached log memory",
              "description": "Releases one cached log, or every cached log when path is omitted. This never changes the log file.",
              "inputSchema": {
                "type": "object",
                "properties": { "path": { "type": "string" } },
                "additionalProperties": false
              },
              "annotations": { "readOnlyHint": true, "destructiveHint": false, "idempotentHint": true, "openWorldHint": false }
            }
          ]
        }
        """).RootElement.Clone();

    public async Task<int> RunAsync()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = new UTF8Encoding(false);

        // ponytail: serial requests keep LogFileDocument refresh/search thread-safe; add per-document locks only if concurrent clients appear.
        while (await Console.In.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            await HandleMessageAsync(line);
        }

        return 0;
    }

    private async Task HandleMessageAsync(string message)
    {
        JsonElement id = default;
        var hasId = false;
        try
        {
            using var json = JsonDocument.Parse(message);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                hasId = root.TryGetProperty("id", out id);
            }

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("jsonrpc", out var jsonrpc) ||
                jsonrpc.GetString() != "2.0" ||
                !root.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String)
            {
                throw new RpcException(-32600, "Invalid Request");
            }

            if (!hasId)
            {
                return;
            }

            var method = methodElement.GetString();
            object result = method switch
            {
                "initialize" => Initialize(root),
                "ping" => new { },
                "tools/list" => Tools,
                "tools/call" => await CallToolAsync(root),
                _ => throw new RpcException(-32601, $"Method not found: {method}")
            };
            await WriteAsync(new { jsonrpc = "2.0", id = id.Clone(), result });
        }
        catch (JsonException ex)
        {
            await WriteErrorAsync(hasId ? id.Clone() : null, -32700, ex.Message);
        }
        catch (RpcException ex)
        {
            await WriteErrorAsync(hasId ? id.Clone() : null, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(hasId ? id.Clone() : null, -32603, ex.Message);
        }
    }

    private static object Initialize(JsonElement request)
    {
        var requested = request.TryGetProperty("params", out var parameters) &&
                        parameters.TryGetProperty("protocolVersion", out var version) &&
                        version.ValueKind == JsonValueKind.String
            ? version.GetString()
            : null;
        var supported = requested is "2024-11-05" or "2025-03-26" or "2025-06-18" or LatestProtocolVersion
            ? requested
            : LatestProtocolVersion;

        return new
        {
            protocolVersion = supported,
            capabilities = new { tools = new { listChanged = false } },
            serverInfo = new
            {
                name = "logram",
                title = "LogRAM MCP",
                version = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "1.3.0",
                description = "Fast in-memory search and bounded context reads for very large Windows log files."
            },
            instructions = "search_log and read_log cache each file in this MCP process. Prefer max_results <= 100 and narrow context; call close_log only when the file is no longer needed."
        };
    }

    private async Task<object> CallToolAsync(JsonElement request)
    {
        if (!request.TryGetProperty("params", out var parameters) ||
            !parameters.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
        {
            throw new RpcException(-32602, "tools/call requires params.name.");
        }

        var name = nameElement.GetString()!;
        var arguments = parameters.TryGetProperty("arguments", out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : default;

        try
        {
            object data = name switch
            {
                "search_log" => await SearchLogAsync(arguments),
                "read_log" => ReadLog(arguments),
                "list_open_logs" => ListOpenLogs(),
                "close_log" => CloseLog(arguments),
                _ => throw new RpcException(-32602, $"Unknown tool: {name}")
            };
            var text = JsonSerializer.Serialize(data, _jsonOptions);
            return new { content = new[] { new { type = "text", text } }, isError = false };
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new
            {
                content = new[] { new { type = "text", text = ex.Message } },
                isError = true
            };
        }
    }

    private async Task<object> SearchLogAsync(JsonElement arguments)
    {
        var path = RequiredString(arguments, "path", 32767);
        var encoding = GetEncoding(arguments);
        var (document, cacheHit, refreshed) = GetDocument(path, encoding, GetBool(arguments, "refresh"));
        var query = new LogQuery
        {
            Pattern = OptionalString(arguments, "query", MaxTermLength),
            IncludeAny = GetStrings(arguments, "include_any"),
            IncludeAll = GetStrings(arguments, "include_all"),
            Exclude = GetStrings(arguments, "exclude"),
            UseRegex = GetBool(arguments, "regex"),
            CaseSensitive = GetBool(arguments, "case_sensitive"),
            StartLine = GetLong(arguments, "start_line", 1, 1, long.MaxValue),
            EndLine = GetNullableLong(arguments, "end_line", 1, long.MaxValue)
        };
        if (query.UseRegex && query.Pattern is null)
        {
            throw new ArgumentException("regex=true requires query.");
        }

        var before = GetInt(arguments, "before", 0, 0, 200);
        var after = GetInt(arguments, "after", 0, 0, 200);
        var maxResults = GetInt(arguments, "max_results", 100, 1, 1000);
        var matchLines = new List<long>(maxResults);
        var stopwatch = Stopwatch.StartNew();
        var matchCount = await LogQueryEngine.SearchAsync(document, query, result =>
        {
            if (matchLines.Count < maxResults)
            {
                matchLines.Add(result.LineNumber);
            }
        }, CancellationToken.None);
        stopwatch.Stop();
        var lines = LogQueryEngine.ReadContext(document, matchLines, before, after);

        return new
        {
            path = document.FilePath,
            encoding = EncodingName(document.EncodingKind),
            document.FileSize,
            document.LineCount,
            cacheHit,
            refreshed,
            elapsedMs = stopwatch.ElapsedMilliseconds,
            matchCount,
            returnedMatchCount = matchLines.Count,
            truncated = matchCount > matchLines.Count,
            lines
        };
    }

    private object ReadLog(JsonElement arguments)
    {
        var path = RequiredString(arguments, "path", 32767);
        var startLine = RequiredLong(arguments, "start_line", 1, long.MaxValue);
        var lineCount = GetInt(arguments, "line_count", 100, 1, 2000);
        var (document, cacheHit, refreshed) = GetDocument(path, GetEncoding(arguments), GetBool(arguments, "refresh"));
        var page = document.ReadLinesFromLine(startLine, lineCount);
        return new
        {
            path = document.FilePath,
            encoding = EncodingName(document.EncodingKind),
            document.FileSize,
            document.LineCount,
            cacheHit,
            refreshed,
            requestedStartLine = startLine,
            lines = page.Lines
        };
    }

    private object ListOpenLogs()
    {
        return new
        {
            count = _documents.Count,
            memoryUsage = _documents.Values.Sum(document => document.MemoryUsage),
            logs = _documents.Values.Select(document => new
            {
                path = document.FilePath,
                encoding = EncodingName(document.EncodingKind),
                document.FileSize,
                document.LineCount,
                document.MemoryUsage
            }).ToArray()
        };
    }

    private object CloseLog(JsonElement arguments)
    {
        var path = OptionalString(arguments, "path", 32767);
        if (path is null)
        {
            var count = _documents.Count;
            foreach (var document in _documents.Values)
            {
                document.Dispose();
            }

            _documents.Clear();
            return new { closed = count };
        }

        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (!_documents.Remove(fullPath, out var removed))
        {
            return new { closed = 0 };
        }

        removed.Dispose();
        return new { closed = 1 };
    }

    private (LogFileDocument Document, bool CacheHit, bool Refreshed) GetDocument(
        string path,
        LogTextEncoding encoding,
        bool refresh)
    {
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (_documents.TryGetValue(fullPath, out var document) && document.EncodingKind == encoding)
        {
            if (!refresh)
            {
                return (document, true, false);
            }

            var append = document.AppendNewContent();
            if (!append.IsTruncated)
            {
                return (document, true, append.HasNewContent);
            }

            document.Dispose();
            _documents.Remove(fullPath);
        }
        else if (document is not null)
        {
            document.Dispose();
            _documents.Remove(fullPath);
        }

        document = LogFileDocument.Open(fullPath, encoding);
        _documents.Add(fullPath, document);
        return (document, false, refresh);
    }

    private static string RequiredString(JsonElement arguments, string name, int maxLength)
    {
        return OptionalString(arguments, name, maxLength) ?? throw new ArgumentException($"{name} is required.");
    }

    private static string? OptionalString(JsonElement arguments, string name, int maxLength)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new ArgumentException($"{name} must be a non-empty string.");
        }

        var text = value.GetString()!;
        if (text.Length > maxLength)
        {
            throw new ArgumentException($"{name} is too long (maximum {maxLength} characters).");
        }

        return text;
    }

    private static string[] GetStrings(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value))
        {
            return Array.Empty<string>();
        }

        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaxTerms)
        {
            throw new ArgumentException($"{name} must be an array with at most {MaxTerms} strings.");
        }

        return value.EnumerateArray().Select(item =>
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()) || item.GetString()!.Length > MaxTermLength)
            {
                throw new ArgumentException($"Each {name} item must be a non-empty string up to {MaxTermLength} characters.");
            }

            return item.GetString()!;
        }).ToArray();
    }

    private static bool GetBool(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ArgumentException($"{name} must be a boolean.")
        };
    }

    private static int GetInt(JsonElement arguments, string name, int defaultValue, int minimum, int maximum)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value))
        {
            return defaultValue;
        }

        if (!value.TryGetInt32(out var result) || result < minimum || result > maximum)
        {
            throw new ArgumentException($"{name} must be between {minimum} and {maximum}.");
        }

        return result;
    }

    private static long GetLong(JsonElement arguments, string name, long defaultValue, long minimum, long maximum)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out var value))
        {
            return defaultValue;
        }

        if (!value.TryGetInt64(out var result) || result < minimum || result > maximum)
        {
            throw new ArgumentException($"{name} must be between {minimum} and {maximum}.");
        }

        return result;
    }

    private static long RequiredLong(JsonElement arguments, string name, long minimum, long maximum)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(name, out _))
        {
            throw new ArgumentException($"{name} is required.");
        }

        return GetLong(arguments, name, minimum, minimum, maximum);
    }

    private static long? GetNullableLong(JsonElement arguments, string name, long minimum, long maximum)
    {
        return arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out _)
            ? GetLong(arguments, name, minimum, minimum, maximum)
            : null;
    }

    private static LogTextEncoding GetEncoding(JsonElement arguments)
    {
        var value = OptionalString(arguments, "encoding", 8)?.ToLowerInvariant();
        return value switch
        {
            null or "utf8" or "utf-8" => LogTextEncoding.Utf8,
            "gbk" or "gb2312" or "gb18030" => LogTextEncoding.Gbk,
            _ => throw new ArgumentException("encoding must be utf8 or gbk.")
        };
    }

    private static string EncodingName(LogTextEncoding encoding) => encoding == LogTextEncoding.Gbk ? "gbk" : "utf8";

    private async Task WriteAsync(object message)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(message, _jsonOptions));
        await Console.Out.FlushAsync();
    }

    private Task WriteErrorAsync(JsonElement? id, int code, string message)
    {
        return WriteAsync(new { jsonrpc = "2.0", id, error = new { code, message } });
    }

    public void Dispose()
    {
        foreach (var document in _documents.Values)
        {
            document.Dispose();
        }

        _documents.Clear();
    }

    internal static async Task<int> SelfTestAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"logram-mcp-{Guid.NewGuid():N}.log");
        try
        {
            await File.WriteAllLinesAsync(path,
            [
                "starting request id=42",
                "warning request id=42 retry=1",
                "error request id=42 code=500",
                "错误 request id=42 code=501",
                "error request id=7 ignored"
            ], new UTF8Encoding(false));

            {
                using var document = LogFileDocument.Open(path, LogTextEncoding.Utf8);
                var matches = new List<long>();
                var count = await LogQueryEngine.SearchAsync(document, new LogQuery
                {
                    IncludeAny = ["error", "warning"],
                    IncludeAll = ["id=42"],
                    Exclude = ["retry"]
                }, result => matches.Add(result.LineNumber), CancellationToken.None);
                var context = LogQueryEngine.ReadContext(document, matches, 1, 0);
                if (count != 1 || matches is not [3] || context.Count != 2 || !context[^1].IsMatch)
                {
                    throw new InvalidOperationException("Query/context self-test failed.");
                }

                var unicodeMatches = new List<long>();
                var unicodeCount = await LogQueryEngine.SearchAsync(document, new LogQuery
                {
                    IncludeAny = ["错误", "warning"],
                    IncludeAll = ["id=42"],
                    Exclude = ["retry"]
                }, result => unicodeMatches.Add(result.LineNumber), CancellationToken.None);
                if (unicodeCount != 1 || unicodeMatches is not [4])
                {
                    throw new InvalidOperationException("Unicode multi-term self-test failed.");
                }
            }

            using var server = new McpServer();
            var first = server.GetDocument(path, LogTextEncoding.Utf8, refresh: false);
            await File.AppendAllTextAsync(path, "appended line\n", new UTF8Encoding(false));
            var second = server.GetDocument(path, LogTextEncoding.Utf8, refresh: true);
            if (first.CacheHit || !second.CacheHit || !second.Refreshed || !ReferenceEquals(first.Document, second.Document) || second.Document.LineCount != 6)
            {
                throw new InvalidOperationException("Persistent cache/refresh self-test failed.");
            }

            Console.WriteLine("LogRAM MCP self-test passed.");
            return 0;
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class RpcException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
