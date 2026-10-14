using System.Text;

namespace RtlTerminal;

public static class WslPath
{
    // C:\Users\x -> /mnt/c/Users/x; \\wsl.localhost\Distro\home\x -> /home/x
    public static string FromWindows(string windowsPath)
    {
        foreach (var prefix in new[] { @"\\wsl.localhost\", @"\\wsl$\" })
        {
            if (!windowsPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var rest = windowsPath[prefix.Length..];
            var slash = rest.IndexOf('\\');
            return slash < 0 ? "/" : rest[slash..].Replace('\\', '/');
        }

        if (windowsPath.Length >= 2 && windowsPath[1] == ':' && char.IsAsciiLetter(windowsPath[0]))
            return $"/mnt/{char.ToLowerInvariant(windowsPath[0])}{windowsPath[2..].Replace('\\', '/')}";

        return windowsPath.Replace('\\', '/');
    }

    // Backslash-escapes shell metacharacters, as terminals do for dropped files.
    public static string Quote(string path)
    {
        var builder = new StringBuilder(path.Length + 8);

        foreach (var c in path)
        {
            if (" '\"\\()&;|<>$`!*?[]{}#~".Contains(c))
                builder.Append('\\');
            builder.Append(c);
        }

        return builder.ToString();
    }
}
