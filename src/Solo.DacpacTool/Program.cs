using Solo.DacpacTool;
using System.Reflection;
using static Solo.DacpacTool.Actions;

if (args.Length == 0)
{
    var versionString = Assembly.GetEntryAssembly()?
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion;

    Console.WriteLine($"Dacpac Tool v{versionString}");
    Console.WriteLine("-------------");
    Console.WriteLine("\nUsage:");
    Console.WriteLine("  dpt <message>");
    return;
}

EnvReader.Load(".env");
var opId = Guid.NewGuid().ToString();

var tempPath = Directory.CreateTempSubdirectory($"dacpac_temp_{opId}").FullName;

try
{
    var sourceConnectionString = GetEnvVar("SOURCE_CONNECTION_STRING");
    var destinationConnectionString = GetEnvVar("DESTINATION_CONNECTION_STRING");
    var outputBasePath = GetEnvVar("SQLPROJ_DIR_PATH");

    var dacpacPath = Path.Combine(tempPath, "dump.dacpac");
    var outputDirectory = Path.Combine(tempPath, "output");

    Directory.CreateDirectory(outputBasePath);

    GenerateDacpac(sourceConnectionString, dacpacPath);
    UnpackDacpac(dacpacPath, outputDirectory);
    OutputSql(outputDirectory, outputBasePath);
    GenerateMigration(destinationConnectionString, dacpacPath, $"{outputBasePath}/Migrations");
}
finally
{
    Directory.Delete(tempPath, true);
}