namespace Solo.DacpacTool;

public static class StringExtensions
{
    public static string ToRooted(this string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(path);
    }
}