using Microsoft.SqlServer.Dac;
using System.Data.Common;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Solo.DacpacTool;

public static class Actions
{
    public static string GetEnvVar(string key)
    {
        return Environment.GetEnvironmentVariable(key)
               ?? throw new Exception($"The environment variable '{key}' is not set.");
    }

    public static string? ExtractDatabaseName(string connectionString)
    {
        var builder = new DbConnectionStringBuilder
        {
            ConnectionString = connectionString
        };

        if (builder.TryGetValue("Initial Catalog", out var value) ||
            builder.TryGetValue("Database", out value))
        {
            return value.ToString();
        }

        return null;
    }

    public static string GetSqlprojPath(string sqlprojDir)
    {
        // Retrieve all files with the .sqlproj extension in the directory and subdirectories.
        var sqlprojFiles = Directory.GetFiles(sqlprojDir, "*.csproj", SearchOption.AllDirectories);

        if (sqlprojFiles.Length == 0)
        {
            throw new Exception("No .csproj files found in the specified directory.");
        }

        if (sqlprojFiles.Length > 1)
        {
            throw new Exception(
                $"Multiple .csproj files found ({string.Join(',', sqlprojFiles)}) in the specified directory.");
        }

        var projPath = sqlprojFiles[0];
        var xdoc = XDocument.Load(projPath);
        var sdkAttribute = xdoc.Root?.Attribute("Sdk")?.Value;

        if (sdkAttribute?.StartsWith("MSBuild.Sdk.SqlProj") != true)
        {
            throw new Exception("No .csproj files found with MSBuild.Sdk.SqlProj SDK in the specified directory.");
        }

        return projPath;
    }

    public static void GenerateDacpac(string connectionString, string dacpacPath)
    {
        var databaseName = ExtractDatabaseName(connectionString);
        var dacServices = new DacServices(connectionString);
        dacServices.Extract(dacpacPath, databaseName, "something", new Version(1, 0));
    }

    public static void UnpackDacpac(string dacpacPath, string outputDirectory)
    {
        using var dacpac = DacPackage.Load(dacpacPath);
        dacpac.Unpack(outputDirectory);
    }

    public static void OutputSql(string outputDirectory, string outputBasePath)
    {
        var allSql = File.ReadAllText($"{outputDirectory}/model.sql");

        var batches = Regex.Split(allSql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .ToList();

        // Regex definitions for different objects
        var tableRegex = new Regex(@"CREATE\s+TABLE\s+(?:\[?(?<schema>[^\]\.]+)\]?\.?)?\[?(?<object>[^\]\s]+)\]?",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        var procRegex = new Regex(@"CREATE\s+PROCEDURE\s+(?:\[?(?<schema>[^\]\.]+)\]?\.?)?\[?(?<object>[^\]\s]+)\]?",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        var viewRegex = new Regex(@"CREATE\s+VIEW\s+(?:\[?(?<schema>[^\]\.]+)\]?\.?)?\[?(?<object>[^\]\s]+)\]?",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        var functionRegex = new Regex(@"CREATE\s+FUNCTION\s+(?:\[?(?<schema>[^\]\.]+)\]?\.?)?\[?(?<object>[^\]\s]+)\]?",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        // Trigger regex extracts target table from "ON" clause
        var triggerRegex = new Regex(
            @"CREATE\s+TRIGGER\s+(?:\[?(?<schema>[^\]\.]+)\]?\.?)?\[?(?<object>[^\]\s]+)\]?\s+ON\s+(?:\[?(?<targetSchema>[^\]\.]+)\]?\.?)?\[?(?<table>[^\]\s]+)\]?",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        // Index regex extracts target table from "ON" clause
        var indexRegex = new Regex(
            @"CREATE\s+(?:UNIQUE\s+)?(?:CLUSTERED\s+|NONCLUSTERED\s+)?INDEX\s+(?:\[?(?<indexName>[^\]\s]+)\]?)\s+ON\s+(?:\[?(?<schema>[^\]\.]+)\]?\.?)?\[?(?<table>[^\]\s]+)\]?",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        // ALTER TABLE regex for constraints
        var alterTableRegex = new Regex(@"ALTER\s+TABLE\s+(?:\[?(?<schema>[^\]\.]+)\]?\.?)?\[?(?<object>[^\]\s]+)\]?",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        // Dictionaries to keep track of table files and pending extras (triggers, indexes, alters)
        var tableFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pendingExtras = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        var counter = 0;

        foreach (var batch in batches)
        {
            counter++;
            var trimmedBatch = batch.Trim();

            // Process CREATE TABLE
            var tableMatch = tableRegex.Match(trimmedBatch);
            if (tableMatch.Success)
            {
                var schema = tableMatch.Groups["schema"].Success ? tableMatch.Groups["schema"].Value : "dbo";
                var tableName = tableMatch.Groups["object"].Value;
                var key = schema + "." + tableName;

                var schemaFolder = Path.Combine(outputBasePath, schema);
                Directory.CreateDirectory(schemaFolder);
                var tablesFolder = Path.Combine(schemaFolder, "Tables");
                Directory.CreateDirectory(tablesFolder);
                var filePath = Path.Combine(tablesFolder, tableName + ".sql");

                // Write the table creation SQL
                File.WriteAllText(filePath, trimmedBatch + Environment.NewLine);
                tableFiles[key] = filePath;

                // Append any pending extras (triggers, indexes, ALTER TABLE constraints) for this table
                if (pendingExtras.ContainsKey(key))
                {
                    foreach (var extra in pendingExtras[key])
                    {
                        File.AppendAllText(filePath, Environment.NewLine + Environment.NewLine + extra);
                    }

                    pendingExtras.Remove(key);
                }

                continue;
            }

            // Process CREATE PROCEDURE
            var procMatch = procRegex.Match(trimmedBatch);
            if (procMatch.Success)
            {
                var schema = procMatch.Groups["schema"].Success ? procMatch.Groups["schema"].Value : "dbo";
                var procName = procMatch.Groups["object"].Value;
                var schemaFolder = Path.Combine(outputBasePath, schema);
                Directory.CreateDirectory(schemaFolder);
                var procFolder = Path.Combine(schemaFolder, "Stored Procedures");
                Directory.CreateDirectory(procFolder);
                var filePath = Path.Combine(procFolder, procName + ".sql");
                File.WriteAllText(filePath, trimmedBatch);
                continue;
            }

            // Process CREATE VIEW
            var viewMatch = viewRegex.Match(trimmedBatch);
            if (viewMatch.Success)
            {
                var schema = viewMatch.Groups["schema"].Success ? viewMatch.Groups["schema"].Value : "dbo";
                var viewName = viewMatch.Groups["object"].Value;
                var schemaFolder = Path.Combine(outputBasePath, schema);
                Directory.CreateDirectory(schemaFolder);
                var viewFolder = Path.Combine(schemaFolder, "Views");
                Directory.CreateDirectory(viewFolder);
                var filePath = Path.Combine(viewFolder, viewName + ".sql");
                File.WriteAllText(filePath, trimmedBatch);
                continue;
            }

            // Process CREATE FUNCTION
            var functionMatch = functionRegex.Match(trimmedBatch);
            if (functionMatch.Success)
            {
                var schema = functionMatch.Groups["schema"].Success ? functionMatch.Groups["schema"].Value : "dbo";
                var functionName = functionMatch.Groups["object"].Value;
                var schemaFolder = Path.Combine(outputBasePath, schema);
                Directory.CreateDirectory(schemaFolder);
                var functionFolder = Path.Combine(schemaFolder, "Functions");
                Directory.CreateDirectory(functionFolder);
                var filePath = Path.Combine(functionFolder, functionName + ".sql");
                File.WriteAllText(filePath, trimmedBatch);
                continue;
            }

            // Process CREATE TRIGGER and append to corresponding table SQL
            var triggerMatch = triggerRegex.Match(trimmedBatch);
            if (triggerMatch.Success)
            {
                var targetSchema = triggerMatch.Groups["targetSchema"].Success
                    ? triggerMatch.Groups["targetSchema"].Value
                    : "dbo";
                var targetTable = triggerMatch.Groups["table"].Value;
                var key = targetSchema + "." + targetTable;
                var extraSql = "-- Trigger: " + triggerMatch.Groups["object"].Value + Environment.NewLine +
                               trimmedBatch;

                if (tableFiles.ContainsKey(key))
                {
                    File.AppendAllText(tableFiles[key], Environment.NewLine + "GO" + Environment.NewLine + extraSql);
                }
                else
                {
                    if (!pendingExtras.ContainsKey(key))
                    {
                        pendingExtras[key] = new List<string>();
                    }

                    pendingExtras[key].Add(extraSql);
                }

                continue;
            }

            // Process CREATE INDEX and append to corresponding table SQL
            var indexMatch = indexRegex.Match(trimmedBatch);
            if (indexMatch.Success)
            {
                var schema = indexMatch.Groups["schema"].Success ? indexMatch.Groups["schema"].Value : "dbo";
                var targetTable = indexMatch.Groups["table"].Value;
                var key = schema + "." + targetTable;
                var extraSql = "-- Index: " + indexMatch.Groups["indexName"].Value + Environment.NewLine + trimmedBatch;

                if (tableFiles.ContainsKey(key))
                {
                    File.AppendAllText(tableFiles[key], Environment.NewLine + "GO" + Environment.NewLine + extraSql);
                }
                else
                {
                    if (!pendingExtras.ContainsKey(key))
                    {
                        pendingExtras[key] = new List<string>();
                    }

                    pendingExtras[key].Add(extraSql);
                }

                continue;
            }

            // Process ALTER TABLE for adding constraints and append to corresponding table SQL
            var alterMatch = alterTableRegex.Match(trimmedBatch);
            if (alterMatch.Success && trimmedBatch.IndexOf("ADD CONSTRAINT", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var schema = alterMatch.Groups["schema"].Success ? alterMatch.Groups["schema"].Value : "dbo";
                var tableName = alterMatch.Groups["object"].Value;
                var key = schema + "." + tableName;
                var extraSql = "-- Alter Table Constraint" + Environment.NewLine + trimmedBatch;

                if (tableFiles.ContainsKey(key))
                {
                    File.AppendAllText(tableFiles[key], Environment.NewLine + "GO" + Environment.NewLine + extraSql);
                }
                else
                {
                    if (!pendingExtras.ContainsKey(key))
                    {
                        pendingExtras[key] = new List<string>();
                    }

                    pendingExtras[key].Add(extraSql);
                }

                continue;
            }

            // For any unrecognized batch, save to a Misc folder
            var miscFolder = Path.Combine(outputBasePath, "Misc");
            Directory.CreateDirectory(miscFolder);
            var miscPath = Path.Combine(miscFolder, $"Batch_{counter}.sql");
            File.WriteAllText(miscPath, trimmedBatch);
        }

        // Write any pending extras that could not be associated with a table into Misc folder
        if (pendingExtras.Any())
        {
            var miscFolder = Path.Combine(outputBasePath, "Misc");
            Directory.CreateDirectory(miscFolder);
            foreach (var kvp in pendingExtras)
            {
                var fileName = $"MissingTable_{kvp.Key.Replace('.', '_')}.sql";
                var filePath = Path.Combine(miscFolder, fileName);
                var content = string.Join(Environment.NewLine + Environment.NewLine, kvp.Value);
                File.WriteAllText(filePath, content);
            }
        }
    }

    public static string GenerateMigration(string connectionString, string dacpacPath)
    {
        var databaseName = ExtractDatabaseName(connectionString);
        var dacServices = new DacServices(connectionString);
        using var dacpac = DacPackage.Load(dacpacPath);
        // Optionally, define deployment options (customize as needed)
        var deployOptions = new PublishOptions
        {
            DeployOptions = new DacDeployOptions
            {
                ScriptDatabaseOptions = false,
                IgnorePermissions = true,
                IgnoreLoginSids = true,
                IgnoreRoleMembership = true,
                IgnoreUserSettingsObjects = true,
                IgnoreAnsiNulls = true,
                IgnoreAuthorizer = true,
                DoNotEvaluateSqlCmdVariables = true,
                IgnoreWithNocheckOnCheckConstraints = true,
                DropConstraintsNotInSource = true,
                ScriptNewConstraintValidation = false
            },
        };

        // Generate a full deployment script that can be used to create the database.
        var script = dacServices.Script(dacpac, databaseName, deployOptions);

        // (\r?\n)* matches zero or more newlines
        const string canStartWithGoRegx = @"^(GO(\r?\n)+)?";
        const string removeCommentsRegx = @$"^({canStartWithGoRegx}\/\*([\s\S]*?)\*\/(\r?\n)*)"; // remove comments and empty lines
        const string removeSetRegx = @$"^({canStartWithGoRegx}SET([\s\S]*?);(\r?\n)*)"; // remove SET*; statements and empty lines
        const string removeGoCmdVarRegx = @"^(GO(\r?\n)+(\:.*(\r?\n)+)+(\r?\n)*)"; // remove :cmd variables
        const string removeCmdCheckRegex = @"^(:setvar\s+__IsSqlCmdEnabled([\s\S]*?)GO([\s\S]*?)END(\r?\n)*)"; // remove cmd check statements
        const string removeUseOrPrintRegx = @"^(GO(\r?\n)+(USE|PRINT)(.*?);(\r?\n)*)"; // remove USE and PRINT statements

        var regex = $"{removeCommentsRegx}|{removeSetRegx}|{removeGoCmdVarRegx}|{removeCmdCheckRegex}|{removeUseOrPrintRegx}";

        var cleanedScript = Regex.Replace(
            script.DatabaseScript,
            regex,
            string.Empty,
            RegexOptions.IgnorePatternWhitespace | RegexOptions.Multiline);

        return cleanedScript;
    }

    public static void Run(string program, string[] args, string? workingDir = null)
    {
        // run process
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = program,
                Arguments = string.Join(' ', args),
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDir ?? Directory.GetCurrentDirectory(),
            }
        };

        process.Start();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new Exception($"Process exited with code {process.ExitCode}");
        }
    }

    public static void BuildSqlproj(string sqlprojDir)
    {
        var sqlprojPath = GetSqlprojPath(sqlprojDir);

        Run("dotnet", ["build", "/consoleLoggerParameters:ForceAnsi", sqlprojPath], sqlprojDir);
    }

    public static string FindDacpac(string sqlprojDir)
    {
        _ = GetSqlprojPath(sqlprojDir);

        var dacpacFiles = Directory.GetFiles(Path.Join(sqlprojDir, "bin"), "*.dacpac", SearchOption.AllDirectories);

        if (dacpacFiles.Length == 0)
        {
            throw new Exception("No .dacpac files found in the specified directory.");
        }

        if (dacpacFiles.Length > 1)
        {
            throw new Exception(
                $"Multiple .dacpac files found ({string.Join(',', dacpacFiles)}) in the specified directory.");
        }

        return dacpacFiles[0];
    }
}