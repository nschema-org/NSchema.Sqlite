using System.Runtime.CompilerServices;

namespace NSchema.Sqlite.Tests;

internal static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        DerivePathInfo((sourceFile, _, type, method) => new PathInfo(
            directory: Path.Combine(Path.GetDirectoryName(sourceFile)!, "Snapshots"),
            typeName: type.Name,
            methodName: method.Name
        ));
    }
}
