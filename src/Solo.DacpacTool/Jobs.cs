using System.Text.RegularExpressions;
using static Solo.DacpacTool.Actions;

namespace Solo.DacpacTool;

public static class Jobs
{
    public static readonly string TempPath = Directory.CreateTempSubdirectory($"{Guid.NewGuid()}").FullName;

    public static void ExportDatabase()
    {
        var sourceConnectionString = GetEnvVar("SOURCE_CONNECTION_STRING");
        var sqlprojDir = GetEnvVar("SQLPROJ_DIR_PATH");
        _ = GetSqlprojPath(sqlprojDir);

        var dacpacPath = Path.Combine(TempPath, "dump.dacpac");
        var outputDirectory = Path.Combine(TempPath, "output");

        Directory.CreateDirectory(sqlprojDir);

        GenerateDacpac(sourceConnectionString, dacpacPath);
        UnpackDacpac(dacpacPath, outputDirectory);
        OutputSql(outputDirectory, sqlprojDir);

        try
        {
            BuildSqlproj(sqlprojDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.WriteLine("Failed to build .sqlproj project");
        }
    }

    private static string GenerateMigrationScript()
    {
        var sqlprojDir = GetEnvVar("SQLPROJ_DIR_PATH");
        var sqlprojPath = GetSqlprojPath(sqlprojDir);
        var destinationConnectionString = GetEnvVar("DESTINATION_CONNECTION_STRING");

        Run("dotnet", ["build", sqlprojPath], sqlprojDir);

        var dacpacPath = FindDacpac(sqlprojDir);
        return Actions.GenerateMigration(destinationConnectionString, dacpacPath);
    }

    public static void GenerateMigration()
    {
        Console.WriteLine(GenerateMigrationScript());
    }

    public static void GenerateEfMigration()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        
        if (args.Length < 2)
        {
            Console.WriteLine("Missing migration name");
            return;
        }

        if (args.Length > 2)
        {
            Console.WriteLine("Too many arguments");
            return;
        }

        var migrationName = args[1];

        var targetProjectPath = GetEnvVar("EF_TARGET_PROJECT_PATH");
        var startupProjectPath = GetEnvVar("EF_STARTUP_PROJECT_PATH");
        var contextName = GetEnvVar("EF_CONTEXT_NAME");
        var migrationsDirPath = GetEnvVar("EF_MIGRATIONS_DIR_PATH");

        var script = GenerateMigrationScript();
        Run("dotnet", [
            "ef", "migrations", "add", migrationName,
            "--startup-project", startupProjectPath,
            "--project", targetProjectPath,
            "--context", contextName,
            "--output-dir", migrationsDirPath
        ]);

        var directoryPath = migrationsDirPath.StartsWith('/')
            ? migrationsDirPath
            : Path.Combine(Path.GetDirectoryName(targetProjectPath)!, migrationsDirPath);

        var file = Directory.GetFiles(directoryPath, $"*_{migrationName}.cs", SearchOption.TopDirectoryOnly)
            .OrderByDescending(x => x)
            .First();

        var content = File.ReadAllText(file);
        var lineToInsert = $@"    migrationBuilder.Sql($""""""
{script}
"""""");

        ";

        var pattern = @"(protected\s+override\s+void\s+Up\s*\(MigrationBuilder\s+\w+\)\s*\{\s*)";
        var updatedContent = Regex.Replace(content, pattern, $"$0{lineToInsert}");
        File.WriteAllText(file, updatedContent);
    }
}