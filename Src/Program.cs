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
    [DocumentationRhoML($$"""{h}Writes a test file {field}{{nameof(WriteSize)}}{} MB long (millions of bytes), then reads and verifies it.{}{n}{}When omitted, DiskTrip will verify the previously-created test file.""")]
    public double? WriteSize = null;
    public long WriteSizeBytes;
    public bool Write => WriteSize != null;

    [Option("-d", "--delete")]
    [DocumentationRhoML("{h}Deletes the test file.{}{n}{}Normally the test file is not deleted regardless of the outcome of the test.")]
    public bool Delete = false;

    [Option("-o", "--overwrite")]
    [DocumentationRhoML("{h}Allows overwriting the test file if it already exists.{}{n}{}Normally DiskTrip exits with an error code if the test file already exists.")]
    public bool Overwrite = false;

    public ConsoleColoredString Validate()
    {
        if (Write && WriteSize <= 0)
            return CommandLineParser.Colorize(RhoML.Parse($$"""The {option}--write{} size must be greater than zero."""));
        WriteSizeBytes = WriteSize == null ? 0 : (long) Math.Round(WriteSize.Value * 1_000_000);
        if (!Write && Overwrite)
            return CommandLineParser.Colorize(RhoML.Parse($$"""Option {option}--overwrite{} has no effect unless {option}--write{} is also specified."""));
        return null;
    }

    private static void PostBuildCheck(IPostBuildReporter rep)
    {
        CommandLineParser.PostBuildStep<CommandLine>(rep);
    }
}

static class Program
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
            long length = Args.WriteSizeBytes;
            long remaining = length;
            long remainingAtMsg = -1;
            int progress = 0;
            Ut.Tic();
            while (remaining > 0)
            {
                rnd.NextBytes(data);
                int chunk = (int) Math.Min(remaining, data.Length);
                stream.Write(data, 0, chunk);
                remaining -= chunk;
                progress += chunk;
                if (progress >= 500_000_000)
                {
                    double speed = progress / Ut.Tic();
                    speeds.Enqueue(speed);
                    while (speeds.Count > 80) // 20 GB
                        speeds.Dequeue();
                    progress -= 500_000_000;
                    remainingAtMsg = remaining;
                    Log.Info($"  written {(length - remaining) / 1_000_000:#,0} MB @ {speed / 1_000_000:#,0} MB/s ({averageSpeed(speeds) / 1_000_000:#,0} MB/s average), {(length - remaining) / (double) length * 100.0:0.00}%");
                }
            }
            if (remainingAtMsg != 0)
                Log.Info($"  written {length / 1_000_000:#,0} MB, {100.0:0.00}%");
            return true;
        }
        catch (Exception e)
        {
            Log.Error("Could not write to file: " + e.Message);
            Log.Error("");
            return false;
        }
    }

    static (int errors, int signature) ReadAndVerifyFile()
    {
        Log.Info("Reading file...");
        using var stream = new FileStream(Args.FileName, FileMode.Open, FileAccess.Read, FileShare.Read);
        var rnd = new RandomXorshift();
        var speeds = new Queue<double>();
        long length = stream.Length;
        byte[] data = new byte[32768];
        byte[] read = new byte[32768];
        long remaining = length;
        long remainingAtMsg = -1;
        int progress = 0;
        int errors = 0;
        var signature = new CRC32Stream(new VoidStream());
        var signatureWriter = new BinaryWriter(signature);
        Ut.Tic();
        while (remaining > 0)
        {
            int chunkLength = (int) Math.Min(remaining, data.Length);
            stream.FillBuffer(read, 0, chunkLength);
            rnd.NextBytes(data);

            // Compare chunk's worth of bytes in "read" and "data"
            unsafe
            {
                fixed (byte* pb1 = read, pb2 = data)
                {
                    ulong* pl1 = (ulong*) pb1;
                    ulong* pl2 = (ulong*) pb2;
                    ulong* pend1 = pl1 + (chunkLength / (sizeof(ulong) * 4)) * 4; // number of comparisons in the unrolled loop

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
                    for (int i = 0; i < chunkLength; i++)
                        if (data[i] != read[i])
                        {
                            errors++;
                            signatureWriter.Write(length - remaining + i);
                        }
                    goto done;

                    equal:;
                    // Most of the block compared as equal. Compare the bit at the end.
                    for (int i = (int) ((byte*) pend1 - pb1); i < chunkLength; i++)
                        if (data[i] != read[i])
                        {
                            errors++;
                            signatureWriter.Write(length - remaining + i);
                        }

                    done:;
                }
            }

            remaining -= chunkLength;
            progress += chunkLength;
            if (progress >= 500_000_000)
            {
                double speed = progress / Ut.Tic();
                speeds.Enqueue(speed);
                while (speeds.Count > 80) // 20 GB
                    speeds.Dequeue();
                progress -= 500_000_000;
                remainingAtMsg = remaining;
                Log.Info($"  verified {(length - remaining) / 1_000_000:#,0} MB @ {speed / 1_000_000:#,0} MB/s ({averageSpeed(speeds) / 1_000_000:#,0} MB/s average), {(length - remaining) / (double) length * 100.0:0.00}%, errors: {errors:#,0}, signature: {signature.CRC:X8}");
            }
        }
        if (remainingAtMsg != 0)
            Log.Info($"  verified {length / 1_000_000:#,0} MB, {100.0:0.00}%, errors: {errors:#,0}, signature: {signature.CRC:X8}");
        Log.Info("");
        return (errors, (int) signature.CRC);
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
}

sealed class RandomXorshift
{
    private uint _x = 123456789;
    private uint _y = 362436069;
    private uint _z = 521288629;
    private uint _w = 88675123;

    public unsafe void NextBytes(byte[] buf)
    {
        if (buf.Length % 16 != 0)
            throw new ArgumentException("The buffer length must be a multiple of 16.", nameof(buf));
        uint x = _x, y = _y, z = _z, w = _w;
        fixed (byte* pbytes = buf)
        {
            uint* pbuf = (uint*) pbytes;
            uint* pend = (uint*) (pbytes + buf.Length);
            while (pbuf < pend)
            {
                uint tx = x ^ (x << 11);
                uint ty = y ^ (y << 11);
                uint tz = z ^ (z << 11);
                uint tw = w ^ (w << 11);
                *(pbuf++) = x = w ^ (w >> 19) ^ (tx ^ (tx >> 8));
                *(pbuf++) = y = x ^ (x >> 19) ^ (ty ^ (ty >> 8));
                *(pbuf++) = z = y ^ (y >> 19) ^ (tz ^ (tz >> 8));
                *(pbuf++) = w = z ^ (z >> 19) ^ (tw ^ (tw >> 8));
            }
        }
        _x = x; _y = y; _z = z; _w = w;
    }
}
