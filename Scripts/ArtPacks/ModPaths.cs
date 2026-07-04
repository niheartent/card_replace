using System.Reflection;

namespace CardReplace.Scripts.ArtPacks;

public static class ModPaths
{
    public static string GetModRoot(Assembly assembly)
    {
        var assemblyLocation = assembly.Location;
        if (!string.IsNullOrWhiteSpace(assemblyLocation))
        {
            var directory = Path.GetDirectoryName(assemblyLocation);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                return directory;
            }
        }

        return AppContext.BaseDirectory;
    }
}
