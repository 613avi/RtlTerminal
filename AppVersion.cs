using System.Reflection;

namespace RtlTerminal;

public static class AppVersion
{
    // The SDK generates assembly metadata from <Version> in RtlTerminal.csproj.
    public static string Display { get; } = typeof(AppVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
        .InformationalVersion.Split('+')[0];
}
