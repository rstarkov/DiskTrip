using System.Text.RegularExpressions;
using RT.CommandLine;
using RT.PostBuild;
using RT.Util;
using RT.Util.Consoles;

namespace DiskTrip;

[DocumentationRhoML("""
    {h}DiskTrip vDEV{}
    Reads, writes and verifies large pseudo-random files in order to confirm error-less filesystem read/write operations.
    """)]
class CommandLine : ICommandLineValidatable
{
    [IsPositional, IsMandatory]
    [Documentation("The name of the test file to write and/or verify.")]
    public string FileName = null;

    [Option("-w", "--write")]
    [DocumentationRhoML($$"""
        {h}Writes a test file {field}{{nameof(WriteSize)}}{} MB long (millions of bytes), then reads and verifies it.{}
        When omitted, DiskTrip will verify the previously-created test file.
        The number may be suffixed with {h}K{}, {h}M{}, {h}G{} (default), {h}T{}, {h}Ki{}, {h}Mi{}, {h}Gi{}, {h}Ti{} or {h}b{} to override the units (case-insensitive).
        A special value of {h}fill{} (or {h}full{} or {h}free{}) specifies that all free space should be consumed.
        """)]
    public string WriteSize = null;
    public long WriteSizeBytes;
    public bool Write => WriteSize != null;
    public bool WriteFill => Write && WriteSizeBytes == 0;

    [Option("-d", "--delete")]
    [DocumentationRhoML("""
        {h}Deletes the test file.{}
        Normally the test file is not deleted regardless of the outcome of the test.
        """)]
    public bool Delete = false;

    [Option("-o", "--overwrite")]
    [DocumentationRhoML("""
        {h}Allows overwriting the test file if it already exists.{}
        Normally DiskTrip exits with an error code if the test file already exists.
        """)]
    public bool Overwrite = false;

    public ConsoleColoredString Validate()
    {
        WriteSizeBytes = WriteSize == null ? 0 : parseSize(WriteSize);
        if (!Write && Overwrite)
            return CommandLineParser.Colorize(RhoML.Parse($$"""Option {option}--overwrite{} has no effect unless {option}--write{} is also specified."""));
        FileName = Path.GetFullPath(FileName);
        return null;
    }

    private long parseSize(string size)
    {
        size = size.Trim().ToLowerInvariant();
        if (size == "fill" || size == "full" || size == "free")
            return 0;
        var parsed = Regex.Match(size, @"^(?<num>\d+([,\.]\d*)?)(?<suf>\w+)?$");
        if (!parsed.Success || !double.TryParse(parsed.Groups["num"].Value, out var num))
            throw new CommandLineValidationException(RhoML.Parse($$"""The format of option {option}--write{} {h}{{size}}{} is not recognized."""));
        var suf = parsed.Groups["suf"].Success ? parsed.Groups["suf"].Value : null;
        if (suf == "b")
            return (long) Math.Round(num);
        else if (suf == "k")
            return (long) Math.Round(num * 1_000);
        else if (suf == "m")
            return (long) Math.Round(num * 1_000_000);
        else if (suf == "g" || suf == null)
            return (long) Math.Round(num * 1_000_000_000);
        else if (suf == "t")
            return (long) Math.Round(num * 1_000_000_000_000);
        else if (suf == "ki")
            return (long) Math.Round(num * 1024);
        else if (suf == "mi")
            return (long) Math.Round(num * 1024 * 1024);
        else if (suf == "gi")
            return (long) Math.Round(num * 1024 * 1024 * 1024);
        else if (suf == "ti")
            return (long) Math.Round(num * 1024 * 1024 * 1024 * 1024);
        else
            throw new CommandLineValidationException(RhoML.Parse($$"""The suffix "{h}{{suf}}{}" in option {option}--write{} {h}{{size}}{} is not recognized."""));
    }

    private static void PostBuildCheck(IPostBuildReporter rep)
    {
        CommandLineParser.PostBuildStep<CommandLine>(rep);
    }
}
