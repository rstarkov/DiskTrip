using System;
using System.IO;
using System.Reflection;
using RT.Util;
using RT.Util.ExtensionMethods;
using RT.Util.Streams;

[assembly: AssemblyTitle("DiskTrip")]
[assembly: AssemblyCompany("CuteBits")]
[assembly: AssemblyProduct("DiskTrip")]
[assembly: AssemblyCopyright("Copyright © CuteBits 2008-2009")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace DiskTrip
{
    public static class Program
    {
        static string Filename;
        static int Seed;
        static ConsoleLogger Log;

        static int Main(string[] args)
        {
            CmdLineParser cmd = new CmdLineParser();
            cmd.DefineOption("f", "filename", CmdOptionType.Value, CmdOptionFlags.Required, "The name of the file to be written to and read from");
            cmd.DefineOption("s", "size", CmdOptionType.Value, CmdOptionFlags.Optional, "The size of the file to create, in MB (megabytes). Defaults to 1000. Ignored in --read-only mode.");
            cmd.DefineHelpSeparator();
            cmd.DefineOption("wo", "write-only", CmdOptionType.Switch, CmdOptionFlags.Optional, "If specified, only the write phase of the test will be executed. The resulting file will not be deleted.");
            cmd.DefineOption("ro", "read-only", CmdOptionType.Switch, CmdOptionFlags.Optional, "If specified, only the read phase of the test will be executed. The file will not be deleted.");
            cmd.DefineHelpSeparator();
            cmd.DefineOption(null, "seed", CmdOptionType.Value, CmdOptionFlags.Optional, "A seed to be used for random data generation. Defaults to 0.");

            cmd.Parse(args);
            cmd.ErrorIfPositionalArgsCountNot(0);
            if (cmd.OptSwitch("read-only") && cmd.OptSwitch("write-only"))
                cmd.Error("The options --read-only and --write-only are mutually exlusive - it is an error to specify both.");
            cmd.ProcessHelpAndErrors();

            Filename = cmd.OptValue("filename");
            Seed = int.Parse(cmd.OptValue("seed", "0"));

            long writelength = long.Parse(cmd.OptValue("size", "1000")) * 1024 * 1024;

            Log = new ConsoleLogger();
            Log.ConfigureVerbosity("1");

            if (cmd.OptSwitch("write-only"))
            {
                WriteRandomFile(writelength);
                return 0;
            }
            else if (cmd.OptSwitch("read-only"))
            {
                return ReadAndVerifyFile();
            }
            else
            {
                int errors = 0;
                WriteRandomFile(writelength);
                errors += ReadAndVerifyFile();
                errors += ReadAndVerifyFile();
                File.Delete(Filename);
                Log.Info("Test file deleted. Total errors: {0}.".Fmt(errors));
                return errors;
            }
        }

        static void WriteRandomFile(long length)
        {
            Log.Info("Writing file...");
            FileStream stream = new FileStream(Filename, FileMode.Create, FileAccess.Write, FileShare.Read);
            Random rnd = new Random(Seed);
            try
            {
                byte[] data = new byte[32768];
                long remaining = length;
                long remainingAtMsg = -1;
                int progress = 0;
                while (remaining > 0)
                {
                    rnd.NextBytes(data);
                    int chunk = (int)Math.Min(remaining, data.Length);
                    stream.Write(data, 0, chunk);
                    remaining -= chunk;
                    progress += chunk;
                    if (progress >= 250*1024*1024)
                    {
                        progress = 0;
                        remainingAtMsg = remaining;
                        Log.Info("  written {0} MB, {1:0.00}%", (length - remaining) / (1024*1024), (length - remaining) / (double)length * 100.0);
                    }
                }
                if (remainingAtMsg != 0)
                    Log.Info("  written {0} MB, {1:0.00}%", length / (1024*1024), 100.0);
                Log.Info("");
            }
            finally
            {
                stream.Close();
            }
        }

        static int ReadAndVerifyFile()
        {
            Log.Info("Reading file...");
            FileStream stream = new FileStream(Filename, FileMode.Open, FileAccess.Read, FileShare.Read);
            Random rnd = new Random(Seed);
            try
            {
                long length = stream.Length;
                byte[] data = new byte[32768];
                byte[] read = new byte[32768];
                long remaining = length;
                long remainingAtMsg = -1;
                int progress = 0;
                int errors = 0;
                CRC32Stream signature = new CRC32Stream(new VoidStream());
                BinaryWriter signatureWriter = new BinaryWriter(signature);
                while (remaining > 0)
                {
                    int chunk = (int)Math.Min(remaining, data.Length);
                    stream.Read(read, 0, chunk);
                    rnd.NextBytes(data);

                    for (int i = 0; i < chunk; i++)
                        if (data[i] != read[i])
                        {
                            errors++;
                            signatureWriter.Write(length - remaining + i);
                        }

                    remaining -= chunk;
                    progress += chunk;
                    if (progress >= 250*1024*1024)
                    {
                        progress = 0;
                        remainingAtMsg = remaining;
                        Log.Info("  read and verified {0} MB, {1:0.00}%, errors: {2}, signature: {3:X8}", (length - remaining) / (1024*1024), (length - remaining) / (double)length * 100.0, errors, signature.CRC);
                    }
                }
                if (remainingAtMsg != 0)
                    Log.Info("  read and verified {0} MB, {1:0.00}%, errors: {2}, signature: {3:X8}", length / (1024*1024), 100.0, errors, signature.CRC);
                Log.Info("");
                return errors;
            }
            finally
            {
                stream.Close();
            }
        }
    }
}
