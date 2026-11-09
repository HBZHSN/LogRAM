using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace LogRAM;

internal static class SshConfig
{
    internal static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");

    internal static IReadOnlyList<string> ReadHosts(string configPath)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadFile(Path.GetFullPath(configPath));
        return hosts.OrderBy(host => host, StringComparer.OrdinalIgnoreCase).ToArray();

        void ReadFile(string path)
        {
            if (!visited.Add(path)) return;
            foreach (var line in File.ReadLines(path))
            {
                var tokens = Regex.Matches(line, "\"[^\"]*\"|'[^']*'|#[^\r\n]*|[^\\s#=]+")
                    .Select(match => match.Value).TakeWhile(value => !value.StartsWith('#'))
                    .Select(value => value.Trim('"', '\'')).ToArray();
                if (tokens.Length < 2) continue;
                if (tokens[0].Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var host in tokens.Skip(1))
                        if (!host.StartsWith('!') && host.IndexOfAny(['*', '?']) < 0) hosts.Add(host);
                }
                else if (tokens[0].Equals("Include", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var include in tokens.Skip(1))
                    {
                        var expanded = include.StartsWith("~/", StringComparison.Ordinal) || include.StartsWith("~\\", StringComparison.Ordinal)
                            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), include[2..]) : include;
                        if (!Path.IsPathRooted(expanded)) expanded = Path.Combine(Path.GetDirectoryName(configPath)!, expanded);
                        var directory = Path.GetDirectoryName(expanded)!;
                        if (!Directory.Exists(directory)) continue;
                        foreach (var file in Directory.EnumerateFiles(directory, Path.GetFileName(expanded)).OrderBy(file => file, StringComparer.Ordinal))
                            ReadFile(Path.GetFullPath(file));
                    }
                }
            }
        }
    }
}
