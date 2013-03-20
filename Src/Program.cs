using System;
using System.IO;
using System.Reflection;
using RT.Util;
using RT.Util.CommandLine;
using RT.Util.Consoles;
using RT.Util.ExtensionMethods;
using RT.Util.Streams;

[assembly: AssemblyTitle("DiskTrip")]
[assembly: AssemblyCompany("CuteBits")]
[assembly: AssemblyProduct("DiskTrip")]
[assembly: AssemblyCopyright("Copyright © CuteBits 2008-2013")]
[assembly: AssemblyVersion("2.0.9999.9999")]
[assembly: AssemblyFileVersion("2.0.9999.9999")]

namespace DiskTrip
{
    [DocumentationLiteral("Reads, writes and verifies large pseudo-random files in order to confirm error-less filesystem read//write operations.")]
    class CommandLineParams : ICommandLineValidatable
    {
#pragma warning disable 649 // Field is never assigned to, and will always have its default value null
        [Option("-f", "--filename"), IsMandatory]
        [DocumentationLiteral("The name of the file to be written to and read from.")]
        public string FileName;

        [Option("-s", "--size")]
        [DocumentationLiteral("The size of the file to create, in MB (millions of bytes). Defaults to 1000. Ignored in --read-only mode.")]
        public long Size = 1000;

        [Option("-wo", "--write-only")]
        [DocumentationLiteral("If specified, only the write phase of the test will be executed. The resulting file will not be deleted.")]
        public bool WriteOnly;

        [Option("-ro", "--read-only")]
        [DocumentationLiteral("If specified, only the read phase of the test will be executed. The file will not be deleted.")]
        public bool ReadOnly;
#pragma warning restore 649 // Field is never assigned to, and will always have its default value null

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
            CommandLineParser<CommandLineParams>.PostBuildStep(rep, null);
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
                return Ut.RunPostBuildChecks(args[1], typeof(Program).Assembly);
#endif

            try
            {
                Params = CommandLineParser<CommandLineParams>.Parse(args);
            }
            catch (CommandLineParseException e)
            {
                e.WriteUsageInfoToConsole();
                return 1;
            }

            long writelength = Params.Size * 1000 * 1000;

            Log = new ConsoleLogger();
            Log.ConfigureVerbosity("1");

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
                File.Delete(Params.FileName);
                Log.Info("Test file deleted. Total errors: {0}.".Fmt(errors));
                return errors;
            }
        }

        static bool WriteRandomFile(long length)
        {
            Log.Info("Writing file...");
            var rnd = new RandomXorshift();
            try
            {
                using (var stream = new FileStream(Params.FileName, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
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
                        if (progress >= 250 * 1000 * 1000)
                        {
                            double speed = progress / Ut.Toc(); Ut.Tic();
                            progress = 0;
                            remainingAtMsg = remaining;
                            Log.Info("  written {0} MB @ {2:#,0} MB/s, {1:0.00}%".Fmt((length - remaining) / (1000 * 1000), (length - remaining) / (double) length * 100.0, speed / 1000 / 1000));
                        }
                    }
                    if (remainingAtMsg != 0)
                        Log.Info("  written {0} MB, {1:0.00}%".Fmt(length / (1000 * 1000), 100.0));
                    return true;
                }
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
            FileStream stream = new FileStream(Params.FileName, FileMode.Open, FileAccess.Read, FileShare.Read);
            var rnd = new RandomXorshift();
            try
            {
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
                    int chunk = (int) Math.Min(remaining, data.Length);
                    stream.FillBuffer(read, 0, chunk);
                    rnd.NextBytes(data);

                    for (int i = 0; i < chunk; i++)
                        if (data[i] != read[i])
                        {
                            errors++;
                            signatureWriter.Write(length - remaining + i);
                        }

                    remaining -= chunk;
                    progress += chunk;
                    if (progress >= 250 * 1000 * 1000)
                    {
                        double speed = progress / Ut.Toc(); Ut.Tic();
                        progress = 0;
                        remainingAtMsg = remaining;
                        Log.Info("  read and verified {0} MB @ {4:#,0} MB/s, {1:0.00}%, errors: {2}, signature: {3:X8}".Fmt((length - remaining) / (1000 * 1000), (length - remaining) / (double) length * 100.0, errors, signature.CRC, speed / 1000 / 1000));
                    }
                }
                if (remainingAtMsg != 0)
                    Log.Info("  read and verified {0} MB, {1:0.00}%, errors: {2}, signature: {3:X8}".Fmt(length / (1000 * 1000), 100.0, errors, signature.CRC));
                Log.Info("");
                return errors;
            }
            finally
            {
                stream.Close();
            }
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
                throw new ArgumentException("The buffer length must be a multiple of 16.", "buf");
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
}
