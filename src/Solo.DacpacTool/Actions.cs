using Microsoft.SqlServer.Dac;
using System.Data.Common;
using System.Text.RegularExpressions;

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

        // Dictionaries to keep track of table files and pending triggers/indexes
        var tableFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pendingTriggersIndexes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

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

                // If any triggers or indexes were pending for this table, append them
                if (pendingTriggersIndexes.ContainsKey(key))
                {
                    foreach (var extra in pendingTriggersIndexes[key])
                    {
                        File.AppendAllText(filePath, Environment.NewLine + Environment.NewLine + extra);
                    }

                    pendingTriggersIndexes.Remove(key);
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

            // Process CREATE TRIGGER and save with corresponding table SQL
            var triggerMatch = triggerRegex.Match(trimmedBatch);
            if (triggerMatch.Success)
            {
                var targetSchema = triggerMatch.Groups["targetSchema"].Success
                    ? triggerMatch.Groups["targetSchema"].Value
                    : "dbo";
                var targetTable = triggerMatch.Groups["table"].Value;
                var key = targetSchema + "." + targetTable;
                var triggerSql = trimmedBatch;

                if (tableFiles.ContainsKey(key))
                {
                    // Append trigger SQL to the corresponding table file
                    File.AppendAllText(tableFiles[key],
                        Environment.NewLine + Environment.NewLine + "-- Trigger: " +
                        triggerMatch.Groups["object"].Value + Environment.NewLine + triggerSql);
                }
                else
                {
                    // If table file not found yet, store in pending list
                    if (!pendingTriggersIndexes.ContainsKey(key))
                    {
                        pendingTriggersIndexes[key] = new List<string>();
                    }

                    pendingTriggersIndexes[key].Add("-- Trigger: " + triggerMatch.Groups["object"].Value +
                                                    Environment.NewLine + triggerSql);
                }

                continue;
            }

            // Process CREATE INDEX and save with corresponding table SQL
            var indexMatch = indexRegex.Match(trimmedBatch);
            if (indexMatch.Success)
            {
                var schema = indexMatch.Groups["schema"].Success ? indexMatch.Groups["schema"].Value : "dbo";
                var targetTable = indexMatch.Groups["table"].Value;
                var key = schema + "." + targetTable;
                var indexSql = trimmedBatch;

                if (tableFiles.ContainsKey(key))
                {
                    // Append index SQL to the corresponding table file
                    File.AppendAllText(tableFiles[key],
                        Environment.NewLine + Environment.NewLine + "-- Index: " +
                        indexMatch.Groups["indexName"].Value + Environment.NewLine + indexSql);
                }
                else
                {
                    if (!pendingTriggersIndexes.ContainsKey(key))
                    {
                        pendingTriggersIndexes[key] = new List<string>();
                    }

                    pendingTriggersIndexes[key].Add("-- Index: " + indexMatch.Groups["indexName"].Value +
                                                    Environment.NewLine + indexSql);
                }

                continue;
            }

            // For any unrecognized batch, save to a Misc folder
            var miscFolder = Path.Combine(outputBasePath, "Misc");
            Directory.CreateDirectory(miscFolder);
            var miscPath = Path.Combine(miscFolder, $"Batch_{counter}.sql");
            File.WriteAllText(miscPath, trimmedBatch);
        }

        // For any pending triggers/indexes where no table file was found, write them to Misc
        if (pendingTriggersIndexes.Any())
        {
            var miscFolder = Path.Combine(outputBasePath, "Misc");
            Directory.CreateDirectory(miscFolder);
            foreach (var kvp in pendingTriggersIndexes)
            {
                var fileName = $"MissingTable_{kvp.Key.Replace('.', '_')}.sql";
                var filePath = Path.Combine(miscFolder, fileName);
                var content = string.Join(Environment.NewLine + Environment.NewLine, kvp.Value);
                File.WriteAllText(filePath, content);
            }
        }
    }

    public static void GenerateMigration(string connectionString, string dacpacPath, string deploymentScriptPath)
    {
        var databaseName = ExtractDatabaseName(connectionString);
        var dacServices = new DacServices(connectionString);
        using var dacpac = DacPackage.Load(dacpacPath);
        // Optionally, define deployment options (customize as needed)
        var deployOptions = new PublishOptions();
        // For example, you might set:
        // deployOptions.BlockOnPossibleDataLoss = false;

        // Generate a full deployment script that can be used to create the database.
        var script = dacServices.Script(dacpac, databaseName, deployOptions);

        // // Save the deployment script to a file.
        // var cleanedScript = Regex.Replace(
        //     script.DatabaseScript,
        //     @"^USE\s+\[\$\(.+\)\];\s*(\r?\n)?",
        //     string.Empty,
        //     RegexOptions.Multiline);

        var cleanedScript = Regex.Replace(script.DatabaseScript,
            @"^(?:USE\s+\[\$\(.+\)\];\s*(?:\r?\n)?|:setvar\s+(?:DatabaseName|DefaultFilePrefix|DefaultDataPath|DefaultLogPath).*)",
            string.Empty, RegexOptions.Multiline);

        File.WriteAllText(deploymentScriptPath, cleanedScript);
        Console.WriteLine($"Deployment script generated successfully at: {deploymentScriptPath}");
    }
}