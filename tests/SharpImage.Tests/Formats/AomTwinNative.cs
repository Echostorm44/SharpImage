using System.Reflection;
using System.Runtime.InteropServices;

namespace SharpImage.Tests.Formats;

// Loading of the libaom twin libraries (aomtwin_*, built by tests/native/aomtwin/build.sh; CI builds them all).
// - One DllImport resolver for all of them: NativeLibrary.SetDllImportResolver accepts a single resolver per assembly,
//   so twin classes that each installed their own threw "A resolver is already set for the assembly" as soon as two
//   SHARPIMAGE_AOMTWIN_* libraries were configured in the same run.
// - A twin test whose library is not configured reports Skipped with the reason (SkipUnless); it never passes without
//   comparing anything. A variable that is set but names a missing file is an error.
internal static class AomTwinNative
{
    private static readonly Dictionary<string, IntPtr> Libraries = new();
    private static readonly object Gate = new();
    private static bool installed;

    /// <summary>The library path in <paramref name="envVar"/>: null when unset, FileNotFoundException when it names a
    /// missing file.</summary>
    public static string? PathFromEnv(string envVar)
    {
        string? path = Environment.GetEnvironmentVariable(envVar);
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{envVar} is set but the library does not exist", path);
        }
        return path;
    }

    /// <summary>Skips the calling test unless its twin library loaded. With SHARPIMAGE_AOMTWIN_REQUIRED=1 (CI) a missing
    /// library fails the test instead, so a broken twin build cannot turn the comparisons into skips.</summary>
    public static void SkipUnless(bool available, string envVar)
    {
        if (available)
        {
            return;
        }
        string how = "build the libaom twins with tests/native/aomtwin/build.sh <dir> and set the variables from <dir>/twins.env";
        if (Environment.GetEnvironmentVariable("SHARPIMAGE_AOMTWIN_REQUIRED") == "1")
        {
            throw new InvalidOperationException($"{envVar} is not set but SHARPIMAGE_AOMTWIN_REQUIRED=1: {how}");
        }
        Skip.Test($"{envVar} is not set: {how}");
    }

    /// <summary>Skips the calling test unless it runs on Windows (for twins that only Windows can reproduce).</summary>
    public static void SkipUnlessWindows(string why)
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip.Test(why);
        }
    }

    /// <summary>Loads the library at <paramref name="path"/> (once) and resolves <c>[DllImport("<paramref name="name"/>")]</c>
    /// in this test assembly to it. Returns its handle, for NativeLibrary.GetExport.</summary>
    public static IntPtr Register(string name, string path)
    {
        lock (Gate)
        {
            if (!installed)
            {
                NativeLibrary.SetDllImportResolver(typeof(AomTwinNative).Assembly, Resolve);
                installed = true;
            }
            if (!Libraries.TryGetValue(name, out IntPtr handle))
            {
                handle = NativeLibrary.Load(path);
                Libraries[name] = handle;
            }
            return handle;
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        lock (Gate)
        {
            return Libraries.TryGetValue(name, out IntPtr handle) ? handle : IntPtr.Zero;
        }
    }
}
