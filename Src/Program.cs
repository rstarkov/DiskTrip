using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using RT.CommandLine;
using RT.PostBuild;
using RT.Util;
using RT.Util.Consoles;
using RT.Util.ExtensionMethods;
using RT.Util.Streams;

namespace DiskTrip;

[Documentation("Reads, writes and verifies large pseudo-random files in order to confirm error-less filesystem read/write operations.")]
class CommandLine : ICommandLineValidatable
{
    [IsPositional, IsMandatory]
    [Documentation("The name of the test file to write and/or verify.")]
    public string FileName = null;

    [Option("-w", "--write")]
    [DocumentationRhoML($$"""{h}Writes a test file {field}{{nameof(WriteSize)}}{} MB long (millions of bytes), then reads and verifies it.{}{n}{}When omitted, DiskTrip will verify the previously-created test file.{n}{}The number may be suffixed with {h}K{}, {h}M{}, {h}G{} (default), {h}T{}, {h}Ki{}, {h}Mi{}, {h}Gi{}, {h}Ti{} or {h}b{} to override the units (case-insensitive).{n}{}A special value of {h}fill{} (or {h}full{} or {h}free{}) specifies that all free space should be consumed.""")]
    public string WriteSize = null;
    public long WriteSizeBytes;
    public bool Write => WriteSize != null;
    public bool WriteFill => Write && WriteSizeBytes == 0;

    [Option("-d", "--delete")]
    [DocumentationRhoML("{h}Deletes the test file.{}{n}{}Normally the test file is not deleted regardless of the outcome of the test.")]
    public bool Delete = false;

    [Option("-o", "--overwrite")]
    [DocumentationRhoML("{h}Allows overwriting the test file if it already exists.{}{n}{}Normally DiskTrip exits with an error code if the test file already exists.")]
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

static partial class Program
{
    static ConsoleLogger Log;
    static CommandLine Args;

    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--post-build-check")
            return PostBuildChecker.RunPostBuildChecks(args[1], typeof(Program).Assembly);

        Args = CommandLineParser.ParseOrWriteUsageToConsole<CommandLine>(args);
        if (Args == null)
            return 1;

        if (Args.Write && File.Exists(Args.FileName) && !Args.Overwrite)
        {
            ConsoleUtil.WriteParagraphs(CommandLineParser.Colorize(RhoML.Parse($$"""The file {field}{{nameof(CommandLine.FileName)}}{} already exists. Use option {option}--overwrite{} to overwrite it.""")), stdErr: true);
            return 1;
        }
        if (!Args.Write && !File.Exists(Args.FileName))
        {
            ConsoleUtil.WriteParagraphs(CommandLineParser.Colorize(RhoML.Parse($$"""The file {field}{{nameof(CommandLine.FileName)}}{} does not exist. Use option {option}--write{} to create one.""")), stdErr: true);
            return 1;
        }

        Log = new ConsoleLogger();
        Log.ConfigureVerbosity("1");
        Log.MessageFormat = "{0} | ";

        // Write (if requested)
        if (Args.Write)
            if (!WriteRandomFile())
                return 1;

        // Read and verify
        var (errors, signature) = ReadAndVerifyFile();

        // Delete (if requested)
        if (Args.Delete)
        {
            File.Delete(Args.FileName);
            Log.Info("Test file deleted.");
        }

        // Report
        if (errors == 0)
            Log.Info("No errors detected.");
        else
            Log.Error($"Errors found. Mismatching bytes count: {errors:#,0}. Signature: {signature:X8}");
        return signature;
    }

    static bool WriteRandomFile()
    {
        Log.Info("Writing file...");
        var rnd = new RandomXorshift();
        var speeds = new Queue<double>();
        try
        {
            using var stream = new FileStream(Args.FileName, FileMode.Create, FileAccess.Write, FileShare.Read);
            byte[] data = new byte[32768];
            var path = Path.GetDirectoryName(Args.FileName);
            long remaining = Args.WriteFill ? GetFreeSpace(path) : Args.WriteSizeBytes;
            long lastProgressAt = -1;
            Ut.Tic();
            while (remaining > 0 || Args.WriteFill)
            {
                rnd.NextBytes(data);
                int blockLength = Args.WriteFill ? data.Length : (int) Math.Min(remaining, data.Length);
                try { stream.Write(data, 0, blockLength); }
                catch (IOException ex) when ((ex.HResult & 0xFFFF) == 0x27 || (ex.HResult & 0xFFFF) == 0x70)
                {
                    fillupDisk(stream, data, blockLength);
                    printProgress();
                    Log.Info($"  stopping because the disk is full");
                    return true;
                }
                remaining -= blockLength;
                if (stream.Position / 500_000_000 != (stream.Position - blockLength) / 500_000_000)
                {
                    if (Args.WriteFill)
                        remaining = GetFreeSpace(path);
                    printProgress();
                }
            }
            printProgress();
            return true;

            void printProgress()
            {
                if (stream.Position == lastProgressAt)
                    return;
                double speed = (stream.Position - lastProgressAt) / Ut.Tic();
                speeds.Enqueue(speed);
                while (speeds.Count > 40) // 20 GB
                    speeds.Dequeue();
                lastProgressAt = stream.Position;
                Log.Info($"  written {stream.Position / 1_000_000:#,0} MB @ {speed / 1_000_000:#,0} MB/s ({averageSpeed(speeds) / 1_000_000:#,0} MB/s average), {stream.Position / (double) (stream.Position + remaining) * 100.0:0.00}%");
            }
        }
        catch (Exception e)
        {
            Log.Error("Could not write to file: " + e.Message);
            Log.Error("");
            return false;
        }
    }

    private static void fillupDisk(FileStream stream, byte[] data, int length)
    {
        length = length / 512 * 512; // round down to nearest multiple of 512 bytes
        while (length > 0)
        {
            try
            {
                stream.Write(data, 0, length);
                return;
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) == 0x27 || (ex.HResult & 0xFFFF) == 0x70)
            {
                length -= 512;
            }
        }
    }

    static (int errors, int signature) ReadAndVerifyFile()
    {
        Log.Info("Reading file...");
        using var stream = new FileStream(Args.FileName, FileMode.Open, FileAccess.Read, FileShare.Read);
        var rnd = new RandomXorshift();
        var speeds = new Queue<double>();
        byte[] data = new byte[32768];
        byte[] read = new byte[32768];
        long remaining = stream.Length;
        long lastProgressAt = -1;
        int errors = 0;
        var signature = new CRC32Stream(new VoidStream());
        var signatureWriter = new BinaryWriter(signature);
        Ut.Tic();
        while (remaining > 0)
        {
            int blockLength = (int) Math.Min(remaining, data.Length);
            blockLength = stream.FillBuffer(read, 0, blockLength);
            remaining -= blockLength;
            rnd.NextBytes(data);

            // Compare chunk's worth of bytes in "read" and "data"
            unsafe
            {
                fixed (byte* pb1 = read, pb2 = data)
                {
                    ulong* pl1 = (ulong*) pb1;
                    ulong* pl2 = (ulong*) pb2;
                    ulong* pend1 = pl1 + (blockLength / (sizeof(ulong) * 4)) * 4; // number of comparisons in the unrolled loop

                    // The core comparison
                    while (pl1 < pend1)
                    {
                        if (*(pl1++) != *(pl2++)) goto notequal;
                        if (*(pl1++) != *(pl2++)) goto notequal;
                        if (*(pl1++) != *(pl2++)) goto notequal;
                        if (*(pl1++) != *(pl2++)) goto notequal;
                    }
                    goto equal;

                    notequal:;
                    // Compare the whole block again the slow way since it's by far the easiest way to find the different bytes and add the correct offests to the error signature
                    for (int i = 0; i < blockLength; i++)
                        if (data[i] != read[i])
                        {
                            errors++;
                            signatureWriter.Write(stream.Position - blockLength + i);
                            signatureWriter.Write(read[i]);
                        }
                    goto done;

                    equal:;
                    // Most of the block compared as equal. Compare the bit at the end.
                    for (int i = (int) ((byte*) pend1 - pb1); i < blockLength; i++)
                        if (data[i] != read[i])
                        {
                            errors++;
                            signatureWriter.Write(stream.Position - blockLength + i);
                            signatureWriter.Write(read[i]);
                        }

                    done:;
                }
            }

            if (stream.Position / 500_000_000 != (stream.Position - blockLength) / 500_000_000)
                printProgress();
        }
        printProgress();
        Log.Info("");
        return (errors, (int) signature.CRC);

        void printProgress()
        {
            if (stream.Position == lastProgressAt)
                return;
            double speed = (stream.Position - lastProgressAt) / Ut.Tic();
            speeds.Enqueue(speed);
            while (speeds.Count > 40) // 20 GB
                speeds.Dequeue();
            lastProgressAt = stream.Position;
            Log.Info($"  verified {stream.Position / 1_000_000:#,0} MB @ {speed / 1_000_000:#,0} MB/s ({averageSpeed(speeds) / 1_000_000:#,0} MB/s average), {stream.Position / (double) (stream.Position + remaining) * 100.0:0.00}%, errors: {errors:#,0}, signature: {signature.CRC:X8}");
        }
    }

    private static double averageSpeed(Queue<double> speeds)
    {
        var set = speeds.ToList();
        for (int i = 0; i < speeds.Count / 5; i++) // remove the worst fitting 20%
        {
            var avg = set.Average();
            var worst = set.MaxElement(v => Math.Abs(v - avg));
            if (Math.Max(avg, worst) / Math.Min(avg, worst) < 1.2)
                return avg;
            set.Remove(worst);
        }
        return set.Average();
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceExW(string lpDirectoryName, out ulong lpFreeBytesAvailable, out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    private static long GetFreeSpace(string path)
    {
        if (!GetDiskFreeSpaceExW(Path.GetFullPath(path), out var freeBytesAvailable, out var totalNumberOfBytes, out var totalNumberOfFreeBytes))
            throw new System.ComponentModel.Win32Exception();
        return (long) freeBytesAvailable;
    }
}

sealed class RandomXorshift
{
    private ulong _x = 123456789;
    private ulong _y = 362436069;
    private ulong _z = 521288629;
    private ulong _w = 88675123;

    public unsafe void NextBytes(byte[] buf)
    {
        if (buf.Length % 32 != 0)
            throw new ArgumentException("The buffer length must be a multiple of 32.", nameof(buf));
        ulong x = _x, y = _y, z = _z, w = _w;
        fixed (byte* pbytes = buf)
        {
            ulong* pbuf = (ulong*) pbytes;
            ulong* pend = (ulong*) (pbytes + buf.Length);
            while (pbuf < pend)
            {
                ulong tx = x ^ (x << 11);
                ulong ty = y ^ (y << 11);
                ulong tz = z ^ (z << 11);
                ulong tw = w ^ (w << 11);
                *(pbuf++) = x = w ^ (w >> 19) ^ (tx ^ (tx >> 8));
                *(pbuf++) = y = x ^ (x >> 19) ^ (ty ^ (ty >> 8));
                *(pbuf++) = z = y ^ (y >> 19) ^ (tz ^ (tz >> 8));
                *(pbuf++) = w = z ^ (z >> 19) ^ (tw ^ (tw >> 8));
            }
        }
        _x = x; _y = y; _z = z; _w = w;
    }
}
