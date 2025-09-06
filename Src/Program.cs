using System.Runtime.InteropServices;
using RT.CommandLine;
using RT.PostBuild;
using RT.Util;
using RT.Util.Consoles;
using RT.Util.ExtensionMethods;
using RT.Util.Streams;

namespace DiskTrip;

static partial class Program
{
    static CommandLine Args;
    static ConsoleLogger Log;

    const int PROGRESS_INTERVAL = 500_000_000;
    const int SPEED_SAMPLES = 40;
    const int FILE_BUFFER_SIZE = 256 * 1024;

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
            byte[] data = new byte[FILE_BUFFER_SIZE];
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
                if (stream.Position / PROGRESS_INTERVAL != (stream.Position - blockLength) / PROGRESS_INTERVAL)
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
                while (speeds.Count > SPEED_SAMPLES) // 20 GB
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
        byte[] data = new byte[FILE_BUFFER_SIZE];
        byte[] read = new byte[FILE_BUFFER_SIZE];
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
                    // Compare the whole block again the slow way since it's by far the easiest way to find the different bytes and add the correct offsets to the error signature
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

            if (stream.Position / PROGRESS_INTERVAL != (stream.Position - blockLength) / PROGRESS_INTERVAL)
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
            while (speeds.Count > SPEED_SAMPLES) // 20 GB
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
