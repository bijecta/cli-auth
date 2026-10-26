using System.CommandLine;

var rootCommand = new RootCommand(
    "Bijecta.CliAuth development sample. Browser and device login are not implemented yet.");

return await rootCommand.Parse(args).InvokeAsync();
