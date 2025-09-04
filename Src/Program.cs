using RT.CommandLine;
using RT.PostBuild;
using RT.Util;
using RT.Util.Consoles;
using RT.Util.ExtensionMethods;
using RT.Util.Streams;

namespace DiskTrip;

[Documentation("Reads, writes and verifies large pseudo-random files in order to confirm error-less filesystem read/write operations.")]
class CommandLineParams : ICommandLineValidatable
{
    [Option("-f", "--filename"), IsMandatory]
    [Documentation("The name of the file to be written to and read from.")]
    public string FileName = null;

    [Option("-s", "--size")]
    [Documentation("The size of the file to create, in MB (millions of bytes). Defaults to 1000. Ignored in --read-only mode.")]
    public long Size = 1000;

    [Option("-wo", "--write-only")]
    [Documentation("If specified, only the write phase of the test will be executed. The resulting file will not be deleted.")]
    public bool WriteOnly = false;

    [Option("-ro", "--read-only")]
    [Documentation("If specified, only the read phase of the test will be executed. The file will not be deleted.")]
    public bool ReadOnly = false;

    [Option("-kf", "--keep-file")]
    [Documentation("If specified, the test file will not be deleted once all tests are done. This option is implied in --read-only and --write-only modes.")]
    public bool KeepFile = false;

    public ConsoleColoredString Validate()
    {
        if (ReadOnly && WriteOnly)
            return "The options {0} and {1} are mutually exlusive. Specify only one of the two and try again.".ToConsoleColoredString()
                .Fmt("--read-only".Color(ConsoleColor.White), "--write-only".Color(ConsoleColor.White));
        return null;
    }

#if DEBUG
    private static void PostBuildCheck(IPostBuildReporter rep)
    {
        CommandLineParser.PostBuildStep<CommandLineParams>(rep);
    }
#endif
}

static class Program
{
    static ConsoleLogger Log;
    static CommandLineParams Params;

    static int Main(string[] args)
    {
#if DEBUG
        if (args.Length == 2 && args[0] == "--post-build-check")
            return PostBuildChecker.RunPostBuildChecks(args[1], typeof(Program).Assembly);
#endif

        Params = CommandLineParser.ParseOrWriteUsageToConsole<CommandLineParams>(args);
        if (Params == null)
            return 1;

        long writelength = Params.Size * 1_000_000;

        Log = new ConsoleLogger();
        Log.ConfigureVerbosity("1");
        Log.MessageFormat = "{0} | ";

        if (Params.WriteOnly)
        {
            return WriteRandomFile(writelength) ? 0 : 1;
        }
        else if (Params.ReadOnly)
        {
            return ReadAndVerifyFile();
        }
        else
        {
            int errors = 0;

            if (!WriteRandomFile(writelength))
                return 1;
            errors += ReadAndVerifyFile();
            if (errors != 0)
                errors += ReadAndVerifyFile();
            if (!Params.KeepFile)
            {
                File.Delete(Params.FileName);
                Log.Info("Test file deleted.");
            }
            Log.Info($"Total errors: {errors}.");
            return errors;
        }
    }

    static bool WriteRandomFile(long length)
    {
        Log.Info("Writing file...");
        var rnd = new RandomXorshift();
        var speeds = new Queue<double>();
        try
        {
            using var stream = new FileStream(Params.FileName, FileMode.Create, FileAccess.Write, FileShare.Read);
            byte[] data = new byte[32768];
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
                if (progress >= 250 * 1_000_000)
                {
                    double speed = progress / Ut.Tic();
                    speeds.Enqueue(speed);
                    while (speeds.Count > 80) // 20 GB
                        speeds.Dequeue();
                    progress -= 250 * 1_000_000;
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

    static int ReadAndVerifyFile()
    {
        Log.Info("Reading file...");
        using var stream = new FileStream(Params.FileName, FileMode.Open, FileAccess.Read, FileShare.Read);
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
            if (progress >= 250 * 1_000_000)
            {
                double speed = progress / Ut.Tic();
                speeds.Enqueue(speed);
                while (speeds.Count > 80) // 20 GB
                    speeds.Dequeue();
                progress -= 250 * 1_000_000;
                remainingAtMsg = remaining;
                Log.Info($"  verified {(length - remaining) / 1_000_000:#,0} MB @ {speed / 1_000_000:#,0} MB/s ({averageSpeed(speeds) / 1_000_000:#,0} MB/s average), {(length - remaining) / (double) length * 100.0:0.00}%, errors: {errors}, signature: {signature.CRC:X8}");
            }
        }
        if (remainingAtMsg != 0)
            Log.Info($"  verified {length / 1_000_000:#,0} MB, {100.0:0.00}%, errors: {errors}, signature: {signature.CRC:X8}");
        Log.Info("");
        return errors;
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
