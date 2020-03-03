using EtherCAT.NET;
using EtherCAT.NET.Extensibility;
using Microsoft.DotNet.PlatformAbstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OneDas.Extensibility;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SampleMaster
{
    class Program
    {
        static async Task Main(string[] args)
        {
            /* Set interface name. Edit this to suit your needs. */
            var interfaceName = "Ethernet1"; // "eth0";

            /* Set ESI location. Make sure it contains ESI files! The default path is /home/{user}/.local/share/ESI */
            var localAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var esiDirectoryPath = Path.Combine(localAppDataPath, "ESI");
            Directory.CreateDirectory(esiDirectoryPath);

            /* Copy native file. NOT required in end user scenarios, where EtherCAT.NET package is installed via NuGet! */
            var codeBase = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            Directory.EnumerateFiles(Path.Combine(codeBase, "runtimes"), "*soem_wrapper.*", SearchOption.AllDirectories).ToList().ForEach(filePath =>
            {
                if (filePath.Contains(RuntimeEnvironment.RuntimeArchitecture))
                {
                    File.Copy(filePath, Path.Combine(codeBase, Path.GetFileName(filePath)), true);
                }
            });

            /* prepare dependency injection */
            var services = new ServiceCollection();

            ConfigureServices(services);

            /* create types */
            var provider = services.BuildServiceProvider();
            var extensionFactory = provider.GetRequiredService<IExtensionFactory>();
            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger("EtherCAT Master");

            /* create EtherCAT master settings (with 10 Hz cycle frequency) */
            var cycleFrequency = 10U;
            var settings = new EcSettings(cycleFrequency, esiDirectoryPath, interfaceName);

            /* create root slave info by scanning available slaves */
            var rootSlaveInfo = EcUtilities.ScanDevices(settings.InterfaceName);

            foreach (var current in rootSlaveInfo.Descendants())
            {
                try
                {
                    ExtensibilityHelper.CreateDynamicData(settings.EsiDirectoryPath, extensionFactory, current);
                }
                catch (Exception ex)
                {
                    logger.LogInformation(ex.Message);
                    Console.ReadKey(true);
                    return;
                }
            }

            /* print list of slaves */
            var message = new StringBuilder();
            var slaves = rootSlaveInfo.Descendants().ToList();

            // Example Slave 0 is EP3174-0002
            var EP3174_0002 = slaves[0];
            var EP3174_0002_Ch0 = EP3174_0002.DynamicData.PdoSet[0];
            var EP3174_0002_Ch0_Variables = EP3174_0002_Ch0.VariableSet;
            var EP3174_0002_Ch0_Variable0 = EP3174_0002_Ch0_Variables[0];

            message.AppendLine($"Found {slaves.Count()} slaves:");

            slaves.ForEach(current =>
            {
                message.AppendLine($"{current.DynamicData.Name} (PDOs: {current.DynamicData.PdoSet.Count} - CSA: { current.Csa })");
            });

            logger.LogInformation(message.ToString().TrimEnd());

            /* create variable references for later use */
            var variables = slaves.SelectMany(child => child.GetVariableSet()).ToList();

            /* create EC Master */
            using (var master = new EcMaster(settings, extensionFactory, logger))
            {
                try
                {
                    master.Configure(rootSlaveInfo);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex.Message);
                    throw;
                }

                /* start master */
                var random = new Random();
                var cts = new CancellationTokenSource();

                var EP3174_0002_Ch0_Value = slaves[0].DynamicData.PdoSet[0].VariableSet.Last();
                var task = Task.Run(() =>
                {
                    var sleepTime = 1000 / (int)cycleFrequency;

                    while (!cts.IsCancellationRequested)
                    {
                        try
                        {
                            master.UpdateIO(DateTime.UtcNow);
                            unsafe
                            {
                                if (variables.Any())
                                {
                                    var inputCh0 = new Span<int>(EP3174_0002_Ch0_Value.DataPtr.ToPointer(), 1)[0].ToByteArray();
                                    int inputValue = BitConverter.ToInt16(inputCh0);
                                    double value = inputValue / 32767.0 * 10.0;
                                    logger.LogDebug($"{EP3174_0002_Ch0_Value.Name}: {value}");

                                    var myVariableSpan = new Span<int>(variables.First().DataPtr.ToPointer(), 1);
                                    myVariableSpan[0] = random.Next(0, 100);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex.Message);
                        }
                        Thread.Sleep(sleepTime);
                    }
                }, cts.Token);

                /* wait for stop signal */
                Console.ReadKey(true);

                cts.Cancel();
                await task;
            }
        }

        static void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IExtensionFactory, ExtensionFactory>();

            services.AddLogging(loggingBuilder =>
            {
                loggingBuilder.SetMinimumLevel(LogLevel.Debug);
                loggingBuilder.AddConsole();
            });
        }
    }
}
