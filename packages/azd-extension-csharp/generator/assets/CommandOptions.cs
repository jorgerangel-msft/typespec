// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;

namespace TypeSpec.Azd.Generated;

public sealed class CommandOptions
{
    private readonly Option<string> _output = new("--output", "-o")
    {
        Description = "Output format: default, json, or none.",
        Recursive = true,
        DefaultValueFactory = _ => "default",
    };
    private readonly Option<string> _cwd = new("--cwd", "-C")
    {
        Description = "Sets the current working directory.",
        Recursive = true,
    };

    public void ConfigureRoot(RootCommand root)
    {
        _output.AcceptOnlyFromAmong("default", "json", "none");
        root.Options.Add(_output);
        root.Options.Add(_cwd);
        root.Options.Add(new Option<string>("--environment", "-e")
        {
            Description = "The azd environment name.",
            Recursive = true,
        });
        root.Options.Add(new Option<bool>("--debug") { Recursive = true });
        root.Options.Add(new Option<bool>("--no-prompt") { Recursive = true });
        root.Options.Add(new Option<bool>("--docs") { Recursive = true });
        root.Options.Add(new Option<string>("--trace-log-file") { Recursive = true, Hidden = true });
        root.Options.Add(new Option<string>("--trace-log-url") { Recursive = true, Hidden = true });
    }

    public async Task<int> RunAsync(string[] args, RootCommand root)
    {
        var result = root.Parse(args);
        if (result.Errors.Count != 0)
        {
            return await result.InvokeAsync();
        }
        var cwd = result.GetValue(_cwd) ?? Environment.GetEnvironmentVariable("AZD_CWD");
        if (!string.IsNullOrEmpty(cwd))
        {
            try
            {
                Directory.SetCurrentDirectory(cwd);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Console.Error.WriteLine($"Cannot use working directory '{cwd}': {error.Message}");
                return 1;
            }
        }
        return await result.InvokeAsync();
    }

    public int WriteResult(ParseResult result, string value)
    {
        switch (result.GetValue(_output))
        {
            case "json":
                Console.WriteLine(JsonSerializer.Serialize(value));
                break;
            case "none":
                break;
            default:
                Console.WriteLine(value);
                break;
        }
        return 0;
    }

    public static int WriteMetadata(string metadata)
    {
        Console.WriteLine(metadata);
        return 0;
    }
}
