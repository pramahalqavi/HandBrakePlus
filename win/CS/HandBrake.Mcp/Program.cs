// --------------------------------------------------------------------------------------------------------------------
// <copyright file="Program.cs" company="HandBrake Project (https://handbrake.fr)">
//   This file is part of the HandBrake source code - It may be used under the terms of the GNU General Public License.
// </copyright>
// --------------------------------------------------------------------------------------------------------------------

namespace HandBrake.Mcp
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using HandBrake.App.Core.Utilities;
    using HandBrake.Interop.Interop;
    using HandBrake.Mcp.Mcp;

    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            AppDomain.CurrentDomain.ProcessExit += (s, e) =>
            {
                try
                {
                    HandBrakeUtils.DisposeGlobal();
                }
                catch
                {
                    // Ignore on shutdown
                }
            };

            Portable.Initialise();

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            var server = new McpServer();
            try
            {
                await server.RunAsync(cts.Token);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[HandBrake.Mcp] Fatal error: {ex}");
                return 1;
            }
            finally
            {
                try
                {
                    HandBrakeUtils.DisposeGlobal();
                }
                catch
                {
                }
            }
        }
    }
}
