using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace CodeRag.Core.Indexing;

internal static class SqliteVecLoader
{
    public static void LoadInto(SqliteConnection connection)
    {
        connection.EnableExtensions(true);
        var libraryPath = ResolveLibraryPath();
        connection.LoadExtension(libraryPath);
    }

    private static string ResolveLibraryPath()
    {
        var appBase = AppContext.BaseDirectory;
        var rid = ResolveRuntimeIdentifier();
        var fileName = ResolveFileName();
        var path = Path.Combine(appBase, "runtimes", rid, "native", fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"sqlite-vec native binary not found at '{path}'. Ensure external/sqlite-vec/runtimes/{rid}/native/{fileName} is checked in and copied to output.");
        }
        return path;
    }

    private static string ResolveRuntimeIdentifier()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "win-x64";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "linux-x64";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "osx-arm64";
        }
        throw new PlatformNotSupportedException(
            $"sqlite-vec binaries are not provisioned for this platform ({RuntimeInformation.OSDescription}).");
    }

    private static string ResolveFileName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "vec0.dll";
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "vec0.so";
        }
        return "vec0.dylib";
    }
}
