using Solo.DacpacTool;
using System.Reflection;

if (args.Length == 0)
{
    var versionString = Assembly.GetEntryAssembly()?
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion;

    Console.WriteLine($"Dacpac Tool v{versionString}");
    Console.WriteLine("-------------");
    Console.WriteLine("Usage:");
    Console.WriteLine("  dpt <job>");
    Console.WriteLine("\nJobs:");
    Console.WriteLine("  export-database - Exports the database schema to a .sqlproj project");
    Console.WriteLine("  generate-migration - Builds .sqlproj project and generates migration scripts");
    Console.WriteLine("  generate-ef-migration <name> - Builds .sqlproj project and generates ef migration");
    Console.WriteLine("\nEnvironment Variables:");
    Console.WriteLine("  SOURCE_CONNECTION_STRING - Connection string to the source database");
    Console.WriteLine("  DESTINATION_CONNECTION_STRING - Connection string to the destination database");
    Console.WriteLine("  SQLPROJ_DIR_PATH - Path to the .sqlproj project directory");
    Console.WriteLine("\nCan load .env file from working directory to set environment variables");
    return;
}

var job = args[0];

EnvReader.Load(".env");
Console.WriteLine($"Temp path: {Jobs.TempPath}");

try
{
    switch (job)
    {
        case "export-database":
            Jobs.ExportDatabase();
            break;
        case "generate-migration":
            Jobs.GenerateMigration();
            break;
        case "generate-ef-migration":
            Jobs.GenerateEfMigration();
            break;
        default:
            Console.WriteLine($"Unknown job: {job}");
            break;
    }
}
finally
{
    Directory.Delete(Jobs.TempPath, true);
}