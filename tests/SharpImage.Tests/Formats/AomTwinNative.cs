using System.Reflection;
using System.Runtime.InteropServices;

namespace SharpImage.Tests.Formats;

// The one DllImport resolver for the libaom twin libraries (aomtwin_*). NativeLibrary.SetDllImportResolver accepts a
// single resolver per assembly, so twin classes that each installed their own threw "A resolver is already set for the
// assembly" as soon as two SHARPIMAGE_AOMTWIN_* libraries were configured in the same run (as CI configures INV and SP).
internal static class AomTwinNative
{
    private static readonly Dictionary<string, IntPtr> Libraries = new();
    private static readonly object Gate = new();
    private static bool installed;

    /// <summary>Loads the library at <paramref name="path"/> and resolves <c>[DllImport("<paramref name="name"/>")]</c>
    /// in this test assembly to it.</summary>
    public static void Register(string name, string path)
    {
        lock (Gate)
        {
            if (!installed)
            {
                NativeLibrary.SetDllImportResolver(typeof(AomTwinNative).Assembly, Resolve);
                installed = true;
            }
            if (!Libraries.ContainsKey(name))
            {
                Libraries[name] = NativeLibrary.Load(path);
            }
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
